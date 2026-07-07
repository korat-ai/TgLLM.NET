namespace TgLLM.A2UI

open System.Text.Json
open System.Text.Json.Nodes
open System.Text.Json.Serialization
open TgLLM.Core

/// Carried as a `ServerEvent` Button's Tool Router STRUCTURED ARGUMENT (the tool button's `arg`),
/// so a tap routes through the hardened engine and survives a restart via the durable binding
/// store. `Context` stays UNRESOLVED (key, JSON-Pointer) pairs — resolving them against the
/// surface's data model happens later, at tap time, not here at render time (captures current
/// values rather than a render-time snapshot).
type ActionDescriptor =
    { SurfaceId: string
      SourceComponentId: string
      Name: string
      Context: (string * string) list
      WantResponse: bool
      ActionId: string option }

/// The Telegram message content a `telegram-basic` component tree maps to. `Text` is the
/// MarkdownV2-escaped body; `Keyboard` reuses the Tool Router's own neutral plan type;
/// `Unsupported` lists every `(componentType, id)` this render pass encountered but couldn't
/// render, surfaced rather than dropped.
type RenderedSurface =
    { Text: string
      Keyboard: ToolKeyboard
      Unsupported: (string * string) list }

/// The pure `telegram-basic` renderer: a component tree + data model -> the Telegram message
/// content it implies. No transport, no IO.
module Renderer =

    let private descriptorJsonOptions =
        let options = JsonSerializerOptions()
        options.Converters.Add(JsonFSharpConverter())
        options

    /// One node's own contribution to the surface being built, before it's combined with its
    /// siblings'. `Rows` is already in final `ToolKeyboard.Rows` shape — a leaf `Button` node
    /// contributes exactly one singleton row `[[button]]`; `Row`/`Column` combine their children's
    /// contributions differently (see `renderNode`).
    [<NoComparison; NoEquality>]
    type private Contribution =
        { BodyLines: string list
          Rows: PlanButton list list
          Unsupported: (string * string) list }

    let private emptyContribution: Contribution =
        { BodyLines = []
          Rows = []
          Unsupported = [] }

    let private mergeContributions (a: Contribution) (b: Contribution) : Contribution =
        { BodyLines = a.BodyLines @ b.BodyLines
          Rows = a.Rows @ b.Rows
          Unsupported = a.Unsupported @ b.Unsupported }

    /// The catalog's own name for a node's shape — used only for the defense-in-depth
    /// `catalog.Supports` re-check below (a `Component` value is already catalog-narrowed by
    /// `Component.toTelegramBasic` before it ever reaches this renderer, so this re-check exists
    /// purely to catch a caller that hand-builds a `Component`/passes a DIFFERENT catalog than the
    /// one that narrowed it — the same "validate again at the point of use" discipline
    /// `ToolPlan.plan` applies to an already-validated `ToolKeyboard`).
    let private componentTypeName (node: ComponentNode) : string =
        match node with
        | Text _ -> "Text"
        | Button _ -> "Button"
        | Row _ -> "Row"
        | Column _ -> "Column"
        | Divider -> "Divider"
        | Image _ -> "Image"
        | Unsupported componentType -> componentType

    let private buttonToPlanButton (surfaceId: string) (componentId: string) (dataModel: JsonNode) (label: DynString) (action: ButtonAction) : PlanButton =
        let resolvedLabel = DynString.resolve dataModel label

        match action with
        | LocalOpenUrl url -> UrlButton(resolvedLabel, url)
        | ServerEvent(name, context, wantResponse, actionId) ->
            let descriptor: ActionDescriptor =
                { SurfaceId = surfaceId
                  SourceComponentId = componentId
                  Name = name
                  Context = context
                  WantResponse = wantResponse
                  ActionId = actionId }

            ToolButton(resolvedLabel, "a2ui-action", Some(JsonSerializer.Serialize(descriptor, descriptorJsonOptions)))

    /// A sane, generous bound on how many `Row`/`Column` levels deep `renderNode` ever recurses —
    /// defense-in-depth alongside `ancestors` (below): `ancestors` alone stops a CYCLE immediately
    /// (the moment an id repeats), but an extremely deep, perfectly ACYCLIC chain has no repeated id
    /// for `ancestors` to catch, so nothing else would ever stop it short of exhausting the stack. No
    /// legitimate `telegram-basic` surface (a chat message's worth of content) nests anywhere close
    /// to this deep.
    [<Literal>]
    let private MaxTreeDepth = 64

    /// Threads `ancestors` — the ids on the CURRENT root-to-here path, not every id seen anywhere in
    /// the tree — so two different branches may legitimately share a child id without tripping this,
    /// while a node that reappears among its OWN ancestors (a genuine cycle) is caught the moment it
    /// recurs, before ever recursing into it again. `depth` is the current path length, checked
    /// independently as defense-in-depth against a very deep but acyclic chain (see `MaxTreeDepth`).
    /// Either condition short-circuits to a surfaced `Unsupported` entry rather than recursing —
    /// exactly the same "surfaced, not silently dropped, siblings unaffected" policy already applied
    /// to an out-of-catalog component type.
    let rec private renderNode
        (surfaceId: string)
        (catalog: Catalog)
        (dataModel: JsonNode)
        (byId: Map<string, Component>)
        (ancestors: Set<string>)
        (depth: int)
        (componentId: string)
        : Contribution =
        match Map.tryFind componentId byId with
        | None -> emptyContribution
        | Some { Id = id; Node = node } ->
            if Set.contains componentId ancestors then
                { emptyContribution with Unsupported = [ "Cycle", id ] }
            elif depth > MaxTreeDepth then
                { emptyContribution with Unsupported = [ "MaxDepthExceeded", id ] }
            elif not (catalog.Supports(componentTypeName node)) then
                { emptyContribution with Unsupported = [ componentTypeName node, id ] }
            else
                let ancestors = Set.add componentId ancestors
                let depth = depth + 1

                match node with
                | Text value -> { emptyContribution with BodyLines = [ Markdown.escapeV2 (DynString.resolve dataModel value) ] }
                | Divider -> { emptyContribution with BodyLines = [ Markdown.escapeV2 "---" ] }
                | Image url -> { emptyContribution with BodyLines = [ Markdown.escapeV2 (DynString.resolve dataModel url) ] }
                | Unsupported componentType -> { emptyContribution with Unsupported = [ componentType, id ] }
                | Button(label, action) -> { emptyContribution with Rows = [ [ buttonToPlanButton surfaceId id dataModel label action ] ] }
                | Row children ->
                    // Direct Button children merge into ONE row; any other direct child (Text,
                    // Divider, Image, a nested Row/Column, or an Unsupported node) is rendered
                    // generically instead, so its own contribution (body text / its own rows /
                    // surfaced-unsupported entries) is never silently dropped.
                    let childComponents = children |> List.choose (fun cid -> Map.tryFind cid byId)

                    let directButtons =
                        childComponents
                        |> List.choose (fun c ->
                            match c.Node with
                            | Button(label, action) -> Some(buttonToPlanButton surfaceId c.Id dataModel label action)
                            | _ -> None)

                    let rowContribution =
                        if List.isEmpty directButtons then emptyContribution else { emptyContribution with Rows = [ directButtons ] }

                    let otherContributions =
                        childComponents
                        |> List.filter (fun c ->
                            match c.Node with
                            | Button _ -> false
                            | _ -> true)
                        |> List.map (fun c -> renderNode surfaceId catalog dataModel byId ancestors depth c.Id)

                    otherContributions |> List.fold mergeContributions rowContribution
                | Column children ->
                    // Every child is rendered generically and stacked in order: a Button child's
                    // own singleton row keeps it on its own row; a Text/Divider/Image child's body
                    // line concatenates; a nested Row/Column contributes its own rows/body inline.
                    children
                    |> List.map (renderNode surfaceId catalog dataModel byId ancestors depth)
                    |> List.fold mergeContributions emptyContribution

    /// Renders `components` (already narrowed to `telegram-basic` — see `Component.toTelegramBasic`)
    /// against `dataModel` into the Telegram message content implied by the subtree reachable from
    /// the node whose id is `"root"`. Every `DynString` is resolved; every reserved MarkdownV2
    /// character in body text is escaped; a `ServerEvent` Button becomes a tool button whose
    /// structured argument is its serialized `ActionDescriptor` (stamped with `surfaceId` and the
    /// pressed component's own id); a `LocalOpenUrl` Button becomes a plain URL button;
    /// `Unsupported` nodes are recorded, never rendered, without disturbing supported siblings. No
    /// `"root"` component (or an empty `components` list) renders nothing — an empty
    /// `RenderedSurface`, not an error: a surface simply not yet in a renderable state is normal
    /// mid-stream A2UI, not a failure.
    ///
    /// Always `Ok` in this pure mapping — `Result` is kept for symmetry with the rest of the A2UI
    /// pipeline (parsing, catalog matching), where a genuine `A2uiError` IS possible; nothing at
    /// this layer currently produces one.
    let render (catalog: Catalog) (surfaceId: string) (dataModel: JsonNode) (components: Component list) : Result<RenderedSurface, A2uiError> =
        let byId = components |> List.map (fun c -> c.Id, c) |> Map.ofList

        let contribution =
            match Map.tryFind "root" byId with
            | None -> emptyContribution
            | Some root -> renderNode surfaceId catalog dataModel byId Set.empty 0 root.Id

        Ok
            { Text = contribution.BodyLines |> String.concat "\n"
              Keyboard = { Rows = contribution.Rows }
              Unsupported = contribution.Unsupported }
