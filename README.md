# TgLLM.NET

[![CI](https://github.com/TgLLM-NET/TgLLM.NET/actions/workflows/ci.yml/badge.svg)](https://github.com/TgLLM-NET/TgLLM.NET/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

An open-source **agent-UI layer for Telegram**, for .NET. It renders agent-facing UI protocols —
[A2UI](https://a2ui.org) today — onto Telegram, with the Tool Router as the native primitive an
agent's UI taps route through. The core is written in F#, with idiomatic public APIs for both F# and
C# consumers.

> **Status**: under active development. This README will grow with each shipped feature; see
> `CHANGELOG.md` for what has landed so far.

## Features

- Send an interactive inline keyboard and attach a hook (a plain function/delegate) to each
  button — no manual `callback_data` bookkeeping.
- **Tool Router**: register named tools once; turn an LLM agent's decision into a neutral keyboard
  *plan* (labels + tool names + optional string args); taps route to the exact registered tool with
  its arg — no per-button glue, no vendor LLM parsing in the library.
- **A2UI renderer**: render the Telegram-representable subset of Google's open
  [A2UI protocol](https://a2ui.org) (Text, Button, Row, Column, Divider, Image) onto the Tool Router
  and edit-in-place, bidirectionally — an agent that already speaks A2UI drives a Telegram bot with no
  Telegram-specific code; taps flow back as A2UI `action` messages to a host-provided sink.
- **MAF bridge**: turn a [Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/)
  agent's tool-approval pause into an owner-scoped, single-use `[Approve][Reject]` Telegram keyboard
  — a tap resumes the agent exactly once and edits the same message in place to the outcome. Also
  projects the agent's own declared `AIFunction`s into the Tool Router's manifest in one call, and
  answers an incoming chat message with the agent's reply automatically.
- React in place: edit the tapped message's text/keyboard, or answer with a toast/alert.
- **Owner-scoped keyboards**: restrict a keyboard's tool buttons to the one user it was sent for; a
  different presser is acked with a notice and no tool ever runs.
- Registered tools describe themselves as a neutral **manifest** (`ManifestJson()`) ready for an
  LLM's function-calling API, and buttons can bind **structured, typed arguments** (not just strings).
- **WebApp** and **CopyText** buttons — launch a Mini App or copy text to the clipboard, entirely
  client-side — alongside tool and URL buttons in the same keyboard.
- Bindings can **expire**, be **confirm-once** (single-use), and be **persisted** — a file-based
  store or an embedded LiteDB store, so taps still route after a restart.
- Works identically over **long polling** and **webhooks**.
- Two dedicated, idiomatic public packages: `TgLLM.FSharp` and `TgLLM.CSharp`.
- Per-chat ordered, cross-chat concurrent press handling.

## Install

```bash
dotnet add package TgLLM.FSharp   # F# consumers
dotnet add package TgLLM.CSharp   # C# consumers
```

*(Not yet published to NuGet — packaging lands in a later milestone; see `CHANGELOG.md`.)*

## Quickstart

See [`docs/quickstart.md`](docs/quickstart.md) for a full walkthrough. In short:

```fsharp
// F#
open TgLLM.Core
open TgLLM.FSharp

let keyboard =
    Keyboard.create [
        [ Button.on "Yes" (fun ctx -> ctx.ReplyTextAsync "You said yes!")
          Button.on "No"  (fun ctx -> ctx.ReplyTextAsync "You said no.") ]
    ]   // : Result<KeyboardSpec, KeyboardError>

task {
    use! bot = TgBot.startPolling (TgBotConfig.create botToken)   // or TgBot.startWebhook
    match keyboard with
    | Ok spec -> let! _ = bot.SendKeyboard(chatId, MessageText.unsafe "Deploy?", spec) in ()
    | Error e -> eprintfn "invalid keyboard: %A" e
}
```

```csharp
// C#
using TgLLM.CSharp;

var keyboard = new KeyboardBuilder()
    .Row(row => row
        .Button("Yes", ctx => ctx.ReplyTextAsync("You said yes!"))
        .Button("No",  ctx => ctx.ReplyTextAsync("You said no.")))
    .Build();

await using var agent = await TelegramAgent.StartPollingAsync(   // or StartWebhookAsync
    new TelegramAgentOptions { BotToken = botToken });
await agent.SendKeyboardAsync(chatId, "Deploy?", keyboard);
```

## Tool Router

Register named tools once; build a keyboard *plan* from data (an LLM agent's decision, or any other
data source) instead of wiring a hook per button. A press resolves by name to the exact registered
tool, with its bound argument on `ctx.Arg`.

```fsharp
// F#
let tools =
    ToolRegistry.create()
        .Register("approve", fun ctx -> ctx.EditTextAsync $"Approved by {ctx.User.FirstName}")
        .Register("reject",  fun ctx -> task { ctx.Answer("Rejected", alert = true) })

task {
    use! bot = TgBot.startPolling ((TgBotConfig.create botToken).WithTools tools)

    match Plan.rows [ [ Plan.tool "Approve" "approve"; Plan.tool "Reject" "reject" ] ] with
    | Ok plan -> let! _ = bot.SendKeyboardPlan(chatId, MessageText.unsafe "Deploy?", plan) in ()
    | Error e -> eprintfn "%A" e
}
```

```csharp
// C#
var tools = new ToolRegistry()
    .Register("approve", ctx => ctx.EditTextAsync($"Approved by {ctx.User.FirstName}"))
    .Register("reject",  ctx => { ctx.Answer("Rejected", alert: true); return Task.CompletedTask; });

await using var agent = await TelegramAgent.StartPollingAsync(
    new TelegramAgentOptions { BotToken = botToken, Tools = tools });

var plan = new PlanBuilder()
    .Row(r => r.Tool("Approve", "approve").Tool("Reject", "reject"))
    .Build();
await agent.SendKeyboardPlanAsync(chatId, "Deploy?", plan);
```

Bindings can be made durable with a file-based store (`TgLLM.Persistence`) so taps still route after
a restart — see [`docs/quickstart.md`](docs/quickstart.md#tool-router) for the full walkthrough
(edit-in-place, toasts, URL buttons, persistence).

Owner-scoped keyboards, a neutral tool manifest for LLM function-calling, structured typed
arguments, WebApp/CopyText buttons, expiry/confirm-once bindings, and an embedded LiteDB store are
covered in [`docs/quickstart.md`](docs/quickstart.md#tool-router-extensions).

## A2UI renderer

TgLLM.NET renders [A2UI](https://a2ui.org) (Google, Apache-2.0) — an open, declarative protocol for
describing a UI as agent-emitted messages — onto Telegram. `TgLLM.A2UI` maps the
Telegram-representable subset of A2UI's component catalog (`telegram-basic`: Text, Button, Row,
Column, Divider, Image) onto the Tool Router and edit-in-place above, bidirectionally: a surface
becomes one Telegram message, a Button tap becomes an A2UI `action` handed to your own sink, and the
agent's follow-up messages re-render that same message in place.

```fsharp
// F#
let sink: ActionSink = fun action -> myAgent.RelayActionAsync(action)

task {
    use! bot = TgBot.startPolling ((TgBotConfig.create botToken).WithTools(ToolRegistry.create ()))
    let renderer = A2ui.renderer bot sink   // requires a Tool Router already wired in
    match! renderer.Ingest(chatId, agentEmittedA2uiJson) with
    | Ok() -> ()
    | Error e -> eprintfn "%A" e
}
```

```csharp
// C#
var renderer = A2uiRenderer.Create(agent, action => myAgent.RelayActionAsync(action));
var result = await renderer.IngestAsync(chatId, agentEmittedA2uiJson);
```

A component outside `telegram-basic`, an unknown catalog, or a malformed message is always
surfaced — never silently dropped or rendered wrong. See
[`docs/quickstart.md`](docs/quickstart.md#a2ui-renderer) for the full walkthrough (the tap → action →
re-render loop and the error observer).

## MAF bridge

`TgLLM.Maf` turns a [Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/)
(`Microsoft.Agents.AI`) agent's human-in-the-loop tool approval into Telegram buttons — no button or
callback plumbing written by the host. A run that pauses on an approval-required tool renders one
owner-scoped `[Approve][Reject]` message; a tap resumes the agent exactly once and edits that same
message in place to the outcome (or to the next request's fresh buttons, for a chained approval).

```fsharp
// F#
open TgLLM.FSharp
open TgLLM.Maf

let tools = ToolRegistry.create ()   // Maf.startPolling registers maf-approve/maf-reject here

task {
    let! bridge = Maf.startPolling ((TgBotConfig.create botToken).WithTools tools) agent
    do! bridge.StartRun(chatId, "Email alice@example.com that the deploy is done.")
    // bridge.Bot is a regular TgBot for anything else the host wants to send.
}
```

```csharp
// C#
using TgLLM.Maf;

var tools = ToolRegistry.create();
var config = TgBotConfig.create(botToken).WithTools(tools);

await using var bridge = await MafTelegramBridge.StartPollingAsync(config, agent);
await bridge.StartRunAsync(chatId, "Email alice@example.com that the deploy is done.");
```

The agent's own declared `AIFunction`s project into the SAME Tool Router manifest in one call
(`MafTools.project`/`MafTools.Project`), and an incoming chat message is answered by the agent's
reply automatically, on the same per-chat ordering the rest of the Tool Router uses.

**Durable sessions** (optional): survive a process restart. The agent's conversation session lives
in memory by default; opt into a durable `ISessionStore` (`TgLLM.Persistence.FileSessionStore` or
`TgLLM.Persistence.LiteDb.LiteDbSessionStore`) via `WithSessionStore`, alongside a durable
`IBindingStore` via `WithBindingStore` — both stores must be durable together, since the approval
message's own buttons route through the binding store while the agent's conversation and its
still-pending approvals rehydrate through the session store.

```fsharp
open TgLLM.Persistence

let config =
    (TgBotConfig.create botToken)
        .WithTools(tools)
        .WithBindingStore(FileBindingStore.openAt "bindings.json")
        .WithSessionStore(FileSessionStore.OpenAt "sessions.json")

task {
    let! bridge = Maf.startPolling config agent
    // ...
}
```

```csharp
var config = TgBotConfig.create(botToken)
    .WithTools(tools)
    .WithBindingStore(TgLLM.Persistence.FileBindingStore.openAt("bindings.json"))
    .WithSessionStore(TgLLM.Persistence.FileSessionStore.OpenAt("sessions.json"));

await using var bridge = await MafTelegramBridge.StartPollingAsync(config, agent);
```

With both stores wired, an approval message shown BEFORE a restart is still honored AFTER one — the
tap resumes the agent and edits that same message in place, exactly as if the process had never gone
down. Without a session store (the default), a process restart loses any in-flight approval; a tap
on a pre-restart approval message is still acknowledged and owner-checked, then surfaced as stale
rather than silently misrouted — the SAME fallback a durable setup also uses whenever a decision
genuinely can no longer be honored (an incompatible persisted format, a missing tool after restart,
and so on). See [`docs/quickstart.md`](docs/quickstart.md#maf-bridge) for the full walkthrough,
including durable sessions' honest limits, the approval formatter override, and more.

## Project layout

```text
src/        TgLLM.Core (F#), transport adapters, and the two public façades
tests/      Core unit/property tests, façade tests, integration tests
examples/   Runnable polling/webhook examples in both F# and C#
```

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md).

## License

[MIT](LICENSE)
