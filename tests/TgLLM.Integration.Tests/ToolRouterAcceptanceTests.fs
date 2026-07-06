/// T018: F# façade acceptance over long polling (US1 scenarios). Drives the real Tool Router façade
/// end-to-end against the fake Bot API server: register tools, send a plan naming them (with args),
/// simulate a tap, and assert the exact bound tool runs with its arg (SC-002); a plan naming an
/// unregistered tool is acked with no tool run (SC-005). Mirrors
/// `FSharpPollingAcceptanceTests.fs`'s structure for the slice-1 hook API.
module TgLLM.Integration.Tests.ToolRouterAcceptanceTests

open System.Text.Json.Nodes
open System.Threading.Tasks
open Expecto
open FSharp.UMX
open TgLLM.Core
open TgLLM.FSharp
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

/// `reply_markup.inline_keyboard[row][col].callback_data` of a recorded `sendMessage` body — the
/// opaque token the library assigned to that button (the agent never sees it; FR-011).
let private callbackDataAt (row: int) (col: int) (sendBody: JsonNode) : string =
    sendBody
    |> field "reply_markup"
    |> field "inline_keyboard"
    |> at row
    |> at col
    |> field "callback_data"
    |> asString

let private awaitOrTimeout (ms: int) (t: Task) : Task =
    task {
        let! completed = Task.WhenAny(t, Task.Delay ms)
        if completed <> t then failtest "timed out waiting for the tool to run"
        do! t
    }

let private config (server: FakeBotApiServer) (tools: ToolRegistry) : TgBotConfig =
    (TgBotConfig.create "123456789:TEST-fake-token").WithBaseUrl(server.BaseUrl).WithTools tools

[<Tests>]
let toolRouterAcceptanceTests =
    testList
        "ToolRouterAcceptance"
        [

          testCaseAsync "a plan-sent tool button runs the exact bound tool with its arg (SC-002)"
          <| async {
              do!
                  task {
                      use! server = FakeBotApiServer.start ()
                      let approveRan = TaskCompletionSource<string>()

                      let tools =
                          ToolRegistry
                              .create()
                              .Register(
                                  "approve",
                                  (fun ctx ->
                                      task { approveRan.TrySetResult(ctx.Arg |> Option.ofObj |> Option.defaultValue "<no-arg>") |> ignore })
                              )
                              .Register("reject", (fun _ -> task { return () }))

                      use! bot = TgBot.startPolling (config server tools)

                      let plan =
                          Plan.rows [ [ Plan.toolWithArg "Approve" "approve" "42"; Plan.tool "Reject" "reject" ] ]

                      match plan with
                      | Error e -> failtestf "plan should be valid: %A" e
                      | Ok p ->
                          let! _ = bot.SendKeyboardPlan(UMX.tag<chatId> 555L, MessageText.unsafe "Deploy?", p)

                          let sentKeyboard = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                          let approveToken = callbackDataAt 0 0 sentKeyboard

                          server.EnqueueResult(
                              "getUpdates",
                              TelegramJson.batch [ TelegramJson.callbackQueryUpdate 1 "q-approve" approveToken 555L 99 777L "Bob" ]
                          )

                          do! awaitOrTimeout 5000 (approveRan.Task :> Task)

                          Expect.equal approveRan.Task.Result "42" "the bound tool ran with its bound arg"
                  }
                  |> Async.AwaitTask
          }

          testCaseAsync "a plan naming an unregistered tool is acked, and no tool runs (SC-005)"
          <| async {
              do!
                  task {
                      use! server = FakeBotApiServer.start ()
                      let mutable anyToolRan = false

                      let tools =
                          ToolRegistry.create().Register("approve", (fun _ -> task { anyToolRan <- true }))

                      use! bot = TgBot.startPolling (config server tools)

                      // "ghost" is never registered.
                      let plan = Plan.rows [ [ Plan.tool "Ghost" "ghost" ] ]

                      match plan with
                      | Error e -> failtestf "plan should be valid: %A" e
                      | Ok p ->
                          let! _ = bot.SendKeyboardPlan(UMX.tag<chatId> 556L, MessageText.unsafe "Run?", p)

                          let sentKeyboard = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                          let ghostToken = callbackDataAt 0 0 sentKeyboard

                          server.EnqueueResult(
                              "getUpdates",
                              TelegramJson.batch [ TelegramJson.callbackQueryUpdate 1 "q-ghost" ghostToken 556L 100 778L "Eve" ]
                          )

                          let mutable tries = 0
                          while (server.RequestsFor "answerCallbackQuery").IsEmpty && tries < 300 do
                              do! Task.Delay 10
                              tries <- tries + 1

                          Expect.isNonEmpty (server.RequestsFor "answerCallbackQuery") "the unresolvable press is still acked (FR-010)"
                          Expect.isFalse anyToolRan "no tool ran for the unregistered name"
                  }
                  |> Async.AwaitTask
          } ]
