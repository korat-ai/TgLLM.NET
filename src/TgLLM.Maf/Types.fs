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

/// One `MafTools.project` call's outcome: what registered, what was surfaced.
type ProjectionReport =
    { Registered: string list
      Problems: ProjectionProblem list }

/// The bridge's observability seam — every condition it surfaces rather than silently drops
/// reaches ONE observer, mirroring the A2UI leaf's `IA2uiObserver` shape. Noop default; the F#
/// start functions bridge it to the bot's own logger when one is wired.
type IMafObserver =
    /// A well-formed decision whose pending request is no longer known (already decided, the run
    /// ended, the process restarted, or the binding expired). Acked by the engine as always, but
    /// never resumed.
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
