/// US3 (T038–T040): routing correctness, per-chat ordering, and failure isolation exercised through
/// the REAL engine (UpdateProcessor + PerChatChannelDispatcher + InMemoryHookStore) driven by an
/// in-memory update source and a recording Bot API client — deterministic and fast, no HTTP.
///
/// Consolidated into one file (shared harness) rather than the three separate files nominally named
/// in tasks.md, to avoid duplicating the fakes across projects.
module TgLLM.Integration.Tests.RoutingAtScaleTests

open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open System.Threading.Channels
open Expecto
open FSharp.UMX
open TgLLM.Core

// --- fakes over the core ports ---

let private anyLabel: ButtonLabel =
    match ButtonLabel.create "b" with
    | Ok l -> l
    | Error e -> failwithf "unreachable %A" e

/// An IUpdateSource that yields a fixed list of events (in order) then completes.
let private sourceOf (events: AgentEvent list) : IUpdateSource =
    { new IUpdateSource with
        member _.Updates(ct: CancellationToken) =
            let channel = Channel.CreateUnbounded<AgentEvent>()
            for e in events do
                channel.Writer.TryWrite e |> ignore
            channel.Writer.TryComplete() |> ignore
            channel.Reader.ReadAllAsync ct }

/// Records `AnswerCallback` calls; sends are no-ops returning a dummy message id.
type private RecordingApi() =
    let mutable acks = 0
    member _.Acks = acks

    interface IBotApiClient with
        member _.SendText(_, _, _) = Task.FromResult(UMX.tag<messageId> 1L)
        member _.SendKeyboard(_, _, _, _) = Task.FromResult(UMX.tag<messageId> 1L)

        member _.AnswerCallback(_, _) =
            Interlocked.Increment(&acks) |> ignore
            Task.CompletedTask

type private CountingObserver() =
    let mutable failed = 0
    let mutable unknown = 0
    member _.Failed = failed
    member _.Unknown = unknown

    interface IHookObserver with
        member _.OnHookFailed(_, _) = Interlocked.Increment(&failed) |> ignore
        member _.OnUnknownToken(_) = Interlocked.Increment(&unknown) |> ignore

let private pressOf (token: CallbackToken) (chat: int64) : AgentEvent =
    ButtonPressed
        { Token = token
          QueryId = UMX.tag<callbackQueryId> "q"
          Chat = UMX.tag<chatId> chat
          User =
            { Id = UMX.tag<userId> 1L
              FirstName = "T"
              Username = null }
          MessageId = UMX.tag<messageId> 1L
          ButtonLabel = anyLabel }

/// Drive the real engine over `events`, registering `bindings`, and wait (bounded) until `expected`
/// hook runs have completed. Returns the acks and observer counts.
let private drive
    (bindings: HookBinding list)
    (events: AgentEvent list)
    (expectedRuns: unit -> int)
    (targetRuns: int)
    : Task<int * CountingObserver> =
    task {
        let store = InMemoryHookStore() :> IHookStore
        do! store.Register(bindings, CancellationToken.None)
        let api = RecordingApi()
        let observer = CountingObserver()
        use dispatcher = new PerChatChannelDispatcher()
        let processor = UpdateProcessor(sourceOf events, store, api, dispatcher, observer :> IHookObserver)
        use cts = new CancellationTokenSource()

        // Source completes after yielding all events, so RunAsync returns once they are all acked and
        // enqueued; hooks then drain on the per-chat loops.
        do! processor.RunAsync cts.Token

        let mutable tries = 0

        while expectedRuns () < targetRuns && tries < 500 do
            do! Task.Delay 10
            tries <- tries + 1

        return (api.Acks, observer)
    }

[<Tests>]
let routingAtScaleTests =
    testList
        "RoutingAtScale"
        [

          testCaseAsync "≥100 interleaved taps each run only their own hook, zero cross-invocation (SC-002)"
          <| async {
              do!
                  task {
                      // 6 distinctly-hooked buttons across 2 chats (two keyboards).
                      let tokens = [ for _ in 1..6 -> CallbackToken.generate () ]
                      let counts = ConcurrentDictionary<CallbackToken, int>()

                      let bindings =
                          [ for t in tokens ->
                                { Token = t
                                  Hook = (fun _ -> task { counts.AddOrUpdate(t, 1, (fun _ n -> n + 1)) |> ignore }) } ]

                      // 120 presses distributed deterministically across the 6 tokens / 2 chats.
                      let events =
                          [ for i in 0..119 ->
                                let token = tokens.[i % 6]
                                let chat = int64 (i % 2)
                                pressOf token chat ]

                      let expectedPer = 20 // 120 / 6

                      let! (acks, _) =
                          drive bindings events (fun () -> counts.Values |> Seq.sum) 120

                      Expect.equal (counts.Values |> Seq.sum) 120 "every press ran exactly one hook"
                      Expect.equal acks 120 "every press was acknowledged (ack-first)"

                      for t in tokens do
                          Expect.equal counts.[t] expectedPer $"token ran exactly its own {expectedPer} presses (no cross-invocation)"
                  }
                  |> Async.AwaitTask
          }

          testCaseAsync "per-chat taps run in arrival order (SC-007)"
          <| async {
              do!
                  task {
                      // Two tokens per chat; interleave the two chats.
                      let a1, a2, a3 = CallbackToken.generate (), CallbackToken.generate (), CallbackToken.generate ()
                      let b1, b2, b3 = CallbackToken.generate (), CallbackToken.generate (), CallbackToken.generate ()

                      let order = ConcurrentDictionary<int64, ConcurrentQueue<CallbackToken>>()

                      let record (token: CallbackToken) : PressContext -> Task =
                          fun (ctx: PressContext) ->
                              task {
                                  let chat = UMX.untag ctx.Chat
                                  let q = order.GetOrAdd(chat, (fun _ -> ConcurrentQueue()))
                                  q.Enqueue token
                              }
                              :> Task

                      let bindings =
                          [ for t in [ a1; a2; a3; b1; b2; b3 ] -> { Token = t; Hook = record t } ]

                      // Chat 10: a1,a2,a3 ; chat 20: b1,b2,b3 — interleaved arrival.
                      let events =
                          [ pressOf a1 10L
                            pressOf b1 20L
                            pressOf a2 10L
                            pressOf b2 20L
                            pressOf a3 10L
                            pressOf b3 20L ]

                      let total () =
                          order.Values |> Seq.sumBy (fun q -> q.Count)

                      let! _ = drive bindings events total 6

                      Expect.sequenceEqual (List.ofSeq order.[10L]) [ a1; a2; a3 ] "chat 10 ran in arrival order"
                      Expect.sequenceEqual (List.ofSeq order.[20L]) [ b1; b2; b3 ] "chat 20 ran in arrival order"
                  }
                  |> Async.AwaitTask
          }

          testCaseAsync "a throwing hook is isolated and observed; later presses still run (SC-006)"
          <| async {
              do!
                  task {
                      let bad = CallbackToken.generate ()
                      let good = CallbackToken.generate ()
                      let goodRan = ref 0

                      let bindings =
                          [ { Token = bad; Hook = (fun _ -> task { failwith "boom" }) }
                            { Token = good
                              Hook = (fun _ -> task { incr goodRan }) } ]

                      let events = [ pressOf bad 1L; pressOf good 1L ]

                      let! (acks, observer) = drive bindings events (fun () -> goodRan.Value) 1

                      Expect.equal goodRan.Value 1 "the good hook still ran after the bad one threw"
                      Expect.isGreaterThanOrEqual observer.Failed 1 "the failing hook was reported to the observer"
                      Expect.equal acks 2 "both presses were acknowledged despite the failure"
                  }
                  |> Async.AwaitTask
          } ]
