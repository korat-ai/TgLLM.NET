/// FsCheck model-based ordering property for `PerChatChannelDispatcher`, the `IPressDispatcher`
/// implementation.
module TgLLM.Core.Tests.DispatcherTests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Expecto
open FsCheck
open FSharp.UMX
open TgLLM.Core

/// Model-based ordering check: interpret the generated data as a sequence of "arrivals" — each
/// element names which chat's next item shows up. Enqueuing sequentially in that order simulates
/// arbitrary cross-chat interleaving (per-chat item order is preserved by construction; the
/// *interleaving* across chats is what's random) while keeping the test deterministic to drive.
let private chatCount = 4

let private toChatIndex (PositiveInt n) = n % chatCount

[<Tests>]
let dispatcherTests =
    testList "PerChatChannelDispatcher" [

        testCase "DisposeAsync drains a work item that was already queued (buffered, unread) behind a still-running item, instead of cancelling it out from under it" <| fun _ ->
            task {
                let dispatcher: IPressDispatcher = new PerChatChannelDispatcher()
                let chat = UMX.tag<chatId> 1L
                let item1Started = TaskCompletionSource()
                let item1Gate = TaskCompletionSource()
                let mutable item2Ran = false

                // Item 1: deliberately ignores `ct` and blocks on a gate the test controls, so the
                // consumer's inner drain loop is guaranteed to still be "inside" this call — and item
                // 2 (below) guaranteed to still be sitting BUFFERED, unread — at the moment `DisposeAsync`
                // runs its (synchronous) `TryComplete` + `Cancel`.
                let work1 (_ct: CancellationToken) : Task =
                    task {
                        item1Started.SetResult()
                        do! item1Gate.Task
                    }

                // Item 2: checks its OWN token, same as any real, well-behaved work item — if `cts` was
                // already cancelled (by a `DisposeAsync` that cancels BEFORE draining) by the time this
                // runs, it throws (swallowed by the consumer loop) and `item2Ran` stays false.
                let work2 (ct: CancellationToken) : Task =
                    task {
                        ct.ThrowIfCancellationRequested()
                        item2Ran <- true
                    }

                do! (dispatcher.Enqueue(chat, work1)).AsTask()
                do! item1Started.Task // item 1 is now the consumer's in-flight, awaited item.
                do! (dispatcher.Enqueue(chat, work2)).AsTask() // item 2 sits buffered, unread, behind it.

                // `DisposeAsync`'s own synchronous prefix (`TryComplete` + `Cancel`, per the type under
                // test) has already run by the time this call returns a (still-pending) task — it can't
                // complete yet because item 1 hasn't finished.
                let disposeTask = dispatcher.DisposeAsync().AsTask()

                item1Gate.SetResult() // let item 1 finish; the consumer now loops to item 2.
                do! disposeTask

                Expect.isTrue
                    item2Ran
                    "an item already queued (buffered) behind an in-flight one must still be drained by DisposeAsync, per the type's own \"drains whatever is already queued\" doc comment — not dropped because Cancel() already fired before it got a turn"
            }
            |> fun t -> t.GetAwaiter().GetResult()

        testPropertyWithConfig
            { FsCheckConfig.defaultConfig with maxTest = 50 }
            "per-chat FIFO order is preserved and every chat that received work makes progress, under interleaved enqueues"
        <| fun (data: NonEmptyArray<PositiveInt>) ->
            let (NonEmptyArray raw) = data
            let arrivals = raw |> Array.toList |> List.map toChatIndex |> List.truncate 80

            task {
                let dispatcher: IPressDispatcher = new PerChatChannelDispatcher()

                // Per-chat: the order items were *observed running*, and how many were expected.
                let observed = ConcurrentDictionary<int, ConcurrentQueue<int>>()
                let expectedCounts = Array.zeroCreate<int> chatCount
                let completions = ResizeArray<Task>()

                for chatIdx in arrivals do
                    let itemIdx = expectedCounts[chatIdx]
                    expectedCounts[chatIdx] <- itemIdx + 1
                    let tcs = TaskCompletionSource()
                    completions.Add(tcs.Task)

                    let work (_ct: CancellationToken) : Task =
                        let queue = observed.GetOrAdd(chatIdx, (fun _ -> ConcurrentQueue()))
                        queue.Enqueue itemIdx
                        tcs.SetResult()
                        Task.CompletedTask

                    // Awaiting each Enqueue in turn makes *this loop's order* the arrival order —
                    // exactly the "random cross-chat interleaving" the property is about.
                    do! (dispatcher.Enqueue(UMX.tag<chatId> (int64 chatIdx), work)).AsTask()

                let allDone = Task.WhenAll(completions)
                let finished = Task.WhenAny(allDone, Task.Delay(TimeSpan.FromSeconds 10.0))
                let! winner = finished
                do! dispatcher.DisposeAsync().AsTask()

                if not (obj.ReferenceEquals(winner, allDone)) then
                    return false
                else
                    let allChatsProgressed =
                        [ 0 .. chatCount - 1 ]
                        |> List.forall (fun c ->
                            if expectedCounts[c] = 0 then
                                true
                            else
                                match observed.TryGetValue c with
                                | true, q -> q.Count = expectedCounts[c]
                                | false, _ -> false)

                    let fifoPreserved =
                        [ 0 .. chatCount - 1 ]
                        |> List.forall (fun c ->
                            match observed.TryGetValue c with
                            | true, q -> (q.ToArray() |> Array.toList) = [ 0 .. expectedCounts[c] - 1 ]
                            | false, _ -> expectedCounts[c] = 0)

                    return allChatsProgressed && fifoPreserved
            }
            |> fun t -> t.GetAwaiter().GetResult()
    ]
