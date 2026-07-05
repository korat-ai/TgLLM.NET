# Phase 1 Data Model: Interactive Keyboards with Button Hooks (Agent PoC)

**Feature**: `001-inline-keyboard-hooks` | **Date**: 2026-07-04

The model lives in `TgLLM.Core` (F#), is transport- and IO-agnostic (Principle III), and is shaped
so its kernel functions are pure and deterministic for FsCheck property tests (Principle I).
Types below are design sketches; smart-constructor validation rules are the source of truth for
tests. Signatures are F#; the C# façade re-expresses them idiomatically (see `contracts/`).

## Identifiers (UMX measures — erase to primitives, no idiom leakage)

| Type | Underlying | Notes |
|------|-----------|-------|
| `ChatId` | `int64<chatId>` | Telegram chat id. C# sees `long`. |
| `UserId` | `int64<userId>` | End-user id. |
| `MessageId` | `int64<messageId>` | Bot API "Integer"; `int64` defensively (verify width, Principle V). |
| `CallbackQueryId` | `string<callbackQueryId>` | Opaque per Bot API; used only to answer the query. |

UMX (`FSharp.UMX`) is compile-time only, so C# consumers see plain `long`/`string` — satisfying
Principle II without a separate C# id model for the PoC.

## Value objects (smart constructors)

### ButtonLabel
- **Represents**: the visible text on a button (FR-001).
- **Invariants**: non-empty after trim; length within Telegram's button-text limit (verify).
- **Construction**: `ButtonLabel.create : string -> Result<ButtonLabel, KeyboardError>`.

### MessageText
- **Represents**: the text of the message that carries a keyboard or a reply (FR-006).
- **Invariants**: non-empty; length ≤ Telegram message text limit (verify: 4096).
- **Construction**: `MessageText.create : string -> Result<MessageText, KeyboardError>`.

### CallbackToken (FR-011, D8)
- **Represents**: the opaque value the library writes into `callback_data` to identify a button.
- **Invariants**: encoded form is ≤ 64 bytes (Bot API `callback_data` limit, 1–64 bytes); parse is
  **total** (any string → `voption`, malformed → `ValueNone`, feeding the FR-010 stale/unknown path).
- **Construction / codec (PURE — property-tested)**:
  - `CallbackToken.ofGuid : Guid -> CallbackToken` (deterministic; FsCheck-drivable)
  - `CallbackToken.generate : unit -> CallbackToken` (= `ofGuid (Guid.NewGuid())`)
  - `CallbackToken.tryParse : string -> CallbackToken voption`
  - `CallbackToken.value : CallbackToken -> string`
- **Properties**: `tryParse (value t) = ValueSome t`; `String.length (value t) ≤ 64` (bytes).

### KeyboardError (validation outcomes)
```fsharp
type KeyboardError =
    | EmptyKeyboard
    | EmptyRow of rowIndex: int
    | EmptyLabel of rowIndex: int * colIndex: int
    | TextTooLong of length: int * max: int
    // row-count / buttons-per-row limits added after Bot API verification (Principle V)
```

## Aggregates & entities

### KeyboardSpec (agent-facing) → RegisteredKeyboard (wire-facing)
- **KeyboardSpec**: what the agent builds — rows of `ButtonSpec { Label; Hook }` (FR-001, FR-002,
  FR-012). Private case; `Keyboard.create : ButtonSpec list list -> Result<KeyboardSpec, KeyboardError>`
  validates ≥1 row and ≥1 non-empty-label button per row.
- **RegisteredKeyboard**: the same shape after token assignment — rows of
  `RegisteredButton { Label; Token }`. The transport layer maps this to Telegram.Bot's
  `InlineKeyboardMarkup`.
- **Pure planning step (FsCheck heart)**:
  `KeyboardPlan.assign : seq<CallbackToken> -> KeyboardSpec -> RegisteredKeyboard * HookBinding list`
  - **Properties**: row/column shape preserved; labels preserved; one binding per button;
    `bindings.length = buttonCount`; binding tokens = keyboard tokens; distinct input tokens ⇒
    distinct button tokens.

### Hook & HookBinding
- **Hook** = `PressContext -> Task` (core representation; façades adapt to their own delegate type).
- **HookBinding** = `{ Token: CallbackToken; Hook: Hook }` — one association stored per button.
- **Note (durability seam honesty, D-open)**: a `Hook` is a live function and cannot be serialized.
  The in-memory store relocates the token→hook map; a future durable store will need a stable
  `HookKey` re-attached at startup. Only the in-memory default ships now.

### PressContext (FR-005, FR-006, FR-014)
A single sealed, bilingual class the runtime constructs per press (carrying a captured
`IBotApiClient` and `CancellationToken`). It both **carries press context** and **exposes reaction
operations**.
```fsharp
[<Sealed>]
type PressContext =
    member ButtonLabel : ButtonLabel        // which button (FR-005)
    member Chat        : ChatId             // in which chat
    member User        : EndUser            // by which end user
    member MessageId   : MessageId          // on which originating message
    member CancellationToken : CancellationToken
    member ReplyTextAsync : text: string -> Task<MessageId>   // react in the chat (FR-006)
```
Reaction surface grows here in later slices (edit message, send another keyboard, …).

### EndUser
```fsharp
type EndUser = { Id: UserId; FirstName: string; Username: string | null }
```
`Username` uses F# nullable-reference annotation → C# sees `string?`, never `FSharpOption`.

### ButtonPress (incoming, post-parse)
```fsharp
type ButtonPress =
    { Token: CallbackToken; QueryId: CallbackQueryId
      Chat: ChatId; User: EndUser; MessageId: MessageId; ButtonLabel: ButtonLabel }
```
Produced by the transport layer from a Telegram.Bot `CallbackQuery` (via a pure mapping). Note: the
Bot API `CallbackQuery.message` may be absent for old messages — the mapper handles that optionality
explicitly (verify handling, Principle V).

### AgentEvent (transport-agnostic domain event)
```fsharp
type AgentEvent =
    | ButtonPressed of ButtonPress
    // future update kinds slot in here without touching transports (FR-013)
```

## State & flows

### Outbound: send a keyboard (agent → chat)
`KeyboardSpec` → `KeyboardPlan.assign` (pure) → `IHookStore.Register bindings` → `IBotApiClient.SendKeyboard`.
The hooks become resolvable the instant registration completes.

### Inbound: a press (chat → hook)  — **ack-first** (D7, SC-003)
`AgentEvent.ButtonPressed` → `Routing.decide resolve press`:
```fsharp
type RouteDecision = RunHook of Hook | AcknowledgeOnly   // unknown/stale/malformed → AcknowledgeOnly
module Routing =
    val decide : resolve:(CallbackToken -> Hook voption) -> press:ButtonPress -> RouteDecision
```
Runtime policy: **always `answerCallbackQuery` immediately** (clears the client spinner within the
3s budget, SC-003), THEN — if `RunHook` — enqueue the hook on the press's per-chat channel (FR-015).
`AcknowledgeOnly` runs no hook and raises no error (FR-010). Hook exceptions are caught in the
per-chat loop, reported to `IHookObserver`, and the loop continues (FR-009).

- **Routing properties (FsCheck)**: a token present in `resolve` ⇒ `RunHook` with exactly that hook
  (FR-004, FR-008); a token absent/malformed ⇒ `AcknowledgeOnly` (FR-010); `decide` is total over
  arbitrary input.

## Entity relationships

```
KeyboardSpec 1──*  ButtonSpec (Label, Hook)
KeyboardPlan.assign : KeyboardSpec ──▶ RegisteredKeyboard + [HookBinding]
HookBinding *──1 IHookStore                      (Token ▶ Hook)
ButtonPress 1──1 CallbackToken ──(resolve)──▶ Hook 0..1
PressContext 1──1 ButtonPress  (+ IBotApiClient, CancellationToken)
AgentEvent = ButtonPressed of ButtonPress
```

## Validation rules → requirement traceability

| Rule | Requirement |
|------|-------------|
| Keyboard has ≥1 row; each row ≥1 button; labels non-empty | FR-001, FR-012 |
| Each button carries exactly one hook | FR-002, FR-008 |
| Token encodes to ≤64 bytes; agent never sees raw payload | FR-011, D8 |
| Token parse is total; unknown → ack-only, no error | FR-010 |
| Present token → exactly its hook | FR-004, FR-008 |
| Press context carries button/chat/user/message | FR-005 |
| Hook can reply in chat | FR-006 |
| Store accessed via `IHookStore` port | FR-016 |
