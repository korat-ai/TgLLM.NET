/// Tests for `ToolBinding`'s additive evolution: `Owner`/`ExpiresAt`/
/// `SingleUse` are new fields, defaulting to `Anyone`/`None`/`false` — the exact slice-2 shape, so
/// every already-green slice-2 binding is unaffected. `ToolBinding.create` is the
/// one-stop constructor for the common (token, toolName, arg) case that fills in those defaults —
/// used at every construction site the record's evolution touches (`ToolPlan.plan`, the stores,
/// the other test files) instead of repeating all six fields as bare record literals everywhere.
module TgLLM.Core.Tests.ToolBindingTests

open Expecto
open TgLLM.Core

let private toolName (s: string) : ToolName =
    match ToolName.create s with
    | Ok n -> n
    | Error e -> failwithf "test setup: unreachable %A" e

[<Tests>]
let toolBindingTests =
    testList "ToolBinding" [

        testCase "ToolBinding.create defaults Owner=Anyone, ExpiresAt=None, SingleUse=false, DeniedNotice=None (backward compat)" <| fun _ ->
            let token = CallbackToken.generate ()
            let name = toolName "approve"

            let created = ToolBinding.create token name (Some "42")

            let expected: ToolBinding =
                { Token = token
                  ToolName = name
                  Arg = Some "42"
                  Owner = Anyone
                  ExpiresAt = None
                  SingleUse = false
                  DeniedNotice = None }

            Expect.equal created expected "a slice-2-shaped binding (no owner/expiry/single-use/notice) equals the fully-defaulted record"

        testCase "ToolBinding.create with no arg still defaults the new fields" <| fun _ ->
            let token = CallbackToken.generate ()
            let name = toolName "reject"

            let created = ToolBinding.create token name None

            Expect.equal created.Owner Anyone "Owner defaults to Anyone"
            Expect.equal created.ExpiresAt None "ExpiresAt defaults to None (never expires)"
            Expect.equal created.SingleUse false "SingleUse defaults to false"
            Expect.equal created.DeniedNotice None "DeniedNotice defaults to None (built-in fallback used at refusal time)"
    ]
