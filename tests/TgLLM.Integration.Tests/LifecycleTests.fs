/// Acceptance tests for lifecycle & reliability: a binding whose expiry has lapsed is refused
/// like an unknown token; a single-use binding runs its tool once and is then unknown; a callback
/// query Telegram (or this library's own webhook retry) redelivers invokes the bound tool at most
/// once; and a keyboard sent before a restart still routes through a reopened `LiteDbBindingStore`.
/// Mirrors `OwnerAuthorizationTests.fs`'s both-transports-plus-restart structure.
module TgLLM.Integration.Tests.LifecycleTests

open System
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Logging
open Expecto
open FSharp.UMX
open TgLLM.Core
open TgLLM.FSharp
open TgLLM.AspNetCore
open TgLLM.Persistence.LiteDb
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

        if not (predicate ()) then failtest "timed out waiting for the expected condition"
    }

let private parseToken (tokenStr: string) : CallbackToken =
    match CallbackToken.tryParse tokenStr with
    | ValueSome t -> t
    | ValueNone -> failtest "the sent keyboard's callback_data must parse as a canonical token"

/// Hosts the webhook-receiving endpoint on a loopback port (same pattern as
/// `OwnerAuthorizationTests.fs`/`ToolRouterBothTransportsTests.fs`).
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

/// One reusable "deliver an update, however the transport gets it there" callback — polling
/// enqueues it as a canned `getUpdates` result; webhooks POST it to the mapped endpoint.
type private Deliver = string -> Task

let private pollingDeliver (server: FakeBotApiServer) : Deliver =
    fun updateJson ->
        server.EnqueueResult("getUpdates", TelegramJson.batch [ updateJson ])
        Task.CompletedTask

let private webhookDeliver (http: HttpClient) (webhookUrl: string) (secret: string) : Deliver =
    fun updateJson ->
        task {
            let! response = post http webhookUrl updateJson secret
            Expect.equal (int response.StatusCode) 200 "the webhook delivery is accepted"
        }

let private pollingConfig (server: FakeBotApiServer) (tools: ToolRegistry) (store: IBindingStore) : TgBotConfig =
    (TgBotConfig.create "123456789:TEST-fake-token").WithBaseUrl(server.BaseUrl).WithTools(tools).WithBindingStore store

let private webhookConfig (server: FakeBotApiServer) (tools: ToolRegistry) (store: IBindingStore) (secret: string) : TgWebhookConfig =
    TgWebhookConfig
        .create("123456789:TEST-fake-token", "https://example.test/ignored", secret)
        .WithBaseUrl(server.BaseUrl)
        .WithTools(tools)
        .WithBindingStore(store)

// --- Scenario 1: an expired binding is refused like an unknown token ---

/// Sends a tool keyboard, then overwrites its binding directly in `store` with an `ExpiresAt`
/// already in the past — a deterministic way to exercise "expired" without a real sleep: `bot`
/// itself was configured with `TgBotConfig.WithClock (fun () -> now)`, so the resolve step sees the
/// SAME "now" this test reasons about.
let private expiredBindingScenario
    (server: FakeBotApiServer)
    (store: IBindingStore)
    (now: DateTimeOffset)
    (chat: int64)
    (send: ToolKeyboard -> Task<MessageId>)
    (toolRan: TaskCompletionSource)
    (deliver: Deliver)
    : Task =
    task {
        let plan = planOrFail [ [ Plan.tool "Approve" "approve" ] ]
        let! _ = send plan
        let sentKeyboard = (server.RequestsFor "sendMessage").Head.Body |> Option.get
        let token = parseToken (callbackDataAt 0 0 sentKeyboard)

        let! existing = store.TryGet(token, CancellationToken.None)

        let expiredBinding =
            match existing with
            | ValueSome binding -> { binding with ExpiresAt = Some(now.AddMinutes -1.0) }
            | ValueNone -> failtest "the freshly-sent keyboard's binding must already be in the store"

        do! store.Save([ expiredBinding ], CancellationToken.None)

        do! deliver (TelegramJson.callbackQueryUpdate 1 "q-expired" (CallbackToken.value token) chat 40 901L "Presser")
        do! pollUntil 5000 (fun () -> server.RequestsFor "answerCallbackQuery" |> List.isEmpty |> not)

        Expect.isFalse toolRan.Task.IsCompleted "an expired binding must never run its tool"
    }

let private runExpiryOverPolling () : Task =
    task {
        use! server = FakeBotApiServer.start ()
        let store = InMemoryBindingStore() :> IBindingStore
        let now = DateTimeOffset.UtcNow
        let toolRan = TaskCompletionSource()
        let tools = ToolRegistry.create().Register("approve", fun _ -> task { toolRan.TrySetResult() |> ignore })
        use! bot = TgBot.startPolling ((pollingConfig server tools store).WithClock(fun () -> now))

        do!
            expiredBindingScenario
                server
                store
                now
                801L
                (fun plan -> bot.SendKeyboardPlan(UMX.tag<chatId> 801L, MessageText.unsafe "Deploy?", plan))
                toolRan
                (pollingDeliver server)
    }

let private runExpiryOverWebhook () : Task =
    task {
        use! server = FakeBotApiServer.start ()
        let store = InMemoryBindingStore() :> IBindingStore
        let now = DateTimeOffset.UtcNow
        let toolRan = TaskCompletionSource()
        let secret = "s3cret-expiry"
        let tools = ToolRegistry.create().Register("approve", fun _ -> task { toolRan.TrySetResult() |> ignore })
        use! bot = TgBot.startWebhook ((webhookConfig server tools store secret).WithClock(fun () -> now))
        use! host = startWebhookHost bot.WebhookSource secret
        let webhookUrl = (Seq.head host.Urls).TrimEnd('/') + "/telegram/webhook"
        use http = new HttpClient()

        do!
            expiredBindingScenario
                server
                store
                now
                802L
                (fun plan -> bot.SendKeyboardPlan(UMX.tag<chatId> 802L, MessageText.unsafe "Deploy?", plan))
                toolRan
                (webhookDeliver http webhookUrl secret)
    }

// --- Scenario 2: a single-use binding is consumed after its first successful tap ---

let private singleUseScenario
    (server: FakeBotApiServer)
    (store: IBindingStore)
    (chat: int64)
    (send: ToolKeyboard -> Task<MessageId>)
    (runCount: int ref)
    (deliver: Deliver)
    : Task =
    task {
        let plan = planOrFail [ [ Plan.tool "Confirm" "confirm" ] ]
        let! _ = send plan
        let sentKeyboard = (server.RequestsFor "sendMessage").Head.Body |> Option.get
        let token = parseToken (callbackDataAt 0 0 sentKeyboard)

        let! existing = store.TryGet(token, CancellationToken.None)

        let singleUseBinding =
            match existing with
            | ValueSome binding -> { binding with SingleUse = true }
            | ValueNone -> failtest "the freshly-sent keyboard's binding must already be in the store"

        do! store.Save([ singleUseBinding ], CancellationToken.None)
        let tokenStr = CallbackToken.value token

        do! deliver (TelegramJson.callbackQueryUpdate 1 "q-single-use-first" tokenStr chat 41 902L "Presser")
        do! pollUntil 5000 (fun () -> runCount.Value >= 1)
        Expect.equal runCount.Value 1 "the first tap ran the tool"

        // A SECOND, independent press (different query id) on the SAME token must now resolve as
        // unknown — the binding was consumed by the first successful tap.
        do! deliver (TelegramJson.callbackQueryUpdate 2 "q-single-use-second" tokenStr chat 42 902L "Presser")
        do! pollUntil 5000 (fun () -> (server.RequestsFor "answerCallbackQuery" |> List.length) >= 2)

        Expect.equal runCount.Value 1 "a second tap on a consumed single-use binding must not run the tool again"
    }

let private runSingleUseOverPolling () : Task =
    task {
        use! server = FakeBotApiServer.start ()
        let store = InMemoryBindingStore() :> IBindingStore
        let runCount = ref 0
        let tools = ToolRegistry.create().Register("confirm", fun _ -> task { runCount.Value <- runCount.Value + 1 })
        use! bot = TgBot.startPolling (pollingConfig server tools store)

        do!
            singleUseScenario
                server
                store
                811L
                (fun plan -> bot.SendKeyboardPlan(UMX.tag<chatId> 811L, MessageText.unsafe "Confirm?", plan))
                runCount
                (pollingDeliver server)
    }

let private runSingleUseOverWebhook () : Task =
    task {
        use! server = FakeBotApiServer.start ()
        let store = InMemoryBindingStore() :> IBindingStore
        let runCount = ref 0
        let secret = "s3cret-single-use"
        let tools = ToolRegistry.create().Register("confirm", fun _ -> task { runCount.Value <- runCount.Value + 1 })
        use! bot = TgBot.startWebhook (webhookConfig server tools store secret)
        use! host = startWebhookHost bot.WebhookSource secret
        let webhookUrl = (Seq.head host.Urls).TrimEnd('/') + "/telegram/webhook"
        use http = new HttpClient()

        do!
            singleUseScenario
                server
                store
                812L
                (fun plan -> bot.SendKeyboardPlan(UMX.tag<chatId> 812L, MessageText.unsafe "Confirm?", plan))
                runCount
                (webhookDeliver http webhookUrl secret)
    }

// --- Scenario 3: a redelivered callback query id invokes the bound tool at most once ---

let private redeliveryScenario
    (server: FakeBotApiServer)
    (chat: int64)
    (send: ToolKeyboard -> Task<MessageId>)
    (runCount: int ref)
    (deliver: Deliver)
    : Task =
    task {
        let plan = planOrFail [ [ Plan.tool "Approve" "approve" ] ]
        let! _ = send plan
        let sentKeyboard = (server.RequestsFor "sendMessage").Head.Body |> Option.get
        let tokenStr = callbackDataAt 0 0 sentKeyboard

        // The SAME callback query id, delivered twice — simulating Telegram (or this library's own
        // webhook transport under a client retry) redelivering the identical update.
        do! deliver (TelegramJson.callbackQueryUpdate 1 "q-redelivered" tokenStr chat 43 903L "Presser")
        do! pollUntil 5000 (fun () -> runCount.Value >= 1)
        do! deliver (TelegramJson.callbackQueryUpdate 2 "q-redelivered" tokenStr chat 43 903L "Presser")

        // Give the (would-be) second run a moment to happen if it were going to.
        do! Task.Delay 200
        Expect.equal runCount.Value 1 "the redelivered query id must not invoke the tool a second time"
    }

let private runRedeliveryOverPolling () : Task =
    task {
        use! server = FakeBotApiServer.start ()
        let store = InMemoryBindingStore() :> IBindingStore
        let runCount = ref 0
        let tools = ToolRegistry.create().Register("approve", fun _ -> task { runCount.Value <- runCount.Value + 1 })
        use! bot = TgBot.startPolling (pollingConfig server tools store)

        do!
            redeliveryScenario
                server
                821L
                (fun plan -> bot.SendKeyboardPlan(UMX.tag<chatId> 821L, MessageText.unsafe "Deploy?", plan))
                runCount
                (pollingDeliver server)
    }

let private runRedeliveryOverWebhook () : Task =
    task {
        use! server = FakeBotApiServer.start ()
        let store = InMemoryBindingStore() :> IBindingStore
        let runCount = ref 0
        let secret = "s3cret-redelivery"
        let tools = ToolRegistry.create().Register("approve", fun _ -> task { runCount.Value <- runCount.Value + 1 })
        use! bot = TgBot.startWebhook (webhookConfig server tools store secret)
        use! host = startWebhookHost bot.WebhookSource secret
        let webhookUrl = (Seq.head host.Urls).TrimEnd('/') + "/telegram/webhook"
        use http = new HttpClient()

        do!
            redeliveryScenario
                server
                822L
                (fun plan -> bot.SendKeyboardPlan(UMX.tag<chatId> 822L, MessageText.unsafe "Deploy?", plan))
                runCount
                (webhookDeliver http webhookUrl secret)
    }

// --- Scenario 4: LiteDB restart persistence — owner enforcement AND the tool itself survive ---

let private tempPath () : string =
    Path.Combine(Path.GetTempPath(), $"tgllm-lifecycle-litedb-restart-tests-{Guid.NewGuid()}.db")

[<Tests>]
let lifecycleTests =
    testList
        "Lifecycle"
        [

          testCaseAsync "an expired binding is refused like an unknown token — identically over long polling and webhooks"
          <| async {
              do!
                  task {
                      do! runExpiryOverPolling ()
                      do! runExpiryOverWebhook ()
                  }
                  |> Async.AwaitTask
          }

          testCaseAsync "a single-use binding runs its tool once; a second tap on it is treated as unknown — identically over long polling and webhooks"
          <| async {
              do!
                  task {
                      do! runSingleUseOverPolling ()
                      do! runSingleUseOverWebhook ()
                  }
                  |> Async.AwaitTask
          }

          testCaseAsync "a callback query redelivered with the same id invokes the bound tool at most once — identically over long polling and webhooks"
          <| async {
              do!
                  task {
                      do! runRedeliveryOverPolling ()
                      do! runRedeliveryOverWebhook ()
                  }
                  |> Async.AwaitTask
          }

          testCaseAsync "a keyboard sent before a simulated restart still routes through a reopened LiteDbBindingStore — owner enforcement and the tool itself both survive"
          <| async {
              do!
                  task {
                      let path = tempPath ()

                      try
                          use! server = FakeBotApiServer.start ()
                          let store1 = LiteDbBindingStore.OpenAt path :> IBindingStore
                          let ownerId = 911L
                          let nonOwnerId = 912L
                          let chat = 831L

                          let tools1 = ToolRegistry.create().Register("approve", fun _ -> task { return () })
                          let! bot1 = TgBot.startPolling (pollingConfig server tools1 store1)

                          let plan = planOrFail [ [ Plan.tool "Approve" "approve" ] ]
                          let! _ = bot1.SendKeyboardPlan(UMX.tag<chatId> chat, MessageText.unsafe "Deploy?", plan, owner = Owner.user ownerId)
                          let sentKeyboard = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                          let tokenStr = callbackDataAt 0 0 sentKeyboard

                          do! (bot1 :> IAsyncDisposable).DisposeAsync().AsTask()
                          (store1 :?> IDisposable).Dispose() // release the LiteDB file before reopening it

                          // --- Simulate a restart: a BRAND NEW LiteDbBindingStore instance over the SAME
                          // file, and a BRAND NEW tool registry with "approve" re-registered fresh. Nothing
                          // but the file connects the two halves of this test. ---
                          let store2 = LiteDbBindingStore.OpenAt path :> IBindingStore
                          let approveRan = TaskCompletionSource<int64>()

                          let tools2 =
                              ToolRegistry
                                  .create()
                                  .Register("approve", (fun ctx -> task { approveRan.TrySetResult(int64 ctx.User.Id) |> ignore }))

                          use! bot2 = TgBot.startPolling (pollingConfig server tools2 store2)

                          // A non-owner taps the PRE-restart button: still refused (owner survived).
                          server.EnqueueResult(
                              "getUpdates",
                              TelegramJson.batch [ TelegramJson.callbackQueryUpdate 1 "q-restart-nonowner" tokenStr chat 50 nonOwnerId "Mallory" ]
                          )

                          do! pollUntil 5000 (fun () -> server.RequestsFor "answerCallbackQuery" |> List.isEmpty |> not)
                          Expect.isFalse approveRan.Task.IsCompleted "a non-owner's tap on a pre-restart button is still refused post-restart"

                          // The owner's tap on the SAME pre-restart button still runs the tool.
                          server.EnqueueResult(
                              "getUpdates",
                              TelegramJson.batch [ TelegramJson.callbackQueryUpdate 2 "q-restart-owner" tokenStr chat 51 ownerId "Owner" ]
                          )

                          let! completed = Task.WhenAny(approveRan.Task, Task.Delay 5000)
                          Expect.isTrue (obj.ReferenceEquals(completed, approveRan.Task)) "timed out waiting for the tool to run"
                          Expect.equal approveRan.Task.Result ownerId "the pre-restart owner scope AND the tool itself both survive the LiteDB-backed restart"
                      finally
                          File.Delete path
                  }
                  |> Async.AwaitTask
          } ]
