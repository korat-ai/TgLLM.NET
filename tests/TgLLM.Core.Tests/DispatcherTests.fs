/// T019: failing FsCheck model-based ordering property for `PerChatChannelDispatcher`
/// (contracts/core-ports.md "IPressDispatcher", research.md D6). Written before
/// `TgLLM.Core.PerChatChannelDispatcher` exists — this file MUST fail to compile until T020
/// implements it (Red).
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
