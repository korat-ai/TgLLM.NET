/// Regression test (review #3, folded into the Foundational phase): the deferred-ack tool path's
/// watchdog previously started counting down INSIDE the enqueued work thunk — i.e. only once the
/// per-chat dispatcher actually RAN it — rather than at press ARRIVAL (enqueue time). A tool
/// queued behind a slow tool on the SAME chat therefore inherited that wait before its own
/// watchdog clock even started, so its effective ack latency was (queue wait) + (watchdog budget)
/// instead of just (watchdog budget), silently blowing the client's spinner budget regardless of
/// the budget's own configured value.
///
/// Fix: the watchdog is now started at ENQUEUE time (`UpdateProcessor.processPress`'s `Deferred`
/// branch, before `dispatcher.Enqueue` is even called) rather than inside the enqueued thunk
/// itself (`buildToolWork`). This is exercised here over the REAL production chain
/// (`PerChatChannelDispatcher`, which genuinely serializes same-chat work), mirroring
/// `ToolRoutingAtScaleTests.fs`'s "drive the real engine" pattern.
///
/// NOTE (scope, reported explicitly per the task brief): this file does NOT test — and this slice
/// does NOT fix — the separate ack-first (`processHookStorePress`) head-of-line-blocking issue
/// (its `AnswerCallback` HTTP call is awaited inline in the single ingestion loop, so a slow ack
/// for one chat can delay reading the NEXT chat's press off the update stream). That reshaping
/// risked breaking existing ack-first ordering tests (see the task report) and is left as a
/// documented follow-up — only the watchdog-at-enqueue fix (bounded scope) is covered here.
module TgLLM.Integration.Tests.AckTimingTests

open System
open System.Collections.Generic
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Expecto
open FSharp.UMX
open TgLLM.Core

let private validLabel =
    match ButtonLabel.create "Go" with
    | Ok l -> l
    | Error e -> failwithf "test setup: unreachable %A" e

let private toolName (s: string) : ToolName =
    match ToolName.create s with
    | Ok n -> n
    | Error e -> failwithf "test setup: unreachable %A" e

let private samplePress (token: CallbackToken) (chat: int64) : ButtonPress =
    { Token = token
      QueryId = UMX.tag<callbackQueryId> $"query-{CallbackToken.value token}"
      Chat = UMX.tag<chatId> chat
      User = { Id = UMX.tag<userId> 1L; FirstName = "Alice"; Username = null }
      MessageId = UMX.tag<messageId> 1L
      ButtonLabel = validLabel }

/// A finite `IUpdateSource` fake that yields a fixed list of events, then completes (same shape as
/// `ToolDispatchProcessorTests.fs`'s private fake).
type private FakeUpdateSource(events: AgentEvent list) =
    interface IUpdateSource with
        member _.Updates(_ct: CancellationToken) : IAsyncEnumerable<AgentEvent> =
            { new IAsyncEnumerable<AgentEvent> with
                member _.GetAsyncEnumerator(_ct2: CancellationToken) : IAsyncEnumerator<AgentEvent> =
                    let queue = Queue<AgentEvent>(events)
                    let mutable current: AgentEvent voption = ValueNone

                    { new IAsyncEnumerator<AgentEvent> with
                        member _.Current =
                            match current with
                            | ValueSome e -> e
                            | ValueNone -> failwith "Current accessed before a successful MoveNextAsync"

                        member _.MoveNextAsync() =
                            if queue.Count > 0 then
                                current <- ValueSome(queue.Dequeue())
                                ValueTask<bool>(true)
                            else
                                ValueTask<bool>(false)

                        member _.DisposeAsync() = ValueTask.CompletedTask } }

/// Records each deferred-ack (`AnswerCallback` text/alert overload) call together with the elapsed
/// time since `clock` started, so the test can assert WHEN an ack arrived, not just THAT it did.
type private TimestampingBotApiClient(clock: Stopwatch) =
    let deferredAcks = ResizeArray<CallbackQueryId * TimeSpan>()
    member _.DeferredAcks: (CallbackQueryId * TimeSpan) list = List.ofSeq deferredAcks

    interface IBotApiClient with
        member _.SendText(_chat, _text, _ct) = Task.FromResult(UMX.tag<messageId> 0L)
        member _.SendKeyboard(_chat, _text, _keyboard, _ct) = Task.FromResult(UMX.tag<messageId> 0L)
        member _.AnswerCallback(_query, _ct) = Task.CompletedTask

        member _.AnswerCallback(query, _text, _showAlert, _ct) =
            lock deferredAcks (fun () -> deferredAcks.Add(query, clock.Elapsed))
            Task.CompletedTask

        member _.EditMessageText(_chat, _message, _text, _keyboard, _ct) = Task.CompletedTask
        member _.EditMessageReplyMarkup(_chat, _message, _keyboard, _ct) = Task.CompletedTask

type private NoopObserver() =
    interface IHookObserver with
        member _.OnHookFailed(_press, _error) = ()
        member _.OnUnknownToken(_press) = ()
        member _.OnRunLoopFailed(_error) = ()

[<Tests>]
let ackTimingTests =
    testList
        "deferred-ack watchdog timing [review #3]"
        [

          testCaseAsync
              "a tool queued behind a slow tool on the SAME chat is still acked within its OWN watchdog budget, not after the queue drains"
          <| async {
              do!
                  task {
                      let slowToken = CallbackToken.generate ()
                      let queuedToken = CallbackToken.generate ()
                      let chat = 42L
                      let slowPress = samplePress slowToken chat
                      let queuedPress = samplePress queuedToken chat

                      // Both tools run far longer than the watchdog budget — neither finishes
                      // within the assertion window, so any ack observed for `queuedPress` MUST
                      // have come from ITS watchdog, never from the tool's own `ctx.Answer`.
                      let slowTool: Tool = fun _ -> task { do! Task.Delay 3000 }
                      let queuedTool: Tool = fun _ -> task { do! Task.Delay 3000 }

                      let registry = InMemoryToolRegistry() :> IToolRegistry
                      registry.Register(toolName "slow", slowTool)
                      registry.Register(toolName "queued", queuedTool)
                      let store = InMemoryBindingStore() :> IBindingStore

                      do!
                          store.Save(
                              [ ToolBinding.create slowToken (toolName "slow") None
                                ToolBinding.create queuedToken (toolName "queued") None ],
                              CancellationToken.None
                          )

                      let dispatch = ToolDispatch(registry, store)
                      let clock = Stopwatch.StartNew()
                      let api = TimestampingBotApiClient(clock)
                      use dispatcher = new PerChatChannelDispatcher()
                      let observer = NoopObserver()

                      // Both presses arrive back-to-back on the SAME chat — the REAL per-chat
                      // dispatcher serializes their WORK (the queued tool's thunk can't even
                      // START running until the slow tool's 3s work finishes), but each press's
                      // watchdog must still start at ENQUEUE (press arrival), not at work-start.
                      let source = FakeUpdateSource [ ButtonPressed slowPress; ButtonPressed queuedPress ]

                      let processor =
                          UpdateProcessor(
                              source,
                              InMemoryHookStore(),
                              api,
                              dispatcher,
                              observer,
                              toolDispatch = dispatch,
                              watchdogBudget = TimeSpan.FromMilliseconds 150.0
                          )

                      do! processor.RunAsync CancellationToken.None

                      // Poll (bounded) for the queued press's own ack.
                      let mutable tries = 0

                      while (api.DeferredAcks |> List.forall (fun (q, _) -> q <> queuedPress.QueryId)) && tries < 500 do
                          do! Task.Delay 10
                          tries <- tries + 1

                      match api.DeferredAcks |> List.tryFind (fun (q, _) -> q = queuedPress.QueryId) with
                      | Some(_, elapsed) ->
                          Expect.isLessThan
                              elapsed.TotalMilliseconds
                              1000.0
                              "the QUEUED press's ack arrived well within its own ~150ms watchdog budget — not after waiting ~3s for the slow press's tool to even start running"
                      | None -> failwith "expected the queued press to have been acked by its own watchdog within the poll window"
                  }
                  |> Async.AwaitTask
          } ]
