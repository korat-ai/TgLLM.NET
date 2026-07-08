# Quickstart

Send an inline keyboard to a Telegram chat and run your own handler when a button is tapped — in
either F# or C#, over either long polling or webhooks. **The handler code is identical across
transports**; only the bootstrap call differs.

## Prerequisites

- .NET 8 or .NET 10 SDK.
- A bot token from [@BotFather](https://t.me/BotFather).
- For webhooks: a public HTTPS URL (a dev tunnel works) and a secret token you choose
  (1–256 chars, `A–Z a–z 0–9 _ -`).

## Install

```bash
dotnet add package TgLLM.FSharp   # F# projects
# or
dotnet add package TgLLM.CSharp   # C# projects
```

## F# — long polling

```fsharp
open TgLLM.Core
open TgLLM.FSharp

let keyboard =
    Keyboard.create [
        [ Button.on "Yes" (fun ctx -> ctx.ReplyTextAsync "You picked Yes")
          Button.on "No"  (fun ctx -> ctx.ReplyTextAsync "You picked No") ] ]

task {
    use! bot = TgBot.startPolling (TgBotConfig.create "<BOT_TOKEN>")
    match keyboard with
    | Ok spec -> let! _ = bot.SendKeyboard(chatId, MessageText.unsafe "Deploy?", spec) in ()
    | Error e -> eprintfn "invalid keyboard: %A" e
    do! Task.Delay Timeout.InfiniteTimeSpan   // keep the process alive
}
```

Switch to webhooks by replacing the bootstrap (handlers unchanged), then map the endpoint in your
ASP.NET Core app:

```fsharp
use! bot = TgBot.startWebhook (TgWebhookConfig.create ("<BOT_TOKEN>", "<PUBLIC_URL>", "<SECRET>"))
// in your ASP.NET Core app:
app.MapTelegramWebhook("/telegram/webhook", bot.WebhookSource, "<SECRET>")
```

## C# — long polling

```csharp
using TgLLM.CSharp;

var keyboard = new KeyboardBuilder()
    .Row(r => r
        .Button("Yes", ctx => ctx.ReplyTextAsync("You picked Yes"))
        .Button("No",  ctx => ctx.ReplyTextAsync("You picked No")))
    .Build();

await using var agent = await TelegramAgent.StartPollingAsync(
    new TelegramAgentOptions { BotToken = "<BOT_TOKEN>" });

await agent.SendKeyboardAsync(chatId, "Deploy?", keyboard);
await Task.Delay(Timeout.InfiniteTimeSpan);   // keep the process alive
```

Switch to webhooks (handlers unchanged):

```csharp
using TgLLM.AspNetCore;

await using var agent = await TelegramAgent.StartWebhookAsync(new TelegramAgentOptions
{
    BotToken = "<BOT_TOKEN>", PublicUrl = "<PUBLIC_URL>", SecretToken = "<SECRET>",
});
// in your ASP.NET Core app:
app.MapTelegramWebhook("/telegram/webhook", agent.WebhookSource, "<SECRET>");
```

## Tool Router

Instead of attaching a hook to each button at keyboard-build time, register named **tools** once,
then turn a runtime decision (an LLM agent's tool call, or any data source) into a neutral keyboard
**plan** — labels, tool names, optional string args, and plain URL buttons. A press resolves by name
to the exact registered tool; the bound argument is on `ctx.Arg`. The library ships no vendor LLM
parsers — you map your own agent's output into `Plan.tool`/`Plan.toolWithArg`/`Plan.url` calls.

```fsharp
open TgLLM.Core
open TgLLM.FSharp

let tools =
    ToolRegistry.create()
        .Register("approve", fun ctx -> ctx.EditTextAsync $"Approved by {ctx.User.FirstName}")
        .Register("reject",  fun ctx -> task { ctx.Answer("Rejected", alert = true) })

task {
    use! bot = TgBot.startPolling ((TgBotConfig.create "<BOT_TOKEN>").WithTools tools)

    let plan =
        Plan.rows [ [ Plan.toolWithArg "Approve" "approve" "build-42"; Plan.tool "Reject" "reject" ]
                    [ Plan.url "Docs" "https://example.test/docs" ] ]

    match plan with
    | Ok p -> let! _ = bot.SendKeyboardPlan(chatId, MessageText.unsafe "Deploy?", p) in ()
    | Error e -> eprintfn "%A" e
}
```

```csharp
using TgLLM.CSharp;

var tools = new ToolRegistry()
    .Register("approve", ctx => ctx.EditTextAsync($"Approved by {ctx.User.FirstName}"))
    .Register("reject",  ctx => { ctx.Answer("Rejected", alert: true); return Task.CompletedTask; });

await using var agent = await TelegramAgent.StartPollingAsync(
    new TelegramAgentOptions { BotToken = "<BOT_TOKEN>", Tools = tools });

var plan = new PlanBuilder()
    .Row(r => r.Tool("Approve", "approve", "build-42").Tool("Reject", "reject"))
    .Row(r => r.Url("Docs", "https://example.test/docs"))
    .Build();

await agent.SendKeyboardPlanAsync(chatId, "Deploy?", plan);
```

**What a tool can do**, via its `PressContext` (`ctx`):

- `ctx.Arg` — the bound string argument (or `null`/`None` if the button carried none).
- `ctx.EditTextAsync(text)` / `ctx.EditKeyboardAsync(plan)` — edit the pressed message's text and/or
  replace its keyboard **in place** — no new message is sent.
- `ctx.Answer(text, alert)` — show a toast (or a blocking alert) when the tap resolves; sent exactly
  once, after the tool returns (a watchdog keeps the client's loading spinner responsive even if the
  tool is slow).
- URL buttons (`Plan.url` / `PlanRowBuilder.Url`) open client-side and invoke no tool — mix them with
  tool buttons in the same keyboard.

**Durable bindings** (survive a restart): back the registry with a file-based store from
`TgLLM.Persistence` instead of the in-memory default.

```fsharp
open TgLLM.Persistence

let store = FileBindingStore.openAt "bindings.json"
use! bot = TgBot.startPolling (((TgBotConfig.create "<BOT_TOKEN>").WithTools tools).WithBindingStore store)
```

```csharp
var store = TgLLM.Persistence.FileBindingStore.openAt("bindings.json");
await using var agent = await TelegramAgent.StartPollingAsync(
    new TelegramAgentOptions { BotToken = "<BOT_TOKEN>", Tools = tools, BindingStore = store });
```

A NEW `FileBindingStore.openAt` over the SAME path loads whatever was saved by a previous run —
re-register your tools (by the same names) and taps sent before a restart still route.

Full runnable examples: [`examples/ToolRouterFSharp`](../examples/ToolRouterFSharp),
[`examples/ToolRouterCSharp`](../examples/ToolRouterCSharp) (both demonstrate long polling and
webhooks via a `TRANSPORT` environment variable).

## Tool Router extensions

Additive on top of everything above — a bot using none of this keeps working unchanged.

**Owner-scoped keyboards.** Restrict a keyboard's tool buttons to the one user it was sent for; a
different presser is acked with a notice and no tool ever runs.

```fsharp
let! _ = bot.SendKeyboardPlan(chatId, MessageText.unsafe "Deploy?", plan, owner = Owner.user requesterId)
// A different user tapping a button gets "This button isn't for you." and no tool runs.
```

```csharp
await agent.SendKeyboardPlanAsync(chatId, "Deploy?", plan, owner: Owner.User(requesterId));
```

**A tool manifest for your LLM's function-calling API.** Register tools with a description and an
argument schema; the registry renders itself as neutral JSON.

```fsharp
let tools =
    ToolRegistry
        .create()
        .Register(
            "approve",
            approveTool,
            description = "Approve the pending deploy",
            argSchema = """{ "type": "object", "properties": { "env": { "type": "string" } } }"""
        )

let toolsJson = tools.ManifestJson()   // [{ "name":"approve", "description":"...", "parameters":{...} }]
```

```csharp
var tools = new ToolRegistry().Register(
    "approve", ApproveAsync,
    description: "Approve the pending deploy",
    argSchema: """{ "type": "object", "properties": { "env": { "type": "string" } } }""");

var toolsJson = tools.ManifestJson();
```

**Structured arguments.** Bind a typed payload to a button instead of a raw string; read it back typed.

```fsharp
let plan = Plan.rows [ [ Plan.toolWith "Ship v2" "ship" {| version = "2.0"; canary = true |} ] ]

let shipTool (ctx: PressContext) = task {
    let req = ctx.GetArg<{| version: string; canary: bool |}>()
    do! deploy req.version req.canary
}
// A plain string arg (Plan.toolWithArg) still routes through ctx.Arg, unchanged.
```

```csharp
record ShipRequest(string Version, bool Canary);

var plan = new PlanBuilder().Row(r => r.Tool("Ship v2", "ship", new ShipRequest("2.0", true))).Build();

Task ShipAsync(PressContext ctx)
{
    var req = ctx.GetArg<ShipRequest>();
    return Deploy(req.Version, req.Canary);
}
```

**WebApp and CopyText buttons.** Both are handled entirely client-side — no tool runs, no callback
event is produced.

```fsharp
let plan =
    Plan.rows
        [ [ Plan.webApp "Open form" "https://app.example.com/form" ]     // launches a Mini App; url must be https
          [ Plan.copyText "Copy token" "ghp_xxx..." ]                    // copies to the clipboard; 1..256 chars
          [ Plan.tool "Done" "finish" ] ]                                 // still routes server-side
```

```csharp
var plan = new PlanBuilder()
    .Row(r => r.WebApp("Open form", "https://app.example.com/form"))
    .Row(r => r.CopyText("Copy token", "ghp_xxx..."))
    .Row(r => r.Tool("Done", "finish"))
    .Build();
```

**Expiry, confirm-once, idle eviction, and a second durable store.** `expiresIn`/`singleUse` are
send-time options on `SendKeyboardPlan` itself — one expiry and one single-use flag per send, shared
by every tool button it produces.

```fsharp
let! _ =
    bot.SendKeyboardPlan(
        chatId, MessageText.unsafe "Confirm?", plan,
        expiresIn = TimeSpan.FromMinutes 10., singleUse = true)
// After 10 minutes, or after the first successful press, a tap acks with no tool invoked.

open TgLLM.Persistence.LiteDb

let store = LiteDbBindingStore.OpenAt "bindings.db"   // interchangeable with FileBindingStore/InMemory
use! bot =
    TgBot.startPolling (
        (TgBotConfig.create "<BOT_TOKEN>")
            .WithTools(tools)
            .WithBindingStore(store)
            .WithIdleChatEviction(TimeSpan.FromMinutes 30.))
```

```csharp
await agent.SendKeyboardPlanAsync(chatId, "Confirm?", plan, expiresIn: TimeSpan.FromMinutes(10), singleUse: true);

await using var agent = await TelegramAgent.StartPollingAsync(new TelegramAgentOptions
{
    BotToken = "<BOT_TOKEN>",
    Tools = tools,
    BindingStore = TgLLM.Persistence.LiteDb.LiteDbBindingStore.OpenAt("bindings.db"),
    IdleChatEviction = TimeSpan.FromMinutes(30),
});
```

## A2UI renderer

[A2UI](https://a2ui.org) (Google, Apache-2.0) is an open, declarative protocol where an agent
describes a UI as messages — `createSurface` / `updateComponents` / `updateDataModel` /
`deleteSurface` — built from a pre-approved component catalog, and user interactions flow back as
`action` messages. `TgLLM.A2UI` renders the Telegram-representable subset of that catalog —
**`telegram-basic`**: `Text`, `Button`, `Row`, `Column`, `Divider`, `Image` — onto the Tool Router and
edit-in-place above, **bidirectionally**: a surface becomes one Telegram message, a Button tap becomes
an A2UI `action` handed to a host-provided sink, and the agent's follow-up messages re-render that
same message in place. An agent that already emits A2UI for a web/mobile renderer drives a Telegram
bot with no Telegram-specific code.

The renderer reuses whatever Tool Router the bot/agent already has wired in (`.WithTools`/`Tools`) —
it registers its own internal tool into that SAME registry, so a Button tap routes through the same
hardened engine (durable bindings, per-chat ordering, deferred ack) the rest of the Tool Router uses.
Building a renderer with no Tool Router wired in throws — the same fail-fast check `SendKeyboardPlan`
itself already applies to a plan with tool buttons.

**Wire the renderer and ingest a surface.**

```fsharp
open TgLLM.Core
open TgLLM.FSharp
open TgLLM.A2UI

// The sink is where a tap's A2UI `action` goes — relay it to your agent over whatever transport it uses.
let sink: ActionSink =
    fun action ->
        System.Console.WriteLine($"tap: {action.Name} on {action.SurfaceId} (component {action.SourceComponentId})")
        Task.CompletedTask

task {
    use! bot = TgBot.startPolling ((TgBotConfig.create "<BOT_TOKEN>").WithTools(ToolRegistry.create ()))
    let renderer = A2ui.renderer bot sink

    // The agent emits standard A2UI; the host hands each message to the renderer for a target chat
    // (A2UI carries no chat identity of its own).
    match!
        renderer.Ingest(
            chatId,
            """{ "version":"v1.0", "createSurface": {
                   "surfaceId":"deploy-1", "catalogId":"telegram-basic",
                   "dataModel": { "title": "Deploy to prod?" },
                   "components": [
                     { "id":"root", "component":"Column", "children":["title","actions"] },
                     { "id":"title", "component":"Text", "text":{"path":"/title"} },
                     { "id":"actions", "component":"Row", "children":["ok","docs"] },
                     { "id":"ok", "component":"Button", "text":"Approve",
                       "action":{"event":{"name":"approve","wantResponse":true,"actionId":"a1"}} },
                     { "id":"docs", "component":"Button", "text":"Docs",
                       "action":{"functionCall":{"call":"openUrl","args":{"url":"https://example.test/docs"}}} }
                   ] } }"""
        )
    with
    | Ok() -> ()   // one message sent: body "Deploy to prod?", one keyboard row [Approve][Docs]
    | Error e -> eprintfn "%s" (A2uiError.describe e)
}
```

```csharp
using TgLLM.CSharp;

// The sink is where a tap's A2UI `action` goes.
ActionSink sink = action =>
{
    Console.WriteLine($"tap: {action.Name} on {action.SurfaceId} (component {action.SourceComponentId})");
    return Task.CompletedTask;
};

var renderer = A2uiRenderer.Create(agent, sink);

var result = await renderer.IngestAsync(chatId, createSurfaceJson);   // same JSON as above
if (!result.Success) Console.Error.WriteLine(result.Error);
```

Text renders as literal, safe body text — reserved MarkdownV2 characters are escaped before the
message is sent, so agent-produced text can never break formatting or inject markup.

**A tap flows back, and the agent's reply re-renders in place.**

When a user taps "Approve", `sink` receives an `A2uiAction` — `Name = "approve"`, `SurfaceId =
"deploy-1"`, `SourceComponentId = "ok"`, `WantResponse = true`, `ActionId = Some "a1"` (`ActionId =
"a1"` in C#) — for your host to relay to its agent. "Docs" opens client-side (its action is a local
`openUrl` `functionCall`): no `action` is ever emitted for it, and no tool ever runs. The agent's
reply re-renders the SAME surface in place:

```fsharp
match!
    renderer.Ingest(
        chatId,
        """{ "version":"v1.0", "updateComponents": {
               "surfaceId":"deploy-1",
               "components": [ { "id":"title", "component":"Text", "text":"Deploying..." },
                               { "id":"root", "component":"Column", "children":["title"] } ] } }"""
    )
with
| Ok() -> ()   // the SAME message is edited in place: body "Deploying...", keyboard removed
| Error e -> eprintfn "%s" (A2uiError.describe e)

match! renderer.Ingest(chatId, """{ "version":"v1.0", "deleteSurface": { "surfaceId":"deploy-1" } }""") with
| Ok() -> ()   // the message is deleted
| Error e -> eprintfn "%s" (A2uiError.describe e)
```

```csharp
await renderer.IngestAsync(chatId, updateComponentsJson);   // edits the SAME message in place
await renderer.IngestAsync(chatId, """{ "version":"v1.0", "deleteSurface": { "surfaceId":"deploy-1" } }""");
```

A burst of `updateComponents`/`updateDataModel` for the same surface, sent without waiting for the
previous one to land, still produces exactly one send and further edits — never a flood of new
messages.

**Unsupported components are surfaced, not silent.** A component outside `telegram-basic` (e.g. a
`Slider`), an unknown `catalogId`, or a malformed message never crashes the bot and is never silently
dropped. It always comes back as a failed `Ingest`/`IngestAsync` result, and — independently — to an
observer, so it's visible even when the call it rode in on still succeeds because supported siblings
rendered fine.

```fsharp
let observer =
    { new IA2uiObserver with
        member _.OnA2uiError(error) = eprintfn "%s" (A2uiError.describe error)
        member _.OnMalformedAction(_descriptor) = () }

let renderer = A2ui.rendererWithObserver bot sink observer
```

```csharp
var renderer = A2uiRenderer.Create(agent, sink, onError: error =>
    Console.Error.WriteLine($"A2UI: {error.Kind} — {error.Description}"));
```

**What you get**

- An agent that already emits A2UI (for a web/mobile renderer) drives a Telegram bot with no
  Telegram-specific code.
- Only what a Telegram message can honestly show renders; everything else is surfaced, never
  silently wrong.
- The same tap → action → re-render loop works identically over long polling and webhooks, and in
  both the F# and C# façades.

## MAF bridge

[Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/) (`Microsoft.Agents.AI`) is
Microsoft's .NET agent-orchestration framework: an `AIAgent` runs tool calls on your behalf, and can
pause a run mid-turn to ask a human to approve or reject one before it executes
(`ToolApprovalRequestContent`/`ToolApprovalResponseContent`). `TgLLM.Maf` turns that pause into an
owner-scoped, single-use Telegram `[Approve][Reject]` keyboard — no button or callback plumbing
written by the host — reusing the Tool Router and edit-in-place machinery above: a tap resumes the
agent exactly once and edits the SAME message in place to the outcome.

**Author the agent** (plain MAF — your own code; `TgLLM.Maf` never sees your model or your tools'
implementations).

```fsharp
open System.ComponentModel
open Microsoft.Extensions.AI

// Tool methods live on a class so AIFunctionFactory can reflect parameter names and [<Description>]
// attributes into the tool's JSON schema — a let-bound F# function compiles to FSharpFunc, whose
// MethodInfo carries neither.
type MailTools() =
    [<Description "Send an email to a recipient.">]
    member _.SendEmail
        ([<Description "Recipient address">] toAddr: string, [<Description "Message body">] body: string) : string =
        $"Sent to {toAddr}."

let mailTools = MailTools()

// The lambda wrap is required: F# gives an instance method with tupled arguments (`SendEmail`'s own
// shape) the function type `string * string -> string`, and a bare method-group reference does not
// adapt directly to a multi-argument `Func<_,_,_>` constructor — wrap it explicitly.
let sendEmailTool =
    AIFunctionFactory.Create(Func<string, string, string>(fun toAddr body -> mailTools.SendEmail(toAddr, body)), name = "send_email")

// ApprovalRequiredAIFunction is what makes the agent PAUSE and ask a human before this tool runs.
let agent =
    chatClient.AsAIAgent(   // any IChatClient backend (OpenAI, Azure, local, ...)
        instructions = "You are a helpful assistant.",
        tools = [| ApprovalRequiredAIFunction(sendEmailTool) :> AITool |])
```

**Wire the bridge and start a run.**

```fsharp
open TgLLM.FSharp
open TgLLM.Maf

task {
    let tools = ToolRegistry.create ()   // Maf.startPolling registers maf-approve/maf-reject here
    let! bridge = Maf.startPolling ((TgBotConfig.create "<BOT_TOKEN>").WithTools tools) agent

    do! bridge.StartRun(chatId, "Email alice@example.com that the deploy is done.")
    // bridge.Bot is a regular TgBot for anything else the host wants to send.
}
```

```csharp
using TgLLM.FSharp;
using TgLLM.Maf;

var tools = ToolRegistry.create();
var config = TgBotConfig.create("<BOT_TOKEN>").WithTools(tools);

await using var bridge = await MafTelegramBridge.StartPollingAsync(config, agent);
await bridge.StartRunAsync(chatId, "Email alice@example.com that the deploy is done.");
```

**The approval loop.**

```text
user:  Email alice@example.com that the deploy is done.
bot:   Approval required: send_email            ← ONE message, buttons scoped to the sender
       toAddr: "alice@example.com"
       body: "The deploy is done."
       [ Approve ]  [ Reject ]
```

The tap is acknowledged immediately (the agent's continuation may take seconds). **Approve** runs the
tool and edits that SAME message to the outcome (`✔ send_email approved — Sent to
alice@example.com.`); **Reject** skips the tool and edits it to the rejection instead. Someone else
tapping is refused with "This button isn't for you." — the agent is never resumed; a second tap on an
already-decided approval is refused the same way. If the turn immediately raises a further approval,
the SAME message gets that next request's fresh buttons — the chat never fills with one message per
decision step.

**The text turn** (automatic): a person writes to the bot, and the bridge runs one agent turn and
replies in the same chat — no extra wiring beyond `Maf.startPolling`/`MafTelegramBridge.StartPollingAsync`
itself. Messages and taps in one chat are processed one at a time, in arrival order, so the
conversation keeps its context across turns.

```text
user:  What can you do?
bot:   I can draft and send emails for you. …
```

**Custom rendering and host-initiated runs** (optional). Redact arguments or localize labels with a
formatter; `owner` on `StartRun` (or `DefaultOwner` on the options) overrides who may decide an
approval a host-initiated run raises — a message-initiated turn always defaults to its own sender.

```fsharp
let options =
    { Formatter = ValueSome(fun p -> { Body = $"Allow {p.Tool}?"; ApproveLabel = "Да"; RejectLabel = "Нет" })
      Observer = ValueNone
      DefaultOwner = ValueNone
      ApprovalExpiry = ValueSome(TimeSpan.FromMinutes 10.) }

let! bridge = Maf.startPollingWith options (TgBotConfig.create("<BOT_TOKEN>").WithTools tools) agent
```

```csharp
await using var bridge = await MafTelegramBridge.StartPollingAsync(config, agent, new MafBridgeSettings
{
    ApprovalExpiry = TimeSpan.FromMinutes(10),
    Formatter = p => new ApprovalRenderInfo($"Allow {p.Tool}?"),
    OnSurfaced = e => logger.LogWarning("MAF bridge: {Event}", e),   // stale/malformed/failed — never silent
});
```

**Tool projection — one call, no double descriptions.** The agent's own declared `AIFunction`s
(name, description, JSON schema) become Tool Router tools and appear in `ManifestJson()` verbatim; an
unprojectable declaration is reported while its siblings still register.

```fsharp
let report = MafTools.project tools [ sendEmailTool; searchTool ]
// report.Registered = ["send_email"; "search"]; report.Problems = []
```

```csharp
var result = MafTools.Project(tools, new[] { sendEmailTool, searchTool });
```

**Nothing is silently dropped.** Every `IMafObserver` member (F#) / `MafBridgeSettings.OnSurfaced`
event (C#) reports a condition the bridge could not honor rather than swallowing it: a stale/unknown
decision, a malformed decision payload, a resume failure (the message is left with no live buttons),
an empty turn, or over-long/invalid output.

**Honest limits.**

- The agent's conversation (`AgentSession`) is held **in memory** for this release. Decision buttons
  ride the durable binding store, so a tap on a pre-restart approval message still routes, is
  acknowledged, and is owner-checked — it is then surfaced as **stale** rather than silently dropped
  or wrongly resumed, since the run it belonged to no longer exists after a restart.
- The reply path is non-streaming: one turn, one reply message.
- `Microsoft.Agents.AI` is a preview package, pinned to an exact version — it has renamed types
  across releases, so an upgrade is a deliberate, verified change, not a routine bump.

Full runnable examples: [`examples/MafFSharp`](../examples/MafFSharp),
[`examples/MafCSharp`](../examples/MafCSharp).

## What you can rely on

- **Right hook, every tap.** Each button routes to exactly its own handler; taps never cross.
- **Instant acknowledgement.** The tapped button's loading state clears within ~3s, even if your
  handler is slow or the keyboard is stale.
- **Ordered per chat.** Taps in one chat run one at a time in arrival order; different chats run
  concurrently.
- **Failures don't stop the bot.** A throwing handler is isolated and reported; later taps still work.
- **Proactive sends.** Call `SendKeyboard`/`SendKeyboardAsync` any time — you don't need an incoming
  message first.
- **Honest rendering.** An A2UI surface renders only the `telegram-basic` subset (Text, Button, Row,
  Column, Divider, Image); anything else is surfaced, never silently dropped or rendered wrong.

Full runnable examples live under [`examples/`](../examples): `PollingFSharp`, `PollingCSharp`,
`WebhookFSharp`, `WebhookCSharp`.
