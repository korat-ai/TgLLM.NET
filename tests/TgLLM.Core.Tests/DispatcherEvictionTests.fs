/// Tests for `PerChatChannelDispatcher`'s idle per-chat reclaim: a chat's
/// channel/consumer-loop entry is reclaimed once no new work has arrived for the configured idle
/// deadline, WITHOUT dropping or reordering any in-flight/buffered press — and a chat that keeps
/// receiving work faster than the deadline is never reclaimed. `?idleTimeout` defaults to `None`
/// (never reclaim, exactly the slice-1 behavior `DispatcherTests.fs` already covers) — these tests
/// are the ONLY ones that opt in, with a short deadline so the suite stays fast.
module TgLLM.Core.Tests.DispatcherEvictionTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open FSharp.UMX
open TgLLM.Core

/// `Enqueue` is an explicit `IPressDispatcher` member — invisible on `PerChatChannelDispatcher`'s
/// own concrete type, which these tests otherwise need directly for `ActiveChatCount` (a
/// testability-only member, not part of the interface). This upcasts once per call site instead of
/// typing every `dispatcher` binding as the interface (which would hide `ActiveChatCount`).
let private enqueue (dispatcher: PerChatChannelDispatcher) (chat: ChatId) (work: CancellationToken -> Task) : Task =
    ((dispatcher :> IPressDispatcher).Enqueue(chat, work)).AsTask()

/// Polls `predicate` until it's true or `ms` elapses, failing the test on timeout — the idle
/// reclaim pass runs on its own schedule (driven by the per-chat consumer's own wait), so tests
/// observe its effect rather than a fixed delay.
let private pollUntil (ms: int) (predicate: unit -> bool) : Task =
    task {
        let mutable tries = 0

        while not (predicate ()) && tries < ms / 10 do
            do! Task.Delay 10
            tries <- tries + 1

        if not (predicate ()) then failtest "timed out waiting for the expected condition"
    }

[<Tests>]
let dispatcherEvictionTests =
    testList "PerChatChannelDispatcher idle eviction" [

        testCase "an idle chat's resources are reclaimed after the configured deadline" <| fun _ ->
            task {
                use dispatcher = new PerChatChannelDispatcher(idleTimeout = TimeSpan.FromMilliseconds 50.0)
                let chat = UMX.tag<chatId> 1L
                let ran = TaskCompletionSource()

                let work (_ct: CancellationToken) : Task =
                    ran.TrySetResult() |> ignore
                    Task.CompletedTask

                do! enqueue dispatcher chat work
                do! ran.Task

                Expect.equal dispatcher.ActiveChatCount 1 "the chat's channel/consumer exists right after its only item ran"

                do! pollUntil 2000 (fun () -> dispatcher.ActiveChatCount = 0)

                Expect.equal dispatcher.ActiveChatCount 0 "the idle chat's resources were reclaimed once the deadline elapsed"
            }
            |> fun t -> t.GetAwaiter().GetResult()

        testCase "a chat that keeps receiving work faster than the idle deadline is never reclaimed, and FIFO order is preserved" <| fun _ ->
            task {
                use dispatcher = new PerChatChannelDispatcher(idleTimeout = TimeSpan.FromMilliseconds 100.0)
                let chat = UMX.tag<chatId> 2L
                let observed = ResizeArray<int>()
                let gate = obj ()

                for i in 1..10 do
                    let work (_ct: CancellationToken) : Task =
                        lock gate (fun () -> observed.Add i)
                        Task.CompletedTask

                    do! enqueue dispatcher chat work
                    // Faster than the idle deadline — this chat must never be reclaimed mid-stream.
                    do! Task.Delay 20

                Expect.sequenceEqual observed [ 1..10 ] "every item ran, in FIFO order, despite the idle timer being armed throughout"
                Expect.equal dispatcher.ActiveChatCount 1 "a continuously-active chat is never reclaimed"
            }
            |> fun t -> t.GetAwaiter().GetResult()

        testCase "a reclaimed chat that receives work again is re-created cleanly and still runs it" <| fun _ ->
            task {
                use dispatcher = new PerChatChannelDispatcher(idleTimeout = TimeSpan.FromMilliseconds 50.0)
                let chat = UMX.tag<chatId> 3L
                let firstRan = TaskCompletionSource()

                let firstWork (_ct: CancellationToken) : Task =
                    firstRan.TrySetResult() |> ignore
                    Task.CompletedTask

                do! enqueue dispatcher chat firstWork
                do! firstRan.Task

                // Wait past the idle deadline so the chat's resources are reclaimed.
                do! pollUntil 2000 (fun () -> dispatcher.ActiveChatCount = 0)

                let secondRan = TaskCompletionSource()

                let secondWork (_ct: CancellationToken) : Task =
                    secondRan.TrySetResult() |> ignore
                    Task.CompletedTask

                // A fresh press on the SAME chat after reclaim must still work — a brand-new
                // channel/consumer is created cleanly, transparently to the caller.
                do! enqueue dispatcher chat secondWork
                do! pollUntil 2000 (fun () -> secondRan.Task.IsCompletedSuccessfully)

                Expect.isTrue secondRan.Task.IsCompletedSuccessfully "a reclaimed chat resolves a later press through a freshly (re)created channel/consumer"
            }
            |> fun t -> t.GetAwaiter().GetResult()

        testCase "an idle timer racing a concurrent press never drops the press (repeated to shake out the race)" <| fun _ ->
            task {
                for _ in 1..25 do
                    use dispatcher = new PerChatChannelDispatcher(idleTimeout = TimeSpan.FromMilliseconds 5.0)
                    let chat = UMX.tag<chatId> 4L
                    let ran = TaskCompletionSource()

                    // Give the idle timer a head start so it's likely armed/close to firing right as
                    // this press lands — the exact race window `Enqueue`'s retry-on-ChannelClosed
                    // exists for.
                    do! Task.Delay 5

                    let work (_ct: CancellationToken) : Task =
                        ran.TrySetResult() |> ignore
                        Task.CompletedTask

                    do! enqueue dispatcher chat work
                    do! pollUntil 2000 (fun () -> ran.Task.IsCompletedSuccessfully)
                    Expect.isTrue ran.Task.IsCompletedSuccessfully "the press was never dropped, even when racing the idle-reclaim pass"
            }
            |> fun t -> t.GetAwaiter().GetResult()
    ]
