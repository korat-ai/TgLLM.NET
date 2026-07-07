/// FsCheck property tests for the extended `ToolPlan.plan`/`validate`: `WebAppButton`/
/// `CopyTextButton` plus the review's property gaps folded into this slice
/// (duplicate input-token consumption, `validate` Ok ⇒ `plan` Ok, url/webapp/copytext
/// passthrough). `ToolPlanTests.fs` already covers `ToolButton`/`UrlButton`'s own shape/token/
/// binding invariants in isolation — this file is additive, not a re-run of those.
module TgLLM.Core.Tests.ToolPlanExtTests

open Expecto
open FsCheck
open TgLLM.Core

let private toSmallCount (PositiveInt n) = (n % 5) + 1

/// One button of EVERY kind (`ToolButton`, `UrlButton`, `WebAppButton`, `CopyTextButton`), chosen
/// deterministically by column index so every generated row exercises all four kinds interleaved —
/// always individually valid (https url, 1..256-char copy text), so `plan`/`validate` never fail on
/// these inputs; that lets the properties below focus purely on shape/token/passthrough
/// invariants (button-level validation itself is covered by the `testCase`s further down).
let private buildMixedRows (rowButtonCounts: int list) : PlanButton list list =
    rowButtonCounts
    |> List.mapi (fun rowIdx count ->
        [ for colIdx in 0 .. count - 1 ->
              let tag = $"r{rowIdx}b{colIdx}"

              match colIdx % 4 with
              | 0 -> ToolButton(tag, $"tool_{tag}", Some $"arg_{tag}")
              | 1 -> UrlButton(tag, $"https://example.test/url/{tag}")
              | 2 -> WebAppButton(tag, $"https://example.test/webapp/{tag}")
              | _ -> CopyTextButton(tag, $"copy_{tag}") ])

let private countToolButtons (rows: PlanButton list list) : int =
    rows |> List.sumBy (List.filter (function ToolButton _ -> true | UrlButton _ | WebAppButton _ | CopyTextButton _ -> false) >> List.length)

let private planOrFail (rowButtonCounts: int list) =
    let rows = buildMixedRows rowButtonCounts
    let toolButtonCount = countToolButtons rows
    let tokens = List.init (max toolButtonCount 1) (fun _ -> CallbackToken.generate ())

    match ToolPlan.plan tokens { Rows = rows } with
    | Error e -> failwithf "test construction produced an invalid plan: %A" e
    | Ok(registeredKeyboard, bindings) -> rows, registeredKeyboard, bindings, toolButtonCount

[<Tests>]
let toolPlanExtTests =
    testList "ToolPlan.plan/validate — WebApp/CopyText + review property gaps" [

        testProperty "every button kind passes through verbatim (label + url/text/tool-name/arg), WebApp/CopyText carry no binding" <| fun (data: NonEmptyArray<PositiveInt>) ->
            let (NonEmptyArray rowCounts) = data
            let rowButtonCounts = rowCounts |> Array.toList |> List.map toSmallCount
            let rows, RegisteredKeyboard registeredRows, bindings, _ = planOrFail rowButtonCounts

            List.forall2
                (fun (regRow: RegisteredButton list) (rawRow: PlanButton list) ->
                    List.forall2
                        (fun (rb: RegisteredButton) (pb: PlanButton) ->
                            match rb, pb with
                            | Callback(label, token), ToolButton(rawLabel, rawToolName, rawArg) ->
                                let matchingBinding = bindings |> List.tryFind (fun b -> b.Token = token)

                                ButtonLabel.value label = rawLabel
                                && (match matchingBinding with
                                    | Some binding -> ToolName.value binding.ToolName = rawToolName && binding.Arg = rawArg
                                    | None -> false)
                            | Url(label, url), UrlButton(rawLabel, rawUrl) -> ButtonLabel.value label = rawLabel && url = rawUrl
                            | WebApp(label, url), WebAppButton(rawLabel, rawUrl) -> ButtonLabel.value label = rawLabel && url = rawUrl
                            | CopyText(label, text), CopyTextButton(rawLabel, rawText) -> ButtonLabel.value label = rawLabel && text = rawText
                            | _ -> false)
                        regRow
                        rawRow)
                registeredRows
                rows

        testProperty "existing tool invariants still hold when interleaved with WebApp/CopyText: one binding per tool button, distinct tokens, others carry none" <| fun (data: NonEmptyArray<PositiveInt>) ->
            let (NonEmptyArray rowCounts) = data
            let rowButtonCounts = rowCounts |> Array.toList |> List.map toSmallCount
            let _, RegisteredKeyboard registeredRows, bindings, toolButtonCount = planOrFail rowButtonCounts

            let callbackTokens =
                registeredRows
                |> List.collect (
                    List.choose (function
                        | Callback(_, token) -> Some token
                        | Url _ | WebApp _ | CopyText _ -> None)
                )

            List.length bindings = toolButtonCount
            && List.length callbackTokens = toolButtonCount
            && List.distinct callbackTokens |> List.length = toolButtonCount

        testProperty "each input token — even a repeated value — is consumed at most once per button, one per tool button" <| fun (data: NonEmptyArray<PositiveInt>) ->
            let (NonEmptyArray rowCounts) = data
            let rowButtonCounts = rowCounts |> Array.toList |> List.map toSmallCount
            let rows = buildMixedRows rowButtonCounts
            let toolButtonCount = countToolButtons rows

            // Deliberately feed the SAME token value for every tool button: this exercises that
            // `plan`'s enumerator advances (`MoveNext`) exactly once per tool button regardless of
            // the values it yields — it neither dedupes nor skips nor "reuses" a position across
            // two different buttons. With every element identical, the only externally observable
            // proof left is that `plan` still succeeds and assigns exactly one binding per tool
            // button (never fewer, never throwing for "not enough distinct tokens").
            let repeated = CallbackToken.generate ()
            let tokens = List.replicate toolButtonCount repeated

            match ToolPlan.plan tokens { Rows = rows } with
            | Error e -> failwithf "test construction produced an invalid plan: %A" e
            | Ok(_, bindings) -> List.length bindings = toolButtonCount && bindings |> List.forall (fun b -> b.Token = repeated)

        testProperty "ToolPlan.validate Ok implies ToolPlan.plan Ok (given one token per tool button)" <| fun (data: NonEmptyArray<PositiveInt>) ->
            let (NonEmptyArray rowCounts) = data
            let rowButtonCounts = rowCounts |> Array.toList |> List.map toSmallCount
            let rows = buildMixedRows rowButtonCounts
            let keyboard: ToolKeyboard = { Rows = rows }

            match ToolPlan.validate keyboard with
            | Error _ -> true // validate itself rejected the shape; nothing for plan to contradict
            | Ok _ ->
                let toolButtonCount = countToolButtons rows
                let tokens = List.init (max toolButtonCount 1) (fun _ -> CallbackToken.generate ())

                match ToolPlan.plan tokens keyboard with
                | Ok _ -> true
                | Error e -> failwithf "validate said Ok but plan failed: %A" e

        testCase "a WebApp button with a non-https url is rejected as InvalidUrl" <| fun _ ->
            match ToolPlan.plan [] { Rows = [ [ WebAppButton("Open", "http://example.test") ] ] } with
            | Error(InvalidUrl "http://example.test") -> ()
            | other -> failwithf "expected Error (InvalidUrl \"http://example.test\"), got %A" other

        testCase "a WebApp button with an empty url is rejected as InvalidUrl" <| fun _ ->
            match ToolPlan.plan [] { Rows = [ [ WebAppButton("Open", "") ] ] } with
            | Error(InvalidUrl "") -> ()
            | other -> failwithf "expected Error (InvalidUrl \"\"), got %A" other

        testCase "a WebApp button with a valid https url plans with no binding" <| fun _ ->
            match ToolPlan.plan [] { Rows = [ [ WebAppButton("Open", "https://example.test") ] ] } with
            | Ok(RegisteredKeyboard [ [ WebApp(label, url) ] ], []) ->
                Expect.equal (ButtonLabel.value label) "Open" "label passes through"
                Expect.equal url "https://example.test" "url passes through"
            | other -> failwithf "expected an Ok plan with one WebApp button and no bindings, got %A" other

        testCase "a CopyText button with empty text is rejected as InvalidCopyText" <| fun _ ->
            match ToolPlan.plan [] { Rows = [ [ CopyTextButton("Copy", "") ] ] } with
            | Error(InvalidCopyText "") -> ()
            | other -> failwithf "expected Error (InvalidCopyText \"\"), got %A" other

        testCase "a CopyText button with text over 256 chars is rejected as InvalidCopyText" <| fun _ ->
            let tooLong = String.replicate 257 "x"

            match ToolPlan.plan [] { Rows = [ [ CopyTextButton("Copy", tooLong) ] ] } with
            | Error(InvalidCopyText t) -> Expect.equal t tooLong "the offending text is carried on the error"
            | other -> failwithf "expected Error (InvalidCopyText _), got %A" other

        testCase "a CopyText button with exactly 256 chars (the boundary) is accepted" <| fun _ ->
            let boundary = String.replicate 256 "x"

            match ToolPlan.plan [] { Rows = [ [ CopyTextButton("Copy", boundary) ] ] } with
            | Ok(RegisteredKeyboard [ [ CopyText(_, text) ] ], []) -> Expect.equal text boundary "256 chars is the max allowed, not rejected"
            | other -> failwithf "expected an Ok plan, got %A" other

        testCase "a CopyText button with exactly 1 char (the boundary) is accepted" <| fun _ ->
            match ToolPlan.plan [] { Rows = [ [ CopyTextButton("Copy", "x") ] ] } with
            | Ok(RegisteredKeyboard [ [ CopyText(_, text) ] ], []) -> Expect.equal text "x" "a single character is the minimum allowed"
            | other -> failwithf "expected an Ok plan, got %A" other
    ]
