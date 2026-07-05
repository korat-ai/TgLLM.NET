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
