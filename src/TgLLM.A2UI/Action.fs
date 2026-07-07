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

/// Builds the internal `a2ui-action` Tool Router tool: the single handler every `ServerEvent`
/// Button's tool button routes through (`Renderer.buttonToPlanButton`), regardless of which
/// surface or action name produced it.
module A2uiActionTool =

    let private descriptorJsonOptions =
        let options = JsonSerializerOptions()
        options.Converters.Add(JsonFSharpConverter())
        options


    /// Builds the internal `a2ui-action` tool: on a press, deserializes the tapped button's
    /// `ActionDescriptor` (the tool's structured argument), resolves its context against the
    /// pressed button's OWN surface (looked up in `registry` by `descriptor.SurfaceId`), and hands
    /// the resulting `A2uiAction` to `sink`. A descriptor that fails to deserialize, or whose
    /// surface is no longer tracked (deleted, or never rendered by this process — see
    /// `SurfaceRegistry`'s in-memory, per-process scope), is a silent no-op: there is no live data
    /// model left to resolve context against, and this handler has no observer seam of its own to
    /// surface it through (mirrors a slice-1 hook's own "nothing to do" outcome, not an error).
    let create (registry: SurfaceRegistry) (sink: ActionSink) (clock: Clock) : Tool =
        fun (ctx: PressContext) ->
            task {
                match ctx.Arg |> Option.ofObj with
                | None -> ()
                | Some json ->
                    let descriptor =
                        try
                            match JsonSerializer.Deserialize<ActionDescriptor>(json, descriptorJsonOptions) with
                            | null -> None
                            | value -> Some value
                        with :? JsonException ->
                            None

                    match descriptor with
                    | None -> ()
                    | Some descriptor ->
                        match registry.TryGetDataModel descriptor.SurfaceId with
                        | None -> ()
                        | Some dataModel ->
                            let resolvedContext =
                                descriptor.Context |> List.map (fun (key, pointer) -> key, JsonPointer.tryResolve dataModel pointer)

                            let action: A2uiAction =
                                { Name = descriptor.Name
                                  SurfaceId = descriptor.SurfaceId
                                  SourceComponentId = descriptor.SourceComponentId
                                  Timestamp = clock ()
                                  Context = resolvedContext
                                  WantResponse = descriptor.WantResponse
                                  ActionId = descriptor.ActionId }

                            do! sink action
            }
            :> Task
