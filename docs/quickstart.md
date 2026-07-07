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

## What you can rely on

- **Right hook, every tap.** Each button routes to exactly its own handler; taps never cross.
- **Instant acknowledgement.** The tapped button's loading state clears within ~3s, even if your
  handler is slow or the keyboard is stale.
- **Ordered per chat.** Taps in one chat run one at a time in arrival order; different chats run
  concurrently.
- **Failures don't stop the bot.** A throwing handler is isolated and reported; later taps still work.
- **Proactive sends.** Call `SendKeyboard`/`SendKeyboardAsync` any time — you don't need an incoming
  message first.

Full runnable examples live under [`examples/`](../examples): `PollingFSharp`, `PollingCSharp`,
`WebhookFSharp`, `WebhookCSharp`.
