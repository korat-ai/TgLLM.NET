/// T034: both-transports acceptance (SC-008, FR-013). The polling path is proven end-to-end in
/// `FSharpPollingAcceptanceTests`; this exercises the webhook path end-to-end through the real
/// `MapTelegramWebhook` ASP.NET Core endpoint — an update delivered by HTTP POST runs the SAME hook
/// code and produces the same reply + ack. Also checks the secret-token gate (mismatch → 401) and
/// that `startWebhook` registered the webhook.
module TgLLM.Integration.Tests.BothTransportsTests

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
    | None -> failwithf "expected JSON field '%s'" key

let private at (i: int) (node: JsonNode) : JsonNode =
    match node.[i] |> Option.ofObj with
    | Some c -> c
    | None -> failwithf "expected JSON index %d" i

let private asString (node: JsonNode) : string = node.AsValue().GetValue<string>()
let private text (body: JsonNode) : string = body |> field "text" |> asString

let private callbackDataAt (row: int) (col: int) (sendBody: JsonNode) : string =
    sendBody |> field "reply_markup" |> field "inline_keyboard" |> at row |> at col |> field "callback_data" |> asString

let private awaitOrTimeout (ms: int) (t: Task) : Task =
    task {
        let! completed = Task.WhenAny(t, Task.Delay ms)
        if completed <> t then failtest "timed out waiting for the hook to run"
        do! t
    }

/// Hosts the webhook-receiving endpoint on a loopback port.
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

[<Tests>]
let bothTransportsTests =
    testList
        "BothTransports"
        [

          testCaseAsync "webhook delivery runs the same hook and replies (SC-008), and gates on the secret token"
          <| async {
              do!
                  task {
                      // Fake Bot API for outbound calls (setWebhook, sendMessage, answerCallbackQuery).
                      use! botApi = FakeBotApiServer.start ()
                      let secret = "s3cret-token"

                      let config =
                          TgWebhookConfig
                              .create("123456789:TEST-fake-token", "https://example.test/ignored", secret)
                              .WithBaseUrl(botApi.BaseUrl)

                      use! bot = TgBot.startWebhook config
                      use! host = startWebhookHost bot.WebhookSource secret
                      let webhookUrl = (Seq.head host.Urls).TrimEnd('/') + "/telegram/webhook"

                      let chat: int64 = 777L
                      let yesRan = TaskCompletionSource()

                      let keyboard =
                          Keyboard.create
                              [ [ Button.on "Yes" (fun ctx ->
                                      task {
                                          let! _ = ctx.ReplyTextAsync "You picked Yes"
                                          yesRan.TrySetResult() |> ignore
                                      })
                                  Button.on "No" (fun ctx -> ctx.ReplyTextAsync "You picked No") ] ]

                      match keyboard with
                      | Error e -> failtestf "keyboard should be valid: %A" e
                      | Ok spec ->
                          let! _ = bot.SendKeyboard(UMX.tag<chatId> chat, MessageText.unsafe "Deploy?", spec)

                          let sent = (botApi.RequestsFor "sendMessage").Head.Body |> Option.get
                          let yesToken = callbackDataAt 0 0 sent

                          let updateJson =
                              TelegramJson.callbackQueryUpdate 1 "q-yes" yesToken chat 50 900L "Web"

                          use http = new HttpClient()

                          // A mismatched secret token is rejected before any processing.
                          let! rejected = post http webhookUrl updateJson "wrong-secret"
                          Expect.equal (int rejected.StatusCode) 401 "mismatched secret ⇒ 401"

                          // The genuine delivery is accepted and runs the same hook.
                          let! accepted = post http webhookUrl updateJson secret
                          Expect.equal (int accepted.StatusCode) 200 "valid webhook ⇒ 200"

                          do! awaitOrTimeout 5000 yesRan.Task

                          let replies =
                              botApi.RequestsFor "sendMessage"
                              |> List.choose (fun r -> r.Body |> Option.map text)
                              |> List.filter (fun t -> t <> "Deploy?")

                          Expect.equal replies [ "You picked Yes" ] "the same Yes hook ran, delivered by webhook"
                          Expect.isNonEmpty (botApi.RequestsFor "answerCallbackQuery") "the tap was acknowledged over webhooks"
                          Expect.isNonEmpty (botApi.RequestsFor "setWebhook") "startWebhook registered the webhook"
                  }
                  |> Async.AwaitTask
          } ]
