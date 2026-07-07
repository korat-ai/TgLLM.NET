/// Tests for the `telegram-basic` `Catalog` and `Component.toTelegramBasic`, the total mapping
/// from a parsed `RawComponent` (adjacency-list node, not yet catalog-narrowed) into the
/// `Component`/`ComponentNode` model this renderer understands.
module TgLLM.A2UI.Tests.CatalogTests

open System.Text.Json.Nodes
open Expecto
open TgLLM.A2UI

let private tryGetString (o: JsonObject) (name: string) : string option =
    let mutable node: JsonNode | null = null

    if o.TryGetPropertyValue(name, &node) then
        match node with
        | :? JsonValue as v -> Some(v.GetValue<string>())
        | _ -> None
    else
        None

let private rawOf (json: string) : RawComponent =
    match JsonNode.Parse json with
    | :? JsonObject as o ->
        match tryGetString o "id", tryGetString o "component" with
        | Some id, Some componentType -> { Id = id; ComponentType = componentType; Fields = o }
        | _ -> failwith "test fixture missing id/component"
    | _ -> failwith "test fixture must be a JSON object"

[<Tests>]
let catalogTests =
    testList "Catalog.telegramBasic" [

        testList
            "supported component types"
            [ for name in [ "Text"; "Button"; "Row"; "Column"; "Divider"; "Image" ] ->
                  testCase name <| fun _ -> Expect.isTrue (Catalog.telegramBasic.Supports name) $"'{name}' is in telegram-basic" ]

        testList
            "unsupported component types"
            [ for name in [ "Slider"; "TextField"; "Tabs"; "Modal"; "Checkbox"; "List" ] ->
                  testCase name <| fun _ -> Expect.isFalse (Catalog.telegramBasic.Supports name) $"'{name}' is outside telegram-basic" ]

        testCase "CatalogId is telegram-basic" <| fun _ -> Expect.equal Catalog.telegramBasic.CatalogId "telegram-basic" "advertised catalog id"

        testCase "Supports is case-sensitive (component names are exact matches)" <| fun _ ->
            Expect.isFalse (Catalog.telegramBasic.Supports "text") "the catalog's own component names are PascalCase"
    ]

[<Tests>]
let toTelegramBasicTests =
    testList "Component.toTelegramBasic" [

        testCase "a literal Text node maps to Text (Literal)" <| fun _ ->
            let raw = rawOf """{ "id":"t1", "component":"Text", "text":"hello" }"""
            let result = Component.toTelegramBasic raw
            Expect.equal result.Id "t1" "id preserved"
            Expect.equal result.Node (Text(Literal "hello")) "literal text value"

        testCase "a bound Text node maps to Text (Bound)" <| fun _ ->
            let raw = rawOf """{ "id":"t1", "component":"Text", "text":{"path":"/title"} }"""
            Expect.equal (Component.toTelegramBasic raw).Node (Text(Bound "/title")) "bound text value"

        testCase "a ServerEvent Button node maps with its context/wantResponse/actionId" <| fun _ ->
            let raw =
                rawOf
                    """{ "id":"ok", "component":"Button", "text":"Approve",
                         "action":{"event":{"name":"approve","context":{"env":{"path":"/env"}},"wantResponse":true,"actionId":"a1"}} }"""

            match (Component.toTelegramBasic raw).Node with
            | Button(Literal "Approve", ServerEvent(name, context, wantResponse, actionId)) ->
                Expect.equal name "approve" "action name"
                Expect.equal context [ "env", "/env" ] "context path bindings"
                Expect.isTrue wantResponse "wantResponse"
                Expect.equal actionId (Some "a1") "actionId"
            | other -> failwithf "expected a ServerEvent Button, got %A" other

        testCase "a ServerEvent Button with no context/actionId maps with empty context and no actionId" <| fun _ ->
            let raw =
                rawOf """{ "id":"no", "component":"Button", "text":"Reject", "action":{"event":{"name":"reject","context":{},"wantResponse":false}} }"""

            match (Component.toTelegramBasic raw).Node with
            | Button(Literal "Reject", ServerEvent(name, context, wantResponse, actionId)) ->
                Expect.equal name "reject" "action name"
                Expect.equal context [] "no context bindings"
                Expect.isFalse wantResponse "wantResponse"
                Expect.equal actionId None "no actionId"
            | other -> failwithf "expected a ServerEvent Button, got %A" other

        testCase "a LocalOpenUrl Button node maps its url" <| fun _ ->
            let raw =
                rawOf
                    """{ "id":"link", "component":"Button", "text":"Docs",
                         "action":{"functionCall":{"call":"openUrl","args":{"url":"https://example.com"}}} }"""

            Expect.equal (Component.toTelegramBasic raw).Node (Button(Literal "Docs", LocalOpenUrl "https://example.com")) "url button"

        testCase "a Row node maps its children in order" <| fun _ ->
            let raw = rawOf """{ "id":"actions", "component":"Row", "children":["ok","no"] }"""
            Expect.equal (Component.toTelegramBasic raw).Node (Row [ "ok"; "no" ]) "row children order preserved"

        testCase "a Column node maps its children in order" <| fun _ ->
            let raw = rawOf """{ "id":"root", "component":"Column", "children":["title","actions"] }"""
            Expect.equal (Component.toTelegramBasic raw).Node (Column [ "title"; "actions" ]) "column children order preserved"

        testCase "a Divider node maps to Divider" <| fun _ ->
            let raw = rawOf """{ "id":"d1", "component":"Divider" }"""
            Expect.equal (Component.toTelegramBasic raw).Node Divider "divider has no payload"

        testCase "an Image node maps its url" <| fun _ ->
            let raw = rawOf """{ "id":"img1", "component":"Image", "url":"https://example.com/pic.png" }"""
            Expect.equal (Component.toTelegramBasic raw).Node (Image(Literal "https://example.com/pic.png")) "image url"

        testCase "a component type outside telegram-basic maps to Unsupported" <| fun _ ->
            let raw = rawOf """{ "id":"s1", "component":"Slider", "min":0, "max":10 }"""
            Expect.equal (Component.toTelegramBasic raw).Node (Unsupported "Slider") "unrecognized type is surfaced, not dropped"

        testCase "a recognized type with a malformed required field degrades to Unsupported rather than crashing" <| fun _ ->
            // "Text" recognized, but its "text" field is a number, not a literal string or a
            // {"path":...} binding — never a throw, degrades to Unsupported instead.
            let raw = rawOf """{ "id":"t1", "component":"Text", "text":42 }"""
            Expect.equal (Component.toTelegramBasic raw).Node (Unsupported "Text") "malformed known-type field degrades to Unsupported"

        testCase "a Row with no children maps to an empty children list, not Unsupported" <| fun _ ->
            let raw = rawOf """{ "id":"r1", "component":"Row" }"""
            Expect.equal (Component.toTelegramBasic raw).Node (Row []) "missing children is an empty row, not malformed"
    ]
