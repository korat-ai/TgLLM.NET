/// Regression tests for review finding #1 (003-tool-router-extensions, BLOCKER — availability): a
/// single transient error must not permanently kill update ingestion. Exercises the REAL production
/// chain — `UpdateProcessor.RunAsync` driven by a REAL `LongPollingUpdateSource` against
/// `FakeBotApiServer` — rather than a fake `IUpdateSource`, since the fix lives in
/// `LongPollingUpdateSource`'s own retry/backoff (it must never surface a transient failure as a
/// `MoveNextAsync`-level exception in the first place), not in `UpdateProcessor` itself.
module TgLLM.Integration.Tests.RunLoopResilienceTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open FSharp.UMX
open Telegram.Bot
open TgLLM.Core
open TgLLM.BotApi
open TgLLM.Integration.Tests.FakeBotApiServer

let private makeClient (server: FakeBotApiServer) : ITelegramBotClient =
    TelegramBotClient(TelegramBotClientOptions("123456789:TEST-fake-token", server.BaseUrl)) :> ITelegramBotClient

/// Records `OnUnknownToken` presses instead of talking to `ILogger` — enough to prove a press
/// reached the processor, without needing a real hook/tool registered.
type private RecordingHookObserver() =
    let unknown = ResizeArray<ButtonPress>()
    member _.Unknown: ButtonPress list = List.ofSeq unknown

    interface IHookObserver with
        member _.OnHookFailed(_press, _error) = ()
        member _.OnUnknownToken(press) = unknown.Add press
        member _.OnRunLoopFailed(_error) = ()

/// Runs enqueued work immediately and synchronously — this suite never registers a hook, so nothing
/// is ever actually enqueued; implemented only to satisfy `IPressDispatcher`.
type private ImmediateDispatcher() =
    interface IPressDispatcher with
        member _.Enqueue(_chat, work) = ValueTask(work CancellationToken.None)
        member _.DisposeAsync() = ValueTask.CompletedTask

[<Tests>]
let runLoopResilienceTests =
    testList
        "UpdateProcessor run-loop resilience (review finding #1)"
        [

          testCaseAsync
              "a transient GetUpdates failure does not kill ingestion — the LATER press still reaches the processor"
          <| async {
              do!
                  task {
                      use! server = FakeBotApiServer.start ()
                      let token = CallbackToken.generate ()

                      // The FIRST getUpdates call fails (transient); the SECOND (after the fix's
                      // retry/backoff) carries a real button press.
                      server.EnqueueError("getUpdates", 429, "Too Many Requests: retry later")

                      server.EnqueueResult(
                          "getUpdates",
                          TelegramJson.batch [ TelegramJson.callbackQueryUpdate 1 "q1" (CallbackToken.value token) 801L 10 950L "Deb" ]
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
                      let observer = RecordingHookObserver()
                      let processor = UpdateProcessor(source, store, api, dispatcher, observer)

                      use cts = new CancellationTokenSource()
                      let runTask = processor.RunAsync cts.Token

                      let mutable tries = 0

                      while observer.Unknown |> List.isEmpty && tries < 500 do
                          do! Task.Delay 10
                          tries <- tries + 1

                      cts.Cancel()

                      try
                          do! runTask
                      with :? OperationCanceledException ->
                          ()

                      match observer.Unknown with
                      | [ press ] -> Expect.equal press.Token token "the press AFTER the transient getUpdates failure reached the processor"
                      | other -> failwithf "expected exactly one press to reach the processor, got %d" (List.length other)

                      Expect.isTrue
                          (server.RequestsFor "getUpdates" |> List.length >= 2)
                          "getUpdates was retried at least once after the transient failure, not abandoned"
                  }
                  |> Async.AwaitTask
          } ]
