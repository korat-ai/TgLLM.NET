/// Regression tests (review #8, folded into the Foundational phase): a callback query whose `Data`
/// the transport cannot map to a canonical token — garbage/non-canonical `callback_data`, or (not
/// exercised at the wire level here, see the mapping-level test) a callback query with no
/// originating `Message` — was previously dropped with NO `AnswerCallback` at all, so the client's
/// spinner ran until Telegram's own unpublished timeout, since nothing else in the pipeline would
/// ever ack it. The port's own `AnswerCallback` contract (`Ports.fs`) says "MUST be called for
/// EVERY press, including unknown/stale ones" — a non-canonical press wasn't even reaching that
/// guarantee, because `Mapping.toAgentEvent` returned `ValueNone` (no event at all), never handing
/// UpdateProcessor a chance to ack it.
///
/// Fix: `Mapping.toAgentEvent` now emits `AcknowledgeOnly queryId` for such callback queries instead
/// of `ValueNone`, and `UpdateProcessor.RunAsync` acks it (no hook/tool ever runs — this is a pure
/// ack-only signal). Exercised here over the REAL production chain (`LongPollingUpdateSource`
/// against `FakeBotApiServer`), same pattern as `RunLoopResilienceTests.fs`; both transports share
/// the same `Mapping.toAgentEvent`, so this closes the gap for webhooks too.
module TgLLM.Integration.Tests.DroppedCallbackTests

open System
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Expecto
open Telegram.Bot
open TgLLM.Core
open TgLLM.BotApi
open TgLLM.Integration.Tests.FakeBotApiServer

let private field (key: string) (node: JsonNode) : JsonNode =
    match node.[key] |> Option.ofObj with
    | Some child -> child
    | None -> failwithf "expected JSON field '%s' in %s" key (node.ToJsonString())

let private asString (node: JsonNode) : string = node.AsValue().GetValue<string>()

let private makeClient (server: FakeBotApiServer) : ITelegramBotClient =
    TelegramBotClient(TelegramBotClientOptions("123456789:TEST-fake-token", server.BaseUrl)) :> ITelegramBotClient

/// Runs enqueued work immediately — this suite never registers a hook, so nothing is ever actually
/// enqueued; implemented only to satisfy `IPressDispatcher`.
type private ImmediateDispatcher() =
    interface IPressDispatcher with
        member _.Enqueue(_chat, work) = ValueTask(work CancellationToken.None)
        member _.DisposeAsync() = ValueTask.CompletedTask

type private NoopObserver() =
    interface IHookObserver with
        member _.OnHookFailed(_press, _error) = ()
        member _.OnUnknownToken(_press) = ()
        member _.OnRunLoopFailed(_error) = ()

[<Tests>]
let droppedCallbackTests =
    testList
        "dropped (non-canonical) callback queries are still acked [review #8]"
        [

          testCaseAsync
              "a callback_query whose Data does not parse to a canonical token is still acked, not silently dropped"
          <| async {
              do!
                  task {
                      use! server = FakeBotApiServer.start ()

                      server.EnqueueResult(
                          "getUpdates",
                          TelegramJson.batch [ TelegramJson.callbackQueryUpdate 1 "q-garbage" "not-a-real-token" 801L 10 950L "Deb" ]
                      )

                      let client = makeClient server

                      let source =
                          LongPollingUpdateSource(
                              client,
                              timeoutSeconds = 0,
                              initialBackoff = TimeSpan.FromMilliseconds 20.0,
                              maxBackoff = TimeSpan.FromMilliseconds 100.0
                          )
                          :> IUpdateSource

                      let store = InMemoryHookStore() :> IHookStore
                      let api = TelegramBotApiClient(client) :> IBotApiClient
                      let dispatcher = ImmediateDispatcher() :> IPressDispatcher
                      let observer = NoopObserver()
                      let processor = UpdateProcessor(source, store, api, dispatcher, observer)

                      use cts = new CancellationTokenSource()
                      let runTask = processor.RunAsync cts.Token

                      let mutable tries = 0

                      while (server.RequestsFor "answerCallbackQuery").IsEmpty && tries < 500 do
                          do! Task.Delay 10
                          tries <- tries + 1

                      cts.Cancel()

                      try
                          do! runTask
                      with :? OperationCanceledException ->
                          ()

                      match server.RequestsFor "answerCallbackQuery" with
                      | [ request ] ->
                          let queryId = request.Body |> Option.get |> field "callback_query_id" |> asString
                          Expect.equal queryId "q-garbage" "the non-canonical callback query was acked by its OWN query id"
                      | other ->
                          failwithf
                              "expected exactly one answerCallbackQuery call for the non-canonical press, got %d"
                              (List.length other)
                  }
                  |> Async.AwaitTask
          } ]
