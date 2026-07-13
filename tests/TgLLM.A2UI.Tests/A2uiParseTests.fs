/// Tests for `A2uiMessage.parse`: the total, never-throwing parser from an A2UI agent->renderer
/// JSON envelope (`version` "v1.0") to the domain `A2uiMessage`/`A2uiError` model.
module TgLLM.A2UI.Tests.A2uiParseTests

open System.Text.Json.Nodes
open Expecto
open FsCheck
open FsCheck.FSharp
open TgLLM.A2UI

// ---------------------------------------------------------------------------------------------
// Generators for the round-trip property. Ids/paths/component-types are restricted to a small
// alphanumeric alphabet: the property is about the ENVELOPE shape round-tripping faithfully, not
// about covering every legal JSON-string character (control chars, unicode, escaping) — that is
// System.Text.Json's own concern, not this parser's.
// ---------------------------------------------------------------------------------------------

let private safeStringGen: Gen<string> =
    GenBuilder.gen {
        let! len = Gen.choose (1, 12)
        let! chars = Gen.arrayOfLength len (Gen.elements [ 'a' .. 'z' ])
        return System.String(chars)
    }

/// A small `RawComponent`: `Fields` carries its own `id`/`component` plus one extra string
/// property, matching what a real adjacency-list node looks like once parsed.
let private rawComponentGen: Gen<RawComponent> =
    GenBuilder.gen {
        let! id = safeStringGen
        let! componentType = safeStringGen
        let! extraKeySuffix = safeStringGen
        let! extraValue = safeStringGen
        let fields = JsonObject()
        fields["id"] <- JsonValue.Create id
        fields["component"] <- JsonValue.Create componentType
        fields["x" + extraKeySuffix] <- JsonValue.Create extraValue
        return { Id = id; ComponentType = componentType; Fields = fields }
    }

/// `JsonValue.Create`/`JsonNode.Parse` are annotated to return a nullable `JsonNode` (a JSON
/// `null` literal parses to an actual `null` reference in the tree) — these two helpers assert
/// the non-null case for inputs that are themselves never null, so the rest of this file can stay
/// in non-nullable `JsonNode` territory (Always-Rule 5).
let private jsonString (s: string) : JsonNode =
    match JsonValue.Create s with
    | null -> failwith "JsonValue.Create of a non-null string is never null"
    | v -> v

let private parseNonNull (json: string) : JsonNode =
    match JsonNode.Parse json with
    | null -> failwith "JsonNode.Parse of previously-serialized, well-formed JSON is never null"
    | node -> node

let private smallJsonObjectGen: Gen<JsonObject> =
    GenBuilder.gen {
        let! key = safeStringGen
        let! value = safeStringGen
        let obj = JsonObject()
        obj["d" + key] <- JsonValue.Create value
        return obj
    }

let private createSurfaceGen: Gen<A2uiMessage> =
    GenBuilder.gen {
        let! surfaceId = safeStringGen
        let! catalogId = safeStringGen
        let! componentCount = Gen.choose (0, 3)
        let! components = Gen.listOfLength componentCount rawComponentGen
        let! includeDataModel = Gen.elements [ true; false ]
        let! dataModel = smallJsonObjectGen
        return CreateSurface(surfaceId, catalogId, components, (if includeDataModel then Some(dataModel :> JsonNode) else None))
    }

let private updateComponentsGen: Gen<A2uiMessage> =
    GenBuilder.gen {
        let! surfaceId = safeStringGen
        let! componentCount = Gen.choose (0, 3)
        let! components = Gen.listOfLength componentCount rawComponentGen
        return UpdateComponents(surfaceId, components)
    }

let private updateDataModelGen: Gen<A2uiMessage> =
    GenBuilder.gen {
        let! surfaceId = safeStringGen
        let! pathSegment = safeStringGen
        let! includeValue = Gen.elements [ true; false ]
        let! value = safeStringGen
        return UpdateDataModel(surfaceId, "/" + pathSegment, (if includeValue then Some(jsonString value) else None))
    }

let private deleteSurfaceGen: Gen<A2uiMessage> = safeStringGen |> Gen.map DeleteSurface

let private a2uiMessageGen: Gen<A2uiMessage> =
    Gen.oneof [ createSurfaceGen; updateComponentsGen; updateDataModelGen; deleteSurfaceGen ]

// ---------------------------------------------------------------------------------------------
// A test-only wire encoder — the exact inverse of `A2uiMessage.parse` — plus a semantic equality
// helper (`A2uiMessage` carries `JsonNode`s, which only have reference equality by default, so
// the type itself is `[<NoComparison; NoEquality>]`; `JsonNode.DeepEquals` is the correct
// structural comparison here).
// ---------------------------------------------------------------------------------------------

/// Deep-clones via a serialize/re-parse round trip so the SAME `JsonNode` is never attached to
/// two different parents in the tree being built.
let private cloneNode (node: JsonNode) : JsonNode = parseNonNull (node.ToJsonString())

let private componentToJson (c: RawComponent) : JsonNode = cloneNode c.Fields

let private componentsArrayJson (components: RawComponent list) : JsonArray =
    let arr = JsonArray()
    for c in components do
        arr.Add(componentToJson c)
    arr

let private envelope (key: string) (body: JsonObject) : string =
    let root = JsonObject()
    root["version"] <- JsonValue.Create "v1.0"
    root[key] <- body
    root.ToJsonString()

let private toJsonString (message: A2uiMessage) : string =
    match message with
    | CreateSurface(surfaceId, catalogId, components, dataModel) ->
        let body = JsonObject()
        body["surfaceId"] <- JsonValue.Create surfaceId
        body["catalogId"] <- JsonValue.Create catalogId
        body["components"] <- componentsArrayJson components
        dataModel |> Option.iter (fun dm -> body["dataModel"] <- cloneNode dm)
        envelope "createSurface" body
    | UpdateComponents(surfaceId, components) ->
        let body = JsonObject()
        body["surfaceId"] <- JsonValue.Create surfaceId
        body["components"] <- componentsArrayJson components
        envelope "updateComponents" body
    | UpdateDataModel(surfaceId, path, value) ->
        let body = JsonObject()
        body["surfaceId"] <- JsonValue.Create surfaceId
        body["path"] <- JsonValue.Create path
        value |> Option.iter (fun v -> body["value"] <- cloneNode v)
        envelope "updateDataModel" body
    | DeleteSurface surfaceId ->
        let body = JsonObject()
        body["surfaceId"] <- JsonValue.Create surfaceId
        envelope "deleteSurface" body

let private jsonNodeOptionEqual (a: JsonNode option) (b: JsonNode option) : bool =
    match a, b with
    | None, None -> true
    | Some x, Some y -> JsonNode.DeepEquals(x, y)
    | _ -> false

let private rawComponentsEqual (a: RawComponent list) (b: RawComponent list) : bool =
    List.length a = List.length b
    && List.forall2
        (fun (x: RawComponent) (y: RawComponent) -> x.Id = y.Id && x.ComponentType = y.ComponentType && JsonNode.DeepEquals(x.Fields, y.Fields))
        a
        b

let private messagesEqual (a: A2uiMessage) (b: A2uiMessage) : bool =
    match a, b with
    | CreateSurface(s1, c1, comps1, dm1), CreateSurface(s2, c2, comps2, dm2) ->
        s1 = s2 && c1 = c2 && rawComponentsEqual comps1 comps2 && jsonNodeOptionEqual dm1 dm2
    | UpdateComponents(s1, comps1), UpdateComponents(s2, comps2) -> s1 = s2 && rawComponentsEqual comps1 comps2
    | UpdateDataModel(s1, p1, v1), UpdateDataModel(s2, p2, v2) -> s1 = s2 && p1 = p2 && jsonNodeOptionEqual v1 v2
    | DeleteSurface s1, DeleteSurface s2 -> s1 = s2
    | _ -> false

[<Tests>]
let a2uiParseTests =
    testList "A2uiMessage.parse" [

        testProperty "parse (print msg) recovers msg, for any well-formed message" (
            Prop.forAll (Arb.fromGen a2uiMessageGen) (fun message ->
                match A2uiMessage.parse (toJsonString message) with
                | Ok parsed -> messagesEqual message parsed
                | Error e -> failwithf "expected Ok, got %A" e)
        )

        testProperty "parse never throws, for arbitrary input" <| fun (s: string) ->
            try
                A2uiMessage.parse s |> ignore
                true
            with _ ->
                false

        testCase "createSurface with Text/Row/Column/Button parses with the expected shape" <| fun _ ->
            let json =
                """
                { "version":"v1.0", "createSurface": {
                    "surfaceId":"deploy-1", "catalogId":"telegram-basic",
                    "components":[
                      { "id":"root", "component":"Column", "children":["title","actions"] },
                      { "id":"title", "component":"Text", "text":{"path":"/title"} },
                      { "id":"actions", "component":"Row", "children":["ok","no"] },
                      { "id":"ok", "component":"Button", "text":"Approve",
                        "action":{"event":{"name":"approve","context":{"env":{"path":"/env"}},"wantResponse":true,"actionId":"a1"}} },
                      { "id":"no", "component":"Button", "text":"Reject",
                        "action":{"event":{"name":"reject","context":{},"wantResponse":false}} } ],
                    "dataModel":{ "title":"Deploy **v2** to prod?", "env":"prod" } } }
                """

            match A2uiMessage.parse json with
            | Ok(CreateSurface(surfaceId, catalogId, components, dataModel)) ->
                Expect.equal surfaceId "deploy-1" "surfaceId"
                Expect.equal catalogId "telegram-basic" "catalogId"
                Expect.equal (List.length components) 5 "5 adjacency-list nodes"
                Expect.isTrue dataModel.IsSome "dataModel present"
            | other -> failwithf "expected Ok CreateSurface, got %A" other

        testCase "updateComponents parses" <| fun _ ->
            let json =
                """{ "version":"v1.0", "updateComponents": {
                    "surfaceId":"deploy-1",
                    "components":[ { "id":"title", "component":"Text", "text":"Deploying..." },
                                   { "id":"root", "component":"Column", "children":["title"] } ] } }"""

            match A2uiMessage.parse json with
            | Ok(UpdateComponents(surfaceId, components)) ->
                Expect.equal surfaceId "deploy-1" "surfaceId"
                Expect.equal (List.length components) 2 "2 adjacency-list nodes"
            | other -> failwithf "expected Ok UpdateComponents, got %A" other

        testCase "updateDataModel parses" <| fun _ ->
            let json = """{ "version":"v1.0", "updateDataModel": { "surfaceId":"deploy-1", "path":"/env", "value":"staging" } }"""

            match A2uiMessage.parse json with
            | Ok(UpdateDataModel(surfaceId, path, value)) ->
                Expect.equal surfaceId "deploy-1" "surfaceId"
                Expect.equal path "/env" "path"
                Expect.isTrue value.IsSome "value present"
            | other -> failwithf "expected Ok UpdateDataModel, got %A" other

        testCase "updateDataModel with no value means delete" <| fun _ ->
            match A2uiMessage.parse """{ "version":"v1.0", "updateDataModel": { "surfaceId":"deploy-1", "path":"/env" } }""" with
            | Ok(UpdateDataModel(_, _, value)) -> Expect.isTrue value.IsNone "an absent value means delete"
            | other -> failwithf "expected Ok UpdateDataModel, got %A" other

        testCase "deleteSurface parses" <| fun _ ->
            match A2uiMessage.parse """{ "version":"v1.0", "deleteSurface": { "surfaceId":"deploy-1" } }""" with
            | Ok(DeleteSurface surfaceId) -> Expect.equal surfaceId "deploy-1" "surfaceId"
            | other -> failwithf "expected Ok DeleteSurface, got %A" other

        testCase "malformed JSON never throws, surfaces MalformedMessage" <| fun _ ->
            match A2uiMessage.parse "{not json" with
            | Error(MalformedMessage _) -> ()
            | other -> failwithf "expected Error MalformedMessage, got %A" other

        testCase "missing version is malformed" <| fun _ ->
            match A2uiMessage.parse """{ "deleteSurface": { "surfaceId":"x" } }""" with
            | Error(MalformedMessage _) -> ()
            | other -> failwithf "expected Error MalformedMessage, got %A" other

        testCase "wrong version is malformed" <| fun _ ->
            match A2uiMessage.parse """{ "version":"v2.0", "deleteSurface": { "surfaceId":"x" } }""" with
            | Error(MalformedMessage _) -> ()
            | other -> failwithf "expected Error MalformedMessage, got %A" other

        testCase "no recognized envelope key is malformed" <| fun _ ->
            match A2uiMessage.parse """{ "version":"v1.0", "somethingElse": {} }""" with
            | Error(MalformedMessage _) -> ()
            | other -> failwithf "expected Error MalformedMessage, got %A" other

        testCase "more than one envelope key is malformed" <| fun _ ->
            let json =
                """{ "version":"v1.0",
                  "deleteSurface": { "surfaceId":"x" },
                  "updateDataModel": { "surfaceId":"x", "path":"/a" } }"""

            match A2uiMessage.parse json with
            | Error(MalformedMessage _) -> ()
            | other -> failwithf "expected Error MalformedMessage, got %A" other

        testCase "createSurface missing surfaceId is malformed" <| fun _ ->
            match A2uiMessage.parse """{ "version":"v1.0", "createSurface": { "catalogId":"telegram-basic" } }""" with
            | Error(MalformedMessage _) -> ()
            | other -> failwithf "expected Error MalformedMessage, got %A" other

        testCase "createSurface missing catalogId is malformed" <| fun _ ->
            match A2uiMessage.parse """{ "version":"v1.0", "createSurface": { "surfaceId":"x" } }""" with
            | Error(MalformedMessage _) -> ()
            | other -> failwithf "expected Error MalformedMessage, got %A" other

        testCase "updateComponents missing surfaceId is malformed" <| fun _ ->
            match A2uiMessage.parse """{ "version":"v1.0", "updateComponents": { "components":[] } }""" with
            | Error(MalformedMessage _) -> ()
            | other -> failwithf "expected Error MalformedMessage, got %A" other

        testCase "updateDataModel missing path is malformed" <| fun _ ->
            match A2uiMessage.parse """{ "version":"v1.0", "updateDataModel": { "surfaceId":"x" } }""" with
            | Error(MalformedMessage _) -> ()
            | other -> failwithf "expected Error MalformedMessage, got %A" other

        testCase "deleteSurface missing surfaceId is malformed" <| fun _ ->
            match A2uiMessage.parse """{ "version":"v1.0", "deleteSurface": {} }""" with
            | Error(MalformedMessage _) -> ()
            | other -> failwithf "expected Error MalformedMessage, got %A" other

        testCase "a component missing id or component type is malformed" <| fun _ ->
            let json =
                """{ "version":"v1.0", "createSurface": {
                    "surfaceId":"x", "catalogId":"telegram-basic",
                    "components":[ { "component":"Text", "text":"hi" } ] } }"""

            match A2uiMessage.parse json with
            | Error(MalformedMessage _) -> ()
            | other -> failwithf "expected Error MalformedMessage, got %A" other

        testCase "a present components property with a non-array value is malformed" <| fun _ ->
            let json =
                """{ "version":"v1.0", "createSurface": {
                    "surfaceId":"x", "catalogId":"telegram-basic", "components":42 } }"""

            match A2uiMessage.parse json with
            | Error(MalformedMessage _) -> ()
            | other -> failwithf "expected Error MalformedMessage, got %A" other
    ]
