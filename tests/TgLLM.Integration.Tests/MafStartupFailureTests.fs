/// Acceptance for `Maf.startPolling`/`startPollingWith`'s own build-failure path: `TgBot.startPolling`
/// already starts the bot (long-polling in the background) BEFORE `BridgeBuild.build` ever runs —
/// there is no earlier point to build the bridge at, since the config-time `OnMessage` handler needs
/// the bot to exist first. If `build` then throws (no `.WithTools` wired, or a double-attach), the
/// started bot must not be leaked — polling forever with nothing left able to stop it.
module TgLLM.Integration.Tests.MafStartupFailureTests

open System.Threading.Tasks
open Expecto
open TgLLM.FSharp
open TgLLM.Maf
open TgLLM.Integration.Tests.FakeBotApiServer
open TgLLM.Integration.Tests.MafScriptedAgent

[<Tests>]
let mafStartupFailureTests =
    testList "MafBridge startup-failure cleanup" [

        testCaseAsync "Maf.startPolling on a config with no .WithTools fails fast and leaves no orphaned poller running"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let agent = ScriptedAgent []

                    // Deliberately no `.WithTools` — `BridgeBuild.build` fails fast on this.
                    let config = (TgBotConfig.create "123456789:TEST-fake-token").WithBaseUrl(server.BaseUrl)

                    let! threw =
                        task {
                            try
                                let! _ = Maf.startPolling config agent
                                return false
                            with :? System.InvalidOperationException ->
                                return true
                        }

                    Expect.isTrue threw "building the bridge fails fast without a Tool Router wired in"

                    // Give any still-running poller a real window to make another `getUpdates`
                    // call, then confirm the count stopped growing — proof the bot was disposed,
                    // not merely that ONE more poll didn't happen to land in a race.
                    let countRightAfterFailure = List.length (server.RequestsFor "getUpdates")
                    do! Task.Delay 300
                    let countAfterWaiting = List.length (server.RequestsFor "getUpdates")

                    Expect.equal
                        countAfterWaiting
                        countRightAfterFailure
                        "no further polling happened after the failed build — the started bot was disposed, not leaked"
                }
                |> Async.AwaitTask
        }
    ]
