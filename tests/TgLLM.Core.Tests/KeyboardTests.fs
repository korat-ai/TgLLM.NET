/// Tests for `Keyboard.create` validation, covering the KeyboardSpec (agent-facing)
/// → RegisteredKeyboard (wire-facing) transformation.
module TgLLM.Core.Tests.KeyboardTests

open System.Threading.Tasks
open Expecto
open TgLLM.Core

/// A no-op hook, sufficient for structural tests that don't invoke it.
let private noopHook: Hook = fun _ -> Task.CompletedTask

let private button label : ButtonSpec = { Label = label; Hook = noopHook }

[<Tests>]
let keyboardCreateTests =
    testList "Keyboard.create" [

        testCase "a single row with a single non-empty-label button is Ok" <| fun _ ->
            match Keyboard.create [ [ button "Yes" ] ] with
            | Ok _ -> ()
            | Error e -> failwithf "expected Ok, got Error %A" e

        testCase "multiple rows, multiple buttons per row, all Ok" <| fun _ ->
            match Keyboard.create [ [ button "Yes"; button "No" ]; [ button "Maybe" ] ] with
            | Ok _ -> ()
            | Error e -> failwithf "expected Ok, got Error %A" e

        testCase "zero rows is Error EmptyKeyboard" <| fun _ ->
            // `KeyboardSpec` carries `Hook` (a function value) and so cannot support structural
            // equality — pattern-match rather than `Expect.equal` on the whole `Result`.
            match Keyboard.create [] with
            | Error EmptyKeyboard -> ()
            | other -> failwithf "expected Error EmptyKeyboard, got %A" other

        testCase "a row with zero buttons is Error (EmptyRow _)" <| fun _ ->
            match Keyboard.create [ [ button "Yes" ]; [] ] with
            | Error(EmptyRow 1) -> ()
            | other -> failwithf "expected Error (EmptyRow 1), got %A" other

        testCase "an empty label is Error (EmptyLabel (row, col))" <| fun _ ->
            match Keyboard.create [ [ button "Yes"; button "" ] ] with
            | Error(EmptyLabel(0, 1)) -> ()
            | other -> failwithf "expected Error (EmptyLabel (0, 1)), got %A" other

        testCase "a whitespace-only label is Error (EmptyLabel (row, col))" <| fun _ ->
            match Keyboard.create [ [ button "   " ] ] with
            | Error(EmptyLabel(0, 0)) -> ()
            | other -> failwithf "expected Error (EmptyLabel (0, 0)), got %A" other

        testCase "a too-long label is Error (TextTooLong _) at the button's position" <| fun _ ->
            let tooLong = String.replicate (ButtonLabel.MaxLength + 1) "a"
            match Keyboard.create [ [ button "Yes"; button tooLong ] ] with
            | Error(TextTooLong(length, max)) ->
                Expect.equal length tooLong.Length "reported length matches input"
                Expect.equal max ButtonLabel.MaxLength "reported max matches ButtonLabel's bound"
            | other -> failwithf "expected Error (TextTooLong _), got %A" other
    ]
