/// A bot's own binding-eviction sweep runs automatically once wired up:
/// `IBindingStore.EvictExpired` has no production caller of its own, so before `TgBot.wireBot`
/// started a `BindingEvictionSweeper`, an expiring/expired binding accumulated forever regardless
/// of which store backed a bot. No test here ever calls `EvictExpired`/`SweepOnce` itself — the
/// whole point is that a plain `TgBot.startPolling` sweeps on its own.
module TgLLM.Integration.Tests.BindingEvictionSweepWiringTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open FSharp.UMX
open TgLLM.Core
open TgLLM.FSharp
open TgLLM.Integration.Tests.FakeBotApiServer

let private toolName (s: string) : ToolName =
    match ToolName.create s with
    | Ok n -> n
    | Error e -> failwithf "test setup: unreachable %A" e

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
let bindingEvictionSweepWiringTests =
    testList "TgBot binding-eviction sweep wiring" [

        testCaseAsync "a plain TgBot.startPolling sweeps an already-expired binding on its own, with a short interval and a fixed clock"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let store = InMemoryBindingStore() :> IBindingStore
                    let token = CallbackToken.generate ()
                    let expiresAt = DateTimeOffset.UnixEpoch.AddDays 1.0
                    let binding = { ToolBinding.create token (toolName "stale") None with ExpiresAt = Some expiresAt }
                    do! store.Save([ binding ], CancellationToken.None)

                    // A FIXED clock, already past the expiry — only the bot's OWN background sweep
                    // (not this test) decides when the binding disappears.
                    let now = expiresAt.AddDays 1.0

                    let config =
                        (TgBotConfig.create "123456789:TEST-fake-token")
                            .WithBaseUrl(server.BaseUrl)
                            .WithBindingStore(store)
                            .WithClock(fun () -> now)
                            .WithBindingEvictionInterval(TimeSpan.FromMilliseconds 20.0)

                    use! bot = TgBot.startPolling config
                    ignore bot // the bot only needs to be RUNNING; no press is ever sent

                    do!
                        pollUntil 2000 (fun () ->
                            match (store.TryGet(token, CancellationToken.None)).AsTask().GetAwaiter().GetResult() with
                            | ValueNone -> true
                            | ValueSome _ -> false)

                    let! stillThere = store.TryGet(token, CancellationToken.None)
                    Expect.equal stillThere ValueNone "a running bot sweeps an expired binding on its own, with no host code calling EvictExpired"
                }
                |> Async.AwaitTask
        }
    ]
