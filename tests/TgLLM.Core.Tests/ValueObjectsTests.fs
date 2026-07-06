/// Expecto tests for the `ButtonLabel` / `MessageText` smart constructors.
module TgLLM.Core.Tests.ValueObjectsTests

open Expecto
open TgLLM.Core

[<Tests>]
let buttonLabelTests =
    testList "ButtonLabel.create" [
        testCase "non-empty label within bounds is Ok" <| fun _ ->
            match ButtonLabel.create "Yes" with
            | Ok label -> Expect.equal (ButtonLabel.value label) "Yes" "value round-trips"
            | Error e -> failwithf "expected Ok, got Error %A" e

        testCase "label is trimmed" <| fun _ ->
            match ButtonLabel.create "  Yes  " with
            | Ok label -> Expect.equal (ButtonLabel.value label) "Yes" "surrounding whitespace is trimmed"
            | Error e -> failwithf "expected Ok, got Error %A" e

        testCase "empty string is Error" <| fun _ ->
            match ButtonLabel.create "" with
            | Error (EmptyLabel _) -> ()
            | other -> failwithf "expected Error (EmptyLabel _), got %A" other

        testCase "whitespace-only string is Error" <| fun _ ->
            match ButtonLabel.create "   " with
            | Error (EmptyLabel _) -> ()
            | other -> failwithf "expected Error (EmptyLabel _), got %A" other

        testCase "label longer than the max length is Error" <| fun _ ->
            let tooLong = String.replicate (ButtonLabel.MaxLength + 1) "a"
            match ButtonLabel.create tooLong with
            | Error (TextTooLong(length, max)) ->
                Expect.equal length tooLong.Length "reported length matches input"
                Expect.equal max ButtonLabel.MaxLength "reported max matches the configured bound"
            | other -> failwithf "expected Error (TextTooLong _), got %A" other

        testCase "label exactly at the max length is Ok" <| fun _ ->
            let atMax = String.replicate ButtonLabel.MaxLength "a"
            match ButtonLabel.create atMax with
            | Ok label -> Expect.equal (ButtonLabel.value label) atMax "exact-bound label is accepted"
            | Error e -> failwithf "expected Ok, got Error %A" e
    ]

[<Tests>]
let messageTextTests =
    testList "MessageText.create" [
        testCase "non-empty text within bounds is Ok" <| fun _ ->
            match MessageText.create "Hello, world!" with
            | Ok text -> Expect.equal (MessageText.value text) "Hello, world!" "value round-trips"
            | Error e -> failwithf "expected Ok, got Error %A" e

        testCase "empty string is Error" <| fun _ ->
            match MessageText.create "" with
            | Error _ -> ()
            | Ok _ -> failwith "expected Error for empty message text"

        testCase "whitespace-only string is Error" <| fun _ ->
            match MessageText.create "   " with
            | Error _ -> ()
            | Ok _ -> failwith "expected Error for whitespace-only message text"

        testCase "text longer than the max length is Error" <| fun _ ->
            let tooLong = String.replicate (MessageText.MaxLength + 1) "a"
            match MessageText.create tooLong with
            | Error (TextTooLong(length, max)) ->
                Expect.equal length tooLong.Length "reported length matches input"
                Expect.equal max MessageText.MaxLength "reported max matches the configured bound (4096)"
            | other -> failwithf "expected Error (TextTooLong _), got %A" other

        testCase "text exactly at the max length is Ok" <| fun _ ->
            let atMax = String.replicate MessageText.MaxLength "a"
            match MessageText.create atMax with
            | Ok text -> Expect.equal (MessageText.value text) atMax "exact-bound text is accepted"
            | Error e -> failwithf "expected Ok, got Error %A" e
    ]
