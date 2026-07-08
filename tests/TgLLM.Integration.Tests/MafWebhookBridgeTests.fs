/// Behavioral (not just reflection-existence) acceptance for `Maf.startWebhook`/`startWebhookWith`
/// over the WEBHOOK transport: the same approve/resume/edit-in-place flow
/// `MafBridgeApprovalTests.fs` exercises over long polling, driven instead by a real HTTP POST to a
/// hosted webhook endpoint — mirrors `ToolRouterBothTransportsTests.fs`'s webhook-hosting pattern.
/// Both transports share the SAME mapping/dispatch machinery underneath, but this proves the MAF
/// bridge's own `OnMessage`/tool-registration wiring (`Maf.startWebhookWith`) actually works end to
/// end over webhooks, not merely that `MafTelegramBridge.StartWebhookAsync` exists.
module TgLLM.Integration.Tests.MafWebhookBridgeTests

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
open TgLLM.Maf
open TgLLM.Integration.Tests.FakeBotApiServer
open TgLLM.Integration.Tests.MafScriptedAgent

let private field (key: string) (node: JsonNode) : JsonNode =
    match node.[key] |> Option.ofObj with
    | Some c -> c
    | None -> failwithf "expected JSON field '%s' in %s" key (node.ToJsonString())

let private at (i: int) (node: JsonNode) : JsonNode =
    match node.[i] |> Option.ofObj with
    | Some c -> c
    | None -> failwithf "expected JSON index %d in %s" i (node.ToJsonString())

let private asString (node: JsonNode) : string = node.AsValue().GetValue<string>()

let private callbackDataAt (row: int) (col: int) (sendBody: JsonNode) : string =
    sendBody |> field "reply_markup" |> field "inline_keyboard" |> at row |> at col |> field "callback_data" |> asString

let private pollUntil (ms: int) (predicate: unit -> bool) : Task =
    task {
        let mutable tries = 0

        while not (predicate ()) && tries < ms / 10 do
            do! Task.Delay 10
            tries <- tries + 1

        if not (predicate ()) then
            failtest "timed out waiting for the expected request"
    }

/// Same pattern as `ToolRouterBothTransportsTests.fs`/`A2uiRenderTests.fs`'s own
/// `startWebhookHost` — hosts the webhook-receiving endpoint on a loopback port.
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
let mafWebhookBridgeTests =
    testList "MafBridge over webhooks" [

        testCaseAsync "the SAME approve/resume/edit-in-place flow works over the WEBHOOK transport, driven by a real HTTP POST"
        <| async {
            do!
                task {
                    use! botApi = FakeBotApiServer.start ()
                    let secret = "s3cret-maf-webhook"
                    let chat = 9701L

                    let resumes = ResizeArray<string * bool>()

                    let agent =
                        ScriptedAgent(
                            [ PausesFor("req-1", "send_email", [ "toAddr", box "alice@example.com" ])
                              RepliesWith "Email sent to alice@example.com." ],
                            onResume = (fun (reqId, approved) -> resumes.Add(reqId, approved))
                        )

                    let tools = ToolRegistry.create ()

                    let config =
                        TgWebhookConfig
                            .create("123456789:TEST-fake-token", "https://example.test/ignored", secret)
                            .WithBaseUrl(botApi.BaseUrl)
                            .WithTools(tools)

                    use! bridge = Maf.startWebhook config agent
                    use! host = startWebhookHost bridge.Bot.WebhookSource secret
                    let webhookUrl = (Seq.head host.Urls).TrimEnd('/') + "/telegram/webhook"
                    use http = new HttpClient()

                    do! bridge.StartRun(UMX.tag<chatId> chat, "Email alice that the deploy is done.")

                    Expect.equal (List.length (botApi.RequestsFor "sendMessage")) 1 "the pending approval sends exactly one message"
                    let sent = (botApi.RequestsFor "sendMessage").Head.Body |> Option.get
                    let approveToken = callbackDataAt 0 0 sent

                    let updateJson = TelegramJson.callbackQueryUpdate 1 "q-webhook-approve" approveToken chat 1 chat "Tester"
                    let! response = post http webhookUrl updateJson secret
                    Expect.equal (int response.StatusCode) 200 "the webhook delivery is accepted"

                    do! pollUntil 5000 (fun () -> botApi.RequestsFor "editMessageText" |> List.isEmpty |> not)

                    Expect.equal resumes.Count 1 "the agent was resumed exactly once, over the webhook transport"
                    Expect.equal resumes[0] ("req-1", true) "resumed with the tapped request id and Approved = true"

                    let editBody = (botApi.RequestsFor "editMessageText").Head.Body |> Option.get
                    let outcome = editBody |> field "text" |> asString
                    Expect.stringContains outcome "send_email" "the outcome mentions the tool"
                    Expect.stringContains outcome "approved" "the outcome says it was approved"
                }
                |> Async.AwaitTask
        }
    ]
