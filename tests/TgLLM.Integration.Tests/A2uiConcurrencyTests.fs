/// Regression coverage for `SurfaceRegistry`/`A2uiRenderer.Ingest` under concurrency: a burst of
/// `Ingest` calls for the SAME surface id must still only ever send the surface's first message
/// ONCE, with every later call in the burst editing that SAME message in place — never a second
/// `sendMessage`, even when a later call's `Apply` races the still-in-flight Telegram round-trip of
/// an earlier one.
module TgLLM.Integration.Tests.A2uiConcurrencyTests

open System
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

[<Tests>]
let a2uiConcurrencyTests =
    testList "A2uiConcurrency" [

        testCaseAsync "an update racing a still-in-flight initial send for the SAME surface produces exactly one sendMessage" (
            async {
                do!
                    task {
                        use! server = FakeBotApiServer.start ()
                        use! bot = buildBot server
                        let renderer = A2ui.renderer bot noopSink
                        let chat = UMX.tag<chatId> 8001L
                        let surfaceId = "race-loop-surface"

                        // Widens the initial send's in-flight window so the racing update below
                        // reliably lands INSIDE it (between SurfaceRegistry.Apply deciding SendNew
                        // and the façade recording the delivered message id) rather than depending
                        // on incidental scheduling luck.
                        server.DelayNextResponse("sendMessage", TimeSpan.FromMilliseconds 300.0)

                        // `and!`, not two bare `Ingest` calls awaited later: both start together
                        // (the create's own synchronous prefix — parse, Apply, decide SendNew, issue
                        // the delayed sendMessage — runs to its first real suspension before the
                        // update's Ingest ever starts), so the update reliably lands mid-flight.
                        let! createResult = renderer.Ingest(chat, createSurfaceJson surfaceId)
                        and! updateResult = renderer.Ingest(chat, updateComponentsJson surfaceId)

                        match createResult with
                        | Error e -> failtestf "expected the initial send to succeed, got %A" e
                        | Ok() -> ()

                        match updateResult with
                        | Error e -> failtestf "expected the racing update to succeed, got %A" e
                        | Ok() -> ()

                        Expect.equal
                            (List.length (server.RequestsFor "sendMessage"))
                            1
                            "the racing update must never trigger a second sendMessage for the same surface"

                        Expect.equal
                            (List.length (server.RequestsFor "editMessageText"))
                            1
                            "the racing update, once serialized behind the in-flight send, edits the SAME message instead"
                    }
                    |> Async.AwaitTask
            }
        )
    ]
