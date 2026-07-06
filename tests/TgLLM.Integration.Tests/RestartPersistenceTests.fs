/// Bindings survive a restart, exercised end-to-end — send a plan through a bot backed by
/// a `FileBindingStore`, simulate a restart (dispose that bot; build a BRAND NEW bot + processor with
/// a NEW `FileBindingStore` over the SAME file + freshly re-registered tools), then tap a
/// PRE-restart button: the bound tool still runs. Also covers the edge case: a tap whose
/// tool is no longer registered after the restart is still acked and surfaced, no crash. Mirrors
/// `ToolRouterAcceptanceTests.fs`'s / `EditInPlaceTests.fs`'s structure.
module TgLLM.Integration.Tests.RestartPersistenceTests

open System
open System.IO
open System.Text.Json.Nodes
open System.Threading.Tasks
open Expecto
open FSharp.UMX
open TgLLM.Core
open TgLLM.FSharp
open TgLLM.Persistence
open TgLLM.Integration.Tests.FakeBotApiServer

let private field (key: string) (node: JsonNode) : JsonNode =
    match node.[key] |> Option.ofObj with
    | Some child -> child
    | None -> failwithf "expected JSON field '%s' in %s" key (node.ToJsonString())

let private at (i: int) (node: JsonNode) : JsonNode =
    match node.[i] |> Option.ofObj with
    | Some child -> child
    | None -> failwithf "expected JSON index %d in %s" i (node.ToJsonString())

let private asString (node: JsonNode) : string = node.AsValue().GetValue<string>()

let private callbackDataAt (row: int) (col: int) (sendBody: JsonNode) : string =
    sendBody
    |> field "reply_markup"
    |> field "inline_keyboard"
    |> at row
    |> at col
    |> field "callback_data"
    |> asString

let private tempPath () : string =
    Path.Combine(Path.GetTempPath(), $"tgllm-tool-router-restart-tests-{Guid.NewGuid()}.json")

/// `Plan.rows` is a pure, synchronous validation of the neutral keyboard plan — no
/// `task`/`async` involved — so a fail-fast unwrap here is an ordinary (non-monadic) `let`, keeping
/// the async `SendKeyboardPlan` call that follows a clean `let!` with no CE-desugaring ambiguity.
let private planOrFail (rows: PlanButton list list) : ToolKeyboard =
    match Plan.rows rows with
    | Ok p -> p
    | Error e -> failtestf "plan should be valid: %A" e

let private awaitOrTimeout (ms: int) (t: Task) : Task =
    task {
        let! completed = Task.WhenAny(t, Task.Delay ms)
        if completed <> t then failtest "timed out waiting for the tool to run"
        do! t
    }

let private pollUntil (ms: int) (predicate: unit -> bool) : Task =
    task {
        let mutable tries = 0

        while not (predicate ()) && tries < ms / 10 do
            do! Task.Delay 10
            tries <- tries + 1

        if not (predicate ()) then failtest "timed out waiting for the expected request"
    }

let private config (server: FakeBotApiServer) (tools: ToolRegistry) (store: IBindingStore) : TgBotConfig =
    (TgBotConfig.create "123456789:TEST-fake-token")
        .WithBaseUrl(server.BaseUrl)
        .WithTools(tools)
        .WithBindingStore(store)

[<Tests>]
let restartPersistenceTests =
    testList
        "RestartPersistence"
        [

          testCaseAsync "a pre-restart button still routes to the (freshly re-registered) bound tool after a restart"
          <| async {
              do!
                  task {
                      let path = tempPath ()

                      try
                          use! server = FakeBotApiServer.start ()

                          // --- Before the restart: send a plan through a FileBindingStore-backed bot. ---
                          let store1 = FileBindingStore.openAt path :> IBindingStore
                          let tools1 = ToolRegistry.create().Register("approve", (fun _ -> task { return () }))
                          let! bot1 = TgBot.startPolling (config server tools1 store1)
                          let plan = planOrFail [ [ Plan.toolWithArg "Approve" "approve" "7" ] ]
                          let! _ = bot1.SendKeyboardPlan(UMX.tag<chatId> 701L, MessageText.unsafe "Deploy?", plan)
                          let sentKeyboard = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                          let approveToken = callbackDataAt 0 0 sentKeyboard

                          do! (bot1 :> IAsyncDisposable).DisposeAsync().AsTask()

                          // --- Simulate a restart: a BRAND NEW store instance over the SAME file, and a
                          // BRAND NEW tool registry with "approve" re-registered fresh (the process
                          // restarted — nothing but the file connects the two halves of this test). ---
                          let store2 = FileBindingStore.openAt path :> IBindingStore
                          let approveRan = TaskCompletionSource<string>()

                          let tools2 =
                              ToolRegistry
                                  .create()
                                  .Register(
                                      "approve",
                                      fun ctx ->
                                          task { approveRan.TrySetResult(ctx.Arg |> Option.ofObj |> Option.defaultValue "<no-arg>") |> ignore }
                                  )

                          use! bot2 = TgBot.startPolling (config server tools2 store2)

                          server.EnqueueResult(
                              "getUpdates",
                              TelegramJson.batch [ TelegramJson.callbackQueryUpdate 1 "q-restart" approveToken 701L 55 950L "Restarted" ]
                          )

                          do! awaitOrTimeout 5000 (approveRan.Task :> Task)

                          Expect.equal approveRan.Task.Result "7" "the pre-restart binding (token -> approve + arg) survived and ran post-restart"
                      finally
                          File.Delete path
                  }
                  |> Async.AwaitTask
          }

          testCaseAsync "a pre-restart button whose tool is no longer registered after the restart is still acked and surfaced"
          <| async {
              do!
                  task {
                      let path = tempPath ()

                      try
                          use! server = FakeBotApiServer.start ()

                          let store1 = FileBindingStore.openAt path :> IBindingStore
                          let tools1 = ToolRegistry.create().Register("retired", (fun _ -> task { return () }))
                          let! bot1 = TgBot.startPolling (config server tools1 store1)
                          let plan = planOrFail [ [ Plan.tool "Retired" "retired" ] ]
                          let! _ = bot1.SendKeyboardPlan(UMX.tag<chatId> 702L, MessageText.unsafe "Deploy?", plan)
                          let sentKeyboard = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                          let retiredToken = callbackDataAt 0 0 sentKeyboard

                          do! (bot1 :> IAsyncDisposable).DisposeAsync().AsTask()

                          // Post-restart: the binding is still on disk, but "retired" is NOT re-registered.
                          let store2 = FileBindingStore.openAt path :> IBindingStore
                          let tools2 = ToolRegistry.create().Register("something-else", (fun _ -> task { return () }))
                          use! bot2 = TgBot.startPolling (config server tools2 store2)

                          server.EnqueueResult(
                              "getUpdates",
                              TelegramJson.batch [ TelegramJson.callbackQueryUpdate 1 "q-retired" retiredToken 702L 56 951L "Restarted" ]
                          )

                          do! pollUntil 5000 (fun () -> server.RequestsFor "answerCallbackQuery" |> List.isEmpty |> not)

                          Expect.isNonEmpty
                              (server.RequestsFor "answerCallbackQuery")
                              "the tap on a binding whose tool vanished across the restart is still acknowledged, no crash"
                      finally
                          File.Delete path
                  }
                  |> Async.AwaitTask
          } ]
