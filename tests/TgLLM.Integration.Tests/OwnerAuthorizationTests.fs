/// Acceptance tests for press authorization (owner scoping): an owner-scoped keyboard refuses a
/// non-owner's tap (acked, no tool runs) and runs the tool for the owner — identically over long
/// polling and webhooks, and identically after a restart with a durable (`FileBindingStore`)
/// binding store. Mirrors `ToolRouterBothTransportsTests.fs`'s both-transports structure and
/// `RestartPersistenceTests.fs`'s restart structure.
module TgLLM.Integration.Tests.OwnerAuthorizationTests

open System
open System.IO
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
open TgLLM.Persistence
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

let private planOrFail (rows: PlanButton list list) : ToolKeyboard =
    match Plan.rows rows with
    | Ok p -> p
    | Error e -> failtestf "plan should be valid: %A" e

let private pollUntil (ms: int) (predicate: unit -> bool) : Task =
    task {
        let mutable tries = 0

        while not (predicate ()) && tries < ms / 10 do
            do! Task.Delay 10
            tries <- tries + 1

        if not (predicate ()) then failtest "timed out waiting for the expected request"
    }

let private awaitOrTimeout (ms: int) (t: Task) : Task =
    task {
        let! completed = Task.WhenAny(t, Task.Delay ms)
        if completed <> t then failtest "timed out waiting for the tool to run"
        do! t
    }

/// The reusable scenario, shared by the long-polling and webhook scenarios below (only how the
/// update REACHES the processor differs): the caller registers the tool up front (so `approveRan`
/// is captured correctly), then this sends an owner-scoped plan, delivers a non-owner tap followed
/// by the owner's own tap, and returns the user id the tool observed (must equal `ownerId`).
let private ownerAuthorizationScenario
    (server: FakeBotApiServer)
    (bot: TgBot)
    (chat: int64)
    (ownerId: int64)
    (nonOwnerId: int64)
    (approveRan: TaskCompletionSource<int64>)
    (deliverUpdate: string -> Task)
    : Task<int64> =
    task {
        let plan = planOrFail [ [ Plan.tool "Approve" "approve" ] ]
        let! _ = bot.SendKeyboardPlan(UMX.tag<chatId> chat, MessageText.unsafe "Deploy?", plan, owner = Owner.user ownerId)
        let sentKeyboard = (server.RequestsFor "sendMessage").Head.Body |> Option.get
        let token = callbackDataAt 0 0 sentKeyboard

        do! deliverUpdate (TelegramJson.callbackQueryUpdate 1 "q-nonowner" token chat 20 nonOwnerId "Mallory")
        do! pollUntil 5000 (fun () -> server.RequestsFor "answerCallbackQuery" |> List.isEmpty |> not)
        Expect.isFalse approveRan.Task.IsCompleted "the non-owner's tap must never run the tool"

        do! deliverUpdate (TelegramJson.callbackQueryUpdate 2 "q-owner" token chat 21 ownerId "Owner")
        do! awaitOrTimeout 5000 (approveRan.Task :> Task)
        return approveRan.Task.Result
    }

/// Runs the owner-authorization scenario over LONG POLLING.
let private runOverPolling () : Task<int64> =
    task {
        use! server = FakeBotApiServer.start ()
        let approveRan = TaskCompletionSource<int64>()

        let tools =
            ToolRegistry
                .create()
                .Register("approve", (fun ctx -> task { approveRan.TrySetResult(int64 ctx.User.Id) |> ignore }))

        use! bot = TgBot.startPolling ((TgBotConfig.create "123456789:TEST-fake-token").WithBaseUrl(server.BaseUrl).WithTools tools)

        let deliver (updateJson: string) : Task =
            server.EnqueueResult("getUpdates", TelegramJson.batch [ updateJson ])
            Task.CompletedTask

        return! ownerAuthorizationScenario server bot 601L 501L 502L approveRan deliver
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

/// Runs the SAME owner-authorization scenario over WEBHOOKS.
let private runOverWebhook () : Task<int64> =
    task {
        use! server = FakeBotApiServer.start ()
        let secret = "s3cret-owner-auth"
        let approveRan = TaskCompletionSource<int64>()

        let tools =
            ToolRegistry
                .create()
                .Register("approve", (fun ctx -> task { approveRan.TrySetResult(int64 ctx.User.Id) |> ignore }))

        let config =
            TgWebhookConfig
                .create("123456789:TEST-fake-token", "https://example.test/ignored", secret)
                .WithBaseUrl(server.BaseUrl)
                .WithTools(tools)

        use! bot = TgBot.startWebhook config
        use! host = startWebhookHost bot.WebhookSource secret
        let webhookUrl = (Seq.head host.Urls).TrimEnd('/') + "/telegram/webhook"

        let deliver (updateJson: string) : Task =
            task {
                use http = new HttpClient()
                let! response = post http webhookUrl updateJson secret
                Expect.equal (int response.StatusCode) 200 "the webhook delivery is accepted"
            }

        return! ownerAuthorizationScenario server bot 602L 511L 512L approveRan deliver
    }

let private tempPath () : string =
    Path.Combine(Path.GetTempPath(), $"tgllm-owner-auth-restart-tests-{Guid.NewGuid()}.json")

let private config (server: FakeBotApiServer) (tools: ToolRegistry) (store: IBindingStore) : TgBotConfig =
    (TgBotConfig.create "123456789:TEST-fake-token")
        .WithBaseUrl(server.BaseUrl)
        .WithTools(tools)
        .WithBindingStore(store)

[<Tests>]
let ownerAuthorizationTests =
    testList
        "OwnerAuthorization"
        [

          testCaseAsync "an owner-scoped keyboard refuses a non-owner and runs for the owner, identically over long polling and webhooks"
          <| async {
              do!
                  task {
                      let! polledOwner = runOverPolling ()
                      let! webhookOwner = runOverWebhook ()

                      Expect.equal polledOwner 501L "the polling transport ran the tool only for the configured owner"
                      Expect.equal webhookOwner 511L "the webhook transport ran the tool only for the configured owner"
                  }
                  |> Async.AwaitTask
          }

          testCaseAsync "owner scoping survives a restart with a durable (FileBindingStore) binding store"
          <| async {
              do!
                  task {
                      let path = tempPath ()

                      try
                          use! server = FakeBotApiServer.start ()
                          let store1 = FileBindingStore.openAt path :> IBindingStore
                          let ownerId = 701L
                          let nonOwnerId = 702L
                          let chat = 603L

                          let tools1 = ToolRegistry.create().Register("approve", (fun _ -> task { return () }))
                          let! bot1 = TgBot.startPolling (config server tools1 store1)

                          let plan = planOrFail [ [ Plan.tool "Approve" "approve" ] ]

                          let! _ =
                              bot1.SendKeyboardPlan(UMX.tag<chatId> chat, MessageText.unsafe "Deploy?", plan, owner = Owner.user ownerId)

                          let sentKeyboard = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                          let token = callbackDataAt 0 0 sentKeyboard

                          do! (bot1 :> IAsyncDisposable).DisposeAsync().AsTask()

                          // --- Simulate a restart: a BRAND NEW store instance over the SAME file, and a
                          // BRAND NEW tool registry with "approve" re-registered fresh. Nothing but the
                          // file connects the two halves of this test. ---
                          let store2 = FileBindingStore.openAt path :> IBindingStore
                          let approveRan = TaskCompletionSource<int64>()

                          let tools2 =
                              ToolRegistry
                                  .create()
                                  .Register("approve", (fun ctx -> task { approveRan.TrySetResult(int64 ctx.User.Id) |> ignore }))

                          use! bot2 = TgBot.startPolling (config server tools2 store2)

                          // A non-owner taps the PRE-restart button: still refused (owner survived).
                          server.EnqueueResult(
                              "getUpdates",
                              TelegramJson.batch [ TelegramJson.callbackQueryUpdate 1 "q-restart-nonowner" token chat 30 nonOwnerId "Mallory" ]
                          )

                          do! pollUntil 5000 (fun () -> server.RequestsFor "answerCallbackQuery" |> List.isEmpty |> not)
                          Expect.isFalse approveRan.Task.IsCompleted "a non-owner's tap on a pre-restart button is still refused post-restart"

                          // The owner's tap on the SAME pre-restart button still runs the tool.
                          server.EnqueueResult(
                              "getUpdates",
                              TelegramJson.batch [ TelegramJson.callbackQueryUpdate 2 "q-restart-owner" token chat 31 ownerId "Owner" ]
                          )

                          do! awaitOrTimeout 5000 (approveRan.Task :> Task)
                          Expect.equal approveRan.Task.Result ownerId "the pre-restart owner scope still resolves correctly for the real owner"
                      finally
                          File.Delete path
                  }
                  |> Async.AwaitTask
          } ]
