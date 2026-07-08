namespace TgLLM.Core

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

/// Every member returns `Task`/`ValueTask` (Principle VI, no `Async<'T>`). Default
/// implementations that ship in `TgLLM.Core` are noted per port; adapters (Telegram.Bot,
/// webhooks, ASP.NET Core) live in outer layers and depend inward only (Principle III).

/// The transport seam (Principle IV): long polling and webhooks each implement this; the engine
/// consumes ONE ordered stream regardless of transport, so hook code is identical across
/// transports.
type IUpdateSource =
    /// Yields domain events in arrival order — the order of ingestion into this stream (a
    /// single-reader channel for webhooks; the `getUpdates` batch order for polling). MUST NOT
    /// drop events silently; MUST stop yielding promptly on cancellation.
    abstract Updates: ct: CancellationToken -> IAsyncEnumerable<AgentEvent>

/// The storage seam. Default: `InMemoryHookStore` (`ConcurrentDictionary`), which completes
/// synchronously (hence `ValueTask`).
type IHookStore =
    /// Registers all bindings of one keyboard as a unit (a durable implementation MAY make this
    /// atomic). MUST make every binding resolvable before the send call proceeds.
    abstract Register: bindings: IReadOnlyList<HookBinding> * ct: CancellationToken -> ValueTask

    /// MUST return `ValueNone` for unknown tokens — never throw. Feeds the ack-only path for
    /// unknown/stale presses.
    abstract TryResolve: token: CallbackToken * ct: CancellationToken -> ValueTask<Hook voption>

    abstract Remove: tokens: IReadOnlyList<CallbackToken> * ct: CancellationToken -> ValueTask

/// The ordering seam. Default: `PerChatChannelDispatcher` (`System.Threading.Channels`,
/// `SingleReader = true`, one consumer loop per chat).
type IPressDispatcher =
    inherit IAsyncDisposable

    /// Enqueues work for a chat. Work for the SAME chat runs sequentially in enqueue order; work
    /// for DIFFERENT chats runs concurrently. MUST preserve per-chat FIFO order; MUST catch and
    /// report work exceptions (via `IHookObserver`) without terminating the chat's consumer loop.
    abstract Enqueue: chat: ChatId * work: (CancellationToken -> Task) -> ValueTask

/// The outcome of an edit-in-place call: classifies Telegram's two
/// well-known, non-fatal `editMessageText`/`editMessageReplyMarkup` errors so `UpdateProcessor`
/// never has to let them propagate as an exception to a tool author. `EditNotModified` ("message
/// is not modified") is a successful no-op — the tool's own re-render already matches what's on
/// the wire. `EditNotFound` ("message to edit not found") is a soft, OBSERVABLE failure — the
/// message vanished out from under the tool — surfaced via `IHookObserver.OnEditFailed`, never
/// thrown. Any OTHER failure (network error, rate limit, an unrecognized Bot API error, ...) is
/// still a genuine exception and propagates unchanged; only these two specific, well-known
/// outcomes are ever downgraded from an exception to a value.
type EditOutcome =
    | EditApplied
    | EditNotModified
    | EditNotFound

/// The Telegram message formatting mode for a sent message's text (Bot API `parse_mode`).
/// Wherever this type appears it is wrapped in `option`, and `None` sends exactly like the
/// overload that takes no parse mode at all — plain text, no formatting, no escaping obligation
/// on the caller. Legacy `Markdown` is deliberately not offered here (core.telegram.org marks it
/// deprecated in favor of `MarkdownV2`).
type ParseMode =
    | MarkdownV2
    | Html

/// The outbound Bot API seam. Implementation: `TelegramBotApiClient` (`TgLLM.BotApi`) over
/// Telegram.Bot; maps `RegisteredKeyboard` to `InlineKeyboardMarkup`.
type IBotApiClient =
    abstract SendText: chat: ChatId * text: MessageText * ct: CancellationToken -> Task<MessageId>

    /// Overload: send with an explicit parse mode. `None` behaves identically to the overload
    /// above (plain text) — additive, every pre-existing call site keeps using the overload above,
    /// unchanged.
    abstract SendText: chat: ChatId * text: MessageText * parseMode: ParseMode option * ct: CancellationToken -> Task<MessageId>

    abstract SendKeyboard:
        chat: ChatId * text: MessageText * keyboard: RegisteredKeyboard * ct: CancellationToken -> Task<MessageId>

    /// Overload: send with an explicit parse mode, same "`None` = identical to the overload above"
    /// contract as `SendText`'s pair.
    abstract SendKeyboard:
        chat: ChatId * text: MessageText * keyboard: RegisteredKeyboard * parseMode: ParseMode option * ct: CancellationToken ->
            Task<MessageId>

    /// Acknowledges a callback query (stops the client spinner). MUST be called for EVERY press,
    /// including unknown/stale ones.
    abstract AnswerCallback: query: CallbackQueryId * ct: CancellationToken -> Task

    /// Acknowledges a callback query with an optional toast/alert. Used ONLY by the Tool Router's
    /// deferred-ack path — the ack-first `IHookStore` path keeps using the no-arg overload above,
    /// unchanged. `answerCallbackQuery` is one-shot server-side: a second call for the same query
    /// fails (`"query is too old and response timeout expired or query ID is invalid"`), which the
    /// caller is expected to catch and swallow when racing a watchdog — this port itself does not.
    abstract AnswerCallback: query: CallbackQueryId * text: string option * showAlert: bool * ct: CancellationToken -> Task

    /// Edit a message's text in place, optionally replacing its keyboard in the same call.
    /// `keyboard = None` leaves the message's CURRENT keyboard untouched (Telegram's own
    /// `editMessageText` semantics: omitting `reply_markup` preserves whatever markup the message
    /// already has); `Some` replaces it, same as `SendKeyboard`'s unconditional markup.
    /// Implementations classify the two well-known non-fatal `ApiRequestException`s (`"message to
    /// edit not found"`/`"message is not modified"`) into `EditOutcome` rather than
    /// letting them propagate; any OTHER exception still propagates unchanged, caught and reported
    /// by `UpdateProcessor`'s existing per-tool try/with (`buildToolWork`) exactly as before.
    abstract EditMessageText:
        chat: ChatId * message: MessageId * text: MessageText * keyboard: RegisteredKeyboard option * ct: CancellationToken ->
            Task<EditOutcome>

    /// Overload: edit with an explicit parse mode, same "`None` = identical to the overload above"
    /// contract as `SendText`'s parse-mode pair — a bot-level edit of an agent-pushed surface (as
    /// opposed to a tool's own re-render of the message it was pressed from) needs the SAME
    /// MarkdownV2 request its initial send used.
    abstract EditMessageText:
        chat: ChatId *
        message: MessageId *
        text: MessageText *
        keyboard: RegisteredKeyboard option *
        parseMode: ParseMode option *
        ct: CancellationToken ->
            Task<EditOutcome>

    /// Replace a message's keyboard only, leaving its text untouched. Same classify-don't-throw
    /// contract as `EditMessageText` above.
    abstract EditMessageReplyMarkup:
        chat: ChatId * message: MessageId * keyboard: RegisteredKeyboard option * ct: CancellationToken -> Task<EditOutcome>

    /// Deletes a message. Classifies the well-known "message to delete not found" `ApiRequestException`
    /// (already gone — the user deleted it, or a previous call already removed it) into `false`
    /// rather than letting it propagate; `true` means the delete reached the wire. Any OTHER
    /// exception still propagates unchanged — the same classify-don't-throw discipline
    /// `EditMessageText`/`EditMessageReplyMarkup` use.
    abstract DeleteMessage: chat: ChatId * message: MessageId * ct: CancellationToken -> Task<bool>

/// The observability seam. Default: `NoopHookObserver`. Façades bridge this to `ILogger` so
/// failures are surfaced, never swallowed; Core stays dependency-free.
type IHookObserver =
    abstract OnHookFailed: press: ButtonPress * error: exn -> unit
    abstract OnUnknownToken: press: ButtonPress -> unit

    /// An edit-in-place softly failed: `EditNotFound` — the edited message vanished
    /// between send and edit. NOT an exception to the tool author: `PressContext.EditTextAsync`/
    /// `EditKeyboardAsync` complete normally regardless; this is the ONLY way such a soft failure
    /// is ever surfaced. `reason` describes the classified outcome. `EditNotModified` is a silent
    /// success and is never reported here at all — only a genuine (if soft) failure reaches this.
    abstract OnEditFailed: press: ButtonPress * reason: string -> unit

    /// The update-ingestion run loop itself (`UpdateProcessor.RunAsync`) faulted. Unlike
    /// `OnHookFailed`, there is no single `ButtonPress` to attribute this to: the failure means the
    /// WHOLE bot has stopped ingesting updates, not that
    /// one press's handling blew up (that case is `OnHookFailed`'s job, and is caught inside
    /// `RunAsync` so it can never reach here). MUST be surfaced here instead of silently swallowed
    /// at shutdown/dispose.
    abstract OnRunLoopFailed: error: exn -> unit

/// The host-supplied reaction to an incoming text message (additive, message-side sibling of
/// `Hook`). Runs on the message's chat's dispatcher lane (`IPressDispatcher.Enqueue`) — serialized
/// with that chat's button presses, in arrival order.
type MessageHandler = IncomingMessage -> CancellationToken -> Task

/// Message-side observability seam. A NEW, small interface rather than new members on
/// `IHookObserver`: that interface is public and host-implementable, so adding a member to it
/// would break every existing implementor — an additive capability needs its own interface.
/// Default: `NoopMessageObserver`. Façades bridge this to `ILogger`, exactly like `IHookObserver`.
type IMessageObserver =
    /// The host's `MessageHandler` threw while handling this message. Caught by the enqueued work
    /// thunk (`UpdateProcessor`), same containment contract as `IHookObserver.OnHookFailed` for a
    /// button press: the chat's dispatcher lane keeps running afterward.
    abstract OnMessageFailed: message: IncomingMessage * error: exn -> unit

/// Default `IMessageObserver`: silently drops everything — mirrors `NoopHookObserver`. Ships in
/// Core so a consumer never has to supply one just to wire a `MessageHandler`.
type NoopMessageObserver() =
    interface IMessageObserver with
        member _.OnMessageFailed(_message: IncomingMessage, _error: exn) = ()
