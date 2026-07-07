namespace TgLLM.A2UI

open System
open System.Text.Json
open System.Text.Json.Nodes

/// A dynamic string: a literal, or an absolute RFC-6901 JSON-Pointer binding into a surface's
/// data model (resolved by `DynString.resolve`).
type DynString =
    | Literal of string
    | Bound of jsonPointer: string

/// Absolute RFC-6901 JSON-Pointer resolution against a `System.Text.Json.Nodes.JsonNode`.
module JsonPointer =

    /// RFC 6901 §4: decode a reference token by first turning `~1` into `/`, then `~0` into `~`
    /// (that order matters — reversing it would turn an escaped `~1` sequence that itself came
    /// from a literal `~01` into the wrong character).
    let private decodeToken (token: string) : string = token.Replace("~1", "/").Replace("~0", "~")

    let private step (current: JsonNode option) (token: string) : JsonNode option =
        match current with
        | None -> None
        | Some node ->
            match node with
            | :? JsonObject as o ->
                let mutable value: JsonNode | null = null
                if o.TryGetPropertyValue(token, &value) then Option.ofObj value else None
            | :? JsonArray as a ->
                match Int32.TryParse token with
                | true, idx when idx >= 0 && idx < a.Count -> Option.ofObj a[idx]
                | _ -> None
            | _ -> None

    /// Resolves `pointer` against `root`. The empty string resolves to the whole document; a
    /// pointer that doesn't start with `/`, or that walks into a missing property, an
    /// out-of-range array index, or a non-container node before running out of tokens, resolves
    /// to `None` — total, never a throw.
    let tryResolve (root: JsonNode) (pointer: string) : JsonNode option =
        if pointer = "" then
            Some root
        elif not (pointer.StartsWith "/") then
            None
        else
            pointer.Substring(1).Split('/') |> Array.map decodeToken |> Array.fold step (Some root)

    /// Sets (`value = Some _`) or removes (`value = None`) the property at `pointer` within
    /// `root`, mutating `root` in place and returning it. Scoped to the common "data model is a
    /// JSON object tree" shape: `pointer`'s PARENT segments must already resolve to a
    /// `JsonObject` — this never fabricates a missing intermediate container (RFC 6902 "add"
    /// semantics, or writing through an array index, are out of scope) and never replaces the
    /// document root itself (`pointer = ""` has no parent to mutate). Any of those cases — same as
    /// an unresolved path for `tryResolve` — leaves `root` unchanged: documented, never a throw.
    let trySet (root: JsonNode) (pointer: string) (value: JsonNode option) : JsonNode =
        if pointer = "" || not (pointer.StartsWith "/") then
            root
        else
            let lastSlash = pointer.LastIndexOf '/'
            let parentPointer = pointer.Substring(0, lastSlash)
            let leaf = decodeToken (pointer.Substring(lastSlash + 1))

            match tryResolve root parentPointer with
            | Some(:? JsonObject as parentObj) ->
                match value with
                | Some v ->
                    // A `JsonNode` can only ever belong to ONE parent in its own tree — `value`
                    // typically still belongs to the `A2uiMessage` envelope it was parsed from, so
                    // round-tripping it through its own text hands `parentObj` an unattached clone
                    // rather than throwing "the node already has a parent".
                    match JsonNode.Parse(v.ToJsonString()) with
                    | null -> ()
                    | cloned -> parentObj[leaf] <- cloned
                | None -> parentObj.Remove(leaf) |> ignore

                root
            | _ -> root

module DynString =

    /// Resolves a `DynString` against `dataModel`. `Literal` returns itself, ignoring the data
    /// model entirely. `Bound` resolves its JSON-Pointer to a JSON *string* value; a missing
    /// path, an out-of-range index, or a path that resolves to a non-string node (number, bool,
    /// null, object, or array) all resolve to the empty string — documented, never a throw. Only
    /// a genuine JSON string value is ever returned for a `Bound` path: telegram-basic's Text and
    /// Button-label fields are always rendered as plain text, never as an implicit
    /// number/bool-to-string conversion.
    let resolve (dataModel: JsonNode) (value: DynString) : string =
        match value with
        | Literal s -> s
        | Bound pointer ->
            match JsonPointer.tryResolve dataModel pointer with
            | Some(:? JsonValue as v) when v.GetValueKind() = JsonValueKind.String -> v.GetValue<string>()
            | _ -> ""
