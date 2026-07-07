namespace TgLLM.A2UI

open System
open System.Text.Json
open System.Text.Json.Nodes

/// A surfaced A2UI parse/validation error — never a throw to the host (Core never sees these;
/// they live entirely at this leaf's boundary).
type A2uiError =
    /// Bad JSON, or a missing/wrong `version`, or a missing/malformed required field.
    | MalformedMessage of detail: string
    /// `createSurface.catalogId` doesn't match any catalog this renderer advertises.
    | UnknownCatalog of catalogId: string
    /// A component type outside the matched catalog.
    | UnsupportedComponent of componentType: string * id: string
    /// A second `createSurface` for a surface id that is already live.
    | DuplicateSurface of surfaceId: string
    /// An `updateComponents`/`updateDataModel`/`deleteSurface` for a surface id that isn't live.
    | UnknownSurface of surfaceId: string

/// Plain-English descriptions of `A2uiError` — used where a caller wants a readable message
/// rather than pattern-matching the DU itself (e.g. the C# façade's `A2uiIngestResult`, which
/// carries no F# idioms on its public surface).
module A2uiError =

    let describe (error: A2uiError) : string =
        match error with
        | MalformedMessage detail -> $"malformed A2UI message: {detail}"
        | UnknownCatalog catalogId -> $"unknown catalog: {catalogId}"
        | UnsupportedComponent(componentType, id) -> $"unsupported component '{componentType}' (id {id})"
        | DuplicateSurface surfaceId -> $"surface '{surfaceId}' already exists (createSurface is create-once)"
        | UnknownSurface surfaceId -> $"unknown surface: {surfaceId}"

/// One parsed A2UI adjacency-list node, not yet narrowed to a catalog — `Component.toTelegramBasic`
/// does that. `Fields` carries the node's own JSON object verbatim (its `id`/`component`
/// properties included), so a catalog mapper can read whichever properties its component type
/// needs without this module knowing about any of them.
[<NoComparison; NoEquality>]
type RawComponent =
    { Id: string
      ComponentType: string
      Fields: JsonObject }

/// An agent->renderer A2UI message envelope (`version` "v1.0"). Parsed from JSON at this leaf's
/// boundary — `TgLLM.Core` never sees these. `[<NoComparison; NoEquality>]`: every case carries a
/// `JsonNode`/`RawComponent.Fields`, and `JsonNode` has only reference equality — a caller that
/// needs value equality compares via `JsonNode.DeepEquals` on the relevant fields instead.
[<NoComparison; NoEquality>]
type A2uiMessage =
    | CreateSurface of surfaceId: string * catalogId: string * components: RawComponent list * dataModel: JsonNode option
    | UpdateComponents of surfaceId: string * components: RawComponent list
    /// `value = None` means "delete this path" — the wire message simply omits `value`.
    | UpdateDataModel of surfaceId: string * path: string * value: JsonNode option
    | DeleteSurface of surfaceId: string

/// Parses an A2UI agent->renderer JSON message into the domain model above.
module A2uiMessage =

    [<Literal>]
    let private SupportedVersion = "v1.0"

    /// `JsonObject.TryGetPropertyValue` is overloaded (a 2-arg form and a 3-arg form that also
    /// yields the property's index) — an explicit `byref` argument, rather than F#'s tupled-`out`
    /// call sugar, is what picks the 2-arg overload unambiguously.
    let private tryGetNode (obj: JsonObject) (name: string) : JsonNode option =
        let mutable node: JsonNode | null = null
        if obj.TryGetPropertyValue(name, &node) then Option.ofObj node else None

    let private tryGetString (obj: JsonObject) (name: string) : string option =
        match tryGetNode obj name with
        | Some(:? JsonValue as v) when v.GetValueKind() = JsonValueKind.String -> Some(v.GetValue<string>())
        | _ -> None

    let private tryGetObject (obj: JsonObject) (name: string) : JsonObject option =
        match tryGetNode obj name with
        | Some(:? JsonObject as o) -> Some o
        | _ -> None

    let private tryGetArray (obj: JsonObject) (name: string) : JsonArray option =
        match tryGetNode obj name with
        | Some(:? JsonArray as a) -> Some a
        | _ -> None

    let private parseComponent (node: JsonNode) : RawComponent option =
        match node with
        | :? JsonObject as o ->
            match tryGetString o "id", tryGetString o "component" with
            | Some id, Some componentType -> Some { Id = id; ComponentType = componentType; Fields = o }
            | _ -> None
        | _ -> None

    /// A missing `"components"` key is a well-formed empty list (both `createSurface` and
    /// `updateComponents` tolerate it); a present-but-malformed array (a non-object entry, or an
    /// entry missing `id`/`component`) fails the whole parse.
    let private parseComponents (obj: JsonObject) : RawComponent list option =
        match tryGetArray obj "components" with
        | None -> Some []
        | Some arr ->
            let parsed =
                arr
                |> Seq.map (function
                    | null -> None
                    | n -> parseComponent n)
                |> Seq.toList

            if parsed |> List.forall Option.isSome then
                Some(parsed |> List.choose id)
            else
                None

    let private parseCreateSurface (body: JsonObject) : Result<A2uiMessage, A2uiError> =
        match tryGetString body "surfaceId", tryGetString body "catalogId", parseComponents body with
        | Some surfaceId, Some catalogId, Some components ->
            Ok(CreateSurface(surfaceId, catalogId, components, tryGetNode body "dataModel"))
        | _ ->
            Error(MalformedMessage "createSurface requires a string surfaceId, a string catalogId, and a well-formed components array")

    let private parseUpdateComponents (body: JsonObject) : Result<A2uiMessage, A2uiError> =
        match tryGetString body "surfaceId", parseComponents body with
        | Some surfaceId, Some components -> Ok(UpdateComponents(surfaceId, components))
        | _ -> Error(MalformedMessage "updateComponents requires a string surfaceId and a well-formed components array")

    let private parseUpdateDataModel (body: JsonObject) : Result<A2uiMessage, A2uiError> =
        match tryGetString body "surfaceId", tryGetString body "path" with
        | Some surfaceId, Some path -> Ok(UpdateDataModel(surfaceId, path, tryGetNode body "value"))
        | _ -> Error(MalformedMessage "updateDataModel requires a string surfaceId and a string path")

    let private parseDeleteSurface (body: JsonObject) : Result<A2uiMessage, A2uiError> =
        match tryGetString body "surfaceId" with
        | Some surfaceId -> Ok(DeleteSurface surfaceId)
        | None -> Error(MalformedMessage "deleteSurface requires a string surfaceId")

    /// The envelope keys this parser recognizes, paired with their body parser, in the order a
    /// mixed/ambiguous envelope is reported.
    let private envelopeParsers =
        [ "createSurface", parseCreateSurface
          "updateComponents", parseUpdateComponents
          "updateDataModel", parseUpdateDataModel
          "deleteSurface", parseDeleteSurface ]

    let private tryParseNode (json: string) : Result<JsonNode option, string> =
        try
            Ok(JsonNode.Parse json |> Option.ofObj)
        with
        | :? JsonException as ex -> Error ex.Message
        | :? ArgumentNullException -> Error "input is null"

    /// The surface every `A2uiMessage` case addresses — every case carries exactly one.
    let surfaceId (msg: A2uiMessage) : string =
        match msg with
        | CreateSurface(surfaceId, _, _, _) -> surfaceId
        | UpdateComponents(surfaceId, _) -> surfaceId
        | UpdateDataModel(surfaceId, _, _) -> surfaceId
        | DeleteSurface surfaceId -> surfaceId

    /// Total: parses one A2UI agent->renderer JSON message. Bad JSON, a missing/wrong `version`,
    /// an envelope carrying zero or more than one recognized message key, or a missing/malformed
    /// required field within the matched body — all resolve to `Error (MalformedMessage _)`,
    /// never a throw.
    let parse (json: string) : Result<A2uiMessage, A2uiError> =
        match tryParseNode json with
        | Error detail -> Error(MalformedMessage $"invalid JSON: {detail}")
        | Ok None -> Error(MalformedMessage "input is not a JSON value")
        | Ok(Some node) ->
            match node with
            | :? JsonObject as root ->
                match tryGetString root "version" with
                | None -> Error(MalformedMessage "missing required field 'version'")
                | Some v when v <> SupportedVersion -> Error(MalformedMessage $"unsupported version '{v}'")
                | Some _ ->
                    let matches =
                        envelopeParsers
                        |> List.choose (fun (key, bodyParser) -> tryGetObject root key |> Option.map bodyParser)

                    match matches with
                    | [ result ] -> result
                    | [] ->
                        Error(
                            MalformedMessage
                                "message carries none of createSurface/updateComponents/updateDataModel/deleteSurface"
                        )
                    | _ ->
                        Error(
                            MalformedMessage
                                "message carries more than one of createSurface/updateComponents/updateDataModel/deleteSurface"
                        )
            | _ -> Error(MalformedMessage "root is not a JSON object")
