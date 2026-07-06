/// An agent sends a keyboard triggered by something OUTSIDE the chat — no preceding user message —
/// and its buttons route to hooks exactly as for a reply-sent keyboard.
module TgLLM.Integration.Tests.ProactiveSendTests

open System.Text.Json.Nodes
open System.Threading.Tasks
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
let private asInt64 (node: JsonNode) : int64 = node.AsValue().GetValue<int64>()

let private callbackDataAt (row: int) (col: int) (sendBody: JsonNode) : string =
    sendBody |> field "reply_markup" |> field "inline_keyboard" |> at row |> at col |> field "callback_data" |> asString

let private awaitOrTimeout (ms: int) (t: Task) : Task =
    task {
        let! completed = Task.WhenAny(t, Task.Delay ms)
        if completed <> t then failtest "timed out waiting for the hook to run"
        do! t
    }

[<Tests>]
let proactiveSendTests =
    testList
        "ProactiveSend"
        [

          testCaseAsync "an external trigger sends a keyboard to an arbitrary chat (no preceding message) and it routes"
          <| async {
              do!
                  task {
                      use! server = FakeBotApiServer.start ()

                      let config =
                          TgBotConfig.create("123456789:TEST-fake-token").WithBaseUrl server.BaseUrl

                      use! bot = TgBot.startPolling config

                      // No getUpdates results are ever enqueued before this send — the trigger is
                      // "external" (here: straight-line code standing in for a timer/external event).
                      let targetChat: int64 = 90210L
                      let pressed = TaskCompletionSource()

                      let keyboard =
                          Keyboard.create [ [ Button.on "Ping" (fun ctx -> task { pressed.TrySetResult() |> ignore }) ] ]

                      match keyboard with
                      | Error e -> failtestf "keyboard should be valid: %A" e
                      | Ok spec ->
                          let! _ = bot.SendKeyboard(UMX.tag<chatId> targetChat, MessageText.unsafe "Proactive!", spec)

                          // Delivered proactively to the arbitrary chat.
                          let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                          Expect.equal (sent |> field "chat_id" |> asInt64) targetChat "keyboard delivered to the target chat"

                          // A subsequent tap routes to the hook exactly as a reply-sent keyboard would.
                          let token = callbackDataAt 0 0 sent

                          server.EnqueueResult(
                              "getUpdates",
                              TelegramJson.batch [ TelegramJson.callbackQueryUpdate 1 "q" token targetChat 3 42L "Ext" ]
                          )

                          do! awaitOrTimeout 5000 pressed.Task
                          Expect.isNonEmpty (server.RequestsFor "answerCallbackQuery") "the proactive keyboard's button was acknowledged"
                  }
                  |> Async.AwaitTask
          } ]
