/// F# façade builders for client-side buttons: `Plan.webApp`/`Plan.copyText` construct the
/// `WebAppButton`/`CopyTextButton` plan cases. The builders themselves do no validation — https
/// url / 1..256-char text is enforced where every other button's shape already is, `Plan.rows`/
/// `ToolPlan.plan` — so an invalid one surfaces through the SAME `Result<ToolKeyboard, ToolError>`
/// a bad tool name or label would, not a new error path. Mirrors `StructuredArgTests.fs`'s
/// unit-style (no fake server) façade coverage; the full wire-shape/routing acceptance lives in
/// `ClientButtonsTests.fs`.
module TgLLM.Integration.Tests.ClientButtonTests

open Expecto
open TgLLM.Core
open TgLLM.FSharp

[<Tests>]
let clientButtonTests =
    testList "Client-side button builders (Plan.webApp / Plan.copyText)" [

        testCase "Plan.webApp builds a WebAppButton plan case, label and url passed through verbatim" <| fun _ ->
            match Plan.webApp "Open" "https://example.test/app" with
            | WebAppButton(label, url) ->
                Expect.equal label "Open" "label passes through"
                Expect.equal url "https://example.test/app" "url passes through"
            | other -> failwithf "expected WebAppButton, got %A" other

        testCase "Plan.copyText builds a CopyTextButton plan case, label and text passed through verbatim" <| fun _ ->
            match Plan.copyText "Copy" "hello there" with
            | CopyTextButton(label, text) ->
                Expect.equal label "Copy" "label passes through"
                Expect.equal text "hello there" "text passes through"
            | other -> failwithf "expected CopyTextButton, got %A" other

        testCase "a plan with a valid Plan.webApp button builds Ok and carries no binding" <| fun _ ->
            match Plan.rows [ [ Plan.webApp "Open" "https://example.test/app" ] ] with
            | Error e -> failtestf "expected a valid plan, got %A" e
            | Ok keyboard ->
                match ToolPlan.plan [] keyboard with
                | Ok(RegisteredKeyboard [ [ WebApp(label, url) ] ], []) ->
                    Expect.equal (ButtonLabel.value label) "Open" "label reaches the registered keyboard"
                    Expect.equal url "https://example.test/app" "url reaches the registered keyboard"
                | other -> failwithf "expected a single client-side WebApp button and no bindings, got %A" other

        testCase "a plan with a valid Plan.copyText button builds Ok and carries no binding" <| fun _ ->
            match Plan.rows [ [ Plan.copyText "Copy" "hello there" ] ] with
            | Error e -> failtestf "expected a valid plan, got %A" e
            | Ok keyboard ->
                match ToolPlan.plan [] keyboard with
                | Ok(RegisteredKeyboard [ [ CopyText(label, text) ] ], []) ->
                    Expect.equal (ButtonLabel.value label) "Copy" "label reaches the registered keyboard"
                    Expect.equal text "hello there" "text reaches the registered keyboard"
                | other -> failwithf "expected a single client-side CopyText button and no bindings, got %A" other

        testCase "Plan.rows rejects a Plan.webApp button whose url is not https" <| fun _ ->
            match Plan.rows [ [ Plan.webApp "Open" "http://example.test/app" ] ] with
            | Error(InvalidUrl "http://example.test/app") -> ()
            | other -> failwithf "expected Error (InvalidUrl \"http://example.test/app\"), got %A" other

        testCase "Plan.rows rejects a Plan.copyText button whose text is empty" <| fun _ ->
            match Plan.rows [ [ Plan.copyText "Copy" "" ] ] with
            | Error(InvalidCopyText "") -> ()
            | other -> failwithf "expected Error (InvalidCopyText \"\"), got %A" other

        testCase "Plan.rows rejects a Plan.copyText button whose text is over 256 characters" <| fun _ ->
            let tooLong = String.replicate 257 "x"

            match Plan.rows [ [ Plan.copyText "Copy" tooLong ] ] with
            | Error(InvalidCopyText t) -> Expect.equal t tooLong "the offending text is carried on the error"
            | other -> failwithf "expected Error (InvalidCopyText _), got %A" other
    ]
