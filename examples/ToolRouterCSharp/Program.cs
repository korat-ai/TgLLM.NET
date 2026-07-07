// Example: a deploy-approval bot exercising the Tool Router's press authorization, LLM-facing tool
// manifest, structured button arguments, richer client-side buttons, and durable LiteDB storage.
// Runs over LONG POLLING by default, or WEBHOOKS when TRANSPORT=webhook is set — the tool code is
// IDENTICAL either way. Set BOT_TOKEN and CHAT_ID (a private chat, so its chat id doubles as the
// owner's user id); for webhooks also PUBLIC_URL and WEBHOOK_SECRET; then `dotnet run`.
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using TgLLM.AspNetCore;
using TgLLM.CSharp;
using TgLLM.Persistence.LiteDb;

static string RequireEnv(string name) =>
    Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"environment variable {name} is required");

var botToken = RequireEnv("BOT_TOKEN");
var chatId = long.Parse(RequireEnv("CHAT_ID"));
var transport = Environment.GetEnvironmentVariable("TRANSPORT") ?? "polling";

// The tool catalog a real agent would register once at startup. The library ships no business
// tools of its own — these are entirely example/host code. "Ship" carries a description and an
// argument schema; both are advisory metadata that only shape what ManifestJson() reports, never
// routing itself — a tool registered without either still registers and routes identically.
var tools = new ToolRegistry()
    .Register(
        "ship",
        async ctx =>
        {
            var request = ctx.GetArg<DeployRequest>();
            var audience = request.Canary ? "as a canary" : "to everyone";
            await ctx.EditTextAsync($"Shipping {request.Version} {audience} — approved by {ctx.User.FirstName}");
            ctx.Answer("Shipping...", alert: false);
        },
        description: "Ship the pending build to production",
        argSchema: """{ "type": "object", "properties": { "Version": { "type": "string" }, "Canary": { "type": "boolean" } }, "required": ["Version"] }""")
    .Register(
        "reject",
        async ctx =>
        {
            await ctx.EditTextAsync($"Rejected by {ctx.User.FirstName}");
            ctx.Answer("Rejected", alert: true);
        },
        description: "Reject the pending build");

// The neutral wire JSON a host feeds straight into its LLM's function-calling API.
Console.WriteLine($"Tool manifest for this agent's LLM:\n{tools.ManifestJson()}");

// A data-driven plan — stand-in for an LLM's own tool-call decision. Mixes a structured-argument
// tool button with a plain one, a WebApp launch, a CopyText button, and a plain link.
var plan = new PlanBuilder()
    .Row(r => r.Tool("Ship", "ship", new DeployRequest("42", Canary: true)).Tool("Reject", "reject"))
    .Row(r => r.WebApp("Release notes", "https://example.test/release-notes").CopyText("Copy build tag", "build-42"))
    .Row(r => r.Url("Docs", "https://example.test/docs"))
    .Build();

// Owner-scoped to the chat's own user (in a private chat, the chat id and the user id are the same
// account), kept alive for 10 minutes, and consumed after the first successful press.
var owner = Owner.User(chatId);
var expiresIn = TimeSpan.FromMinutes(10);
using var bindingStore = LiteDbBindingStore.OpenAt("bindings.db");

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

    await agent.SendKeyboardPlanAsync(chatId, "Deploy build #42?", plan, owner, expiresIn: expiresIn, singleUse: true);

    var app = WebApplication.CreateBuilder(args).Build();
    app.MapTelegramWebhook("/telegram/webhook", agent.WebhookSource, secret);
    Console.WriteLine($"Tool Router bot (webhook) listening. Telegram POSTs updates to {publicUrl}/telegram/webhook");
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

    await agent.SendKeyboardPlanAsync(chatId, "Deploy build #42?", plan, owner, expiresIn: expiresIn, singleUse: true);
    Console.WriteLine("Tool Router bot (long polling) running. Ctrl+C to stop.");
    await Task.Delay(Timeout.InfiniteTimeSpan);
}

/// <summary>
/// A structured payload bound to the "Ship" button. Any serializable record works as a tool
/// argument — the payload lives in the library's own binding store, free of a callback button's
/// tiny data limit — and comes back out typed via <c>ctx.GetArg&lt;DeployRequest&gt;()</c>.
/// </summary>
internal sealed record DeployRequest(string Version, bool Canary);
