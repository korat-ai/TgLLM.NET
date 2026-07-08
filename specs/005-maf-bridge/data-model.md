# Data Model: MAF Bridge — HITL Approval as Telegram Buttons

Domain model for slice 005. Everything MAF-shaped lives in the new `TgLLM.Maf` leaf; the ONE Core
addition (the incoming-message seam) is MAF-agnostic. Type sketches are indicative F# (real signatures
land via TDD). Reuses slice-1/2/3/4 types (`ChatId`, `UserId`, `MessageId`, `EndUser`, `OwnerScope`,
`ToolBinding`, `ToolKeyboard`/`ToolButton`, `IBindingStore`, `Clock`, edit-in-place, the per-chat
dispatcher).

## Core seam (TgLLM.Core — additive, MAF-agnostic)

```fsharp
/// An incoming user text message, post-parse. Produced by the shared transport mapping
/// (Mapping.toAgentEvent) from a Telegram `Update` carrying a user text `Message` — the message-side
/// sibling of `ButtonPress`. Carries bare identity + text only; no vendor/agent-framework types.
type IncomingMessage =
    { Chat: ChatId
      Sender: EndUser
      MessageId: MessageId
      Text: string }

/// Transport-agnostic domain event — `MessageReceived` is the NEW case; the existing cases are
/// untouched, so every pre-slice-005 consumer compiles and behaves identically.
type AgentEvent =
    | ButtonPressed of ButtonPress
    | AckOnly of queryId: CallbackQueryId
    | MessageReceived of IncomingMessage

/// The host-supplied reaction to an incoming text message. Runs on the message's chat's dispatcher
/// lane — serialized with that chat's button presses, in arrival order.
type MessageHandler = IncomingMessage -> CancellationToken -> Task

/// Message-side observability. A NEW interface (not new members on `IHookObserver` — that interface is
/// public and host-implementable, so growing it would break existing implementors). Noop default;
/// façades bridge it to `ILogger`.
type IMessageObserver =
    /// The host's `MessageHandler` threw for this message. Caught by the enqueued work thunk, same
    /// containment contract as `IHookObserver.OnHookFailed` for a press.
    abstract OnMessageFailed: message: IncomingMessage * error: exn -> unit
```

Validation rules: `Text` is non-empty by construction (the mapping only yields `MessageReceived` for a
`Message` with non-empty text; media/captions/edits/channel posts keep being skipped, not guessed at).
`UpdateProcessor` without `?onMessage` treats `MessageReceived` as a no-op.

## Approval descriptor (leaf — serialized into `ToolBinding.Arg`)

```fsharp
/// What a decision button carries as its Tool Router STRUCTURED ARGUMENT (stored server-side in the
/// binding store; never in Telegram's 64-byte callback_data). Deliberately a plain, flat DTO —
/// System.Text.Json-friendly without converters, readable in a stale/failure report even after the
/// process that created it is gone. The DECISION itself is NOT here: it is the name of the tapped tool
/// (maf-approve / maf-reject).
type ApprovalDescriptor =
    { Chat: int64          // ChatId untagged for serialization; retagged with UMX on parse
      RequestId: string    // MAF ToolApprovalRequestContent.RequestId — correlates tap -> pending request
      Tool: string }       // the MAF tool's name, for human-readable stale/failure reports

module ApprovalDescriptor =
    /// Round-trip pair (FsCheck property target: parse (serialize d) = Some d). `tryParse` is total:
    /// bad JSON / missing fields / empty strings -> None, never a throw inside a tool handler.
    val serialize: ApprovalDescriptor -> string
    val tryParse: string | null -> ApprovalDescriptor option
```

Validation: `RequestId` and `Tool` non-empty (MAF's own `ToolApprovalRequestContent` constructor rejects
an empty/whitespace `requestId`, per its API reference; `tryParse` re-checks anyway — the stored string
is untrusted input by the time it comes back).

## Pending approval (leaf — in-memory table)

```fsharp
/// One live, undecided approval. Holds the LIVE MAF request object — required to build the resume
/// content via request.CreateResponse — which is exactly why this table cannot survive a restart even
/// though the buttons' bindings (IBindingStore) can: a post-restart tap still routes and is acked, but
/// deterministically lands in the stale path below.
[<NoComparison; NoEquality>]
type PendingApproval =
    { Chat: ChatId
      Request: ToolApprovalRequestContent    // MAF type — leaf-only, never crosses into Core/façades
      Owner: OwnerScope                       // resolved once, at render time
      MessageId: MessageId                    // the approval message — edit-in-place target
      ApproveToken: CallbackToken             // both buttons' bindings, for post-decision cleanup
      RejectToken: CallbackToken }

/// Keyed by (chat, requestId). `TryConsume` is the at-most-once gate: it REMOVES atomically and returns
/// the entry to exactly one caller — a racing sibling tap (or a redelivered/duplicate decision that got
/// past the engine's own query-id dedup) gets None and is surfaced as stale, never resumed twice.
type PendingApprovals =
    member Add: PendingApproval -> unit
    member TryConsume: chat: ChatId * requestId: string -> PendingApproval voption
    member AbandonAllFor: chat: ChatId -> PendingApproval list   // run failed/ended -> stale the rest
```

### Approval lifecycle (state transitions)

```text
                   agent run pauses
                        │
                        ▼
   ┌─────────────── PENDING ────────────────┐    (entry in PendingApprovals; message with
   │                                         │     [Approve][Reject] on the wire; owner-scoped,
   │ tap by owner,                           │     single-use, optionally expiring bindings)
   │ TryConsume hit                          │
   ▼                                         ▼
 DECIDED (approve | reject)             STALE — TryConsume miss (already decided, run ended,
   │                                         process restarted) or binding expired/unknown:
   │ RunAsync(approval, session)             engine acks; IMafObserver.OnStaleDecision; no resume
   ├────────────► RESOLVED — outcome text edited in place; next request (if any) -> new PENDING
   └────────────► FAILED   — resume threw: IMafObserver.OnResumeFailed; message edited to a
                             failure note (no live buttons left); remaining entries abandoned
```

Out-of-scope taps never reach this table (refused by the engine's `OwnerScope.isAllowed` before any tool
runs); repeated taps on the tapped button never reach it either (`SingleUse` consumed the binding at
resolve time).

## Conversation (leaf — one per chat, in-memory)

```fsharp
/// A chat's agent conversation. Created lazily on the chat's first turn; only ever touched on that
/// chat's dispatcher lane, so no internal locking is needed (AgentSession documents no thread-safety;
/// the lane provides the serialization).
[<NoComparison; NoEquality>]
type Conversation =
    { Chat: ChatId
      Session: AgentSession                  // MAF type — leaf-only; in-memory this release
      /// The owner scope for approvals raised by the CURRENT run: `User sender` for a
      /// message-initiated turn, the host's explicit scope (or the private-chat/`Anyone` default)
      /// for a host-initiated run.
      RunOwner: OwnerScope }

type Conversations =
    member GetOrCreate: chat: ChatId * create: (unit -> ValueTask<AgentSession>) -> ValueTask<Conversation>
    member Drop: chat: ChatId -> unit        // a failed conversation restarts fresh on the next turn
```

## Owner-scope resolution (leaf — pure)

```fsharp
/// Bot API fact (core.telegram.org): a private chat's chat.id IS the peer's user id (positive;
/// group/channel ids are negative) — the "peer in a private chat" default is knowable from ChatId
/// alone. Pure and total: an FsCheck property target.
module RunOwner =
    /// Explicit host scope wins; a message-initiated turn defaults to its sender; a host-initiated
    /// run defaults to the peer in a private chat and to Anyone elsewhere.
    val resolve: explicitScope: OwnerScope option -> initiator: UserId option -> chat: ChatId -> OwnerScope
```

## Approval rendering (leaf — pure, formatter-overridable)

```fsharp
/// What the default renderer (and any host formatter) works from — extracted from the MAF request's
/// FunctionCallContent (Name + Arguments) so formatters never touch MAF types.
type ApprovalPrompt =
    { Tool: string
      Arguments: (string * string) list      // values JSON-rendered; order preserved
      Chat: ChatId }

/// The rendered approval message. Two fixed decisions — a formatter renames labels and rewrites the
/// body (e.g. redaction, localization); it cannot add or remove decisions.
type ApprovalRender =
    { Body: string
      ApproveLabel: string
      RejectLabel: string }

type ApprovalFormatter = ApprovalPrompt -> ApprovalRender

module ApprovalRendering =
    /// Zero-config default: tool name + one "key: value" line per argument, plain text (no parse mode,
    /// so arbitrary argument content can never break Telegram entity parsing), labels
    /// "Approve"/"Reject". Property target: total on arbitrary prompts; body non-empty.
    val defaultRender: ApprovalPrompt -> ApprovalRender

    /// Validation applied to EITHER renderer's output before send — body through MessageText.create,
    /// labels through ButtonLabel.create; an invalid render surfaces via the observer, never throws
    /// mid-loop (a formatter is host code, but its output reaches the wire like agent content).
    val validate: ApprovalRender -> Result<ApprovalRender, MafError>
```

## Tool projection (leaf)

```fsharp
/// Why one declared tool could not be projected. Its valid siblings still register (per-tool results,
/// not all-or-nothing).
type ProjectionProblem =
    | InvalidToolName of name: string * detail: ToolError   // ToolName.create rejected it
    | DuplicateName of name: string                          // twice within ONE projected set

/// One call's outcome: what registered, what was surfaced. Also mirrored to IMafObserver so a host
/// watching overall health sees projection problems without inspecting each report.
type ProjectionReport =
    { Registered: string list
      Problems: ProjectionProblem list }
```

Field mapping (per projected `AIFunction`): `Name` → `ToolName.create`; `Description` → 
`ToolMetadata.Description` (empty → `None`); `JsonSchema.GetRawText()` → `ToolMetadata.ArgSchema`
(schema text carried verbatim — the registry never parses it); handler = parse the button's structured
arg into `AIFunctionArguments`, `InvokeAsync`, reply the JSON-serialized result.

## Observation (leaf — the reporting seam)

```fsharp
/// Everything the bridge surfaces rather than silently drops — ONE observer interface, mirroring
/// IA2uiObserver's single-seam shape. Noop default; the F# wiring bridges it to the bot's own logger.
type IMafObserver =
    /// A well-formed decision whose pending request is no longer known (already decided, run ended,
    /// process restarted, binding expired). Acked but never resumed.
    abstract OnStaleDecision: descriptor: ApprovalDescriptor -> unit
    /// A decision arg that did not parse back into a descriptor. Acked, not acted upon.
    abstract OnMalformedDecision: raw: string -> unit
    /// The agent threw while resuming after a decision; the approval message was edited to a failure
    /// note so no live buttons remain for a step that will not complete.
    abstract OnResumeFailed: descriptor: ApprovalDescriptor * error: exn -> unit
    /// A turn produced neither text nor a pending approval — nothing was sent; the host learns why
    /// the user saw no reply.
    abstract OnEmptyTurn: chat: ChatId -> unit
    /// A turn's reply (or a formatter's output) failed send-side validation, e.g. over the Bot API
    /// message-length limit — surfaced instead of crashing the turn.
    abstract OnInvalidOutput: chat: ChatId * error: MafError -> unit
    /// One declared tool could not be projected into the registry (its siblings still were).
    abstract OnProjectionProblem: problem: ProjectionProblem -> unit

/// Leaf-level error vocabulary for surfaced (never thrown) conditions.
type MafError =
    | BodyInvalid of detail: string          // empty/over-limit body, invalid label, ...
    | ReplyTooLong of length: int * max: int
```

## Bridge (leaf — the top-level object)

```fsharp
/// Options a host may set when wiring the bridge; all optional — the zero-config path is complete.
[<NoComparison; NoEquality>]
type MafBridgeOptions =
    { Formatter: ApprovalFormatter voption   // overrides ApprovalRendering.defaultRender
      Observer: IMafObserver voption          // defaults to the bot's logger, else noop
      DefaultOwner: OwnerScope voption        // explicit scope for host-initiated runs
      ApprovalExpiry: TimeSpan voption }      // optional expiry for decision keyboards

/// Wires ONE agent to ONE bot: registers maf-approve/maf-reject into the bot's tool registry, owns the
/// per-chat Conversations and PendingApprovals, handles incoming text (the config-time OnMessage
/// handler), and exposes host-initiated runs. Constructed only through the leaf's start functions —
/// the message handler must exist before the bot starts consuming updates, so the leaf wraps bot
/// startup rather than attaching to a running bot.
[<Sealed>]
type MafBridge =
    member Bot: TgBot
    /// Start an agent turn in a chat on the host's initiative. Routed through the same per-chat lane
    /// as taps and incoming messages; `owner` overrides the default owner resolution for approvals
    /// this run raises.
    member StartRun: chat: ChatId * prompt: string * ?owner: OwnerScope -> Task
```

Invariants: at most one bridge per bot (guarded exactly like `A2ui.renderer`'s double-attach check — a
second attach would orphan the first bridge's pending approvals); `maf-approve`/`maf-reject` are
registered before the start function returns, so no approval keyboard can precede its tools; every
MAF-typed field (`AgentSession`, `ToolApprovalRequestContent`) lives in `[<NoComparison; NoEquality>]`
leaf types that never appear in Core or façade signatures.
