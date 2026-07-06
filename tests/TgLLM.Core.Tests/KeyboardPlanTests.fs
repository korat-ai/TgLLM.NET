/// T012: failing FsCheck property tests for `KeyboardPlan.assign` (data-model.md "Pure planning
/// step"). Written before the real implementation lands in T013 — `assign` already exists as a
/// stub-free member of `KeyboardPlan` module by the time this compiles (T011 already implements
/// `Keyboard.create`; T013 is `KeyboardPlan.assign`), so this file is Red until T013 is done.
module TgLLM.Core.Tests.KeyboardPlanTests

open System.Threading.Tasks
open Expecto
open FsCheck
open TgLLM.Core

let private noopHook: Hook = fun _ -> Task.CompletedTask

/// Builds a valid `ButtonSpec list list` from a row-shape (button count per row). Labels are
/// deterministic and always valid — these properties are about *shape*/*token* invariants, not
/// label validation (already covered by KeyboardTests.fs).
let private buildRows (rowButtonCounts: int list) : ButtonSpec list list =
    rowButtonCounts
    |> List.mapi (fun rowIdx count -> [ for colIdx in 0 .. count - 1 -> { Label = $"r{rowIdx}b{colIdx}"; Hook = noopHook } ])

/// Bounds an FsCheck-generated `PositiveInt` to a small, test-fast row/button count (1..5).
let private toSmallCount (PositiveInt n) = (n % 5) + 1

[<Tests>]
let keyboardPlanTests =
    testList "KeyboardPlan.assign" [

        testProperty "row/column shape preserved, labels preserved, one binding per button" <| fun (data: NonEmptyArray<PositiveInt>) ->
            let (NonEmptyArray rowCounts) = data
            let rowButtonCounts = rowCounts |> Array.toList |> List.map toSmallCount
            let rows = buildRows rowButtonCounts

            match Keyboard.create rows with
            | Error e -> failwithf "test construction produced an invalid keyboard: %A" e
            | Ok spec ->
                let buttonCount = List.sum rowButtonCounts
                let tokens = List.init buttonCount (fun _ -> CallbackToken.generate ())
                let (RegisteredKeyboard registeredRows), bindings = KeyboardPlan.assign tokens spec

                let shapePreserved = List.map List.length registeredRows = rowButtonCounts

                // `KeyboardPlan.assign` only ever produces `Callback` buttons (T007: `Url` buttons
                // are a Tool Router concept — see `ToolPlan.plan` in Tools.fs); a `Url` case here
                // would itself be a test failure, not a case to special-case away.
                let labelsPreserved =
                    List.forall2
                        (fun (regRow: RegisteredButton list) (rawRow: ButtonSpec list) ->
                            List.forall2
                                (fun (rb: RegisteredButton) (bs: ButtonSpec) ->
                                    match rb with
                                    | Callback(label, _) -> ButtonLabel.value label = bs.Label
                                    | Url _ -> false)
                                regRow
                                rawRow)
                        registeredRows
                        rows

                let oneBindingPerButton = List.length bindings = buttonCount

                let outputTokens =
                    registeredRows
                    |> List.collect (
                        List.map (function
                            | Callback(_, token) -> token
                            | Url _ -> failwith "KeyboardPlan.assign never produces Url buttons")
                    )

                let bindingTokensMatchKeyboardTokens =
                    (bindings |> List.map (fun b -> b.Token) |> List.sort) = (outputTokens |> List.sort)

                shapePreserved && labelsPreserved && oneBindingPerButton && bindingTokensMatchKeyboardTokens

        testProperty "bindings.length equals the total button count" <| fun (data: NonEmptyArray<PositiveInt>) ->
            let (NonEmptyArray rowCounts) = data
            let rowButtonCounts = rowCounts |> Array.toList |> List.map toSmallCount
            let rows = buildRows rowButtonCounts

            match Keyboard.create rows with
            | Error e -> failwithf "test construction produced an invalid keyboard: %A" e
            | Ok spec ->
                let buttonCount = List.sum rowButtonCounts
                let tokens = List.init buttonCount (fun _ -> CallbackToken.generate ())
                let _, bindings = KeyboardPlan.assign tokens spec
                List.length bindings = buttonCount

        testProperty "distinct input tokens yield distinct button tokens" <| fun (data: NonEmptyArray<PositiveInt>) ->
            let (NonEmptyArray rowCounts) = data
            let rowButtonCounts = rowCounts |> Array.toList |> List.map toSmallCount
            let rows = buildRows rowButtonCounts

            match Keyboard.create rows with
            | Error e -> failwithf "test construction produced an invalid keyboard: %A" e
            | Ok spec ->
                let buttonCount = List.sum rowButtonCounts
                // `CallbackToken.generate` is Guid-backed, so these inputs are (for all practical
                // purposes) already pairwise distinct — exactly the premise this property checks.
                let tokens = List.init buttonCount (fun _ -> CallbackToken.generate ())
                let (RegisteredKeyboard registeredRows), _ = KeyboardPlan.assign tokens spec

                let outputTokens =
                    registeredRows
                    |> List.collect (
                        List.map (function
                            | Callback(_, token) -> token
                            | Url _ -> failwith "KeyboardPlan.assign never produces Url buttons")
                    )

                List.distinct tokens |> List.length = buttonCount
                && List.distinct outputTokens |> List.length = buttonCount
    ]
