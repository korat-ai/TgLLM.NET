/// Regression coverage: a rendered surface whose Button label is empty (an unresolved DynString
/// binding resolves to `""`), over the Bot API's label length limit (ordinary LLM-authored text,
/// no length guard anywhere upstream), or whose `LocalOpenUrl` carries a blank url is exactly the
/// kind of "unvalidated LLM output" `ToolPlan.plan` itself treats as a PROGRAMMER error
/// (`invalidArg`, Always-Rule 6) — appropriate for a caller-controlled literal plan, not for a
/// value this renderer derived from agent-authored A2UI content. `Ingest` must validate a rendered
/// keyboard BEFORE handing it to `SendKeyboardPlan`/`EditKeyboardPlan`, surfacing the same
/// condition as an ordinary `A2uiError` instead of letting it escape as a thrown exception.
module TgLLM.Integration.Tests.A2uiValidationTests

open System.Threading.Tasks
open Expecto
open FSharp.UMX
open TgLLM.Core
open TgLLM.FSharp
open TgLLM.A2UI
open TgLLM.Integration.Tests.FakeBotApiServer

let private noopSink: ActionSink = fun _ -> Task.CompletedTask

let private buildBot (server: FakeBotApiServer) : Task<TgBot> =
    TgBot.startPolling ((TgBotConfig.create "123456789:TEST-fake-token").WithBaseUrl(server.BaseUrl).WithTools(ToolRegistry.create ()))

let private validButtonJson =
    """{ "id": "b1", "component": "Button", "text": "Go", "action": { "event": { "name": "go" } } }"""

let private emptyPathLabelButtonJson =
    """{ "id": "b1", "component": "Button", "text": { "path": "/missing" }, "action": { "event": { "name": "go" } } }"""

let private overlongLabelButtonJson =
    let label = String.replicate 65 "x"
    $$"""{ "id": "b1", "component": "Button", "text": "{{label}}", "action": { "event": { "name": "go" } } }"""

let private blankUrlButtonJson =
    """{ "id": "b1", "component": "Button", "text": "Docs", "action": { "functionCall": { "call": "openUrl", "args": { "url": " " } } } }"""

let private surfaceWithButtonJson (surfaceId: string) (buttonJson: string) : string =
    $$"""
    {
      "version": "v1.0",
      "createSurface": {
        "surfaceId": "{{surfaceId}}",
        "catalogId": "telegram-basic",
        "components": [
          { "id": "root", "component": "Column", "children": [ "t1", "b1" ] },
          { "id": "t1", "component": "Text", "text": "hi" },
          {{buttonJson}}
        ]
      }
    }
    """

let private updateWithButtonJson (surfaceId: string) (buttonJson: string) : string =
    $$"""
    {
      "version": "v1.0",
      "updateComponents": {
        "surfaceId": "{{surfaceId}}",
        "components": [
          { "id": "root", "component": "Column", "children": [ "t1", "b1" ] },
          { "id": "t1", "component": "Text", "text": "hi" },
          {{buttonJson}}
        ]
      }
    }
    """

let private updateTextJson (surfaceId: string) : string =
    $$"""
    {
      "version": "v1.0",
      "updateComponents": {
        "surfaceId": "{{surfaceId}}",
        "components": [ { "id": "t1", "component": "Text", "text": "updated" } ]
      }
    }
    """

let private deleteSurfaceJson (surfaceId: string) : string =
    $$"""{ "version": "v1.0", "deleteSurface": { "surfaceId": "{{surfaceId}}" } }"""

[<Tests>]
let a2uiValidationTests =
    testList "A2uiValidation" [

        for name, buttonJson in
            [ "an unresolved DynString label (resolves to empty)", emptyPathLabelButtonJson
              "a label over the Bot API's length limit", overlongLabelButtonJson
              "a blank openUrl", blankUrlButtonJson ] do

            testCaseAsync $"createSurface with {name} surfaces a validation error rather than throwing" (
                async {
                    do!
                        task {
                            use! server = FakeBotApiServer.start ()
                            use! bot = buildBot server
                            let renderer = A2ui.renderer bot noopSink

                            let! result = renderer.Ingest(UMX.tag<chatId> 7001L, surfaceWithButtonJson "bad-surface" buttonJson)

                            match result with
                            | Ok() -> failtest "expected Ingest to surface a validation error, not Ok"
                            | Error _ -> ()

                            Expect.isEmpty (server.RequestsFor "sendMessage") "an invalid rendered keyboard is never sent to Telegram"

                            match! renderer.Ingest(UMX.tag<chatId> 7001L, surfaceWithButtonJson "bad-surface" validButtonJson) with
                            | Error e -> failtestf "the corrected create should retry the rolled-back surface, got %A" e
                            | Ok() -> ()

                            Expect.equal (List.length (server.RequestsFor "sendMessage")) 1 "the corrected create sends normally"
                        }
                        |> Async.AwaitTask
                }
            )

        testCaseAsync "updateComponents that replaces a button with a degenerate label surfaces a validation error rather than throwing" (
            async {
                do!
                    task {
                        use! server = FakeBotApiServer.start ()
                        use! bot = buildBot server
                        let renderer = A2ui.renderer bot noopSink
                        let chat = UMX.tag<chatId> 7002L

                        match! renderer.Ingest(chat, surfaceWithButtonJson "bad-edit-surface" validButtonJson) with
                        | Error e -> failtestf "test setup: expected Ok, got %A" e
                        | Ok() -> ()

                        Expect.equal (List.length (server.RequestsFor "sendMessage")) 1 "test setup: the valid surface sends once"

                        let! result = renderer.Ingest(chat, updateWithButtonJson "bad-edit-surface" overlongLabelButtonJson)

                        match result with
                        | Ok() -> failtest "expected Ingest to surface a validation error, not Ok"
                        | Error _ -> ()

                        Expect.isEmpty (server.RequestsFor "editMessageText") "an invalid replacement keyboard is never edited onto the wire"

                        match! renderer.Ingest(chat, updateTextJson "bad-edit-surface") with
                        | Error e -> failtestf "the failed replacement must not poison the live surface, got %A" e
                        | Ok() -> ()

                        Expect.equal (List.length (server.RequestsFor "editMessageText")) 1 "a later partial update still renders against the last valid state"
                    }
                    |> Async.AwaitTask
            }
        )

        testCaseAsync "a failed delete restores the surface so a later update can still edit it" (
            async {
                do!
                    task {
                        use! server = FakeBotApiServer.start ()
                        use! bot = buildBot server
                        let renderer = A2ui.renderer bot noopSink
                        let chat = UMX.tag<chatId> 7003L
                        let surfaceId = "failed-delete-surface"

                        match! renderer.Ingest(chat, surfaceWithButtonJson surfaceId validButtonJson) with
                        | Error e -> failtestf "test setup: expected Ok, got %A" e
                        | Ok() -> ()

                        server.EnqueueError("deleteMessage", 500, "Internal Server Error")

                        let! threw =
                            task {
                                try
                                    let! _ = renderer.Ingest(chat, deleteSurfaceJson surfaceId)
                                    return false
                                with _ ->
                                    return true
                            }

                        Expect.isTrue threw "the simulated Telegram delete failure reaches the caller"

                        match! renderer.Ingest(chat, updateWithButtonJson surfaceId validButtonJson) with
                        | Error e -> failtestf "the restored surface should remain editable, got %A" e
                        | Ok() -> ()

                        Expect.equal (List.length (server.RequestsFor "editMessageText")) 1 "the later update edits the original message"
                    }
                    |> Async.AwaitTask
            }
        )
    ]
