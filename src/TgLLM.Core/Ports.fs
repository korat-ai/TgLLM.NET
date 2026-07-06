namespace TgLLM.Core

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

/// T016 (contracts/core-ports.md). Every member returns `Task`/`ValueTask` (Principle VI, no
/// `Async<'T>`). Default implementations that ship in `TgLLM.Core` are noted per port; adapters
/// (Telegram.Bot, webhooks, ASP.NET Core) live in outer layers and depend inward only
/// (Principle III).

/// The transport seam (Principle IV): long polling and webhooks each implement this; the engine
/// consumes ONE ordered stream regardless of transport, so hook code is identical across
/// transports (FR-013).
type IUpdateSource =
    /// Yields domain events in arrival order — the order of ingestion into this stream (a
    /// single-reader channel for webhooks; the `getUpdates` batch order for polling). MUST NOT
    /// drop events silently; MUST stop yielding promptly on cancellation.
    abstract Updates: ct: CancellationToken -> IAsyncEnumerable<AgentEvent>

/// The storage seam (FR-016). Default: `InMemoryHookStore` (`ConcurrentDictionary`), which
/// completes synchronously (hence `ValueTask`).
type IHookStore =
    /// Registers all bindings of one keyboard as a unit (a durable implementation MAY make this
    /// atomic). MUST make every binding resolvable before the send call proceeds.
    abstract Register: bindings: IReadOnlyList<HookBinding> * ct: CancellationToken -> ValueTask

    /// MUST return `ValueNone` for unknown tokens — never throw. Feeds the FR-010 ack-only path.
    abstract TryResolve: token: CallbackToken * ct: CancellationToken -> ValueTask<Hook voption>

    abstract Remove: tokens: IReadOnlyList<CallbackToken> * ct: CancellationToken -> ValueTask

/// The ordering seam (FR-015, SC-007). Default: `PerChatChannelDispatcher`
/// (`System.Threading.Channels`, `SingleReader = true`, one consumer loop per chat).
type IPressDispatcher =
    inherit IAsyncDisposable

    /// Enqueues work for a chat. Work for the SAME chat runs sequentially in enqueue order; work
    /// for DIFFERENT chats runs concurrently. MUST preserve per-chat FIFO order; MUST catch and
    /// report work exceptions (via `IHookObserver`) without terminating the chat's consumer loop
    /// (FR-009).
    abstract Enqueue: chat: ChatId * work: (CancellationToken -> Task) -> ValueTask

/// The outbound Bot API seam. Implementation: `TelegramBotApiClient` (`TgLLM.BotApi`, Phase 3)
/// over Telegram.Bot; maps `RegisteredKeyboard` to `InlineKeyboardMarkup`.
type IBotApiClient =
    abstract SendText: chat: ChatId * text: MessageText * ct: CancellationToken -> Task<MessageId>

    abstract SendKeyboard:
        chat: ChatId * text: MessageText * keyboard: RegisteredKeyboard * ct: CancellationToken -> Task<MessageId>

    /// Acknowledges a callback query (stops the client spinner). MUST be called for EVERY press,
    /// including unknown/stale ones (FR-007, FR-010, SC-003).
    abstract AnswerCallback: query: CallbackQueryId * ct: CancellationToken -> Task

    /// Acknowledges a callback query with an optional toast/alert (feature 002-llm-tool-router,
    /// FR-007, research.md D2). Used ONLY by the Tool Router's deferred-ack path — the slice-1
    /// ack-first path keeps using the no-arg overload above (FR-012, unchanged). `answerCallbackQuery`
    /// is one-shot server-side: a second call for the same query fails
    /// (`"query is too old and response timeout expired or query ID is invalid"`), which the caller
    /// is expected to catch and swallow when racing a watchdog (D2) — this port itself does not.
    abstract AnswerCallback: query: CallbackQueryId * text: string option * showAlert: bool * ct: CancellationToken -> Task

    /// Edit a message's text in place, optionally replacing its keyboard in the same call
    /// (feature 002-llm-tool-router, FR-006, research.md D1, T021). `keyboard = None` leaves the
    /// message's CURRENT keyboard untouched (Telegram's own `editMessageText` semantics: omitting
    /// `reply_markup` preserves whatever markup the message already has); `Some` replaces it, same
    /// as `SendKeyboard`'s unconditional markup. Implementations let `ApiRequestException` (e.g.
    /// `"message to edit not found"`/`"message is not modified"`) propagate rather than swallowing
    /// it — `UpdateProcessor`'s existing per-tool try/with (buildToolWork) already catches and
    /// reports ANY exception a tool raises via `IHookObserver`, so a caught-and-surfaced-not-crashed
    /// edit failure falls out of that existing machinery for free, with no new observer plumbing
    /// needed here (this port has no `ButtonPress` to attribute a failure to in the first place).
    abstract EditMessageText:
        chat: ChatId * message: MessageId * text: MessageText * keyboard: RegisteredKeyboard option * ct: CancellationToken ->
            Task

    /// Replace a message's keyboard only, leaving its text untouched (feature 002-llm-tool-router,
    /// FR-006, research.md D1, T021). Same propagate-don't-swallow error handling as
    /// `EditMessageText` above.
    abstract EditMessageReplyMarkup:
        chat: ChatId * message: MessageId * keyboard: RegisteredKeyboard option * ct: CancellationToken -> Task

/// The observability seam (FR-009, FR-010). Default: `NoopHookObserver`. Façades bridge this to
/// `ILogger` so failures are surfaced, never swallowed; Core stays dependency-free.
type IHookObserver =
    abstract OnHookFailed: press: ButtonPress * error: exn -> unit
    abstract OnUnknownToken: press: ButtonPress -> unit
