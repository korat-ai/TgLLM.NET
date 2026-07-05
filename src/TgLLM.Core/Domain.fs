namespace TgLLM.Core

open System.Threading
open System.Threading.Tasks
open FSharp.UMX

// T005 (data-model.md "Identifiers", "Aggregates & entities"). This file compiles AFTER
// Values.fs and CallbackToken.fs (see the compile-order comment in TgLLM.Core.fsproj):
// `ButtonPress` needs `CallbackToken` + `ButtonLabel`, `PressContext` needs `ButtonLabel`, and
// `Hook = PressContext -> Task` needs `PressContext` — none of which exist yet at the point
// tasks.md nominally places Domain.fs (first). `PressContext` itself was also relocated here
// from UpdateProcessor.fs (tasks.md/T022) for the same reason: `Hook` (and therefore
// `HookBinding`, used by `IHookStore` in Ports.fs, and `ButtonSpec` in Keyboard.fs) must be
// defined long before UpdateProcessor.fs compiles.

/// UMX id measures — compile-time-only tagging; C# consumers see plain `long`/`string`
/// (Principle II, data-model.md "Identifiers").
[<Measure>]
type chatId

[<Measure>]
type userId

[<Measure>]
type messageId

[<Measure>]
type callbackQueryId

type ChatId = int64<chatId>
type UserId = int64<userId>
type MessageId = int64<messageId>
type CallbackQueryId = string<callbackQueryId>

/// An end user known to the bot (data-model.md "EndUser"). `Username` uses F#'s nullable
/// reference-type annotation so C# sees `string?`, never `FSharpOption` (Principle II).
type EndUser =
    { Id: UserId
      FirstName: string
      Username: string | null }

/// A button press, post-parse (data-model.md "ButtonPress"). Produced by the transport layer
/// from a Telegram.Bot `CallbackQuery` via a pure mapping (implemented in Phase 3, T024).
type ButtonPress =
    { Token: CallbackToken
      QueryId: CallbackQueryId
      Chat: ChatId
      User: EndUser
      MessageId: MessageId
      ButtonLabel: ButtonLabel }

/// Runtime context passed to a `Hook` when its button is tapped (FR-005, FR-006, FR-014,
/// data-model.md "PressContext"). Constructed by `UpdateProcessor`. `ReplyTextAsync` closes over
/// the bot API's `SendText` call via a plain function rather than an `IBotApiClient` reference,
/// so `PressContext` (and therefore `Hook`) has no dependency on `Ports.fs`, which compiles
/// after this file.
[<Sealed>]
type PressContext
    (
        buttonLabel: ButtonLabel,
        chat: ChatId,
        user: EndUser,
        messageId: MessageId,
        cancellationToken: CancellationToken,
        replyTextAsync: string -> Task<MessageId>
    ) =
    member _.ButtonLabel = buttonLabel
    member _.Chat = chat
    member _.User = user
    member _.MessageId = messageId
    member _.CancellationToken = cancellationToken

    /// React in the chat (FR-006). Fails fast (programmer error, Always-Rule 6) if `text` does
    /// not satisfy `MessageText`'s invariants — an invalid literal passed by the hook author is
    /// not a business error to route through `Result`.
    member _.ReplyTextAsync(text: string) : Task<MessageId> = replyTextAsync text

/// The agent-supplied reaction to a button press (FR-002, data-model.md "Hook & HookBinding").
/// Façades adapt this to their own idiomatic delegate type (`Func<PressContext, Task>` for C#).
type Hook = PressContext -> Task

/// One button→hook association, as stored by `IHookStore` (FR-016). `Hook` is a function value,
/// so this record can't support structural equality/comparison.
[<NoComparison; NoEquality>]
type HookBinding = { Token: CallbackToken; Hook: Hook }

/// Transport-agnostic domain event (data-model.md "AgentEvent"). Future update kinds slot in
/// here without touching transports (FR-013).
type AgentEvent = ButtonPressed of ButtonPress
