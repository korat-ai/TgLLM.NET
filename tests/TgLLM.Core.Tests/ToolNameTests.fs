/// T003: failing tests for `ToolName.create` (non-empty after trim) and `ToolError` cases
/// (data-model.md "ToolName", "ToolError"). Written before `TgLLM.Core.Tools` exists — this file
/// MUST fail to compile until T004 implements `ToolName`/`ToolError` (Red).
module TgLLM.Core.Tests.ToolNameTests

open Expecto
open TgLLM.Core

[<Tests>]
let toolNameTests =
    testList "ToolName" [

        testCase "a non-empty name is accepted and round-trips through .value" <| fun _ ->
            match ToolName.create "approve" with
            | Ok name -> Expect.equal (ToolName.value name) "approve" "value round-trips"
            | Error e -> failtestf "expected Ok, got %A" e

        testCase "surrounding whitespace is trimmed" <| fun _ ->
            match ToolName.create "  approve  " with
            | Ok name -> Expect.equal (ToolName.value name) "approve" "whitespace is trimmed"
            | Error e -> failtestf "expected Ok, got %A" e

        testCase "an empty string is rejected as EmptyToolName" <| fun _ ->
            Expect.equal (ToolName.create "") (Error EmptyToolName) "empty name is rejected"

        testCase "a whitespace-only string is rejected as EmptyToolName" <| fun _ ->
            Expect.equal (ToolName.create "   ") (Error EmptyToolName) "whitespace-only name is rejected"

        testCase "null is rejected as EmptyToolName (public API boundary, Always-Rule 5)" <| fun _ ->
            Expect.equal (ToolName.create null) (Error EmptyToolName) "null name is rejected, not thrown"

        testCase "ToolError cases carry the data they document" <| fun _ ->
            Expect.equal (UnknownTool "approve") (UnknownTool "approve") "UnknownTool carries the tool name"
            Expect.equal (InvalidUrl "") (InvalidUrl "") "InvalidUrl carries the offending value"
            Expect.equal (InvalidKeyboard EmptyKeyboard) (InvalidKeyboard EmptyKeyboard) "InvalidKeyboard wraps a KeyboardError"
    ]
