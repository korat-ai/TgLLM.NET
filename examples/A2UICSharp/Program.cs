// Example: an agent that already speaks A2UI (for a web/mobile renderer) drives a Telegram bot
// with no Telegram-specific code of its own. Wires a durable LiteDB binding store and an A2UI
// renderer over it, then walks the loop the renderer supports end to end: send a surface as one
// message + inline keyboard, edit that SAME message in place on the agent's next reply, delete it,
// show a client-side button that needs no server round trip, and surface a component outside
// telegram-basic through an error observer instead of silently dropping or misrendering it.
// Runs over LONG POLLING by default, or WEBHOOKS when TRANSPORT=webhook is set — identical either
// way. Set BOT_TOKEN and CHAT_ID (a private chat); for webhooks also PUBLIC_URL and
// WEBHOOK_SECRET; then `dotnet run`.
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using TgLLM.AspNetCore;
using TgLLM.CSharp;
using TgLLM.Persistence.LiteDb;

static string RequireEnv(string name) =>
    Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"environment variable {name} is required");

// The initial surface: a Markdown Text bound to the live data model, and two server-bound Buttons
// in a Row. A tap on either flows back through OnAction below as an A2uiAction, resolved against
// this SAME data model at tap time (not render time).
const string DeploySurfaceJson = """
    {
      "version": "v1.0",
      "createSurface": {
        "surfaceId": "deploy-1",
        "catalogId": "telegram-basic",
        "dataModel": { "title": "Deploy **v2** to prod?", "env": "prod" },
        "components": [
          { "id": "root", "component": "Column", "children": [ "title", "actions" ] },
          { "id": "title", "component": "Text", "text": { "path": "/title" } },
          { "id": "actions", "component": "Row", "children": [ "approve", "reject" ] },
          { "id": "approve", "component": "Button", "text": "Approve",
            "action": { "event": { "name": "approve", "context": { "env": { "path": "/env" } }, "wantResponse": true, "actionId": "a1" } } },
          { "id": "reject", "component": "Button", "text": "Reject",
            "action": { "event": { "name": "reject", "context": {}, "wantResponse": false } } }
        ]
      }
    }
    """;

// What the agent sends back once a tap arrives: same surface id, new text, no more buttons — this
// EDITS the same Telegram message in place rather than sending a new one.
const string UpdateDeploySurfaceJson = """
    {
      "version": "v1.0",
      "updateComponents": {
        "surfaceId": "deploy-1",
        "components": [
          { "id": "title", "component": "Text", "text": "Deploying…" },
          { "id": "root", "component": "Column", "children": [ "title" ] }
        ]
      }
    }
    """;

const string DeleteDeploySurfaceJson = """{ "version": "v1.0", "deleteSurface": { "surfaceId": "deploy-1" } }""";

// A client-side Button: tapping "Open docs" opens the URL directly on the device, with no
// callback and no round trip through the bot at all.
const string DocsSurfaceJson = """
    {
      "version": "v1.0",
      "createSurface": {
        "surfaceId": "docs-1",
        "catalogId": "telegram-basic",
        "components": [
          { "id": "root", "component": "Column", "children": [ "t1", "link" ] },
          { "id": "t1", "component": "Text", "text": "Full release notes are one tap away." },
          { "id": "link", "component": "Button", "text": "Open docs",
            "action": { "functionCall": { "call": "openUrl", "args": { "url": "https://example.test/docs" } } } }
        ]
      }
    }
    """;

const string DeleteDocsSurfaceJson = """{ "version": "v1.0", "deleteSurface": { "surfaceId": "docs-1" } }""";

// A Slider isn't in telegram-basic — it is surfaced to OnA2uiError below rather than dropped or
// rendered wrong. Its Text sibling still renders on its own (this surface carries no keyboard at
// all, since its only Button-shaped content is the unsupported one).
const string RatingSurfaceJson = """
    {
      "version": "v1.0",
      "createSurface": {
        "surfaceId": "rating-1",
        "catalogId": "telegram-basic",
        "components": [
          { "id": "root", "component": "Column", "children": [ "prompt", "picker" ] },
          { "id": "prompt", "component": "Text", "text": "Rate this build:" },
          { "id": "picker", "component": "Slider", "min": 1, "max": 5 }
        ]
      }
    }
    """;

const string DeleteRatingSurfaceJson = """{ "version": "v1.0", "deleteSurface": { "surfaceId": "rating-1" } }""";

// Where an outbound A2uiAction goes — the host relays it to its agent over whatever transport it
// uses (myAgent.SendA2uiAsync(action) in the quickstart). This example has no agent transport in
// scope, so it just prints what a tap produced.
Task OnAction(A2uiAction action)
{
    var context = string.Join(", ", action.Context.Select(kv => $"{kv.Key}={kv.Value}"));
    Console.WriteLine($"[sink] '{action.Name}' from surface '{action.SurfaceId}' component '{action.SourceComponentId}'");
    Console.WriteLine($"       wantResponse={action.WantResponse} actionId={action.ActionId} context=[{context}]");
    return Task.CompletedTask;
}

// Every A2UI-level condition the renderer surfaces — an unsupported component, an unknown
// catalog, a malformed message, a duplicate/unknown surface — reaches here independent of any
// single IngestAsync call's own A2uiIngestResult, so overall bot health stays observable even when
// a call itself still succeeded (e.g. an unsupported component next to supported siblings that
// rendered).
void OnA2uiError(A2uiErrorInfo error) => Console.WriteLine($"[a2ui] {error.Kind}: {error.Description}");

var botToken = RequireEnv("BOT_TOKEN");
var chatId = long.Parse(RequireEnv("CHAT_ID"));
var transport = Environment.GetEnvironmentVariable("TRANSPORT") ?? "polling";

// A2uiRenderer.Create requires a Tool Router wired into the agent — its internal a2ui-action tool
// is how a server-bound Button's tap ever reaches OnAction at all. This example registers no
// business tools of its own, so an empty registry is enough.
var tools = new ToolRegistry();
using var bindingStore = LiteDbBindingStore.OpenAt("a2ui-bindings.db");

if (transport == "webhook")
{
    var publicUrl = RequireEnv("PUBLIC_URL");
    var secret = RequireEnv("WEBHOOK_SECRET");

    await using var agent = await TelegramAgent.StartWebhookAsync(new TelegramAgentOptions
    {
        BotToken = botToken,
        PublicUrl = publicUrl,
        SecretToken = secret,
        Tools = tools,
        BindingStore = bindingStore,
    });

    var renderer = A2uiRenderer.Create(agent, OnAction, OnA2uiError);
    await WalkThroughLoopAsync(renderer, chatId);

    var app = WebApplication.CreateBuilder(args).Build();
    app.MapTelegramWebhook("/telegram/webhook", agent.WebhookSource, secret);
    Console.WriteLine($"A2UI renderer (webhook) listening. Telegram POSTs updates to {publicUrl}/telegram/webhook");
    await app.RunAsync();
}
else
{
    await using var agent = await TelegramAgent.StartPollingAsync(new TelegramAgentOptions
    {
        BotToken = botToken,
        Tools = tools,
        BindingStore = bindingStore,
    });

    var renderer = A2uiRenderer.Create(agent, OnAction, OnA2uiError);
    await WalkThroughLoopAsync(renderer, chatId);

    Console.WriteLine("A2UI renderer (long polling) running. Ctrl+C to stop.");
    await Task.Delay(Timeout.InfiniteTimeSpan);
}

// Renders deploy-1, edits it in place, deletes it; sends and tears down the LocalOpenUrl demo
// surface docs-1; then sends and tears down rating-1, whose unsupported Slider only ever reaches
// OnA2uiError above, never this call's own A2uiIngestResult.
async Task WalkThroughLoopAsync(A2uiRenderer renderer, long chat)
{
    Console.WriteLine($"Renderer catalog: {renderer.Catalog.CatalogId}");

    var sent = await renderer.IngestAsync(chat, DeploySurfaceJson);
    Console.WriteLine(sent.Success ? "sent 'deploy-1': one message, one keyboard row of two Buttons" : $"unexpected: {sent.Error}");

    // A real tap on Approve/Reject delivers a callback query that Telegram routes through the
    // agent's own Tool Router into OnAction above. Nothing taps this demo surface, so OnAction
    // never actually runs in this run — it is wired and ready for when it does.

    var updated = await renderer.IngestAsync(chat, UpdateDeploySurfaceJson);
    Console.WriteLine(updated.Success ? "edited 'deploy-1' in place: same message, new text, keyboard removed" : $"unexpected: {updated.Error}");

    var deleted = await renderer.IngestAsync(chat, DeleteDeploySurfaceJson);
    Console.WriteLine(deleted.Success ? "deleted 'deploy-1'" : $"unexpected: {deleted.Error}");

    var docsSent = await renderer.IngestAsync(chat, DocsSurfaceJson);
    Console.WriteLine(docsSent.Success
        ? "sent 'docs-1': a client-side Button opens its link on-device, no server round trip on tap"
        : $"unexpected: {docsSent.Error}");

    var docsDeleted = await renderer.IngestAsync(chat, DeleteDocsSurfaceJson);
    Console.WriteLine(docsDeleted.Success ? "deleted 'docs-1'" : $"unexpected: {docsDeleted.Error}");

    var ratingSent = await renderer.IngestAsync(chat, RatingSurfaceJson);
    Console.WriteLine(ratingSent.Success
        ? "sent 'rating-1': the Text sibling rendered; the Slider was surfaced to OnA2uiError above"
        : $"unexpected: {ratingSent.Error}");

    var ratingDeleted = await renderer.IngestAsync(chat, DeleteRatingSurfaceJson);
    Console.WriteLine(ratingDeleted.Success ? "deleted 'rating-1'" : $"unexpected: {ratingDeleted.Error}");
}
