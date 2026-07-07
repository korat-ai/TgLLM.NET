namespace TgLLM.A2UI

open System
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.Json.Serialization
open System.Threading.Tasks
open TgLLM.Core

/// The outbound A2UI `action` message a Button tap builds, handed to the host-provided sink. Its
/// `Context` carries RESOLVED values (the `ActionDescriptor`'s own `Context` is still unresolved
/// (key, json-pointer) pairs) — resolution happens at tap time against the live surface's CURRENT
/// data model, not at render time, so it always reflects the latest state.
[<NoComparison; NoEquality>]
type A2uiAction =
    { Name: string
      SurfaceId: string
      SourceComponentId: string
      /// Stamped from an injected `Clock`, never ambient `DateTimeOffset.Now`/`UtcNow` — keeps
      /// emission deterministic under test.
      Timestamp: DateTimeOffset
      /// An unresolved context path resolves to `None` — mirrors `DynString.resolve`'s "never a
      /// throw" contract for the same reason, generalized from "always a string" (a `DynString`
      /// binding) to "whatever JSON value is actually there" (a context binding, which may point
      /// at a number/bool/object, not just text).
      Context: (string * JsonNode option) list
      WantResponse: bool
      ActionId: string option }

/// Host-provided: where an outbound `A2uiAction` goes (the host relays it to its agent over
/// whatever transport it uses). The library ships no agent-side A2UI transport.
type ActionSink = A2uiAction -> Task

/// The whole-leaf A2UI observability seam — every condition this leaf surfaces rather than
/// silently drops or renders wrong, delivered to ONE observer so a caller wiring one up (e.g. the
/// F# façade's `LoggingA2uiObserver`) only has one seam to bridge, mirroring
/// `TgLLM.Core.IHookObserver`'s single-interface, multiple-concern shape.
type IA2uiObserver =
    /// A message-level condition `A2uiRenderer.Ingest` surfaces: an unknown `catalogId`, a
    /// duplicate/unknown surface, a malformed message, or an `Unsupported` component collected by a
    /// render pass. Reported ALONGSIDE whatever `Result` the triggering call hands back to its own
    /// caller — so a condition that doesn't fail the call outright (e.g. an unsupported component
    /// next to supported siblings that still render) still reaches an observer watching overall bot
    /// health, not just the immediate caller.
    abstract OnA2uiError: error: A2uiError -> unit

    /// A `ServerEvent` Button's tap requested a response (`WantResponse = true`) but carried no
    /// `ActionId` — the agent would have no way to correlate a later `actionResponse` to this tap.
    /// The action is NOT delivered to the sink in this case (the same "surfaced, not silently
    /// wrong" policy `Renderer.render` applies to an `Unsupported` component): a caller wiring
    /// `wantResponse` correlation logic to the sink never sees a broken one. `Tool = PressContext ->
    /// Task` has no return channel back to a caller, and this handler runs deep inside the Tool
    /// Router's dispatch, far from anything that could inspect its outcome directly — this is the
    /// ONLY way such a tap-time condition is ever surfaced.
    abstract OnMalformedAction: descriptor: ActionDescriptor -> unit

/// Reports nothing — the default when a caller has no need to observe A2UI-level conditions.
type NoopA2uiObserver() =
    interface IA2uiObserver with
        member _.OnA2uiError(_error: A2uiError) = ()
        member _.OnMalformedAction(_descriptor: ActionDescriptor) = ()

/// The observability seam beyond a single tap's own malformed-action report
/// (`IA2uiObserver.OnMalformedAction` above): every message-level condition the F# façade's
/// `A2uiRenderer.Ingest` surfaces alongside whatever `Result` it hands back to ITS OWN caller. Kept
/// here (not inline in the façade) so it is unit-testable against a bare `IA2uiObserver` double,
/// with no `TgBot`/transport/IO involved.
module A2uiObservability =

    /// Reports `error` to `observer` and returns it as `Error` — the single call site
    /// `A2uiRenderer.Ingest` funnels every message-level failure (a bad parse, an unknown catalog, a
    /// duplicate/unknown surface, a surface with no renderable content) through, so the SAME
    /// condition always reaches both channels: the immediate caller (via the returned `Result`) and
    /// `observer` (a caller watching overall bot health, independent of any one call).
    let reportError (observer: IA2uiObserver) (error: A2uiError) : Result<unit, A2uiError> =
        observer.OnA2uiError error
        Error error

    /// Reports one `UnsupportedComponent` per entry `rendered.Unsupported` collected by a
    /// successful render pass. The ONLY place this fact reaches an observer: a render pass with
    /// SOME renderable content among its supported siblings still returns `Ok` overall (the
    /// siblings DO render), so without this call an unsupported component sitting next to them would
    /// leave no trace anywhere.
    let reportUnsupported (observer: IA2uiObserver) (rendered: RenderedSurface) : unit =
        rendered.Unsupported
        |> List.iter (fun (componentType, id) -> observer.OnA2uiError(UnsupportedComponent(componentType, id)))

/// Builds the internal `a2ui-action` Tool Router tool: the single handler every `ServerEvent`
/// Button's tool button routes through (`Renderer.buttonToPlanButton`), regardless of which
/// surface or action name produced it.
module A2uiActionTool =

    let private descriptorJsonOptions =
        let options = JsonSerializerOptions()
        options.Converters.Add(JsonFSharpConverter())
        options

    /// What one press resolves to, decided PURELY (no I/O) before the tool ever touches `sink`/
    /// `observer` — kept as its own step (rather than inlined into the `task {}` body below) so
    /// that body stays a single flat match, the shape the F# resumable-state-machine compiler
    /// handles statically; a deeper nest of `try`/`match`/`if` directly inside a `task {}` CE falls
    /// back to a slower dynamic implementation.
    [<NoComparison; NoEquality>]
    type private PressOutcome =
        /// A well-formed, resolved action, ready for `sink`.
        | Deliver of A2uiAction
        /// A well-formed descriptor requesting a response (`WantResponse = true`) with no
        /// `ActionId` to correlate that response to — reported to `observer` INSTEAD of `sink`.
        | ReportMalformed of ActionDescriptor
        /// An unparseable argument, or a descriptor whose surface is no longer tracked (deleted, or
        /// never rendered by this process — see `SurfaceRegistry`'s in-memory, per-process scope):
        /// there is no live data model to resolve context against, and nothing concrete (no name,
        /// no surface) worth reporting either.
        | Nothing

    let private decide (registry: SurfaceRegistry) (clock: Clock) (argJson: string | null) : PressOutcome =
        match Option.ofObj argJson with
        | None -> Nothing
        | Some json ->
            let descriptor =
                try
                    match JsonSerializer.Deserialize<ActionDescriptor>(json, descriptorJsonOptions) with
                    | null -> None
                    | value -> Some value
                with :? JsonException ->
                    None

            match descriptor with
            | None -> Nothing
            | Some descriptor when descriptor.WantResponse && Option.isNone descriptor.ActionId -> ReportMalformed descriptor
            | Some descriptor ->
                match registry.TryGetDataModel descriptor.SurfaceId with
                | None -> Nothing
                | Some dataModel ->
                    let resolvedContext =
                        descriptor.Context |> List.map (fun (key, pointer) -> key, JsonPointer.tryResolve dataModel pointer)

                    Deliver
                        { Name = descriptor.Name
                          SurfaceId = descriptor.SurfaceId
                          SourceComponentId = descriptor.SourceComponentId
                          Timestamp = clock ()
                          Context = resolvedContext
                          WantResponse = descriptor.WantResponse
                          ActionId = descriptor.ActionId }

    /// Builds the internal `a2ui-action` tool: on a press, deserializes the tapped button's
    /// `ActionDescriptor` (the tool's structured argument), resolves its context against the
    /// pressed button's OWN surface (looked up in `registry` by `descriptor.SurfaceId`), and hands
    /// the resulting `A2uiAction` to `sink` — or, for a malformed descriptor, to `observer` instead
    /// (see `PressOutcome`/`decide` above for the full decision).
    let create (registry: SurfaceRegistry) (sink: ActionSink) (clock: Clock) (observer: IA2uiObserver) : Tool =
        fun (ctx: PressContext) ->
            task {
                match decide registry clock ctx.Arg with
                | Nothing -> ()
                | ReportMalformed descriptor -> observer.OnMalformedAction descriptor
                | Deliver action -> do! sink action
            }
            :> Task
