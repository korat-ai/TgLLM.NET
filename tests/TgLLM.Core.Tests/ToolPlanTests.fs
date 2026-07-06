/// T006: failing FsCheck property tests for `ToolPlan.plan` (data-model.md "Pure kernel"). Written
/// before the real implementation lands in T007 — this file MUST fail to compile until `Tools.fs`
/// exists (Red).
module TgLLM.Core.Tests.ToolPlanTests

open Expecto
open FsCheck
open TgLLM.Core

/// Bounds an FsCheck-generated `PositiveInt` to a small, test-fast row/button count (1..5).
let private toSmallCount (PositiveInt n) = (n % 5) + 1

/// Builds a valid `PlanButton list list` from a row-shape (button count per row) — every button a
/// `ToolButton` with a deterministic, always-valid label/tool-name/arg. These properties are about
/// *shape*/*token*/*binding* invariants (KeyboardPlanTests.fs already covers label validation), so
/// every input here is guaranteed to plan successfully.
let private buildRows (rowButtonCounts: int list) : PlanButton list list =
    rowButtonCounts
    |> List.mapi (fun rowIdx count ->
        [ for colIdx in 0 .. count - 1 -> ToolButton($"r{rowIdx}b{colIdx}", $"tool{rowIdx}_{colIdx}", Some $"arg{rowIdx}_{colIdx}") ])

let private planOrFail (rowButtonCounts: int list) =
    let rows = buildRows rowButtonCounts
    let buttonCount = List.sum rowButtonCounts
    let tokens = List.init buttonCount (fun _ -> CallbackToken.generate ())

    match ToolPlan.plan tokens { Rows = rows } with
    | Error e -> failwithf "test construction produced an invalid plan: %A" e
    | Ok(registeredKeyboard, bindings) -> rows, registeredKeyboard, bindings, buttonCount

[<Tests>]
let toolPlanTests =
    testList "ToolPlan.plan" [

        testProperty "row/label shape preserved, one Callback button + one binding per tool button" <| fun (data: NonEmptyArray<PositiveInt>) ->
            let (NonEmptyArray rowCounts) = data
            let rowButtonCounts = rowCounts |> Array.toList |> List.map toSmallCount
            let rows, RegisteredKeyboard registeredRows, bindings, buttonCount = planOrFail rowButtonCounts

            let shapePreserved = List.map List.length registeredRows = rowButtonCounts

            let labelsAndBindingsPreserved =
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
                                | _ -> false)
                            regRow
                            rawRow)
                    registeredRows
                    rows

            let oneBindingPerButton = List.length bindings = buttonCount

            shapePreserved && labelsAndBindingsPreserved && oneBindingPerButton

        testProperty "URL buttons carry no binding; token count equals the tool-button count" <| fun (data: NonEmptyArray<PositiveInt>) ->
            let (NonEmptyArray rowCounts) = data
            let rowButtonCounts = rowCounts |> Array.toList |> List.map toSmallCount

            // Every OTHER button in each row is a UrlButton instead of a ToolButton.
            let rows =
                rowButtonCounts
                |> List.mapi (fun rowIdx count ->
                    [ for colIdx in 0 .. count - 1 ->
                          if colIdx % 2 = 0 then
                              ToolButton($"r{rowIdx}b{colIdx}", $"tool{rowIdx}_{colIdx}", None)
                          else
                              UrlButton($"r{rowIdx}b{colIdx}", $"https://example.test/{rowIdx}/{colIdx}") ])

            let toolButtonCount =
                rows |> List.sumBy (List.filter (function ToolButton _ -> true | UrlButton _ -> false) >> List.length)

            let tokens = List.init (max toolButtonCount 1) (fun _ -> CallbackToken.generate ())

            match ToolPlan.plan tokens { Rows = rows } with
            | Error e -> failwithf "test construction produced an invalid plan: %A" e
            | Ok(RegisteredKeyboard registeredRows, bindings) ->
                let urlButtonsHaveNoBinding =
                    registeredRows
                    |> List.forall (
                        List.forall (function
                            | Url _ -> true
                            | Callback(_, token) -> bindings |> List.exists (fun b -> b.Token = token))
                    )

                List.length bindings = toolButtonCount && urlButtonsHaveNoBinding

        testProperty "distinct input tokens yield distinct button tokens" <| fun (data: NonEmptyArray<PositiveInt>) ->
            let (NonEmptyArray rowCounts) = data
            let rowButtonCounts = rowCounts |> Array.toList |> List.map toSmallCount
            let _, RegisteredKeyboard registeredRows, _, buttonCount = planOrFail rowButtonCounts

            let outputTokens =
                registeredRows
                |> List.collect (
                    List.map (function
                        | Callback(_, token) -> token
                        | Url _ -> failwith "this test's rows are all ToolButtons, so only Callback buttons are produced")
                )

            List.distinct outputTokens |> List.length = buttonCount

        testCase "an empty/whitespace URL is rejected with InvalidUrl (T030, research.md D3)" <| fun _ ->
            let rows = [ [ UrlButton("Docs", "   ") ] ]
            let tokens = List.empty<CallbackToken>

            match ToolPlan.plan tokens { Rows = rows } with
            | Error(InvalidUrl "   ") -> ()
            | other -> failwithf "expected Error (InvalidUrl \"   \"), got %A" other

        testCase "Plan.validate also rejects an empty URL (façade's build-time check, contracts/tool-router.md Plan.rows)" <| fun _ ->
            match ToolPlan.validate { Rows = [ [ UrlButton("Docs", "") ] ] } with
            | Error(InvalidUrl "") -> ()
            | other -> failwithf "expected Error (InvalidUrl \"\"), got %A" other

        // ToolPlan.hasToolButtons (review finding #10, 003-tool-router-extensions): `TgBot.SendKeyboardPlan`
        // uses this to fail fast when a plan has tool buttons but no Tool Router is wired in.
        testCase "hasToolButtons is false for a URL-only plan" <| fun _ ->
            let keyboard: ToolKeyboard = { Rows = [ [ UrlButton("Docs", "https://example.test") ] ] }
            Expect.isFalse (ToolPlan.hasToolButtons keyboard) "a plan made entirely of URL buttons never needs a Tool Router"

        testCase "hasToolButtons is true for a plan with at least one tool button" <| fun _ ->
            let keyboard: ToolKeyboard = { Rows = [ [ ToolButton("Approve", "approve", None) ] ] }
            Expect.isTrue (ToolPlan.hasToolButtons keyboard) "a plan with a tool button needs a Tool Router wired in"

        testCase "hasToolButtons is true for a mixed plan (any row, any column)" <| fun _ ->
            let keyboard: ToolKeyboard =
                { Rows =
                    [ [ UrlButton("Docs", "https://example.test") ]
                      [ UrlButton("Home", "https://example.test/home"); ToolButton("Approve", "approve", None) ] ] }

            Expect.isTrue (ToolPlan.hasToolButtons keyboard) "even one tool button anywhere in the plan is enough"

        testCase "hasToolButtons is false for a plan with no rows" <| fun _ ->
            let keyboard: ToolKeyboard = { Rows = [] }
            Expect.isFalse (ToolPlan.hasToolButtons keyboard) "an empty plan trivially has no tool buttons"
    ]
