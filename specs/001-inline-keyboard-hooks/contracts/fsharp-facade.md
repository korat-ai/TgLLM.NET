# Contract: F# Public Façade (`TgLLM.FSharp` — NuGet package #1)

**Feature**: `001-inline-keyboard-hooks` | **Date**: 2026-07-04

Idiomatic F#: module functions, `Result` for validation, curried builders, the `task { }` CE, and
`Task`-returning members. No C#-shaped ergonomics forced on F# consumers (Principle II).

## Building a keyboard
```fsharp
module Button =
    /// Attach any Task-returning handler to a labelled button. Return value is ignored by the runtime.
    val on : label: string -> handler: (PressContext -> Task<'a>) -> ButtonSpec

module Keyboard =
    /// Validate rows (≥1 row, each ≥1 button, non-empty labels). Total, never throws.
    val create : ButtonSpec list list -> Result<KeyboardSpec, KeyboardError>
```

## Bot lifecycle & sending
```fsharp
type TgBotConfig =
    static member create : botToken: string -> TgBotConfig
    // fluent: WithAllowedUpdates, etc. (added as needed)

type TgWebhookConfig =
    static member create : botToken: string * publicUrl: string * secretToken: string -> TgWebhookConfig

type TgBot =
    interface IAsyncDisposable
    /// Start ingesting updates via long polling (calls deleteWebhook first).
    static member startPolling : TgBotConfig -> Task<TgBot>
    /// Build the webhook ingress (registers the webhook). Host wiring: TgLLM.AspNetCore.
    static member startWebhook : TgWebhookConfig -> Task<TgBot>
    member SendKeyboard : chat: ChatId * text: MessageText * keyboard: KeyboardSpec -> Task<MessageId>
    member SendText     : chat: ChatId * text: MessageText -> Task<MessageId>
```

## Canonical usage (matches spec US1)
```fsharp
open TgLLM.FSharp

let keyboard =
    Keyboard.create [
        [ Button.on "Yes" (fun ctx -> ctx.ReplyTextAsync "You picked Yes")
          Button.on "No"  (fun ctx -> ctx.ReplyTextAsync "You picked No") ] ]

task {
    use! bot = TgBot.startPolling (TgBotConfig.create botToken)   // swap for startWebhook — hooks unchanged
    match keyboard with
    | Ok spec -> let! _ = bot.SendKeyboard(chat, MessageText.unsafe "Deploy?", spec) in ()
    | Error e -> failwithf "bad keyboard: %A" e
}
```

## Contract guarantees
- Switching `startPolling` ↔ `startWebhook` requires **no change** to hook bodies (FR-013, SC-008).
- `Keyboard.create` returns `Error` (never throws) for invalid layouts.
- Hooks may be any `PressContext -> Task<'a>`; the runtime ignores the result.
- The public surface exposes `Result`, module functions, `ChatId`/`MessageId` (erased UMX = plain
  numbers at runtime), and `PressContext` — no C#-builder or nullable-first idioms.
