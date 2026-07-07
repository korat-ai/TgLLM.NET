/// Acceptance tests for `TgBot.SendKeyboardPlan`'s send-time `expiresIn`/`singleUse` options: they
/// stamp every tool binding the send produces with an `ExpiresAt`/`SingleUse` derived from the
/// bot's own clock, and an expired-by-send-time binding is refused like an unknown token — same
/// "ack, no invoke" contract `LifecycleTests.fs` already proves for a binding mutated directly in
/// the store, but exercised here through the public send-time knobs instead.
module TgLLM.Integration.Tests.SendKeyboardPlanExpirySingleUseTests

open System
open System.Threading
open System.Threading.Tasks
open System.Text.Json.Nodes
open Expecto
open FSharp.UMX
open TgLLM.Core
open TgLLM.FSharp
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

let private parseToken (tokenStr: string) : CallbackToken =
    match CallbackToken.tryParse tokenStr with
    | ValueSome t -> t
    | ValueNone -> failtest "the sent keyboard's callback_data must parse as a canonical token"

let private pollUntil (ms: int) (predicate: unit -> bool) : Task =
    task {
        let mutable tries = 0

        while not (predicate ()) && tries < ms / 10 do
            do! Task.Delay 10
            tries <- tries + 1

        if not (predicate ()) then failtest "timed out waiting for the expected condition"
    }

let private config (server: FakeBotApiServer) (tools: ToolRegistry) (store: IBindingStore) (clock: Clock) : TgBotConfig =
    (TgBotConfig.create "123456789:TEST-fake-token")
        .WithBaseUrl(server.BaseUrl)
        .WithTools(tools)
        .WithBindingStore(store)
        .WithClock(clock)

[<Tests>]
let sendKeyboardPlanExpirySingleUseTests =
    testList
        "TgBot.SendKeyboardPlan expiresIn/singleUse"
        [

          testCaseAsync "a keyboard sent with expiresIn stores bindings whose ExpiresAt is the bot's clock plus the given span"
          <| async {
              do!
                  task {
                      use! server = FakeBotApiServer.start ()
                      let store = InMemoryBindingStore() :> IBindingStore
                      let now = DateTimeOffset.UtcNow
                      let tools = ToolRegistry.create().Register("approve", fun _ -> task { return () })
                      use! bot = TgBot.startPolling (config server tools store (fun () -> now))

                      let plan = planOrFail [ [ Plan.tool "Approve" "approve" ] ]

                      let! _ =
                          bot.SendKeyboardPlan(
                              UMX.tag<chatId> 9101L,
                              MessageText.unsafe "Deploy?",
                              plan,
                              expiresIn = TimeSpan.FromMinutes 10.0
                          )

                      let sentKeyboard = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                      let token = parseToken (callbackDataAt 0 0 sentKeyboard)
                      let! stored = store.TryGet(token, CancellationToken.None)

                      match stored with
                      | ValueSome binding ->
                          Expect.equal binding.ExpiresAt (Some(now.AddMinutes 10.0)) "ExpiresAt is the bot's clock plus expiresIn"
                      | ValueNone -> failtest "the freshly-sent keyboard's binding must already be in the store"
                  }
                  |> Async.AwaitTask
          }

          testCaseAsync "a keyboard sent with singleUse = true stores bindings stamped as single-use"
          <| async {
              do!
                  task {
                      use! server = FakeBotApiServer.start ()
                      let store = InMemoryBindingStore() :> IBindingStore
                      let tools = ToolRegistry.create().Register("confirm", fun _ -> task { return () })
                      use! bot = TgBot.startPolling ((TgBotConfig.create "123456789:TEST-fake-token").WithBaseUrl(server.BaseUrl).WithTools(tools).WithBindingStore(store))

                      let plan = planOrFail [ [ Plan.tool "Confirm" "confirm" ] ]
                      let! _ = bot.SendKeyboardPlan(UMX.tag<chatId> 9102L, MessageText.unsafe "Confirm?", plan, singleUse = true)

                      let sentKeyboard = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                      let token = parseToken (callbackDataAt 0 0 sentKeyboard)
                      let! stored = store.TryGet(token, CancellationToken.None)

                      match stored with
                      | ValueSome binding -> Expect.isTrue binding.SingleUse "SingleUse is stamped onto every binding this send produces"
                      | ValueNone -> failtest "the freshly-sent keyboard's binding must already be in the store"
                  }
                  |> Async.AwaitTask
          }

          testCaseAsync "a tap on a keyboard sent with a short expiresIn is refused once the bot's clock has advanced past it"
          <| async {
              do!
                  task {
                      use! server = FakeBotApiServer.start ()
                      let store = InMemoryBindingStore() :> IBindingStore
                      let mutable now = DateTimeOffset.UtcNow
                      let toolRan = TaskCompletionSource()
                      let tools = ToolRegistry.create().Register("approve", fun _ -> task { toolRan.TrySetResult() |> ignore })
                      let chat = 9103L
                      use! bot = TgBot.startPolling (config server tools store (fun () -> now))

                      let plan = planOrFail [ [ Plan.tool "Approve" "approve" ] ]

                      let! _ =
                          bot.SendKeyboardPlan(
                              UMX.tag<chatId> chat,
                              MessageText.unsafe "Deploy?",
                              plan,
                              expiresIn = TimeSpan.FromSeconds 30.0
                          )

                      let sentKeyboard = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                      let tokenStr = callbackDataAt 0 0 sentKeyboard

                      // Advance the bot's own clock well past the binding's send-time expiry —
                      // deterministic, no real sleep needed.
                      now <- now.AddMinutes 5.0

                      server.EnqueueResult(
                          "getUpdates",
                          TelegramJson.batch [ TelegramJson.callbackQueryUpdate 1 "q-send-time-expiry" tokenStr chat 60 921L "Presser" ]
                      )

                      do! pollUntil 5000 (fun () -> server.RequestsFor "answerCallbackQuery" |> List.isEmpty |> not)

                      Expect.isFalse toolRan.Task.IsCompleted "a tap after the send-time expiry has elapsed must never run the tool"
                  }
                  |> Async.AwaitTask
          } ]
