/// Acceptance test for client-side buttons: a keyboard mixing a tool button with a WebApp
/// button and a CopyText button sends a wire-correct row — the tool button carries
/// `callback_data` and still routes to its bound tool on tap; the WebApp button carries
/// `web_app.url` and the CopyText button carries `copy_text.text`, NEITHER carrying
/// `callback_data` — so, structurally, there is no callback query Telegram could ever deliver for
/// them and no server-side handler is reached for either. Run once over long polling and once over
/// webhooks (mirrors `ToolRouterBothTransportsTests.fs`'s both-transports structure); the C# façade
/// equivalent lives in `tests/TgLLM.CSharp.Tests/ClientButtonsTests.cs`.
module TgLLM.Integration.Tests.ClientButtonsTests

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
open TgLLM.AspNetCore
open TgLLM.Integration.Tests.FakeBotApiServer

let private field (key: string) (node: JsonNode) : JsonNode =
    match node.[key] |> Option.ofObj with
    | Some c -> c
    | None -> failwithf "expected JSON field '%s' in %s" key (node.ToJsonString())

let private at (i: int) (node: JsonNode) : JsonNode =
    match node.[i] |> Option.ofObj with
    | Some c -> c
    | None -> failwithf "expected JSON index %d in %s" i (node.ToJsonString())

let private asString (node: JsonNode) : string = node.AsValue().GetValue<string>()

let private hasField (key: string) (node: JsonNode) : bool = node.[key] |> Option.ofObj |> Option.isSome

let private buttonAt (row: int) (col: int) (sendBody: JsonNode) : JsonNode =
    sendBody |> field "reply_markup" |> field "inline_keyboard" |> at row |> at col

let private awaitOrTimeout (ms: int) (t: Task) : Task =
    task {
        let! completed = Task.WhenAny(t, Task.Delay ms)
        if completed <> t then failtest "timed out waiting for the tool to run"
        do! t
    }

let private planOrFail (rows: PlanButton list list) : ToolKeyboard =
    match Plan.rows rows with
    | Ok p -> p
    | Error e -> failtestf "plan should be valid: %A" e

/// The shared scenario: sends a row of [tool, WebApp, CopyText], asserts the wire shape of all
/// three, taps the tool button, and returns the arg the tool observed (so the caller can also
/// confirm the tool actually ran).
let private mixedButtonsScenario
    (server: FakeBotApiServer)
    (bot: TgBot)
    (chat: int64)
    (approveRan: TaskCompletionSource<string>)
    (deliverUpdate: string -> Task)
    : Task<string> =
    task {
        let plan =
            planOrFail
                [ [ Plan.tool "Approve" "approve"
                    Plan.webApp "Open" "https://example.test/app"
                    Plan.copyText "Copy" "snippet-1" ] ]

        let! _ = bot.SendKeyboardPlan(UMX.tag<chatId> chat, MessageText.unsafe "Deploy?", plan)
        let sentKeyboard = (server.RequestsFor "sendMessage").Head.Body |> Option.get

        // The tool button: carries callback_data, no web_app/copy_text.
        let toolButton = sentKeyboard |> buttonAt 0 0
        Expect.equal (toolButton |> field "text" |> asString) "Approve" "the tool button's label reached the wire"
        Expect.isTrue (toolButton |> hasField "callback_data") "the tool button DOES carry callback_data (its token)"
        Expect.isFalse (toolButton |> hasField "web_app") "the tool button carries no web_app payload"
        Expect.isFalse (toolButton |> hasField "copy_text") "the tool button carries no copy_text payload"

        // The WebApp button: carries web_app.url, NO callback_data — client-side only, nothing to route.
        let webAppButton = sentKeyboard |> buttonAt 0 1
        Expect.equal (webAppButton |> field "text" |> asString) "Open" "the WebApp button's label reached the wire"
        Expect.equal (webAppButton |> field "web_app" |> field "url" |> asString) "https://example.test/app" "the WebApp button's url reached the wire"
        Expect.isFalse (webAppButton |> hasField "callback_data") "a WebApp button carries no callback_data — no callback query can ever reach the server for it"

        // The CopyText button: carries copy_text.text, NO callback_data — client-side only, nothing to route.
        let copyTextButton = sentKeyboard |> buttonAt 0 2
        Expect.equal (copyTextButton |> field "text" |> asString) "Copy" "the CopyText button's label reached the wire"
        Expect.equal (copyTextButton |> field "copy_text" |> field "text" |> asString) "snippet-1" "the CopyText button's clipboard text reached the wire"
        Expect.isFalse (copyTextButton |> hasField "callback_data") "a CopyText button carries no callback_data — no callback query can ever reach the server for it"

        // The tool button still routes on tap, exactly like a tool-only keyboard.
        let approveToken = toolButton |> field "callback_data" |> asString
        do! deliverUpdate approveToken
        do! awaitOrTimeout 5000 (approveRan.Task :> Task)
        return approveRan.Task.Result
    }

let private runOverPolling () : Task<string> =
    task {
        use! server = FakeBotApiServer.start ()
        let approveRan = TaskCompletionSource<string>()

        let tools =
            ToolRegistry
                .create()
                .Register("approve", (fun ctx -> task { approveRan.TrySetResult(ctx.Arg |> Option.ofObj |> Option.defaultValue "<no-arg>") |> ignore }))

        use! bot = TgBot.startPolling ((TgBotConfig.create "123456789:TEST-fake-token").WithBaseUrl(server.BaseUrl).WithTools tools)

        let deliver (approveToken: string) : Task =
            server.EnqueueResult(
                "getUpdates",
                TelegramJson.batch [ TelegramJson.callbackQueryUpdate 1 "q-clientbuttons-polling" approveToken 850L 80 980L "Polly" ]
            )

            Task.CompletedTask

        return! mixedButtonsScenario server bot 850L approveRan deliver
    }

/// Hosts the webhook-receiving endpoint on a loopback port (same pattern as
/// `ToolRouterBothTransportsTests.fs`).
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

let private runOverWebhook () : Task<string> =
    task {
        use! botApi = FakeBotApiServer.start ()
        let secret = "s3cret-client-buttons"
        let approveRan = TaskCompletionSource<string>()

        let tools =
            ToolRegistry
                .create()
                .Register("approve", (fun ctx -> task { approveRan.TrySetResult(ctx.Arg |> Option.ofObj |> Option.defaultValue "<no-arg>") |> ignore }))

        let config =
            TgWebhookConfig
                .create("123456789:TEST-fake-token", "https://example.test/ignored", secret)
                .WithBaseUrl(botApi.BaseUrl)
                .WithTools(tools)

        use! bot = TgBot.startWebhook config
        use! host = startWebhookHost bot.WebhookSource secret
        let webhookUrl = (Seq.head host.Urls).TrimEnd('/') + "/telegram/webhook"

        let deliver (approveToken: string) : Task =
            task {
                use http = new HttpClient()
                let updateJson = TelegramJson.callbackQueryUpdate 1 "q-clientbuttons-webhook" approveToken 851L 81 981L "Webby"
                let! response = post http webhookUrl updateJson secret
                Expect.equal (int response.StatusCode) 200 "the webhook delivery is accepted"
            }

        return! mixedButtonsScenario botApi bot 851L approveRan deliver
    }

[<Tests>]
let clientButtonsTests =
    testList
        "ClientButtons (WebApp / CopyText alongside a tool button)"
        [

          testCaseAsync
              "a keyboard mixing tool + WebApp + CopyText sends a wire-correct row; only the tool button routes, over both long polling and webhooks"
          <| async {
              do!
                  task {
                      let! polledArg = runOverPolling ()
                      let! webhookArg = runOverWebhook ()

                      Expect.equal polledArg "<no-arg>" "the polling transport still routed the tool button's tap to its bound tool"
                      Expect.equal webhookArg "<no-arg>" "the webhook transport still routed the tool button's tap to its bound tool"
                  }
                  |> Async.AwaitTask
          } ]
