namespace TgLLM.Maf

open System
open TgLLM.Core

/// Leaf-level error vocabulary for conditions this bridge SURFACES (via `IMafObserver`) rather
/// than throws mid-loop.
type MafError =
    /// An empty body, an invalid button label, or any other `MessageText`/`ButtonLabel`
    /// constructor rejection not already covered by `ReplyTooLong`.
    | BodyInvalid of detail: string
    /// A rendered body over the Bot API's `sendMessage`/`editMessageText` length limit.
    | ReplyTooLong of length: int * max: int
    /// A Telegram delivery call (an edit, most commonly) failed for a reason OTHER than the
    /// classified `EditNotFound`/`EditNotModified` outcomes — e.g. a transient Bot API error —
    /// AFTER the operation it was reporting on (a resume, a chained-approval render) had already
    /// succeeded on the MAF side. Kept distinct from `OnResumeFailed`: the agent's own turn is
    /// fine; only getting the result onto the wire failed.
    | DeliveryFailed of detail: string

/// The two tool names this bridge itself owns and registers (`MafBridge.RegisterTools`) — a
/// single source of truth shared by `Projection.fs` (which must refuse a declared MAF function
/// under either name, `ProjectionProblem.ReservedName`, rather than let it silently override the
/// approval loop's own handler) and `Bridge.fs` (which registers the real handlers under these
/// exact names). Declared here, ahead of both, so neither has to duplicate the literal strings —
/// `Projection.fs` compiles before `Bridge.fs` (see the .fsproj's own compile-order comment), so
/// this is the earliest shared file both can reach.
module ReservedToolNames =

    [<Literal>]
    let Approve = "maf-approve"

    [<Literal>]
    let Reject = "maf-reject"

/// What the default renderer (and any host formatter) works from — extracted from the MAF
/// request's `FunctionCallContent` (`Name` + `Arguments`) by `ApprovalDetection`, so a formatter
/// never has to touch a MAF type directly.
type ApprovalPrompt =
    { Tool: string
      /// One `(name, JSON-rendered value)` pair per argument, order preserved.
      Arguments: (string * string) list
      Chat: ChatId }

/// The rendered approval message. Two fixed decisions — a formatter renames labels and rewrites
/// the body (e.g. redaction, localization); it cannot add or remove decisions.
type ApprovalRender =
    { Body: string
      ApproveLabel: string
      RejectLabel: string }

/// A host-supplied override of `ApprovalRendering.defaultRender`.
type ApprovalFormatter = ApprovalPrompt -> ApprovalRender

/// Why one declared MAF tool could not be projected into the registry (`MafTools.project`) — its
/// valid siblings still register (per-tool results, not all-or-nothing).
type ProjectionProblem =
    /// `ToolName.create` rejected the function's own `Name`.
    | InvalidToolName of name: string * detail: ToolError
    /// Two declared functions in ONE projected set share a name — a broken declaration, not an
    /// ordinary re-registration.
    | DuplicateName of name: string
    /// The declared function's name collides with a reserved decision-tool name
    /// (`ReservedToolNames.Approve`/`.Reject`) — registering it would silently override the
    /// bridge's own approval-loop handler (`IToolRegistry.Register` is add-or-replace), breaking
    /// every pending/future approval this bridge ever renders. Refused, not registered.
    | ReservedName of name: string

/// One `MafTools.project` call's outcome: what registered, what was surfaced.
type ProjectionReport =
    { Registered: string list
      Problems: ProjectionProblem list }

/// The bridge's observability seam — every condition it surfaces rather than silently drops
/// reaches ONE observer, mirroring the A2UI leaf's `IA2uiObserver` shape. Noop default; the F#
/// start functions bridge it to the bot's own logger when one is wired.
type IMafObserver =
    /// A well-formed decision whose pending request is no longer known — the run already
    /// resolved it, the run ended and abandoned its remaining siblings, or the process restarted
    /// (this bridge's `PendingApprovals` table is in-memory only). Acked by the engine as always,
    /// but never resumed. NOTE: a genuinely EXPIRED binding (`SendKeyboardPlan`'s own
    /// `expiresIn`) never reaches this far at all — `Expiry.isLive` refuses it inside the engine's
    /// own press resolution, which reports it via `IHookObserver.OnUnknownToken`, a Core-level
    /// seam this leaf's own observer has no visibility into.
    abstract OnStaleDecision: descriptor: ApprovalDescriptor -> unit
    /// A decision arg that did not parse back into a descriptor. Acked, never acted upon.
    abstract OnMalformedDecision: raw: string -> unit
    /// The agent threw while resuming after a decision; the approval message was edited to a
    /// failure note so no live buttons remain for a step that will not complete.
    abstract OnResumeFailed: descriptor: ApprovalDescriptor * error: exn -> unit
    /// A turn produced neither text nor a pending approval — nothing was sent; the host learns why
    /// the user saw no reply.
    abstract OnEmptyTurn: chat: ChatId -> unit
    /// A turn's reply (or a formatter's output) failed send-side validation, e.g. over the Bot
    /// API's message-length limit — surfaced instead of crashing the turn.
    abstract OnInvalidOutput: chat: ChatId * error: MafError -> unit
    /// One declared tool could not be projected into the registry (its siblings still were).
    abstract OnProjectionProblem: problem: ProjectionProblem -> unit
    /// A whole turn (a host-initiated `StartRun`, or an incoming text message) failed before it
    /// ever produced a reply or a pending approval — `agent.RunAsync`/`CreateSessionAsync`
    /// throwing (a network/backend error), the only two calls that can fail before there is a
    /// `response` to process. No message was sent for this turn; the chat's own lock is still
    /// released normally, so the NEXT turn on this chat can proceed. NOT for a failure delivering
    /// an already-produced `response` (an approval message, or the plain reply text) onto the
    /// wire — `Bridge.fs`'s `processInitialResponse` catches those itself and reports
    /// `OnInvalidOutput`/`DeliveryFailed` instead, since by then a reply or approval may already
    /// have reached the chat (a multi-approval turn's earlier messages, most commonly).
    abstract OnTurnFailed: chat: ChatId * error: exn -> unit

/// The durable-session observability seam — a SIBLING of `IMafObserver`, not an extension of it, so
/// a host's existing `IMafObserver` implementation is unaffected by this addition. Reports the two
/// failures the durable restore/persist path genuinely surfaces. Idle-session eviction is
/// deliberately NOT here: it is a silent, count-based background sweep (like the binding store's own
/// eviction), and its sweeper is a framework-agnostic Core type that cannot reach this leaf's own
/// observer.
type IMafSessionObserver =
    /// A chat's durable session record could not be turned back into a usable session — decode
    /// failure, or a format/framework-version this build no longer (or not yet) understands. The
    /// chat starts a fresh session; nothing resumes.
    abstract OnSessionRestoreFailed: chat: ChatId * failure: SessionFailure -> unit
    /// Writing a chat's durable session record to the store failed. The in-memory session is
    /// unaffected; only the DURABLE copy is stale, so a restart before the next successful persist
    /// would restore an older record (or none).
    abstract OnSessionPersistFailed: chat: ChatId * error: exn -> unit

/// Reports nothing — the default when a caller has no need to observe MAF-bridge conditions,
/// mirroring `NoopHookObserver`/`NoopA2uiObserver`.
type NoopMafObserver() =
    interface IMafObserver with
        member _.OnStaleDecision(_descriptor: ApprovalDescriptor) = ()
        member _.OnMalformedDecision(_raw: string) = ()
        member _.OnResumeFailed(_descriptor: ApprovalDescriptor, _error: exn) = ()
        member _.OnEmptyTurn(_chat: ChatId) = ()
        member _.OnInvalidOutput(_chat: ChatId, _error: MafError) = ()
        member _.OnProjectionProblem(_problem: ProjectionProblem) = ()
        member _.OnTurnFailed(_chat: ChatId, _error: exn) = ()

    interface IMafSessionObserver with
        member _.OnSessionRestoreFailed(_chat: ChatId, _failure: SessionFailure) = ()
        member _.OnSessionPersistFailed(_chat: ChatId, _error: exn) = ()

/// Options a host may set when wiring the bridge; every field is optional — the zero-config path
/// (`Maf.startPolling`/`startWebhook`) is complete without any of them.
[<NoComparison; NoEquality>]
type MafBridgeOptions =
    { /// Overrides `ApprovalRendering.defaultRender`.
      Formatter: ApprovalFormatter voption
      /// Overrides the default observer resolution (the bot's own logger, else noop).
      Observer: IMafObserver voption
      /// Explicit owner scope for host-initiated runs (`MafBridge.StartRun` without its own
      /// `?owner`) — see `RunOwner.resolve`.
      DefaultOwner: OwnerScope voption
      /// Optional expiry for a decision keyboard's bindings; `ValueNone` never expires.
      ApprovalExpiry: TimeSpan voption }

module MafBridgeOptions =

    /// The zero-config path: every field unset.
    let defaults: MafBridgeOptions =
        { Formatter = ValueNone
          Observer = ValueNone
          DefaultOwner = ValueNone
          ApprovalExpiry = ValueNone }
