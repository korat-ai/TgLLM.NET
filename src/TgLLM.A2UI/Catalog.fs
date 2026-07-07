namespace TgLLM.A2UI

open System.Text.Json
open System.Text.Json.Nodes

/// A Button's action: server-bound (routes through the Tool Router's `a2ui-action` tool) or a
/// client-side local function call (only `openUrl` is mapped by telegram-basic).
type ButtonAction =
    | ServerEvent of name: string * context: (string * string) list * wantResponse: bool * actionId: string option
    | LocalOpenUrl of url: string

/// One node of the A2UI adjacency-list tree, narrowed to what telegram-basic can render. Any
/// component type outside this catalog — or a recognized type with a missing/malformed required
/// field — parses as `Unsupported`, surfaced rather than dropped or rendered wrong.
type ComponentNode =
    | Text of value: DynString
    | Button of label: DynString * action: ButtonAction
    | Row of children: string list
    | Column of children: string list
    | Divider
    | Image of url: DynString
    | Unsupported of componentType: string

type Component = { Id: string; Node: ComponentNode }

/// The catalog a renderer advertises: which component type names it can render. `Supports` is a
/// function value, so this record can't support structural equality/comparison.
[<NoComparison; NoEquality>]
type Catalog = { CatalogId: string; Supports: string -> bool }

module Catalog =

    let private supportedTypes = set [ "Text"; "Button"; "Row"; "Column"; "Divider"; "Image" ]

    /// The Telegram-representable subset of A2UI's component catalog.
    let telegramBasic: Catalog =
        { CatalogId = "telegram-basic"
          Supports = fun name -> Set.contains name supportedTypes }

/// Maps a parsed `RawComponent` into the `telegram-basic` `Component`/`ComponentNode` model.
module Component =

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

    let private tryGetBool (obj: JsonObject) (name: string) : bool option =
        match tryGetNode obj name with
        | Some(:? JsonValue as v) when v.GetValueKind() = JsonValueKind.True || v.GetValueKind() = JsonValueKind.False ->
            Some(v.GetValue<bool>())
        | _ -> None

    /// A `DynamicString`: a literal JSON string, or a `{"path": "/..."}` object — anything else
    /// (missing, wrong shape) isn't a `DynString` at all.
    let private tryParseDynString (node: JsonNode option) : DynString option =
        match node with
        | Some(:? JsonValue as v) when v.GetValueKind() = JsonValueKind.String -> Some(Literal(v.GetValue<string>()))
        | Some(:? JsonObject as o) -> tryGetString o "path" |> Option.map Bound
        | _ -> None

    /// A missing `"children"` key is a well-formed empty list (`Row`/`Column` tolerate no
    /// children); a present-but-malformed one (not an array, or containing a non-string entry)
    /// fails the whole node.
    let private tryParseChildren (obj: JsonObject) : string list option =
        match tryGetNode obj "children" with
        | None -> Some []
        | Some(:? JsonArray as arr) ->
            let items =
                arr
                |> Seq.map (function
                    | :? JsonValue as v when v.GetValueKind() = JsonValueKind.String -> Some(v.GetValue<string>())
                    | _ -> None)
                |> Seq.toList

            if items |> List.forall Option.isSome then Some(items |> List.choose id) else None
        | Some _ -> None

    /// A missing `"context"` key is a well-formed empty binding list. Every present entry MUST be
    /// a `{"path": "/..."}` binding (the `ActionDescriptor`/`ButtonAction.ServerEvent` shape has
    /// no slot for a literal context value) — one malformed entry fails the whole context.
    let private tryParseContext (obj: JsonObject) : (string * string) list option =
        match tryGetObject obj "context" with
        | None -> Some []
        | Some contextObj ->
            let pairs =
                contextObj
                |> Seq.map (fun kv ->
                    match tryParseDynString (Option.ofObj kv.Value) with
                    | Some(Bound pointer) -> Some(kv.Key, pointer)
                    | Some(Literal _)
                    | None -> None)
                |> Seq.toList

            if pairs |> List.forall Option.isSome then Some(pairs |> List.choose id) else None

    let private tryParseButtonAction (obj: JsonObject) : ButtonAction option =
        match tryGetObject obj "action" with
        | None -> None
        | Some actionObj ->
            match tryGetObject actionObj "event" with
            | Some eventObj ->
                match tryGetString eventObj "name", tryParseContext eventObj with
                | Some name, Some context ->
                    let wantResponse = tryGetBool eventObj "wantResponse" |> Option.defaultValue false
                    Some(ServerEvent(name, context, wantResponse, tryGetString eventObj "actionId"))
                | _ -> None
            | None ->
                match tryGetObject actionObj "functionCall" with
                | Some functionCall when tryGetString functionCall "call" = Some "openUrl" ->
                    tryGetObject functionCall "args" |> Option.bind (fun args -> tryGetString args "url") |> Option.map LocalOpenUrl
                | _ -> None

    let private tryParseNode (raw: RawComponent) : ComponentNode option =
        match raw.ComponentType with
        | "Text" -> tryGetNode raw.Fields "text" |> tryParseDynString |> Option.map Text
        | "Button" ->
            match tryGetNode raw.Fields "text" |> tryParseDynString, tryParseButtonAction raw.Fields with
            | Some label, Some action -> Some(Button(label, action))
            | _ -> None
        | "Row" -> tryParseChildren raw.Fields |> Option.map Row
        | "Column" -> tryParseChildren raw.Fields |> Option.map Column
        | "Divider" -> Some Divider
        | "Image" -> tryGetNode raw.Fields "url" |> tryParseDynString |> Option.map Image
        | _ -> None

    /// Total: maps one parsed adjacency-list node into the `telegram-basic` model. A recognized
    /// type whose required field(s) are missing or malformed degrades to `Unsupported` — the same
    /// surfaced-not-dropped policy as a genuinely unrecognized component type — rather than
    /// throwing.
    let toTelegramBasic (raw: RawComponent) : Component =
        { Id = raw.Id
          Node = tryParseNode raw |> Option.defaultValue (Unsupported raw.ComponentType) }
