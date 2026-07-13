/// Acceptance for MAF's startup barrier and build-failure cleanup: queued updates must wait for the
/// bridge and its tools, while a failed build must cancel the paused bot rather than leak it.
module TgLLM.Integration.Tests.MafStartupFailureTests

open System.Threading.Tasks
open Expecto
open TgLLM.Core
open TgLLM.FSharp
open TgLLM.Maf
open TgLLM.Integration.Tests.FakeBotApiServer
open TgLLM.Integration.Tests.MafScriptedAgent

[<Tests>]
let mafStartupFailureTests =
    testList "MafBridge startup-failure cleanup" [

        testCaseAsync "a text update already waiting at startup is handled after the bridge is ready"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9901L
                    let agent = ScriptedAgent [ RepliesWith "Ready." ]

                    server.EnqueueResult("getUpdates", TelegramJson.batch [ TelegramJson.textMessageUpdate 1 chat 1 chat "Tester" "Hello" ])

                    let config =
                        (TgBotConfig.create "123456789:TEST-fake-token")
                            .WithBaseUrl(server.BaseUrl)
                            .WithTools(ToolRegistry.create ())

                    use! bridge = Maf.startPolling config agent

                    let mutable tries = 0

                    while List.isEmpty (server.RequestsFor "sendMessage") && tries < 1500 do
                        do! Task.Delay 10
                        tries <- tries + 1

                    Expect.equal agent.RunCount 1 "the startup backlog message reaches the initialized bridge exactly once"
                    Expect.equal (List.length (server.RequestsFor "sendMessage")) 1 "the agent's reply reaches Telegram"
                }
                |> Async.AwaitTask
        }

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
