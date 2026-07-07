# TgLLM.NET

[![CI](https://github.com/TgLLM-NET/TgLLM.NET/actions/workflows/ci.yml/badge.svg)](https://github.com/TgLLM-NET/TgLLM.NET/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

An open-source Telegram Bot library for .NET. The core is written in F#, with idiomatic public
APIs for both F# and C# consumers.

> **Status**: under active development. This README will grow with each shipped feature; see
> `CHANGELOG.md` for what has landed so far.

## Features

- Send an interactive inline keyboard and attach a hook (a plain function/delegate) to each
  button — no manual `callback_data` bookkeeping.
- **Tool Router**: register named tools once; turn an LLM agent's decision into a neutral keyboard
  *plan* (labels + tool names + optional string args); taps route to the exact registered tool with
  its arg — no per-button glue, no vendor LLM parsing in the library.
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
