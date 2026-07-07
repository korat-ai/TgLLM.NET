/// Regression coverage: a live surface losing ALL its buttons (its own update path collapses to
/// `TgBot.EditText`, which carries no keyboard at all) must clean up the tokens its PREVIOUS
/// keyboard left in the binding store, exactly like `TgBot.EditKeyboardPlan`'s own stale-token
/// removal does for a REPLACEMENT keyboard — otherwise every button a surface ever had before its
/// last one was removed leaks in the binding store forever.
module TgLLM.Integration.Tests.A2uiStaleBindingCleanupTests

open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Expecto
open FSharp.UMX
open TgLLM.Core
open TgLLM.FSharp
open TgLLM.A2UI
open TgLLM.Integration.Tests.FakeBotApiServer

let private noopSink: ActionSink = fun _ -> Task.CompletedTask

let private field (key: string) (node: JsonNode) : JsonNode =
    match node.[key] |> Option.ofObj with
    | Some c -> c
    | None -> failwithf "expected JSON field '%s' in %s" key (node.ToJsonString())

let private at (i: int) (node: JsonNode) : JsonNode =
    match node.[i] |> Option.ofObj with
    | Some c -> c
    | None -> failwithf "expected JSON index %d in %s" i (node.ToJsonString())

let private asString (node: JsonNode) : string = node.AsValue().GetValue<string>()

let private surfaceWithButtonJson (surfaceId: string) : string =
    $$"""
    {
      "version": "v1.0",
      "createSurface": {
        "surfaceId": "{{surfaceId}}",
        "catalogId": "telegram-basic",
        "components": [
          { "id": "root", "component": "Column", "children": [ "t1", "b1" ] },
          { "id": "t1", "component": "Text", "text": "has a button" },
          { "id": "b1", "component": "Button", "text": "Go", "action": { "event": { "name": "go" } } }
        ]
      }
    }
    """

let private surfaceLosesItsButtonJson (surfaceId: string) : string =
    $$"""
    {
      "version": "v1.0",
      "updateComponents": {
        "surfaceId": "{{surfaceId}}",
        "components": [
          { "id": "root", "component": "Column", "children": [ "t1" ] },
          { "id": "t1", "component": "Text", "text": "no more button" }
        ]
      }
    }
    """

[<Tests>]
let a2uiStaleBindingCleanupTests =
    testList "A2uiStaleBindingCleanup" [

        testCaseAsync "a surface that loses its last button cleans up that button's token from the binding store" (
            async {
                do!
                    task {
                        use! server = FakeBotApiServer.start ()
                        let store = InMemoryBindingStore() :> IBindingStore

                        use! bot =
                            TgBot.startPolling (
                                (TgBotConfig.create "123456789:TEST-fake-token")
                                    .WithBaseUrl(server.BaseUrl)
                                    .WithTools(ToolRegistry.create ())
                                    .WithBindingStore(store)
                            )

                        let renderer = A2ui.renderer bot noopSink
                        let chat = UMX.tag<chatId> 8601L
                        let surfaceId = "loses-its-button-surface"

                        match! renderer.Ingest(chat, surfaceWithButtonJson surfaceId) with
                        | Error e -> failtestf "test setup: expected Ok, got %A" e
                        | Ok() -> ()

                        let sendBody = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                        let token = sendBody |> field "reply_markup" |> field "inline_keyboard" |> at 0 |> at 0 |> field "callback_data" |> asString

                        let parsedToken =
                            match CallbackToken.tryParse token with
                            | ValueSome t -> t
                            | ValueNone -> failwithf "test setup: expected a valid CallbackToken, got %s" token

                        match! (store.TryGet(parsedToken, CancellationToken.None)).AsTask() with
                        | ValueSome _ -> ()
                        | ValueNone -> failtest "test setup: expected the button's token to be tracked right after it was sent"

                        match! renderer.Ingest(chat, surfaceLosesItsButtonJson surfaceId) with
                        | Error e -> failtestf "expected Ok, got %A" e
                        | Ok() -> ()

                        Expect.isEmpty (server.RequestsFor "editMessageReplyMarkup") "the no-keyboard edit goes through editMessageText, not editMessageReplyMarkup"

                        match! (store.TryGet(parsedToken, CancellationToken.None)).AsTask() with
                        | ValueNone -> ()
                        | ValueSome _ -> failtest "the removed button's token must be cleaned up from the binding store, not leaked"
                    }
                    |> Async.AwaitTask
            }
        )
    ]
