# Quickstart: Interactive Keyboards with Button Hooks

**Feature**: `001-inline-keyboard-hooks` | **Date**: 2026-07-04

Send an inline keyboard to a Telegram chat and run your own handler when a button is tapped — in
either F# or C#, over either long polling or webhooks. The handler code is identical across
transports; only the bootstrap call differs.

## Prerequisites

- .NET 8 or .NET 10 SDK.
- A bot token from [@BotFather](https://t.me/BotFather).
- For webhooks: a public HTTPS URL (a tunnel such as a dev tunnel works) and a secret token you
  choose (1–256 chars, `A–Z a–z 0–9 _ -`).

## Install

```bash
dotnet add package TgLLM.FSharp   # F# projects
# or
dotnet add package TgLLM.CSharp   # C# projects
```

## F# — long polling

```fsharp
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

Switch to webhooks by replacing the bootstrap line (handlers unchanged):

```fsharp
use! bot = TgBot.startWebhook (TgWebhookConfig.create ("<BOT_TOKEN>", "<PUBLIC_URL>", "<SECRET>"))
// then map the endpoint in your ASP.NET Core app:  app.MapTelegramWebhook(bot)
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
await using var agent = await TelegramAgent.StartWebhookAsync(
    new TelegramAgentOptions { BotToken = "<BOT_TOKEN>", PublicUrl = "<PUBLIC_URL>", SecretToken = "<SECRET>" });
// in your ASP.NET Core app:  app.MapTelegramWebhook(agent);
```

## What you can rely on

- **Right hook, every tap.** Each button routes to exactly its own handler; taps never cross
  handlers (SC-002).
- **Instant acknowledgement.** The tapped button's loading state clears within ~3s, even if your
  handler is slow or the keyboard is stale (SC-003, FR-010).
- **Ordered per chat.** Taps in one chat run one at a time in arrival order; different chats run
  concurrently (SC-007).
- **Failures don't stop the bot.** A throwing handler is isolated and logged; later taps still work
  (SC-006).
- **Proactive sends.** Call `SendKeyboard`/`SendKeyboardAsync` any time — you don't need an incoming
  message first (US2).

## Validate the slice (acceptance)

1. Send the two-button keyboard to a test chat.
2. Tap **Yes** → you get "You picked Yes"; tap **No** → "You picked No" (never swapped).
3. Observe the tapped button's spinner clears immediately.
4. Restart the bot, tap an old button → nothing happens, no crash (stale keyboard).
5. Repeat steps 1–3 with `startWebhook`/`StartWebhookAsync` — identical behavior (SC-008).

Full end-to-end examples live under `examples/` (`PollingFSharp`, `PollingCSharp`, `WebhookFSharp`,
`WebhookCSharp`).
