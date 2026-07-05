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
