/// Tests for `ApprovalRendering.defaultRender` (total, non-empty, plain-text, fixed labels) and
/// `.validate` (surfaces an over-limit body / invalid label as `Result.Error`, never throws).
module TgLLM.Maf.Tests.ApprovalRenderingTests

open Expecto
open FsCheck
open FSharp.UMX
open TgLLM.Core
open TgLLM.Maf

let private chat (id: int64) : ChatId = UMX.tag<chatId> id

[<Tests>]
let approvalRenderingTests =
    testList "ApprovalRendering" [

        testProperty "defaultRender is total and always produces a non-empty body, for any prompt" <| fun (tool: string, args: (string * string) list, chatId: int64) ->
            let prompt: ApprovalPrompt = { Tool = tool; Arguments = args; Chat = chat chatId }
            let render = ApprovalRendering.defaultRender prompt
            not (System.String.IsNullOrEmpty render.Body)

        testProperty "defaultRender's labels are always the fixed Approve/Reject pair" <| fun (tool: string, args: (string * string) list, chatId: int64) ->
            let prompt: ApprovalPrompt = { Tool = tool; Arguments = args; Chat = chat chatId }
            let render = ApprovalRendering.defaultRender prompt
            render.ApproveLabel = "Approve" && render.RejectLabel = "Reject"

        testCase "defaultRender's body includes the tool name" <| fun _ ->
            let prompt: ApprovalPrompt =
                { Tool = "send_email"
                  Arguments = []
                  Chat = chat 1L }

            let render = ApprovalRendering.defaultRender prompt
            Expect.stringContains render.Body "send_email" "the tool name appears in the body"

        testCase "defaultRender's body includes one line per argument" <| fun _ ->
            let prompt: ApprovalPrompt =
                { Tool = "send_email"
                  Arguments = [ "toAddr", "\"alice@example.com\""; "body", "\"hello\"" ]
                  Chat = chat 1L }

            let render = ApprovalRendering.defaultRender prompt
            Expect.stringContains render.Body "toAddr" "the first argument's name appears"
            Expect.stringContains render.Body "alice@example.com" "the first argument's value appears"
            Expect.stringContains render.Body "body" "the second argument's name appears"

        testCase "validate accepts the default render" <| fun _ ->
            let prompt: ApprovalPrompt =
                { Tool = "send_email"
                  Arguments = []
                  Chat = chat 1L }

            match ApprovalRendering.validate (ApprovalRendering.defaultRender prompt) with
            | Ok _ -> ()
            | Error e -> failwithf "expected Ok, got %A" e

        testCase "validate surfaces an over-limit body as ReplyTooLong" <| fun _ ->
            let render: ApprovalRender =
                { Body = String.replicate (MessageText.MaxLength + 1) "x"
                  ApproveLabel = "Approve"
                  RejectLabel = "Reject" }

            match ApprovalRendering.validate render with
            | Error(ReplyTooLong(length, max)) ->
                Expect.equal length (MessageText.MaxLength + 1) "the reported length matches the over-limit body"
                Expect.equal max MessageText.MaxLength "the reported max matches MessageText's own limit"
            | other -> failwithf "expected Error(ReplyTooLong _), got %A" other

        testCase "validate surfaces an empty body as an error" <| fun _ ->
            let render: ApprovalRender =
                { Body = ""
                  ApproveLabel = "Approve"
                  RejectLabel = "Reject" }

            Expect.isError (ApprovalRendering.validate render) "an empty body is invalid"

        testCase "validate surfaces an over-limit Approve label as an error" <| fun _ ->
            let render: ApprovalRender =
                { Body = "Approve?"
                  ApproveLabel = String.replicate (ButtonLabel.MaxLength + 1) "x"
                  RejectLabel = "Reject" }

            Expect.isError (ApprovalRendering.validate render) "an over-limit label is invalid"

        testCase "validate surfaces an empty Reject label as an error" <| fun _ ->
            let render: ApprovalRender = { Body = "Approve?"; ApproveLabel = "Approve"; RejectLabel = "" }
            Expect.isError (ApprovalRendering.validate render) "an empty label is invalid"

        testCase "validate never throws on a whitespace-only render" <| fun _ ->
            let render: ApprovalRender =
                { Body = "   "
                  ApproveLabel = "   "
                  RejectLabel = "   " }

            Expect.isError (ApprovalRendering.validate render) "a whitespace-only body/labels are invalid, not merely accepted"
    ]
