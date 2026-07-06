namespace TgLLM.Core

open System.Threading
open System.Threading.Tasks
open FSharp.UMX

// This file compiles AFTER Values.fs and CallbackToken.fs (see the compile-order comment in
// TgLLM.Core.fsproj): `ButtonPress` needs `CallbackToken` + `ButtonLabel`, `PressContext` needs
// `ButtonLabel`, and `Hook = PressContext -> Task` needs `PressContext` — none of which exist yet
// earlier in the file list. `PressContext` itself lives here (rather than alongside
// `UpdateProcessor`) for the same reason: `Hook` (and therefore `HookBinding`, used by
// `IHookStore` in Ports.fs, and `ButtonSpec` in Keyboard.fs) must be defined long before
// UpdateProcessor.fs compiles.

/// UMX id measures — compile-time-only tagging; C# consumers see plain `long`/`string`
/// (Principle II).
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

/// An end user known to the bot. `Username` uses F#'s nullable reference-type annotation so
/// C# sees `string?`, never `FSharpOption` (Principle II).
type EndUser =
    { Id: UserId
      FirstName: string
      Username: string | null }

/// A button press, post-parse. Produced by the transport layer from a Telegram.Bot
/// `CallbackQuery` via a pure mapping.
type ButtonPress =
    { Token: CallbackToken
      QueryId: CallbackQueryId
      Chat: ChatId
      User: EndUser
      MessageId: MessageId
      ButtonLabel: ButtonLabel }

/// Runtime context passed to a `Hook` when its button is tapped. Constructed by
/// `UpdateProcessor`. `ReplyTextAsync` closes over the bot API's `SendText` call via a plain
/// function rather than an `IBotApiClient` reference, so `PressContext` (and therefore `Hook`)
/// has no dependency on `Ports.fs`, which compiles after this file.
[<Sealed>]
type PressContext
    (
        buttonLabel: ButtonLabel,
        chat: ChatId,
        user: EndUser,
        messageId: MessageId,
        cancellationToken: CancellationToken,
        replyTextAsync: string -> Task<MessageId>,
        ?arg: string,
        ?answerAction: (string -> bool -> unit),
        ?editTextAction: (string -> Task),
        ?editKeyboardAction: (ToolKeyboard -> Task)
    ) =
    member _.ButtonLabel = buttonLabel
    member _.Chat = chat
    member _.User = user
    member _.MessageId = messageId
    member _.CancellationToken = cancellationToken

    /// React in the chat. Fails fast (programmer error, Always-Rule 6) if `text` does
    /// not satisfy `MessageText`'s invariants — an invalid literal passed by the hook author is
    /// not a business error to route through `Result`.
    member _.ReplyTextAsync(text: string) : Task<MessageId> = replyTextAsync text

    /// The bound tool argument. `null` for slice-1 closure-style hooks and for argument-less tool
    /// buttons — annotated nullable, not `string option`, because this is a public API boundary
    /// consumed directly by the C# façade (Principle II: no `FSharpOption` on the C# surface).
    member _.Arg: string | null = arg |> Option.toObj

    /// Sets the ack directive for the deferred-ack tool path: the processor sends it via
    /// `AnswerCallback` exactly once, after the tool returns (or the watchdog fires, whichever
    /// first). Available ONLY on the deferred-ack TOOL path — on the slice-1 closure
    /// (`IHookStore`) path the ack has already fired ack-first before the hook ever runs, so
    /// `answerAction` is absent there and calling this is a programmer error by the hook author:
    /// it fails fast (`InvalidOperationException`) rather than silently doing nothing.
    member _.Answer(text: string, ?alert: bool) : unit =
        match answerAction with
        | Some action -> action text (defaultArg alert false)
        | None -> invalidOp "PressContext.Answer is available only for Tool Router buttons; a plain hook should reply/send instead."

    /// Edit the pressed message's text in place. Wired ONLY on the deferred-ack TOOL path
    /// (`UpdateProcessor.buildToolWork`) — a slice-1 closure-style hook has no keyboard-plan/
    /// binding-store concept to re-plan against, so `editTextAction` is absent there and calling
    /// this is a programmer error by the hook author (same fail-fast convention as `Answer`).
    member _.EditTextAsync(text: string) : Task =
        match editTextAction with
        | Some action -> action text
        | None ->
            invalidOp "PressContext.EditTextAsync is available only for Tool Router buttons; a plain hook should reply/send instead."

    /// Replace the pressed message's keyboard with one built from a fresh `ToolKeyboard` plan —
    /// re-plans via `ToolPlan.plan` and registers the replacement bindings before the edit reaches
    /// Telegram, same before-the-wire ordering guarantee as `SendKeyboardPlan`. Same fail-fast
    /// convention as `EditTextAsync` when this press didn't come through the Tool Router's
    /// deferred-ack path.
    member _.EditKeyboardAsync(keyboard: ToolKeyboard) : Task =
        match editKeyboardAction with
        | Some action -> action keyboard
        | None ->
            invalidOp
                "PressContext.EditKeyboardAsync is available only for Tool Router buttons; a plain hook should reply/send instead."

/// The agent-supplied reaction to a button press. Façades adapt this to their own idiomatic
/// delegate type (`Func<PressContext, Task>` for C#).
type Hook = PressContext -> Task

/// One button→hook association, as stored by `IHookStore`. `Hook` is a function value, so this
/// record can't support structural equality/comparison.
[<NoComparison; NoEquality>]
type HookBinding = { Token: CallbackToken; Hook: Hook }

/// Transport-agnostic domain event. Future update kinds slot in here without touching
/// transports.
type AgentEvent = ButtonPressed of ButtonPress
