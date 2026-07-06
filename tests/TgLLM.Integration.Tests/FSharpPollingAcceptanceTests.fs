/// F# façade acceptance over long polling. Drives the real façade end-to-end against the fake Bot
/// API server: send a keyboard, simulate a tap by feeding a `callback_query` carrying the button's
/// generated token, and assert the exact hook runs and replies, the tap is acknowledged, and the
/// ack is issued BEFORE the hook runs (never awaited behind it). A stale/unknown token is
/// acknowledged with no hook and no error.
module TgLLM.Integration.Tests.FSharpPollingAcceptanceTests

open System.Text.Json.Nodes
open System.Threading.Tasks
open Expecto
open FSharp.UMX
open TgLLM.Core
open TgLLM.FSharp
open TgLLM.Integration.Tests.FakeBotApiServer

// --- tiny non-null JSON navigation helpers (Nullable is enabled project-wide) ---
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
/// opaque token the library assigned to that button (the agent never sees it).
let private callbackDataAt (row: int) (col: int) (sendBody: JsonNode) : string =
    sendBody
    |> field "reply_markup"
    |> field "inline_keyboard"
    |> at row
    |> at col
    |> field "callback_data"
    |> asString

let private text (body: JsonNode) : string = body |> field "text" |> asString

let private awaitOrTimeout (ms: int) (t: Task) : Task =
    task {
        let! completed = Task.WhenAny(t, Task.Delay ms)
        if completed <> t then failtest "timed out waiting for the hook to run"
        do! t
    }

let private config (server: FakeBotApiServer) : TgBotConfig =
    TgBotConfig.create("123456789:TEST-fake-token").WithBaseUrl server.BaseUrl

[<Tests>]
let fSharpPollingAcceptanceTests =
    testList
        "FSharpPollingAcceptance"
        [

          testCaseAsync "tap runs the exact hook, replies, and the ack precedes the hook"
          <| async {
              do!
                  task {
                      use! server = FakeBotApiServer.start ()
                      use! bot = TgBot.startPolling (config server)

                      let chat: int64 = 555L
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

                          // The token the library minted for "Yes" (row 0, col 0 of the sent keyboard).
                          let sentKeyboard = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                          let yesToken = callbackDataAt 0 0 sentKeyboard

                          // Simulate the user tapping "Yes".
                          server.EnqueueResult(
                              "getUpdates",
                              TelegramJson.batch [ TelegramJson.callbackQueryUpdate 1 "q-yes" yesToken chat 99 777L "Bob" ]
                          )

                          do! awaitOrTimeout 5000 yesRan.Task

                          // The exact hook replied (and the "No" hook did not).
                          let replies =
                              server.RequestsFor "sendMessage"
                              |> List.choose (fun r -> r.Body |> Option.map text)
                              |> List.filter (fun t -> t <> "Deploy?")

                          Expect.equal replies [ "You picked Yes" ] "only the Yes hook replied"

                          Expect.isNonEmpty (server.RequestsFor "answerCallbackQuery") "the tap was acknowledged"

                          // Ack-first: the ack request was recorded before the hook's reply.
                          let ordered = server.Requests
                          let ackIndex = ordered |> List.tryFindIndex (fun r -> r.Method = "answerCallbackQuery")

                          let replyIndex =
                              ordered
                              |> List.tryFindIndex (fun r ->
                                  r.Method = "sendMessage"
                                  && (r.Body |> Option.map (fun b -> text b = "You picked Yes") |> Option.defaultValue false))

                          match ackIndex, replyIndex with
                          | Some a, Some rp -> Expect.isLessThan a rp "ack is issued before the hook's reply (ack-first)"
                          | _ -> failtest "expected both an ack and the hook's reply to be recorded"
                  }
                  |> Async.AwaitTask
          }

          testCaseAsync "a stale/unknown token is acknowledged with no hook and no error"
          <| async {
              do!
                  task {
                      use! server = FakeBotApiServer.start ()
                      use! bot = TgBot.startPolling (config server)

                      // A token that was never registered (e.g. a keyboard from before a restart).
                      let unknown = CallbackToken.value (CallbackToken.generate ())

                      server.EnqueueResult(
                          "getUpdates",
                          TelegramJson.batch [ TelegramJson.callbackQueryUpdate 1 "q-x" unknown 42L 5 111L "X" ]
                      )

                      let mutable tries = 0

                      while (server.RequestsFor "answerCallbackQuery").IsEmpty && tries < 300 do
                          do! Task.Delay 10
                          tries <- tries + 1

                      Expect.isNonEmpty (server.RequestsFor "answerCallbackQuery") "unknown press is still acknowledged"
                      Expect.isEmpty (server.RequestsFor "sendMessage") "no hook ran, so nothing was sent"
                  }
                  |> Async.AwaitTask
          } ]
