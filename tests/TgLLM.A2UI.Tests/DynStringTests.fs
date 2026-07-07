/// Tests for `DynString` resolution against a `System.Text.Json.Nodes.JsonNode` data model:
/// `Literal` returns itself; `Bound` resolves an absolute RFC-6901 JSON-Pointer, or the empty
/// string on any unresolved path (documented, never a throw).
module TgLLM.A2UI.Tests.DynStringTests

open System.Text.Json.Nodes
open Expecto
open TgLLM.A2UI

/// `JsonValue.Create` is annotated as possibly returning `null`, but never does for a non-null
/// string literal — narrowed here once so the tests below stay in non-nullable `JsonNode` territory.
let private jsonString (s: string) : JsonNode =
    match JsonValue.Create s with
    | null -> failwith "JsonValue.Create of a non-null string literal is never null"
    | v -> v

let private dataModel: JsonNode =
    JsonNode.Parse(
        """
        { "title": "Deploy v2 to prod?",
          "env": "prod",
          "count": 3,
          "flag": true,
          "nested": { "user": { "name": "Ada" } },
          "tags": [ "a", "b", "c" ],
          "empty": "",
          "nullish": null }
        """
    )
    |> Option.ofObj
    |> Option.get

[<Tests>]
let dynStringTests =
    testList "DynString.resolve" [

        testCase "Literal resolves to itself, regardless of the data model" <| fun _ ->
            Expect.equal (DynString.resolve dataModel (Literal "hello")) "hello" "a literal ignores the data model entirely"

        testCase "Literal resolves to itself even when empty" <| fun _ ->
            Expect.equal (DynString.resolve dataModel (Literal "")) "" "an empty literal is still just itself"

        testCase "Bound with a top-level string path resolves to that value" <| fun _ ->
            Expect.equal (DynString.resolve dataModel (Bound "/title")) "Deploy v2 to prod?" "top-level string lookup"

        testCase "Bound with a nested path resolves through objects" <| fun _ ->
            Expect.equal (DynString.resolve dataModel (Bound "/nested/user/name")) "Ada" "nested object traversal"

        testCase "Bound with an array-index path resolves through arrays" <| fun _ ->
            Expect.equal (DynString.resolve dataModel (Bound "/tags/1")) "b" "array index traversal"

        testCase "Bound to an existing empty string resolves to empty string" <| fun _ ->
            Expect.equal (DynString.resolve dataModel (Bound "/empty")) "" "an explicit empty string value round-trips as empty"

        testCase "Bound to a missing top-level path resolves to empty string" <| fun _ ->
            Expect.equal (DynString.resolve dataModel (Bound "/doesNotExist")) "" "unresolved path documented as empty string"

        testCase "Bound to a missing nested path resolves to empty string" <| fun _ ->
            Expect.equal (DynString.resolve dataModel (Bound "/nested/user/missing")) "" "unresolved nested path is empty string"

        testCase "Bound to an out-of-range array index resolves to empty string" <| fun _ ->
            Expect.equal (DynString.resolve dataModel (Bound "/tags/99")) "" "out-of-range index is empty string"

        testCase "Bound to a non-string (number) value resolves to empty string" <| fun _ ->
            Expect.equal (DynString.resolve dataModel (Bound "/count")) "" "a number isn't a string, so it's unresolved"

        testCase "Bound to a non-string (bool) value resolves to empty string" <| fun _ ->
            Expect.equal (DynString.resolve dataModel (Bound "/flag")) "" "a bool isn't a string, so it's unresolved"

        testCase "Bound to an explicit JSON null resolves to empty string" <| fun _ ->
            Expect.equal (DynString.resolve dataModel (Bound "/nullish")) "" "a JSON null isn't a string, so it's unresolved"

        testCase "Bound to an object (not a leaf) resolves to empty string" <| fun _ ->
            Expect.equal (DynString.resolve dataModel (Bound "/nested")) "" "an object isn't a string, so it's unresolved"

        testCase "Bound to an array (not a leaf) resolves to empty string" <| fun _ ->
            Expect.equal (DynString.resolve dataModel (Bound "/tags")) "" "an array isn't a string, so it's unresolved"

        testCase "Bound to the empty pointer resolves the whole document (also not a string)" <| fun _ ->
            Expect.equal (DynString.resolve dataModel (Bound "")) "" "the root object itself isn't a string"

        testCase "Bound to a pointer not starting with '/' resolves to empty string" <| fun _ ->
            Expect.equal (DynString.resolve dataModel (Bound "title")) "" "a relative-looking pointer is not an absolute JSON-Pointer"

        testCase "Bound resolves an escaped '~1' as a literal '/' in a property name" <| fun _ ->
            let model = JsonNode.Parse("""{ "a/b": "slash-key" }""") |> Option.ofObj |> Option.get
            Expect.equal (DynString.resolve model (Bound "/a~1b")) "slash-key" "RFC 6901 '~1' decodes to '/'"

        testCase "Bound resolves an escaped '~0' as a literal '~' in a property name" <| fun _ ->
            let model = JsonNode.Parse("""{ "a~b": "tilde-key" }""") |> Option.ofObj |> Option.get
            Expect.equal (DynString.resolve model (Bound "/a~0b")) "tilde-key" "RFC 6901 '~0' decodes to '~'"

        testCase "JsonPointer.tryResolve on the empty pointer returns the whole document" <| fun _ ->
            match JsonPointer.tryResolve dataModel "" with
            | Some node -> Expect.isTrue (JsonNode.DeepEquals(node, dataModel)) "empty pointer resolves to the document root"
            | None -> failwith "expected Some"

        testCase "JsonPointer.tryResolve on a missing path returns None" <| fun _ ->
            Expect.equal (JsonPointer.tryResolve dataModel "/missing") None "a missing property is None, not an exception"

        testCase "JsonPointer.trySet on an existing top-level path replaces the value" <| fun _ ->
            let model = JsonNode.Parse("""{ "title": "old" }""") |> Option.ofObj |> Option.get
            JsonPointer.trySet model "/title" (Some(jsonString "new")) |> ignore
            Expect.equal (DynString.resolve model (Bound "/title")) "new" "the leaf value is replaced"

        testCase "JsonPointer.trySet on a missing top-level path adds it" <| fun _ ->
            let model = JsonNode.Parse("""{}""") |> Option.ofObj |> Option.get
            JsonPointer.trySet model "/title" (Some(jsonString "new")) |> ignore
            Expect.equal (DynString.resolve model (Bound "/title")) "new" "a new top-level key is set"

        testCase "JsonPointer.trySet on a nested path replaces the value" <| fun _ ->
            let model = JsonNode.Parse("""{ "nested": { "user": { "name": "Ada" } } }""") |> Option.ofObj |> Option.get
            JsonPointer.trySet model "/nested/user/name" (Some(jsonString "Grace")) |> ignore
            Expect.equal (DynString.resolve model (Bound "/nested/user/name")) "Grace" "a nested leaf value is replaced"

        testCase "JsonPointer.trySet with value=None removes the leaf" <| fun _ ->
            let model = JsonNode.Parse("""{ "title": "old" }""") |> Option.ofObj |> Option.get
            JsonPointer.trySet model "/title" None |> ignore
            Expect.equal (JsonPointer.tryResolve model "/title") None "the leaf no longer resolves"

        testCase "JsonPointer.trySet whose parent doesn't resolve to an object leaves the document unchanged" <| fun _ ->
            let model = JsonNode.Parse("""{ "title": "old" }""") |> Option.ofObj |> Option.get
            let unchanged = JsonPointer.trySet model "/missing/deeper" (Some(jsonString "x"))
            Expect.equal (DynString.resolve unchanged (Bound "/title")) "old" "the document is untouched when the parent doesn't resolve"

        testCase "JsonPointer.trySet on the root pointer leaves the document unchanged" <| fun _ ->
            let model = JsonNode.Parse("""{ "title": "old" }""") |> Option.ofObj |> Option.get
            let unchanged = JsonPointer.trySet model "" (Some(jsonString "x"))
            Expect.equal (DynString.resolve unchanged (Bound "/title")) "old" "there is no parent to mutate for the root pointer"
    ]
