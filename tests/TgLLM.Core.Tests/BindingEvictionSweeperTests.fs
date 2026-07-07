/// Tests for `BindingEvictionSweeper`: the background sweeper that periodically calls
/// `IBindingStore.EvictExpired` so a long-lived bot's store doesn't grow unbounded — before this
/// type existed, `EvictExpired` had zero production callers, so an expiring/expired binding
/// accumulated forever regardless of which store backed a bot.
module TgLLM.Core.Tests.BindingEvictionSweeperTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open TgLLM.Core

let private toolName (s: string) : ToolName =
    match ToolName.create s with
    | Ok n -> n
    | Error e -> failwithf "test setup: unreachable %A" e

/// Polls `predicate` until it's true or `ms` elapses, failing the test on timeout — matches
/// `DispatcherEvictionTests.fs`'s own helper, used here for the background-loop test only (the
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

[<Tests>]
let bindingEvictionSweeperTests =
    testList "BindingEvictionSweeper" [

        testCase "SweepOnce removes an expired binding using the injected clock — no host code beyond construction" <| fun _ ->
            task {
                let store = InMemoryBindingStore() :> IBindingStore
                let token = CallbackToken.generate ()
                let expiresAt = DateTimeOffset.UnixEpoch.AddDays 1.0
                let binding = { ToolBinding.create token (toolName "stale") None with ExpiresAt = Some expiresAt }
                do! store.Save([ binding ], CancellationToken.None)

                // The fixed clock is well PAST the binding's expiry — deterministic, no real waiting.
                let now = expiresAt.AddDays 1.0
                use sweeper = new BindingEvictionSweeper(store, (fun () -> now), interval = TimeSpan.FromHours 1.0)

                let! removed = sweeper.SweepOnce()

                Expect.equal removed 1 "the sweep removed exactly the one expired binding"

                let! stillThere = store.TryGet(token, CancellationToken.None)
                Expect.equal stillThere ValueNone "the expired binding no longer resolves after the sweep"
            }
            |> fun t -> t.GetAwaiter().GetResult()

        testCase "SweepOnce leaves a still-live binding untouched" <| fun _ ->
            task {
                let store = InMemoryBindingStore() :> IBindingStore
                let token = CallbackToken.generate ()
                let now = DateTimeOffset.UnixEpoch.AddDays 1.0
                let binding = { ToolBinding.create token (toolName "fresh") None with ExpiresAt = Some(now.AddHours 1.0) }
                do! store.Save([ binding ], CancellationToken.None)

                use sweeper = new BindingEvictionSweeper(store, (fun () -> now), interval = TimeSpan.FromHours 1.0)
                let! removed = sweeper.SweepOnce()

                Expect.equal removed 0 "a still-live binding is not swept"

                let! stillThere = store.TryGet(token, CancellationToken.None)
                Expect.equal stillThere (ValueSome binding) "the live binding still resolves"
            }
            |> fun t -> t.GetAwaiter().GetResult()

        testCase "the background loop itself sweeps an expired binding automatically, with no host code ever calling SweepOnce" <| fun _ ->
            task {
                let store = InMemoryBindingStore() :> IBindingStore
                let token = CallbackToken.generate ()
                let expiresAt = DateTimeOffset.UnixEpoch.AddDays 1.0
                let binding = { ToolBinding.create token (toolName "stale") None with ExpiresAt = Some expiresAt }
                do! store.Save([ binding ], CancellationToken.None)

                // A FIXED clock, already past the expiry from the moment the sweeper starts — only
                // the background loop's own timer (not this test) decides when the sweep runs.
                let now = expiresAt.AddDays 1.0
                use sweeper = new BindingEvictionSweeper(store, (fun () -> now), interval = TimeSpan.FromMilliseconds 20.0)

                do!
                    pollUntil 2000 (fun () ->
                        match (store.TryGet(token, CancellationToken.None)).AsTask().GetAwaiter().GetResult() with
                        | ValueNone -> true
                        | ValueSome _ -> false)

                let! stillThere = store.TryGet(token, CancellationToken.None)
                Expect.equal stillThere ValueNone "the automatic background sweep removed the expired binding without any host code driving it"
            }
            |> fun t -> t.GetAwaiter().GetResult()

        testCase "DisposeAsync stops the background loop cleanly (no leaked timer, no exception)" <| fun _ ->
            task {
                let store = InMemoryBindingStore() :> IBindingStore
                let sweeper = new BindingEvictionSweeper(store, (fun () -> DateTimeOffset.UnixEpoch), interval = TimeSpan.FromMilliseconds 20.0)
                do! Task.Delay 50 // let the loop tick at least once
                do! (sweeper :> IAsyncDisposable).DisposeAsync().AsTask()
            }
            |> fun t -> t.GetAwaiter().GetResult()
    ]
