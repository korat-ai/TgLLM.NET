# Contract: Core Ports (`TgLLM.Core.Ports`)

**Feature**: `001-inline-keyboard-hooks` | **Date**: 2026-07-04

These are the seams the transport-agnostic core defines. Every member returns `Task`/`ValueTask`
(Principle VI). Default implementations that ship in `TgLLM.Core` are noted. Adapters live in
outer layers (`TgLLM.BotApi`, `TgLLM.Webhooks`, `TgLLM.AspNetCore`) and depend inward only.

## `IUpdateSource` — transport seam (Principle IV)

Long polling and webhooks each implement this; the runtime consumes ONE ordered stream regardless
of transport. Hook code is identical across transports (FR-013).

```fsharp
type IUpdateSource =
    /// Yields domain events in arrival order. "Arrival order" is defined as the order of ingestion
    /// into this stream (a single-reader channel for webhooks; the getUpdates batch order for polling).
    abstract Updates : ct: CancellationToken -> IAsyncEnumerable<AgentEvent>
```
- **Adapters**: `LongPollingUpdateSource` (`TgLLM.BotApi`) — `getUpdates` loop, confirm-by-offset;
  `WebhookUpdateSource` (`TgLLM.Webhooks`) — single unbounded channel, `SingleReader = true`, fed by
  the HTTP endpoint which returns 200 immediately.
- **Contract**: MUST NOT drop events silently; MUST stop yielding promptly on cancellation.

## `IHookStore` — storage seam (FR-016)

```fsharp
type HookBinding = { Token: CallbackToken; Hook: Hook }

type IHookStore =
    /// Register all bindings of one keyboard as a unit (a durable impl MAY make this atomic).
    abstract Register   : bindings: IReadOnlyList<HookBinding> * ct: CancellationToken -> ValueTask
    abstract TryResolve : token: CallbackToken * ct: CancellationToken -> ValueTask<Hook voption>
    abstract Remove     : tokens: IReadOnlyList<CallbackToken> * ct: CancellationToken -> ValueTask
```
- **Default**: `InMemoryHookStore` (`ConcurrentDictionary<CallbackToken, Hook>`), completes
  synchronously (hence `ValueTask`).
- **Contract**: `TryResolve` MUST return `ValueNone` for unknown tokens (never throw) — feeds the
  FR-010 ack-only path. `Register` MUST make every binding resolvable before the send call proceeds.
- **C# note**: the C# façade ships its own `Task<Hook?>`-shaped store interface + internal adapter,
  so C# implementers never see `FSharpValueOption` (Principle II).

## `IPressDispatcher` — ordering seam (FR-015, SC-007)

```fsharp
type IPressDispatcher =
    inherit IAsyncDisposable
    /// Enqueue work for a chat. Work for the SAME chat runs sequentially in enqueue order;
    /// work for DIFFERENT chats runs concurrently.
    abstract Enqueue : chat: ChatId * work: (CancellationToken -> Task) -> ValueTask
```
- **Default**: `PerChatChannelDispatcher` — `ConcurrentDictionary<ChatId, Channel<_>>`, one unbounded
  channel (`SingleReader = true`) + one consumer loop per chat.
- **Contract**: MUST preserve per-chat FIFO order; MUST catch and report work exceptions (via
  `IHookObserver`) without terminating the chat's consumer loop (FR-009); `DisposeAsync` MUST drain
  or cancel in-flight work within the host shutdown budget.
- **Known follow-up**: idle-chat channel eviction (not required for PoC correctness).

## `IBotApiClient` — outbound Bot API seam

```fsharp
type IBotApiClient =
    abstract SendText       : chat: ChatId * text: MessageText * ct: CancellationToken -> Task<MessageId>
    abstract SendKeyboard   : chat: ChatId * text: MessageText * keyboard: RegisteredKeyboard * ct: CancellationToken -> Task<MessageId>
    /// Acknowledge a callback query (stops the client spinner). MUST be called for EVERY press,
    /// including unknown/stale ones (FR-007, FR-010, SC-003).
    abstract AnswerCallback : query: CallbackQueryId * ct: CancellationToken -> Task
```
- **Impl**: `TelegramBotApiClient` (`TgLLM.BotApi`) over Telegram.Bot; maps `RegisteredKeyboard` →
  `InlineKeyboardMarkup`. Grows with the Bot API surface in later slices.

## `IHookObserver` — observability seam (FR-009, FR-010)

```fsharp
type IHookObserver =
    abstract OnHookFailed   : press: ButtonPress * error: exn -> unit
    abstract OnUnknownToken : press: ButtonPress -> unit
```
- **Default**: `NoopHookObserver`. Façades bridge this to `ILogger` so failures are surfaced, never
  swallowed. Core stays dependency-free.

## `UpdateProcessor` — the transport-agnostic engine (Application layer, in Core)

```fsharp
[<Sealed>]
type UpdateProcessor =
    new : source: IUpdateSource * store: IHookStore * api: IBotApiClient *
          dispatcher: IPressDispatcher * observer: IHookObserver -> UpdateProcessor
    /// For each AgentEvent: resolve token → AnswerCallback immediately → if RunHook, enqueue on the
    /// press's per-chat channel. Ack-first (SC-003); unknown → ack, no hook, no error (FR-010);
    /// hook exceptions caught, reported, loop continues (FR-009). Runs until ct is cancelled.
    member RunAsync : ct: CancellationToken -> Task
```

### Agent-facing send op (used by both façades)
```fsharp
module AgentOps =
    val sendKeyboard :
        store: IHookStore -> api: IBotApiClient -> tokenGen: (unit -> CallbackToken) ->
        chat: ChatId -> text: MessageText -> spec: KeyboardSpec -> ct: CancellationToken -> Task<MessageId>
```

## Behavioural contract checklist (drives integration tests)

| Guarantee | Requirement | Test |
|-----------|-------------|------|
| Same scenario passes over BOTH transports | FR-013, SC-008 | Integration: run US1 over polling and webhook |
| Every press acknowledged (incl. unknown) | FR-007, FR-010, SC-003 | Assert `AnswerCallback` called per press |
| Exact hook per button, no cross-invocation | FR-004, FR-008, SC-002 | Routing property + integration |
| Per-chat order preserved, cross-chat concurrent | FR-015, SC-007 | Dispatcher model-based ordering property |
| Hook failure isolated & observed | FR-009, SC-006 | Failing hook → observer called, loop continues |
