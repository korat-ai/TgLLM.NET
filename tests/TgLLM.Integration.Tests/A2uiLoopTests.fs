/// Acceptance for the full A2UI tap → action → re-render loop: render a surface with a
/// `ServerEvent` Button and a `LocalOpenUrl` Button, tap the server-bound one, confirm the sink
/// receives the correct `A2uiAction`, feed the agent's `updateComponents` reply for the SAME
/// surface and confirm the SAME message is edited in place (no new `sendMessage`), then
/// `deleteSurface` and confirm the message is deleted — over both long polling and webhooks,
/// through the F# façade (`A2ui.renderer`).
module TgLLM.Integration.Tests.A2uiLoopTests

open System.Net.Http
open System.Text
open System.Text.Json.Nodes
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Logging
open Expecto
open FSharp.UMX
open TgLLM.Core
open TgLLM.FSharp
open TgLLM.A2UI
open TgLLM.AspNetCore
open TgLLM.Integration.Tests.FakeBotApiServer

let private field (key: string) (node: JsonNode) : JsonNode =
    match node.[key] |> Option.ofObj with
    | Some c -> c
    | None -> failwithf "expected JSON field '%s' in %s" key (node.ToJsonString())

let private tryField (key: string) (node: JsonNode) : JsonNode option = node.[key] |> Option.ofObj

let private at (i: int) (node: JsonNode) : JsonNode =
    match node.[i] |> Option.ofObj with
    | Some c -> c
    | None -> failwithf "expected JSON index %d in %s" i (node.ToJsonString())

let private asString (node: JsonNode) : string = node.AsValue().GetValue<string>()
let private asInt64 (node: JsonNode) : int64 = node.AsValue().GetValue<int64>()

let private awaitOrTimeout (ms: int) (t: Task) : Task =
    task {
        let! completed = Task.WhenAny(t, Task.Delay ms)
        if completed <> t then failtest "timed out waiting for the expected condition"
        do! t
    }

let private stringContextValue (context: (string * JsonNode option) list) (key: string) : string option =
    context
    |> List.tryFind (fun (k, _) -> k = key)
    |> Option.bind snd
    |> Option.bind (function
        | :? JsonValue as v -> Some(v.GetValue<string>())
        | _ -> None)

let private createSurfaceJson (surfaceId: string) : string =
    $$"""
    {
      "version": "v1.0",
      "createSurface": {
        "surfaceId": "{{surfaceId}}",
        "catalogId": "telegram-basic",
        "dataModel": { "env": "prod" },
        "components": [
          { "id": "root", "component": "Column", "children": [ "t1", "row1" ] },
          { "id": "t1", "component": "Text", "text": "Deploy?" },
          { "id": "row1", "component": "Row", "children": [ "b1", "b2" ] },
          { "id": "b1", "component": "Button", "text": "Approve",
            "action": { "event": { "name": "approve", "context": { "env": { "path": "/env" } }, "wantResponse": true, "actionId": "a1" } } },
          { "id": "b2", "component": "Button", "text": "Docs",
            "action": { "functionCall": { "call": "openUrl", "args": { "url": "https://example.com/docs" } } } }
        ]
      }
    }
    """

let private updateComponentsJson (surfaceId: string) : string =
    $$"""
    {
      "version": "v1.0",
      "updateComponents": {
        "surfaceId": "{{surfaceId}}",
        "components": [
          { "id": "root", "component": "Column", "children": [ "t1", "b3" ] },
          { "id": "t1", "component": "Text", "text": "Deployed!" },
          { "id": "b3", "component": "Button", "text": "Rollback", "action": { "event": { "name": "rollback" } } }
        ]
      }
    }
    """

let private deleteSurfaceJson (surfaceId: string) : string =
    $$"""{ "version": "v1.0", "deleteSurface": { "surfaceId": "{{surfaceId}}" } }"""

/// The full loop, over an already-built `renderer`/`server`, delivering the simulated tap via
/// whatever `deliverPress` does (enqueue-and-poll for long polling, POST for webhooks) — every
/// assertion below is shared by both transports, so a regression in either shows up here once.
let private runLoopScenario
    (server: FakeBotApiServer)
    (renderer: A2uiRenderer)
    (chat: ChatId)
    (surfaceId: string)
    (deliverPress: string -> Task)
    (actionReceived: TaskCompletionSource<A2uiAction>)
    : Task<unit> =
    task {
        match! renderer.Ingest(chat, createSurfaceJson surfaceId) with
        | Error e -> failtestf "expected Ok, got %A" e
        | Ok() -> ()

        let sendRequests = server.RequestsFor "sendMessage"
        Expect.equal (List.length sendRequests) 1 "the surface's first render sends exactly one message"
        let sentBody = sendRequests.Head.Body |> Option.get
        Expect.equal (sentBody |> field "text" |> asString) "Deploy?" "the initial body renders"
        Expect.equal (sentBody |> field "parse_mode" |> asString) "MarkdownV2" "the A2UI send path requests MarkdownV2"

        let row0 = sentBody |> field "reply_markup" |> field "inline_keyboard" |> at 0
        let approveButton = row0 |> at 0
        let docsButton = row0 |> at 1
        let approveToken = approveButton |> field "callback_data" |> asString
        Expect.isNone (tryField "url" approveButton) "the ServerEvent button carries no client-side url"
        Expect.equal (docsButton |> field "url" |> asString) "https://example.com/docs" "the LocalOpenUrl button opens its link client-side"
        Expect.isNone (tryField "callback_data" docsButton) "the LocalOpenUrl button carries no callback — no server round-trip"

        // `FakeBotApiServer` assigns `message_id` sequentially PER CHAT starting at 1; this is the
        // first (and, until the edit below, only) message ever sent to this scenario's own chat.
        let messageId = UMX.tag<messageId> 1L

        do! deliverPress approveToken
        do! awaitOrTimeout 5000 (actionReceived.Task :> Task)
        let action = actionReceived.Task.Result

        Expect.equal action.Name "approve" "the tapped button's action name reaches the sink"
        Expect.equal action.SurfaceId surfaceId "the action carries its own surface id"
        Expect.equal action.SourceComponentId "b1" "the action carries the pressed component's own id"
        Expect.equal action.WantResponse true "wantResponse carried through"
        Expect.equal action.ActionId (Some "a1") "actionId carried through"
        Expect.equal (stringContextValue action.Context "env") (Some "prod") "the action's context resolves against the live data model"

        match! renderer.Ingest(chat, updateComponentsJson surfaceId) with
        | Error e -> failtestf "expected Ok, got %A" e
        | Ok() -> ()

        Expect.equal (List.length (server.RequestsFor "sendMessage")) 1 "the update edits the SAME message — no new sendMessage"

        match server.RequestsFor "editMessageText" with
        | [ request ] ->
            let body = request.Body |> Option.get
            Expect.equal (body |> field "chat_id" |> asInt64) (UMX.untag chat) "the edit targets the SAME chat"
            Expect.equal (body |> field "message_id" |> asInt64) (UMX.untag messageId) "the edit targets the SAME (originally sent) message"
            Expect.equal (body |> field "text" |> asString) (Markdown.escapeV2 "Deployed!") "the new body reached the wire"
            Expect.equal (body |> field "parse_mode" |> asString) "MarkdownV2" "the edit keeps requesting MarkdownV2"

            let newButtonText =
                body |> field "reply_markup" |> field "inline_keyboard" |> at 0 |> at 0 |> field "text" |> asString

            Expect.equal newButtonText "Rollback" "the new keyboard reached the wire"
        | other -> failwithf "expected exactly one editMessageText call, got %d" (List.length other)

        match! renderer.Ingest(chat, deleteSurfaceJson surfaceId) with
        | Error e -> failtestf "expected Ok, got %A" e
        | Ok() -> ()

        match server.RequestsFor "deleteMessage" with
        | [ request ] ->
            let body = request.Body |> Option.get
            Expect.equal (body |> field "chat_id" |> asInt64) (UMX.untag chat) "the delete targets the SAME chat"
            Expect.equal (body |> field "message_id" |> asInt64) (UMX.untag messageId) "the delete targets the SAME message"
        | other -> failwithf "expected exactly one deleteMessage call, got %d" (List.length other)
    }

let private runLoopOverPolling () : Task<unit> =
    task {
        use! server = FakeBotApiServer.start ()
        let chat = UMX.tag<chatId> 6001L
        let actionReceived = TaskCompletionSource<A2uiAction>()
        let sink: ActionSink = fun action -> actionReceived.TrySetResult action |> ignore; Task.CompletedTask

        use! bot =
            TgBot.startPolling (
                (TgBotConfig.create "123456789:TEST-fake-token")
                    .WithBaseUrl(server.BaseUrl)
                    .WithTools(ToolRegistry.create ())
            )

        let renderer = A2ui.renderer bot sink

        let deliverPress (token: string) : Task =
            server.EnqueueResult(
                "getUpdates",
                TelegramJson.batch [ TelegramJson.callbackQueryUpdate 1 "q-loop-polling" token 6001L 1 9001L "Al" ]
            )

            Task.CompletedTask

        do! runLoopScenario server renderer chat "loop-polling" deliverPress actionReceived
    }

let private startWebhookHost (source: TgLLM.Webhooks.WebhookUpdateSource) (secret: string) : Task<WebApplication> =
    task {
        let builder = WebApplication.CreateBuilder()
        builder.WebHost.UseUrls "http://127.0.0.1:0" |> ignore
        builder.Logging.ClearProviders() |> ignore
        let app = builder.Build()
        app.MapTelegramWebhook("/telegram/webhook", source, secret) |> ignore
        do! app.StartAsync()
        return app
    }

let private post (http: HttpClient) (url: string) (json: string) (secret: string) : Task<HttpResponseMessage> =
    let request = new HttpRequestMessage(HttpMethod.Post, url)
    request.Content <- new StringContent(json, Encoding.UTF8, "application/json")
    request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", secret)
    http.SendAsync request

let private runLoopOverWebhook () : Task<unit> =
    task {
        use! server = FakeBotApiServer.start ()
        let secret = "s3cret-a2ui-loop"
        let chat = UMX.tag<chatId> 6002L
        let actionReceived = TaskCompletionSource<A2uiAction>()
        let sink: ActionSink = fun action -> actionReceived.TrySetResult action |> ignore; Task.CompletedTask

        let config =
            TgWebhookConfig
                .create("123456789:TEST-fake-token", "https://example.test/ignored", secret)
                .WithBaseUrl(server.BaseUrl)
                .WithTools(ToolRegistry.create ())

        use! bot = TgBot.startWebhook config
        use! host = startWebhookHost bot.WebhookSource secret
        let webhookUrl = (Seq.head host.Urls).TrimEnd('/') + "/telegram/webhook"
        let renderer = A2ui.renderer bot sink

        let deliverPress (token: string) : Task =
            task {
                let updateJson = TelegramJson.callbackQueryUpdate 1 "q-loop-webhook" token 6002L 1 9002L "Webby"
                use http = new HttpClient()
                let! response = post http webhookUrl updateJson secret
                Expect.equal (int response.StatusCode) 200 "the webhook delivery is accepted"
            }
            :> Task

        do! runLoopScenario server renderer chat "loop-webhook" deliverPress actionReceived
    }

[<Tests>]
let a2uiLoopTests =
    testList "A2uiLoop" [

        testCaseAsync
            "render → tap → action → agent update edits the SAME message → deleteSurface removes it, over both transports"
        <| async {
            do!
                task {
                    do! runLoopOverPolling ()
                    do! runLoopOverWebhook ()
                }
                |> Async.AwaitTask
        }
    ]
