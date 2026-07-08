/// Acceptance for `MafBridge.DisposeAsync`'s own defensive shape: it must complete promptly and
/// without throwing even while a chat still has an in-flight turn holding that chat's own lock
/// (`Bridge.fs`'s `chatLocks`) — disposal never blocks waiting on a chat's turn to finish, and (per
/// `DisposeAsync`'s own doc comment) never disposes the shared `CancellationTokenSource` out from
/// under a turn that hasn't yet read `cts.Token` — only cancels it, so a queued turn that later
/// runs still reads a valid (if already-cancelled) token rather than hitting
/// `ObjectDisposedException`.
module TgLLM.Integration.Tests.MafBridgeDisposeTests

open System
open System.Threading.Tasks
open Expecto
open FSharp.UMX
open TgLLM.Core
open TgLLM.FSharp
open TgLLM.Maf
open TgLLM.Integration.Tests.FakeBotApiServer
open TgLLM.Integration.Tests.MafScriptedAgent

let private startBridge (server: FakeBotApiServer) (agent: ScriptedAgent) : Task<MafBridge> =
    let tools = ToolRegistry.create ()
    let config = (TgBotConfig.create "123456789:TEST-fake-token").WithBaseUrl(server.BaseUrl).WithTools(tools)
    Maf.startPolling config agent

[<Tests>]
let mafBridgeDisposeTests =
    testList "MafBridge disposal safety" [

        testCaseAsync "DisposeAsync completes promptly, without throwing, while a chat's turn is still in-flight holding that chat's own lock"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 7701L

                    let agent =
                        ScriptedAgent [
                            Delayed(TimeSpan.FromMilliseconds 300.0, RepliesWith "slow")
                        ]

                    let! bridge = startBridge server agent

                    // Fire the slow turn WITHOUT awaiting it — it acquires chat `7701L`'s own lock
                    // and holds it for ~300ms.
                    let inFlight = bridge.StartRun(UMX.tag<chatId> chat, "go")

                    // Give the turn a moment to actually enter its lock before disposing.
                    do! Task.Delay 50

                    let stopwatch = Diagnostics.Stopwatch.StartNew()
                    let! disposeCompleted = Task.WhenAny((bridge :> IAsyncDisposable).DisposeAsync().AsTask(), Task.Delay 5000)
                    stopwatch.Stop()

                    Expect.isLessThan
                        stopwatch.Elapsed.TotalMilliseconds
                        5000.0
                        "DisposeAsync must not block waiting for the in-flight turn's chat lock to free up"

                    // Let the in-flight turn actually finish so the test doesn't leave a dangling
                    // background task; `StartRun` never re-throws a turn failure (F# `Bridge.fs`'s
                    // own contract — see `OnTurnFailed`'s doc comment), so this always completes.
                    do! inFlight

                    ignore disposeCompleted
                }
                |> Async.AwaitTask
        }
    ]
