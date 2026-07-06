/// T023/T024: US2 (edit the pressed message in place + toast/alert) exercised end-to-end against the
/// fake Bot API server, mirroring `ToolRouterAcceptanceTests.fs`'s structure. T023: a tool that edits
/// the pressed message's text and replaces its keyboard changes the SAME message (no new `sendMessage`,
/// SC-003); a tool that calls `Answer(text, alert)` makes `answerCallbackQuery` carry that text/alert.
/// T024: editing a vanished message (the fake server returns `"message to edit not found"`) is caught
/// and surfaced via the observer (here: the `ILogger` `LoggingHookObserver` bridges to) without
/// crashing the bot — proven by the ack STILL happening afterwards.
module TgLLM.Integration.Tests.EditInPlaceTests

open System
open System.Text.Json.Nodes
open System.Threading.Tasks
open Microsoft.Extensions.Logging
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
let private asInt64 (node: JsonNode) : int64 = node.AsValue().GetValue<int64>()
let private asBool (node: JsonNode) : bool = node.AsValue().GetValue<bool>()

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

let private pollUntil (ms: int) (predicate: unit -> bool) : Task =
    task {
        let mutable tries = 0

        while not (predicate ()) && tries < ms / 10 do
            do! Task.Delay 10
            tries <- tries + 1

        if not (predicate ()) then failtest "timed out waiting for the expected request"
    }

let private config (server: FakeBotApiServer) (tools: ToolRegistry) : TgBotConfig =
    (TgBotConfig.create "123456789:TEST-fake-token").WithBaseUrl(server.BaseUrl).WithTools tools

/// A minimal `ILogger` fake that records `LogError` calls, so a test can observe
/// `LoggingHookObserver.OnHookFailed` firing (T024) without a dedicated `IHookObserver` seam on the
/// public façade — `TgBotConfig.WithLogger` is the only publicly wired way to plug in an observer.
type private NoopScope() =
    interface IDisposable with
        member _.Dispose() : unit = ()

type private RecordingLogger() =
    let errors = ResizeArray<exn>()
    member _.Errors: exn list = List.ofSeq errors

    interface ILogger with
        member _.BeginScope<'TState when 'TState: not null>(_state: 'TState) : IDisposable = new NoopScope()

        member _.IsEnabled(_logLevel: LogLevel) : bool = true

        member _.Log<'TState>
            (
                logLevel: LogLevel,
                _eventId: EventId,
                _state: 'TState,
                error: exn | null,
                _formatter: Func<'TState, exn | null, string>
            ) : unit =
            if logLevel = LogLevel.Error then
                error |> Option.ofObj |> Option.iter errors.Add

[<Tests>]
let editInPlaceTests =
    testList
        "EditInPlace"
        [

          testCaseAsync "a tapped tool edits the pressed message in place (text + keyboard); no new message is sent (SC-003)"
          <| async {
              do!
                  task {
                      use! server = FakeBotApiServer.start ()
                      let editRan = TaskCompletionSource<string>()

                      let tools =
                          ToolRegistry
                              .create()
                              .Register(
                                  "approve",
                                  fun ctx ->
                                      task {
                                          do! ctx.EditTextAsync "Approved!"

                                          match Plan.rows [ [ Plan.tool "Undo" "undo" ] ] with
                                          | Error e -> failtestf "replacement plan should be valid: %A" e
                                          | Ok replacementPlan -> do! ctx.EditKeyboardAsync replacementPlan

                                          editRan.TrySetResult "done" |> ignore
                                      }
                              )

                      use! bot = TgBot.startPolling (config server tools)

                      match Plan.rows [ [ Plan.tool "Approve" "approve" ] ] with
                      | Error e -> failtestf "plan should be valid: %A" e
                      | Ok plan ->
                          let! _ = bot.SendKeyboardPlan(UMX.tag<chatId> 601L, MessageText.unsafe "Deploy?", plan)

                          let sentKeyboard = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                          let approveToken = callbackDataAt 0 0 sentKeyboard

                          server.EnqueueResult(
                              "getUpdates",
                              TelegramJson.batch [ TelegramJson.callbackQueryUpdate 1 "q-approve" approveToken 601L 42 900L "Al" ]
                          )

                          do! awaitOrTimeout 5000 (editRan.Task :> Task)

                          match server.RequestsFor "editMessageText" with
                          | [ request ] ->
                              let body = request.Body |> Option.get
                              Expect.equal (body |> field "chat_id" |> asInt64) 601L "editMessageText targets the SAME chat"
                              Expect.equal (body |> field "message_id" |> asInt64) 42L "editMessageText targets the SAME (pressed) message"
                              Expect.equal (body |> field "text" |> asString) "Approved!" "the new text reached the wire"
                          | other -> failwithf "expected exactly one editMessageText call, got %d" (List.length other)

                          match server.RequestsFor "editMessageReplyMarkup" with
                          | [ request ] ->
                              let body = request.Body |> Option.get
                              Expect.equal (body |> field "chat_id" |> asInt64) 601L "editMessageReplyMarkup targets the SAME chat"
                              Expect.equal (body |> field "message_id" |> asInt64) 42L "editMessageReplyMarkup targets the SAME (pressed) message"

                              let replacementButtonText =
                                  body |> field "reply_markup" |> field "inline_keyboard" |> at 0 |> at 0 |> field "text" |> asString

                              Expect.equal replacementButtonText "Undo" "the replacement keyboard reached the wire"
                          | other -> failwithf "expected exactly one editMessageReplyMarkup call, got %d" (List.length other)

                          Expect.equal
                              (List.length (server.RequestsFor "sendMessage"))
                              1
                              "editing in place sent NO new message — only the original SendKeyboardPlan sendMessage (SC-003)"
                  }
                  |> Async.AwaitTask
          }

          testCaseAsync "a tapped tool that calls Answer(text, alert) makes answerCallbackQuery carry the text/show_alert"
          <| async {
              do!
                  task {
                      use! server = FakeBotApiServer.start ()

                      let tools =
                          ToolRegistry
                              .create()
                              .Register("reject", (fun ctx -> task { ctx.Answer("Rejected", alert = true) }))

                      use! bot = TgBot.startPolling (config server tools)

                      match Plan.rows [ [ Plan.tool "Reject" "reject" ] ] with
                      | Error e -> failtestf "plan should be valid: %A" e
                      | Ok plan ->
                          let! _ = bot.SendKeyboardPlan(UMX.tag<chatId> 602L, MessageText.unsafe "Deploy?", plan)

                          let sentKeyboard = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                          let rejectToken = callbackDataAt 0 0 sentKeyboard

                          server.EnqueueResult(
                              "getUpdates",
                              TelegramJson.batch [ TelegramJson.callbackQueryUpdate 1 "q-reject" rejectToken 602L 43 901L "Bo" ]
                          )

                          do! pollUntil 5000 (fun () -> server.RequestsFor "answerCallbackQuery" |> List.isEmpty |> not)

                          match server.RequestsFor "answerCallbackQuery" with
                          | [ request ] ->
                              let body = request.Body |> Option.get
                              Expect.equal (body |> field "text" |> asString) "Rejected" "the tool's ack text reached the wire"
                              Expect.equal (body |> field "show_alert" |> asBool) true "the tool's alert flag reached the wire"
                          | other -> failwithf "expected exactly one answerCallbackQuery call, got %d" (List.length other)
                  }
                  |> Async.AwaitTask
          }

          testCaseAsync "editing a vanished message is caught, surfaced via the observer, and does not crash the bot"
          <| async {
              do!
                  task {
                      use! server = FakeBotApiServer.start ()
                      let logger = RecordingLogger()

                      let tools =
                          ToolRegistry
                              .create()
                              .Register("approve", (fun ctx -> task { do! ctx.EditTextAsync "too late" }))

                      server.EnqueueError("editMessageText", 400, "Bad Request: message to edit not found")

                      use! bot = TgBot.startPolling ((config server tools).WithLogger logger)

                      match Plan.rows [ [ Plan.tool "Approve" "approve" ] ] with
                      | Error e -> failtestf "plan should be valid: %A" e
                      | Ok plan ->
                          let! _ = bot.SendKeyboardPlan(UMX.tag<chatId> 603L, MessageText.unsafe "Deploy?", plan)

                          let sentKeyboard = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                          let approveToken = callbackDataAt 0 0 sentKeyboard

                          server.EnqueueResult(
                              "getUpdates",
                              TelegramJson.batch [ TelegramJson.callbackQueryUpdate 1 "q-vanished" approveToken 603L 44 902L "Cy" ]
                          )

                          // The tool's own edit attempt fails and propagates; the processor's ambient
                          // try/with (buildToolWork) still sends exactly one ack afterwards — the
                          // system-level proof that this "crashed" nothing.
                          do! pollUntil 5000 (fun () -> server.RequestsFor "answerCallbackQuery" |> List.isEmpty |> not)

                          Expect.isNonEmpty logger.Errors "the edit failure was surfaced via the observer (no silent swallow)"

                          Expect.isNonEmpty
                              (server.RequestsFor "answerCallbackQuery")
                              "the press was still acknowledged after the edit failure — the bot did not crash"
                  }
                  |> Async.AwaitTask
          } ]
