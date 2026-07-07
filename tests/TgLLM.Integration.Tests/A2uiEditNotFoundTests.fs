/// Regression coverage: a user deleting the message a live A2UI surface is rendered as, out from
/// under the agent, makes every LATER edit resolve to Telegram's "message to edit not found" —
/// classified by the Bot API client as `EditNotFound`, a soft failure by convention throughout this
/// library. `Ingest` must surface this to the attached `IA2uiObserver` (rather than completing with
/// silent, permanent no-op) and clear the surface's recorded message id, so the NEXT update for that
/// surface re-sends a fresh message instead of forever trying (and failing) to edit a vanished one.
module TgLLM.Integration.Tests.A2uiEditNotFoundTests

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

let private recordingObserver () : IA2uiObserver * ResizeArray<A2uiError> =
    let errors = ResizeArray<A2uiError>()

    { new IA2uiObserver with
        member _.OnA2uiError(error: A2uiError) = errors.Add error
        member _.OnMalformedAction(_descriptor: ActionDescriptor) = ()
        member _.OnStaleSurfaceAction(_descriptor: ActionDescriptor) = () },
    errors

let private surfaceWithButtonJson (surfaceId: string) (text: string) : string =
    $$"""
    {
      "version": "v1.0",
      "createSurface": {
        "surfaceId": "{{surfaceId}}",
        "catalogId": "telegram-basic",
        "components": [
          { "id": "root", "component": "Column", "children": [ "t1", "b1" ] },
          { "id": "t1", "component": "Text", "text": "{{text}}" },
          { "id": "b1", "component": "Button", "text": "Go", "action": { "event": { "name": "go" } } }
        ]
      }
    }
    """

let private updateWithButtonJson (surfaceId: string) (text: string) : string =
    $$"""
    {
      "version": "v1.0",
      "updateComponents": {
        "surfaceId": "{{surfaceId}}",
        "components": [
          { "id": "root", "component": "Column", "children": [ "t1", "b1" ] },
          { "id": "t1", "component": "Text", "text": "{{text}}" },
          { "id": "b1", "component": "Button", "text": "Go", "action": { "event": { "name": "go" } } }
        ]
      }
    }
    """

let private textOnlySurfaceJson (surfaceId: string) (text: string) : string =
    $$"""
    {
      "version": "v1.0",
      "createSurface": {
        "surfaceId": "{{surfaceId}}",
        "catalogId": "telegram-basic",
        "components": [ { "id": "root", "component": "Text", "text": "{{text}}" } ]
      }
    }
    """

let private updateTextOnlyJson (surfaceId: string) (text: string) : string =
    $$"""
    {
      "version": "v1.0",
      "updateComponents": {
        "surfaceId": "{{surfaceId}}",
        "components": [ { "id": "root", "component": "Text", "text": "{{text}}" } ]
      }
    }
    """

[<Tests>]
let a2uiEditNotFoundTests =
    testList "A2uiEditNotFound" [

        testCaseAsync "a vanished message on the keyboard edit path is reported to the observer and re-sent on the next update" (
            async {
                do!
                    task {
                        use! server = FakeBotApiServer.start ()
                        use! bot = buildBot server
                        let observer, errors = recordingObserver ()
                        let renderer = A2ui.rendererWithObserver bot noopSink observer
                        let chat = UMX.tag<chatId> 8501L
                        let surfaceId = "vanished-keyboard-surface"

                        match! renderer.Ingest(chat, surfaceWithButtonJson surfaceId "v1") with
                        | Error e -> failtestf "test setup: expected Ok, got %A" e
                        | Ok() -> ()

                        Expect.equal (List.length (server.RequestsFor "sendMessage")) 1 "test setup: the initial render sends once"

                        server.EnqueueError("editMessageText", 400, "Bad Request: message to edit not found")

                        match! renderer.Ingest(chat, updateWithButtonJson surfaceId "v2") with
                        | Error e -> failtestf "a vanished message is a SOFT failure — expected Ok, got %A" e
                        | Ok() -> ()

                        Expect.isNonEmpty errors "the vanished message was reported to the observer, not silently swallowed"

                        // The next update must re-send a fresh message rather than trying (and
                        // failing) to edit the same vanished one forever.
                        match! renderer.Ingest(chat, updateWithButtonJson surfaceId "v3") with
                        | Error e -> failtestf "expected Ok, got %A" e
                        | Ok() -> ()

                        Expect.equal
                            (List.length (server.RequestsFor "sendMessage"))
                            2
                            "the surface recovers by sending a FRESH message once its old one is known gone"
                    }
                    |> Async.AwaitTask
            }
        )

        testCaseAsync "a vanished message on the plain-text edit path is reported to the observer and re-sent on the next update" (
            async {
                do!
                    task {
                        use! server = FakeBotApiServer.start ()
                        use! bot = buildBot server
                        let observer, errors = recordingObserver ()
                        let renderer = A2ui.rendererWithObserver bot noopSink observer
                        let chat = UMX.tag<chatId> 8502L
                        let surfaceId = "vanished-text-surface"

                        match! renderer.Ingest(chat, textOnlySurfaceJson surfaceId "v1") with
                        | Error e -> failtestf "test setup: expected Ok, got %A" e
                        | Ok() -> ()

                        Expect.equal (List.length (server.RequestsFor "sendMessage")) 1 "test setup: the initial render sends once"

                        server.EnqueueError("editMessageText", 400, "Bad Request: message to edit not found")

                        match! renderer.Ingest(chat, updateTextOnlyJson surfaceId "v2") with
                        | Error e -> failtestf "a vanished message is a SOFT failure — expected Ok, got %A" e
                        | Ok() -> ()

                        Expect.isNonEmpty errors "the vanished message was reported to the observer, not silently swallowed"

                        match! renderer.Ingest(chat, updateTextOnlyJson surfaceId "v3") with
                        | Error e -> failtestf "expected Ok, got %A" e
                        | Ok() -> ()

                        Expect.equal
                            (List.length (server.RequestsFor "sendMessage"))
                            2
                            "the surface recovers by sending a FRESH message once its old one is known gone"
                    }
                    |> Async.AwaitTask
            }
        )
    ]
