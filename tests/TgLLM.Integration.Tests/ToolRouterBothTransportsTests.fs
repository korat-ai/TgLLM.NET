/// T034 (Phase 8, closing SC-007 for the Tool Router): the SAME US1 tool-routing scenario — register
/// tools, send a plan naming them (with an arg), tap → the bound tool runs with its arg — run once
/// over long polling and once over webhooks, asserting IDENTICAL tool behavior across both
/// transports. Mirrors `BothTransportsTests.fs`'s webhook-hosting pattern and
/// `ToolRouterAcceptanceTests.fs`'s tool-routing assertions.
module TgLLM.Integration.Tests.ToolRouterBothTransportsTests

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

let private callbackDataAt (row: int) (col: int) (sendBody: JsonNode) : string =
    sendBody |> field "reply_markup" |> field "inline_keyboard" |> at row |> at col |> field "callback_data" |> asString

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

/// Runs the US1 tool-routing scenario over LONG POLLING and returns the arg the tool observed.
let private runOverPolling () : Task<string> =
    task {
        use! server = FakeBotApiServer.start ()
        let approveRan = TaskCompletionSource<string>()

        let tools =
            ToolRegistry
                .create()
                .Register("approve", (fun ctx -> task { approveRan.TrySetResult(ctx.Arg |> Option.ofObj |> Option.defaultValue "<no-arg>") |> ignore }))

        use! bot = TgBot.startPolling ((TgBotConfig.create "123456789:TEST-fake-token").WithBaseUrl(server.BaseUrl).WithTools tools)

        let plan = planOrFail [ [ Plan.toolWithArg "Approve" "approve" "over-polling" ] ]
        let! _ = bot.SendKeyboardPlan(UMX.tag<chatId> 810L, MessageText.unsafe "Deploy?", plan)
        let sentKeyboard = (server.RequestsFor "sendMessage").Head.Body |> Option.get
        let approveToken = callbackDataAt 0 0 sentKeyboard

        server.EnqueueResult(
            "getUpdates",
            TelegramJson.batch [ TelegramJson.callbackQueryUpdate 1 "q-polling" approveToken 810L 70 970L "Polly" ]
        )

        do! awaitOrTimeout 5000 (approveRan.Task :> Task)
        return approveRan.Task.Result
    }

/// Hosts the webhook-receiving endpoint on a loopback port (same pattern as `BothTransportsTests.fs`).
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

/// Runs the SAME US1 tool-routing scenario over WEBHOOKS and returns the arg the tool observed.
let private runOverWebhook () : Task<string> =
    task {
        use! botApi = FakeBotApiServer.start ()
        let secret = "s3cret-tool-router"
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

        let plan = planOrFail [ [ Plan.toolWithArg "Approve" "approve" "over-webhook" ] ]
        let! _ = bot.SendKeyboardPlan(UMX.tag<chatId> 811L, MessageText.unsafe "Deploy?", plan)
        let sentKeyboard = (botApi.RequestsFor "sendMessage").Head.Body |> Option.get
        let approveToken = callbackDataAt 0 0 sentKeyboard

        let updateJson = TelegramJson.callbackQueryUpdate 1 "q-webhook" approveToken 811L 71 971L "Webby"
        use http = new HttpClient()
        let! response = post http webhookUrl updateJson secret
        Expect.equal (int response.StatusCode) 200 "the webhook delivery is accepted"

        do! awaitOrTimeout 5000 (approveRan.Task :> Task)
        return approveRan.Task.Result
    }

[<Tests>]
let toolRouterBothTransportsTests =
    testList
        "ToolRouterBothTransports"
        [

          testCaseAsync "the US1 tool-routing scenario (bound tool + arg) behaves identically over long polling and webhooks (SC-007)"
          <| async {
              do!
                  task {
                      let! polledArg = runOverPolling ()
                      let! webhookArg = runOverWebhook ()

                      // Same tool code, same registry/binding-store/dispatch machinery on both
                      // sides — only the transport differs (FR-013); each scenario asserts its OWN
                      // transport routed the tap to the bound tool with exactly ITS OWN arg, which
                      // together is the "identical behavior across transports" this test closes.
                      Expect.equal polledArg "over-polling" "the polling transport routed to the bound tool with its own arg"
                      Expect.equal webhookArg "over-webhook" "the webhook transport routed to the bound tool with its own arg"
                  }
                  |> Async.AwaitTask
          } ]
