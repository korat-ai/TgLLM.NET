/// Soft edit-in-place error handling (FR-015): a tool's `ctx.EditTextAsync`/`ctx.EditKeyboardAsync`
/// must never throw to the tool author when Telegram returns `"message is not modified"` (treated
/// as a successful no-op — the tool keeps running exactly as if the edit had applied) or
/// `"message to edit not found"` (surfaced as a soft, OBSERVED failure via `IHookObserver`, but the
/// tool STILL keeps running — no exception ever reaches it). Exercised against the real fake Bot
/// API server (`FakeBotApiServer.EnqueueError`), mirroring `EditInPlaceTests.fs`'s structure.
module TgLLM.Integration.Tests.EditErrorTests

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

let private callbackDataAt (row: int) (col: int) (sendBody: JsonNode) : string =
    sendBody
    |> field "reply_markup"
    |> field "inline_keyboard"
    |> at row
    |> at col
    |> field "callback_data"
    |> asString

let private pollUntil (ms: int) (predicate: unit -> bool) : Task =
    task {
        let mutable tries = 0

        while not (predicate ()) && tries < ms / 10 do
            do! Task.Delay 10
            tries <- tries + 1

        if not (predicate ()) then failtest "timed out waiting for the expected condition"
    }

let private config (server: FakeBotApiServer) (tools: ToolRegistry) : TgBotConfig =
    (TgBotConfig.create "123456789:TEST-fake-token").WithBaseUrl(server.BaseUrl).WithTools tools

/// A minimal `ILogger` fake that records `LogError`/`LogWarning` calls separately, so a test can
/// tell a genuine failure (`OnHookFailed`, bridged to `LogError`) apart from a soft, observed one
/// (`OnEditFailed`, bridged to `LogWarning`) without a dedicated `IHookObserver` seam on the public
/// façade — `TgBotConfig.WithLogger` is the only publicly wired way to plug in an observer.
type private NoopScope() =
    interface IDisposable with
        member _.Dispose() : unit = ()

type private RecordingLogger() =
    let errors = ResizeArray<string>()
    let warnings = ResizeArray<string>()
    member _.Errors: string list = List.ofSeq errors
    member _.Warnings: string list = List.ofSeq warnings

    interface ILogger with
        member _.BeginScope<'TState when 'TState: not null>(_state: 'TState) : IDisposable = new NoopScope()

        member _.IsEnabled(_logLevel: LogLevel) : bool = true

        member _.Log<'TState>
            (
                logLevel: LogLevel,
                _eventId: EventId,
                state: 'TState,
                _error: exn | null,
                formatter: Func<'TState, exn | null, string>
            ) : unit =
            let message = formatter.Invoke(state, _error)

            match logLevel with
            | LogLevel.Error -> errors.Add message
            | LogLevel.Warning -> warnings.Add message
            | _ -> ()

[<Tests>]
let editErrorTests =
    testList
        "EditError"
        [

          testCaseAsync "EditTextAsync on \"message is not modified\" is a silent no-op: no exception, the tool keeps running, nothing is logged"
          <| async {
              do!
                  task {
                      use! server = FakeBotApiServer.start ()
                      let logger = RecordingLogger()
                      let ranPastEdit = TaskCompletionSource()

                      let tools =
                          ToolRegistry
                              .create()
                              .Register(
                                  "approve",
                                  fun ctx ->
                                      task {
                                          do! ctx.EditTextAsync "Approved!" // must not throw
                                          ranPastEdit.TrySetResult() |> ignore // proves the tool kept running
                                      }
                              )

                      server.EnqueueError("editMessageText", 400, "Bad Request: message is not modified")

                      use! bot = TgBot.startPolling ((config server tools).WithLogger logger)

                      match Plan.rows [ [ Plan.tool "Approve" "approve" ] ] with
                      | Error e -> failtestf "plan should be valid: %A" e
                      | Ok plan ->
                          let! _ = bot.SendKeyboardPlan(UMX.tag<chatId> 701L, MessageText.unsafe "Deploy?", plan)

                          let sentKeyboard = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                          let approveToken = callbackDataAt 0 0 sentKeyboard

                          server.EnqueueResult(
                              "getUpdates",
                              TelegramJson.batch [ TelegramJson.callbackQueryUpdate 1 "q-not-modified" approveToken 701L 42 900L "Al" ]
                          )

                          do! pollUntil 5000 (fun () -> server.RequestsFor "answerCallbackQuery" |> List.isEmpty |> not)
                          do! ranPastEdit.Task

                          Expect.isEmpty logger.Errors "no failure is logged for a silent no-op"
                          Expect.isEmpty logger.Warnings "\"message is not modified\" is a successful no-op, not a soft failure"
                  }
                  |> Async.AwaitTask
          }

          testCaseAsync "EditTextAsync on \"message to edit not found\" does not throw: the tool keeps running past the call, and the failure is surfaced as a soft (Warning), not a hard, failure"
          <| async {
              do!
                  task {
                      use! server = FakeBotApiServer.start ()
                      let logger = RecordingLogger()
                      let ranPastEdit = TaskCompletionSource()

                      let tools =
                          ToolRegistry
                              .create()
                              .Register(
                                  "approve",
                                  fun ctx ->
                                      task {
                                          do! ctx.EditTextAsync "too late" // must not throw
                                          ranPastEdit.TrySetResult() |> ignore // proves the tool kept running
                                      }
                              )

                      server.EnqueueError("editMessageText", 400, "Bad Request: message to edit not found")

                      use! bot = TgBot.startPolling ((config server tools).WithLogger logger)

                      match Plan.rows [ [ Plan.tool "Approve" "approve" ] ] with
                      | Error e -> failtestf "plan should be valid: %A" e
                      | Ok plan ->
                          let! _ = bot.SendKeyboardPlan(UMX.tag<chatId> 702L, MessageText.unsafe "Deploy?", plan)

                          let sentKeyboard = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                          let approveToken = callbackDataAt 0 0 sentKeyboard

                          server.EnqueueResult(
                              "getUpdates",
                              TelegramJson.batch [ TelegramJson.callbackQueryUpdate 1 "q-not-found" approveToken 702L 43 901L "Bo" ]
                          )

                          do! pollUntil 5000 (fun () -> server.RequestsFor "answerCallbackQuery" |> List.isEmpty |> not)
                          do! ranPastEdit.Task // the tool ran to completion — no exception ever reached it

                          Expect.isEmpty logger.Errors "a soft edit failure is not a hard (OnHookFailed) failure"
                          Expect.isNonEmpty logger.Warnings "a vanished message is surfaced as a soft, observed failure"

                          Expect.isNonEmpty
                              (server.RequestsFor "answerCallbackQuery")
                              "the press was still acknowledged despite the soft edit failure"
                  }
                  |> Async.AwaitTask
          }

          testCaseAsync "EditKeyboardAsync on \"message to edit not found\" does not throw: the tool keeps running, and the failure is surfaced as a soft (Warning) failure"
          <| async {
              do!
                  task {
                      use! server = FakeBotApiServer.start ()
                      let logger = RecordingLogger()
                      let ranPastEdit = TaskCompletionSource()

                      let tools =
                          ToolRegistry
                              .create()
                              .Register(
                                  "approve",
                                  fun ctx ->
                                      task {
                                          match Plan.rows [ [ Plan.tool "Undo" "undo" ] ] with
                                          | Error e -> failtestf "replacement plan should be valid: %A" e
                                          | Ok replacementPlan -> do! ctx.EditKeyboardAsync replacementPlan // must not throw

                                          ranPastEdit.TrySetResult() |> ignore
                                      }
                              )

                      server.EnqueueError("editMessageReplyMarkup", 400, "Bad Request: message to edit not found")

                      use! bot = TgBot.startPolling ((config server tools).WithLogger logger)

                      match Plan.rows [ [ Plan.tool "Approve" "approve" ] ] with
                      | Error e -> failtestf "plan should be valid: %A" e
                      | Ok plan ->
                          let! _ = bot.SendKeyboardPlan(UMX.tag<chatId> 703L, MessageText.unsafe "Deploy?", plan)

                          let sentKeyboard = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                          let approveToken = callbackDataAt 0 0 sentKeyboard

                          server.EnqueueResult(
                              "getUpdates",
                              TelegramJson.batch
                                  [ TelegramJson.callbackQueryUpdate 1 "q-keyboard-not-found" approveToken 703L 44 902L "Cy" ]
                          )

                          do! pollUntil 5000 (fun () -> server.RequestsFor "answerCallbackQuery" |> List.isEmpty |> not)
                          do! ranPastEdit.Task

                          Expect.isEmpty logger.Errors "a soft edit-keyboard failure is not a hard (OnHookFailed) failure"
                          Expect.isNonEmpty logger.Warnings "a vanished message is surfaced as a soft, observed failure on the keyboard-edit path too"
                  }
                  |> Async.AwaitTask
          } ]
