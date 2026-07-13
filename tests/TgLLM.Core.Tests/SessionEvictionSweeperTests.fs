/// Tests for `SessionEvictionSweeper`: the background sweeper that periodically calls
/// `ISessionStore.EvictIdle` so a long-lived bot's session store doesn't grow unbounded — mirrors
/// `BindingEvictionSweeperTests.fs`, but against idle-cutoff eviction (`clock() - idleAfter`)
/// instead of expiry.
module TgLLM.Core.Tests.SessionEvictionSweeperTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open FSharp.UMX
open Expecto
open TgLLM.Core

/// Polls `predicate` until it's true or `ms` elapses, failing the test on timeout — matches
/// `BindingEvictionSweeperTests.fs`'s own helper, used here for the background-loop test only (the
/// deterministic `SweepOnce` tests below need no polling at all).
let private pollUntil (ms: int) (predicate: unit -> bool) : Task =
    task {
        let mutable tries = 0

        while not (predicate ()) && tries < ms / 10 do
            do! Task.Delay 10
            tries <- tries + 1

        if not (predicate ()) then
            failtest "timed out waiting for the expected condition"
    }

/// Wraps an `ISessionStore` so its `EvictIdle` throws once — an `IOException`, the shape a real
/// `FileSessionStore.EvictIdle` failure takes on a file lock or a full disk mid-rewrite — then
/// delegates to `inner` on every later call. Reproduces a transient eviction IO failure for the
/// sole purpose of proving the background loop survives it instead of dying silently.
type private ThrowOnceThenDelegateSessionStore(inner: ISessionStore) =
    let mutable evictIdleCalls = 0

    member _.EvictIdleCallCount = evictIdleCalls

    interface ISessionStore with
        member _.Save(chat: ChatId, record: SessionRecord, ct: CancellationToken) : ValueTask = inner.Save(chat, record, ct)
        member _.TryGet(chat: ChatId, ct: CancellationToken) : ValueTask<SessionRecord voption> = inner.TryGet(chat, ct)
        member _.Remove(chat: ChatId, ct: CancellationToken) : ValueTask = inner.Remove(chat, ct)

        member _.EvictIdle(olderThan: DateTimeOffset) : ValueTask<int> =
            evictIdleCalls <- evictIdleCalls + 1

            if evictIdleCalls = 1 then
                raise (IOException "simulated transient eviction IO failure")
            else
                inner.EvictIdle(olderThan)

[<Tests>]
let sessionEvictionSweeperTests =
    testList "SessionEvictionSweeper" [

        testCase "non-positive idle and sweep durations are rejected" <| fun _ ->
            let store = InMemorySessionStore()
            let clock = fun () -> DateTimeOffset.UnixEpoch

            Expect.throwsT<ArgumentException>
                (fun () -> SessionEvictionSweeper(store, clock, TimeSpan.Zero) |> ignore)
                "an invalid idle window must fail at construction"

            Expect.throwsT<ArgumentException>
                (fun () -> SessionEvictionSweeper(store, clock, TimeSpan.FromHours 1.0, interval = TimeSpan.Zero) |> ignore)
                "an invalid interval must fail at construction"

        testCase "SweepOnce evicts a record at the idle boundary (LastActivityAt = now - idleAfter) using the injected clock" <| fun _ ->
            task {
                let store = InMemorySessionStore() :> ISessionStore
                let chat = UMX.tag<chatId> 1L
                let idleAfter = TimeSpan.FromHours 1.0
                let now = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)

                let record: SessionRecord =
                    { Payload = [| 1uy |]
                      LastActivityAt = now - idleAfter }

                do! store.Save(chat, record, CancellationToken.None)

                use sweeper = new SessionEvictionSweeper(store, (fun () -> now), idleAfter, interval = TimeSpan.FromHours 1.0)
                let! removed = sweeper.SweepOnce()

                Expect.equal removed 1 "the sweep removed exactly the one idle-at-the-boundary record"

                let! stillThere = store.TryGet(chat, CancellationToken.None)
                Expect.equal stillThere ValueNone "the boundary instant itself already counts as idle"
            }
            |> fun t -> t.GetAwaiter().GetResult()

        testCase "SweepOnce leaves a still-active record (well within idleAfter) untouched" <| fun _ ->
            task {
                let store = InMemorySessionStore() :> ISessionStore
                let chat = UMX.tag<chatId> 1L
                let idleAfter = TimeSpan.FromHours 1.0
                let now = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)

                let record: SessionRecord =
                    { Payload = [| 2uy |]
                      LastActivityAt = now.AddMinutes -1.0 }

                do! store.Save(chat, record, CancellationToken.None)

                use sweeper = new SessionEvictionSweeper(store, (fun () -> now), idleAfter, interval = TimeSpan.FromHours 1.0)
                let! removed = sweeper.SweepOnce()

                Expect.equal removed 0 "a record active well within the idle window is not swept"

                let! stillThere = store.TryGet(chat, CancellationToken.None)
                Expect.equal stillThere (ValueSome record) "the still-active record still resolves"
            }
            |> fun t -> t.GetAwaiter().GetResult()

        testCase "a record re-saved with a recent LastActivityAt (touched) survives a sweep that would otherwise have evicted its old activity" <| fun _ ->
            task {
                let store = InMemorySessionStore() :> ISessionStore
                let chat = UMX.tag<chatId> 1L
                let idleAfter = TimeSpan.FromHours 1.0
                let now = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)

                let stale: SessionRecord =
                    { Payload = [| 3uy |]
                      LastActivityAt = now.AddHours -5.0 }

                do! store.Save(chat, stale, CancellationToken.None)

                // Touched right before the sweep — the fresh activity supersedes the old one.
                let touched: SessionRecord = { stale with LastActivityAt = now.AddMinutes -1.0 }
                do! store.Save(chat, touched, CancellationToken.None)

                use sweeper = new SessionEvictionSweeper(store, (fun () -> now), idleAfter, interval = TimeSpan.FromHours 1.0)
                let! removed = sweeper.SweepOnce()

                Expect.equal removed 0 "the touched record's fresh activity keeps it out of this sweep"

                let! stillThere = store.TryGet(chat, CancellationToken.None)
                Expect.equal stillThere (ValueSome touched) "the touched record — not the stale one it replaced — still resolves"
            }
            |> fun t -> t.GetAwaiter().GetResult()

        testCase "the background loop itself sweeps an idle record automatically, with no host code ever calling SweepOnce" <| fun _ ->
            task {
                let store = InMemorySessionStore() :> ISessionStore
                let chat = UMX.tag<chatId> 1L
                let idleAfter = TimeSpan.FromHours 1.0
                let now = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)

                let record: SessionRecord =
                    { Payload = [| 4uy |]
                      LastActivityAt = now - idleAfter - TimeSpan.FromMinutes 1.0 }

                do! store.Save(chat, record, CancellationToken.None)

                // A FIXED clock, already past the idle cutoff from the moment the sweeper starts —
                // only the background loop's own timer (not this test) decides when the sweep runs.
                use sweeper = new SessionEvictionSweeper(store, (fun () -> now), idleAfter, interval = TimeSpan.FromMilliseconds 20.0)

                do!
                    pollUntil 2000 (fun () ->
                        match (store.TryGet(chat, CancellationToken.None)).AsTask().GetAwaiter().GetResult() with
                        | ValueNone -> true
                        | ValueSome _ -> false)

                let! stillThere = store.TryGet(chat, CancellationToken.None)
                Expect.equal stillThere ValueNone "the automatic background sweep removed the idle record without any host code driving it"
            }
            |> fun t -> t.GetAwaiter().GetResult()

        testCase "DisposeAsync stops the background loop cleanly (no leaked timer, no exception)" <| fun _ ->
            task {
                let store = InMemorySessionStore() :> ISessionStore

                let sweeper =
                    new SessionEvictionSweeper(store, (fun () -> DateTimeOffset.UnixEpoch), TimeSpan.FromHours 1.0, interval = TimeSpan.FromMilliseconds 20.0)

                do! Task.Delay 50 // let the loop tick at least once
                do! (sweeper :> IAsyncDisposable).DisposeAsync().AsTask()
            }
            |> fun t -> t.GetAwaiter().GetResult()

        testCase "the background loop survives a transient EvictIdle failure and keeps sweeping on later ticks" <| fun _ ->
            task {
                let inner = InMemorySessionStore() :> ISessionStore
                let store = ThrowOnceThenDelegateSessionStore(inner)
                let idleAfter = TimeSpan.FromHours 1.0
                let now = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)

                use sweeper =
                    new SessionEvictionSweeper(store :> ISessionStore, (fun () -> now), idleAfter, interval = TimeSpan.FromMilliseconds 20.0)

                // A generous timeout: the first tick's EvictIdle throws, the loop must still be
                // alive to make a second call on the NEXT tick — before the fix this never happens
                // (the throw faults `loopTask`, so the count sticks at 1 forever) and this times out.
                do! pollUntil 10000 (fun () -> store.EvictIdleCallCount > 1)

                Expect.isGreaterThan store.EvictIdleCallCount 1 "the loop kept sweeping after its first EvictIdle call threw"
            }
            |> fun t -> t.GetAwaiter().GetResult()
    ]
