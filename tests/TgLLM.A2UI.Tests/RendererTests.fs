/// FsCheck property tests for the PURE `Renderer.render`: a `telegram-basic` component tree +
/// data model -> `RenderedSurface` (MarkdownV2 body + `ToolKeyboard` + surfaced unsupported
/// nodes). No transport, no IO — this is a plain function over in-memory values.
module TgLLM.A2UI.Tests.RendererTests

open System.Text.Json.Nodes
open Expecto
open FsCheck
open FsCheck.FSharp
open TgLLM.Core
open TgLLM.A2UI

let private emptyDataModel: JsonNode =
    match JsonNode.Parse "{}" with
    | null -> failwith "unreachable"
    | node -> node

let private safeStringGen: Gen<string> =
    GenBuilder.gen {
        let! len = Gen.choose (1, 8)
        let! chars = Gen.arrayOfLength len (Gen.elements [ 'a' .. 'z' ])
        return System.String(chars)
    }

let private smallCountGen: Gen<int> = Gen.choose (1, 5)

let private component_ (id: string) (node: ComponentNode) : Component = { Id = id; Node = node }

let private serverEventButton (id: string) (label: string) (name: string) : Component =
    component_ id (Button(Literal label, ServerEvent(name, [], false, None)))

let private urlButton (id: string) (label: string) (url: string) : Component =
    component_ id (Button(Literal label, LocalOpenUrl url))

[<Tests>]
let rendererTests =
    testList "Renderer.render" [

        testProperty "a Row of N ServerEvent buttons produces exactly one keyboard row, in order" <| fun () ->
            Prop.forAll (Arb.fromGen smallCountGen) (fun n ->
                let buttons = [ for i in 1 .. n -> serverEventButton $"b{i}" $"Label{i}" $"action{i}" ]
                let root = component_ "root" (Row [ for i in 1 .. n -> $"b{i}" ])
                let components = root :: buttons

                match Renderer.render Catalog.telegramBasic "surface-1" emptyDataModel components with
                | Error e -> failwithf "expected Ok, got %A" e
                | Ok surface ->
                    match surface.Keyboard.Rows with
                    | [ row ] ->
                        List.length row = n
                        && List.forall2
                            (fun (btn: PlanButton) i ->
                                match btn with
                                | ToolButton(label, toolName, Some _) -> label = $"Label{i}" && toolName = "a2ui-action"
                                | _ -> false)
                            row
                            [ 1..n ]
                    | _ -> false)

        testProperty "a Column of N ServerEvent buttons produces N stacked rows, one button each, in order" <| fun () ->
            Prop.forAll (Arb.fromGen smallCountGen) (fun n ->
                let buttons = [ for i in 1 .. n -> serverEventButton $"b{i}" $"Label{i}" $"action{i}" ]
                let root = component_ "root" (Column [ for i in 1 .. n -> $"b{i}" ])
                let components = root :: buttons

                match Renderer.render Catalog.telegramBasic "surface-1" emptyDataModel components with
                | Error e -> failwithf "expected Ok, got %A" e
                | Ok surface ->
                    List.length surface.Keyboard.Rows = n
                    && List.forall2
                        (fun (row: PlanButton list) i ->
                            match row with
                            | [ ToolButton(label, "a2ui-action", Some _) ] -> label = $"Label{i}"
                            | _ -> false)
                        surface.Keyboard.Rows
                        [ 1..n ])

        testProperty "Column Text children concatenate into the MarkdownV2 body, each escaped, in order" <| fun () ->
            Prop.forAll (Gen.nonEmptyListOf safeStringGen |> Arb.fromGen) (fun texts ->
                let textIds = texts |> List.mapi (fun i _ -> $"t{i}")
                let textComponents = List.map2 (fun id text -> component_ id (Text(Literal text))) textIds texts
                let root = component_ "root" (Column textIds)
                let components = root :: textComponents

                match Renderer.render Catalog.telegramBasic "surface-1" emptyDataModel components with
                | Error e -> failwithf "expected Ok, got %A" e
                | Ok surface ->
                    let expected = texts |> List.map Markdown.escapeV2 |> String.concat "\n"
                    surface.Text = expected)

        testCase "a ServerEvent Button produces a tool button carrying its ActionDescriptor" <| fun _ ->
            let dataModel = JsonNode.Parse """{ "env": "prod" }""" |> Option.ofObj |> Option.get

            let button =
                component_
                    "ok"
                    (Button(Literal "Approve", ServerEvent("approve", [ "env", "/env" ], true, Some "a1")))

            let root = component_ "root" (Row [ "ok" ])

            match Renderer.render Catalog.telegramBasic "deploy-1" dataModel [ root; button ] with
            | Error e -> failwithf "expected Ok, got %A" e
            | Ok surface ->
                match surface.Keyboard.Rows with
                | [ [ ToolButton(label, toolName, Some argJson) ] ] ->
                    Expect.equal label "Approve" "button label"
                    Expect.equal toolName "a2ui-action" "the internal tool name every ServerEvent button routes through"

                    match JsonNode.Parse argJson with
                    | :? JsonObject as descriptor ->
                        let str (name: string) =
                            match descriptor[name] with
                            | :? JsonValue as v -> v.GetValue<string>()
                            | _ -> failwithf "expected a string property '%s'" name

                        Expect.equal (str "SurfaceId") "deploy-1" "surfaceId stamped from the render call"
                        Expect.equal (str "SourceComponentId") "ok" "the pressed component's own id"
                        Expect.equal (str "Name") "approve" "action event name"
                        Expect.equal (str "ActionId") "a1" "actionId carried through"

                        match descriptor["WantResponse"] with
                        | :? JsonValue as v -> Expect.isTrue (v.GetValue<bool>()) "wantResponse carried through"
                        | _ -> failwith "expected a bool WantResponse property"
                    | _ -> failwith "expected the arg to be a JSON object"
                | other -> failwithf "expected one row with one ToolButton, got %A" other

        testCase "a LocalOpenUrl Button produces a UrlButton with no server round-trip" <| fun _ ->
            let button = urlButton "link" "Docs" "https://example.com"
            let root = component_ "root" (Row [ "link" ])

            match Renderer.render Catalog.telegramBasic "surface-1" emptyDataModel [ root; button ] with
            | Error e -> failwithf "expected Ok, got %A" e
            | Ok surface -> Expect.equal surface.Keyboard.Rows [ [ UrlButton("Docs", "https://example.com") ] ] "a plain URL button, no tool binding"

        testCase "an Unsupported component is recorded, supported siblings still render" <| fun _ ->
            let text = component_ "t1" (Text(Literal "hello"))
            let slider = component_ "s1" (Unsupported "Slider")
            let button = serverEventButton "b1" "Go" "go"
            let root = component_ "root" (Column [ "t1"; "s1"; "b1" ])

            match Renderer.render Catalog.telegramBasic "surface-1" emptyDataModel [ root; text; slider; button ] with
            | Error e -> failwithf "expected Ok, got %A" e
            | Ok surface ->
                Expect.equal surface.Text "hello" "the supported Text sibling still renders"
                Expect.equal surface.Unsupported [ "Slider", "s1" ] "the unsupported node is surfaced, not dropped"

                match surface.Keyboard.Rows with
                | [ [ ToolButton("Go", "a2ui-action", Some _) ] ] -> ()
                | other -> failwithf "the supported Button sibling must still render, got %A" other

        testCase "no root component present renders nothing (empty surface, still Ok)" <| fun _ ->
            let orphan = component_ "not-root" (Text(Literal "unreachable"))

            match Renderer.render Catalog.telegramBasic "surface-1" emptyDataModel [ orphan ] with
            | Error e -> failwithf "expected Ok, got %A" e
            | Ok surface ->
                Expect.equal surface.Text "" "nothing renders without a root"
                Expect.equal surface.Keyboard.Rows [] "no keyboard without a root"
                Expect.equal surface.Unsupported [] "nothing surfaced either — the tree was simply never reached"

        testCase "an empty component list renders nothing (empty surface, still Ok)" <| fun _ ->
            match Renderer.render Catalog.telegramBasic "surface-1" emptyDataModel [] with
            | Error e -> failwithf "expected Ok, got %A" e
            | Ok surface ->
                Expect.equal surface.Text "" "nothing renders"
                Expect.equal surface.Keyboard.Rows [] "no keyboard"

        testCase "a component tree with a cycle back to an ancestor renders without crashing, surfacing the cycle" <| fun _ ->
            let root = component_ "root" (Column [ "a" ])
            let a = component_ "a" (Column [ "root" ])

            match Renderer.render Catalog.telegramBasic "surface-1" emptyDataModel [ root; a ] with
            | Error e -> failwithf "expected Ok, got %A" e
            | Ok surface ->
                Expect.isTrue
                    (surface.Unsupported |> List.exists (fun (componentType, id) -> componentType = "Cycle" && id = "root"))
                    "the revisited ancestor id is surfaced as a cycle rather than recursed into again"

        testCase "a self-referencing component renders without crashing, surfacing the cycle" <| fun _ ->
            let root = component_ "root" (Row [ "root" ])

            match Renderer.render Catalog.telegramBasic "surface-1" emptyDataModel [ root ] with
            | Error e -> failwithf "expected Ok, got %A" e
            | Ok surface ->
                Expect.isTrue
                    (surface.Unsupported |> List.exists (fun (componentType, id) -> componentType = "Cycle" && id = "root"))
                    "a node that lists itself as its own child is surfaced as a cycle rather than recursed into again"

        testCase "an extremely deep, acyclic component chain is bounded rather than overflowing the stack" <| fun _ ->
            let chainLength = 5000
            let ids = [| for i in 0 .. chainLength -> $"n{i}" |]
            let leaf = component_ ids[chainLength] (Text(Literal "bottom"))

            let links =
                [ for i in 0 .. chainLength - 1 -> component_ ids[i] (Column [ ids[i + 1] ]) ]

            let root = component_ "root" (Column [ ids[0] ])
            let components = root :: links @ [ leaf ]

            match Renderer.render Catalog.telegramBasic "surface-1" emptyDataModel components with
            | Error e -> failwithf "expected Ok, got %A" e
            | Ok surface ->
                Expect.isTrue
                    (surface.Unsupported |> List.exists (fun (componentType, _) -> componentType = "MaxDepthExceeded"))
                    "recursion stops at a bounded depth instead of following the chain all the way down"
    ]
