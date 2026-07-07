/// Regression coverage: a live surface stays bound to the chat its `createSurface` was ingested
/// for. A LATER `updateComponents`/`deleteSurface` for the SAME surface id arriving under a
/// DIFFERENT chat must never edit/delete a message in that other chat — Telegram's own `message_id`
/// is unique only PER CHAT, so acting under the wrong chat risks editing/deleting an unrelated
/// message that happens to share the same numeric id in that chat.
module TgLLM.Integration.Tests.A2uiChatIsolationTests

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

let private createSurfaceJson (surfaceId: string) : string =
    $$"""
    {
      "version": "v1.0",
      "createSurface": {
        "surfaceId": "{{surfaceId}}",
        "catalogId": "telegram-basic",
        "components": [ { "id": "root", "component": "Text", "text": "v1" } ]
      }
    }
    """

let private updateComponentsJson (surfaceId: string) : string =
    $$"""
    {
      "version": "v1.0",
      "updateComponents": {
        "surfaceId": "{{surfaceId}}",
        "components": [ { "id": "root", "component": "Text", "text": "v2" } ]
      }
    }
    """

let private deleteSurfaceJson (surfaceId: string) : string =
    $$"""{ "version": "v1.0", "deleteSurface": { "surfaceId": "{{surfaceId}}" } }"""

[<Tests>]
let a2uiChatIsolationTests =
    testList "A2uiChatIsolation" [

        testCaseAsync "updateComponents arriving under a DIFFERENT chat than the surface's own is rejected, never edits any chat" (
            async {
                do!
                    task {
                        use! server = FakeBotApiServer.start ()
                        use! bot = buildBot server
                        let renderer = A2ui.renderer bot noopSink
                        let ownChat = UMX.tag<chatId> 9101L
                        let attackerChat = UMX.tag<chatId> 9102L
                        let surfaceId = "chat-isolation-surface"

                        match! renderer.Ingest(ownChat, createSurfaceJson surfaceId) with
                        | Error e -> failtestf "test setup: expected Ok, got %A" e
                        | Ok() -> ()

                        Expect.equal (List.length (server.RequestsFor "sendMessage")) 1 "test setup: the surface's first render sends once"

                        let! result = renderer.Ingest(attackerChat, updateComponentsJson surfaceId)

                        match result with
                        | Ok() -> failtest "expected the chat-mismatched update to surface an error, not Ok"
                        | Error _ -> ()

                        Expect.isEmpty (server.RequestsFor "editMessageText") "a chat-mismatched update must never reach editMessageText, in EITHER chat"

                        // The surface is still alive in its OWN chat: a correctly-scoped update
                        // still edits it normally afterward.
                        match! renderer.Ingest(ownChat, updateComponentsJson surfaceId) with
                        | Error e -> failtestf "expected the correctly-scoped update to still succeed, got %A" e
                        | Ok() -> ()

                        match server.RequestsFor "editMessageText" with
                        | [ request ] ->
                            let chatIdField = request.Body |> Option.get |> fun b -> b["chat_id"] |> Option.ofObj |> Option.get
                            Expect.equal (chatIdField.AsValue().GetValue<int64>()) (UMX.untag ownChat) "the edit targets the surface's own chat"
                        | other -> failwithf "expected exactly one editMessageText call, got %d" (List.length other)
                    }
                    |> Async.AwaitTask
            }
        )

        testCaseAsync "deleteSurface arriving under a DIFFERENT chat than the surface's own is rejected, never deletes in any chat" (
            async {
                do!
                    task {
                        use! server = FakeBotApiServer.start ()
                        use! bot = buildBot server
                        let renderer = A2ui.renderer bot noopSink
                        let ownChat = UMX.tag<chatId> 9103L
                        let attackerChat = UMX.tag<chatId> 9104L
                        let surfaceId = "chat-isolation-delete-surface"

                        match! renderer.Ingest(ownChat, createSurfaceJson surfaceId) with
                        | Error e -> failtestf "test setup: expected Ok, got %A" e
                        | Ok() -> ()

                        let! result = renderer.Ingest(attackerChat, deleteSurfaceJson surfaceId)

                        match result with
                        | Ok() -> failtest "expected the chat-mismatched delete to surface an error, not Ok"
                        | Error _ -> ()

                        Expect.isEmpty (server.RequestsFor "deleteMessage") "a chat-mismatched delete must never reach deleteMessage, in EITHER chat"

                        // The surface is still alive in its OWN chat: a correctly-scoped delete
                        // still removes it normally afterward.
                        match! renderer.Ingest(ownChat, deleteSurfaceJson surfaceId) with
                        | Error e -> failtestf "expected the correctly-scoped delete to still succeed, got %A" e
                        | Ok() -> ()

                        match server.RequestsFor "deleteMessage" with
                        | [ request ] ->
                            let chatIdField = request.Body |> Option.get |> fun b -> b["chat_id"] |> Option.ofObj |> Option.get
                            Expect.equal (chatIdField.AsValue().GetValue<int64>()) (UMX.untag ownChat) "the delete targets the surface's own chat"
                        | other -> failwithf "expected exactly one deleteMessage call, got %d" (List.length other)
                    }
                    |> Async.AwaitTask
            }
        )
    ]
