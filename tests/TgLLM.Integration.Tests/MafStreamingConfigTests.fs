/// Acceptance for the streaming opt-in's own config surface (`CommonConfig.Streaming`,
/// `TgBotConfig.WithStreaming`/`TgWebhookConfig.WithStreaming`): unconfigured stays `None`; the
/// zero-arg overload applies the built-in default cadence; the one-arg overload overrides it; both
/// long-polling and webhook façades expose the SAME two overloads. Proven both directly against the
/// config record (the cheapest, most direct check) and through a live bridge's own `bot.Streaming`
/// (mirroring how the durable-session opt-in's own config surface is proven through a built bridge
/// elsewhere in this suite) — a host reading `TgBotConfig`/`TgWebhookConfig` never has to guess
/// which layer actually carries the setting.
module TgLLM.Integration.Tests.MafStreamingConfigTests

open System
open System.Threading.Tasks
open Expecto
open TgLLM.Core
open TgLLM.FSharp
open TgLLM.Maf
open TgLLM.Integration.Tests.FakeBotApiServer
open TgLLM.Integration.Tests.MafScriptedAgent

let private pollUntil (ms: int) (predicate: unit -> bool) : Task =
    task {
        let mutable tries = 0

        while not (predicate ()) && tries < ms / 10 do
            do! Task.Delay 10
            tries <- tries + 1

        if not (predicate ()) then
            failtest "timed out waiting for the expected request"
    }

[<Tests>]
let mafStreamingConfigTests =
    testList "MafBridge streaming config surface" [

        test "CommonConfig.create defaults Streaming to None" {
            let common = CommonConfig.create "123456789:TEST-fake-token"
            Expect.isNone common.Streaming "an unconfigured bot never turns streaming on"
        }

        test "non-positive lifecycle durations are rejected at configuration time" {
            let config = TgBotConfig.create "123456789:TEST-fake-token"

            Expect.throwsT<ArgumentException>
                (fun () -> config.WithIdleChatEviction(TimeSpan.Zero) |> ignore)
                "idle chat eviction cannot run with a zero duration"

            Expect.throwsT<ArgumentException>
                (fun () -> config.WithBindingEvictionInterval(TimeSpan.FromSeconds -1.0) |> ignore)
                "a background sweep interval must be positive"

            Expect.throwsT<ArgumentException>
                (fun () -> config.WithSessionStore(InMemorySessionStore(), TimeSpan.Zero) |> ignore)
                "durable session idle eviction must be positive"
        }

        testCaseAsync "a non-positive MAF approval expiry is rejected during bridge construction"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let agent = ScriptedAgent [ RepliesWith "unused" ]

                    let config =
                        (TgBotConfig.create "123456789:TEST-fake-token")
                            .WithBaseUrl(server.BaseUrl)
                            .WithTools(ToolRegistry.create ())

                    let options =
                        { MafBridgeOptions.defaults with
                            ApprovalExpiry = ValueSome TimeSpan.Zero }

                    let mutable threw = false

                    try
                        use! _bridge = Maf.startPollingWith options config agent
                        ()
                    with :? ArgumentException ->
                        threw <- true

                    Expect.isTrue threw "an immediately-expired approval keyboard is a configuration error"
                }
                |> Async.AwaitTask
        }

        test "TgBotConfig.WithStreaming() applies the built-in default cadence (1.5s)" {
            let config = (TgBotConfig.create "123456789:TEST-fake-token").WithStreaming()
            Expect.equal config.Common.Streaming (Some(TimeSpan.FromSeconds 1.5)) "the zero-arg overload applies the built-in default coalescing interval"
        }

        test "TgBotConfig.WithStreaming(custom) overrides the default cadence" {
            let custom = TimeSpan.FromSeconds 0.5

            let config =
                (TgBotConfig.create "123456789:TEST-fake-token").WithStreaming(custom)

            Expect.equal config.Common.Streaming (Some custom) "the one-arg overload carries the caller's own interval, not the built-in default"
        }

        test "TgWebhookConfig.WithStreaming() applies the SAME built-in default cadence as TgBotConfig" {
            let config =
                TgWebhookConfig.create("123456789:TEST-fake-token", "https://example.test/ignored", "s3cret").WithStreaming()

            Expect.equal config.Common.Streaming (Some(TimeSpan.FromSeconds 1.5)) "the webhook façade's zero-arg overload matches the polling façade's own default"
        }

        test "TgWebhookConfig.WithStreaming(custom) overrides the default cadence, mirroring TgBotConfig's own overload" {
            let custom = TimeSpan.FromSeconds 3.0

            let config =
                TgWebhookConfig
                    .create("123456789:TEST-fake-token", "https://example.test/ignored", "s3cret")
                    .WithStreaming(custom)

            Expect.equal config.Common.Streaming (Some custom) "the webhook façade's one-arg overload carries the caller's own interval"
        }

        testCaseAsync "a long-polling bridge with no WithStreaming call exposes bot.Streaming = None — the opt-in stays off end to end"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let tools = ToolRegistry.create ()
                    let agent = ScriptedAgent [ RepliesWith "Hello back!" ]

                    let config =
                        (TgBotConfig.create "123456789:TEST-fake-token")
                            .WithBaseUrl(server.BaseUrl)
                            .WithTools(tools)

                    use! bridge = Maf.startPolling config agent

                    Expect.isNone bridge.Bot.Streaming "with no WithStreaming call, the built bot's own exposed setting stays off"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a long-polling bridge built with WithStreaming() exposes bot.Streaming = Some 1.5s end to end"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let tools = ToolRegistry.create ()
                    let agent = ScriptedAgent [ RepliesWith "Hello back!" ]

                    let config =
                        (TgBotConfig.create "123456789:TEST-fake-token")
                            .WithBaseUrl(server.BaseUrl)
                            .WithTools(tools)
                            .WithStreaming()

                    use! bridge = Maf.startPolling config agent

                    Expect.equal bridge.Bot.Streaming (Some(TimeSpan.FromSeconds 1.5)) "the built bot's own exposed setting reflects the zero-arg overload's default cadence"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a long-polling bridge built with WithStreaming(custom) exposes that SAME custom interval end to end"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let tools = ToolRegistry.create ()
                    let agent = ScriptedAgent [ RepliesWith "Hello back!" ]
                    let custom = TimeSpan.FromSeconds 0.75

                    let config =
                        (TgBotConfig.create "123456789:TEST-fake-token")
                            .WithBaseUrl(server.BaseUrl)
                            .WithTools(tools)
                            .WithStreaming(custom)

                    use! bridge = Maf.startPolling config agent

                    Expect.equal bridge.Bot.Streaming (Some custom) "the built bot's own exposed setting carries the caller's own custom interval, not the built-in default"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a webhook bridge built with WithStreaming() exposes bot.Streaming = Some 1.5s, mirroring the polling façade"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let tools = ToolRegistry.create ()
                    let agent = ScriptedAgent [ RepliesWith "Hello back!" ]

                    let config =
                        TgWebhookConfig
                            .create("123456789:TEST-fake-token", "https://example.test/ignored", "s3cret")
                            .WithBaseUrl(server.BaseUrl)
                            .WithTools(tools)
                            .WithStreaming()

                    use! bridge = Maf.startWebhook config agent

                    Expect.equal bridge.Bot.Streaming (Some(TimeSpan.FromSeconds 1.5)) "the webhook façade's own built bot exposes the SAME default cadence as long polling"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a custom cadence (0.5s) actually paces live edits at that interval, distinct from the 1.5s default used elsewhere"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9750L
                    let mutable now = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                    let clock: Clock = fun () -> now
                    let custom = TimeSpan.FromSeconds 0.5

                    // Three deltas: the first is the initial send (never gated); the second arrives
                    // 0.7s later — past the CUSTOM 0.5s interval, but still well short of the built-in
                    // 1.5s default, so an edit here only fires because the custom cadence is actually
                    // in effect. The third arrives only 0.2s after that — inside the custom interval —
                    // and is picked up only by the mandatory final flush, never its own periodic edit.
                    let steps =
                        [ "Hello", TimeSpan.Zero
                          ", world", TimeSpan.FromSeconds 0.7
                          "!", TimeSpan.FromSeconds 0.2 ]

                    let agent =
                        ScriptedAgent([ StreamsThen(steps, EndsEmpty) ], advanceClock = (fun span -> now <- now + span))

                    let tools = ToolRegistry.create ()

                    let config =
                        (TgBotConfig.create "123456789:TEST-fake-token")
                            .WithBaseUrl(server.BaseUrl)
                            .WithTools(tools)
                            .WithClock(clock)
                            .WithStreaming(custom)

                    use! bridge = Maf.startPolling config agent
                    ignore bridge

                    server.EnqueueResult("getUpdates", TelegramJson.batch [ TelegramJson.textMessageUpdate 1 chat 5 4350L "Nadia" "Hi agent" ])
                    do! pollUntil 15000 (fun () -> (server.RequestsFor "editMessageText") |> List.length >= 2)

                    let sends = server.RequestsFor "sendMessage"
                    let edits = server.RequestsFor "editMessageText"

                    Expect.equal (List.length sends) 1 "exactly one initial send for the whole turn"

                    Expect.equal
                        (List.length edits)
                        2
                        "one edit for the delta that cleared the CUSTOM 0.5s cadence gate, plus the mandatory final flush for the delta that never got its own periodic tick"
                }
                |> Async.AwaitTask
        }

        test "TgBotConfig.WithStreaming(TimeSpan.Zero) throws — a zero interval would fire an edit on every single delta" {
            Expect.throws
                (fun () -> (TgBotConfig.create "123456789:TEST-fake-token").WithStreaming(TimeSpan.Zero) |> ignore)
                "a zero coalescing interval defeats coalescing entirely, guaranteeing sustained 429s on the very first real turn"
        }

        test "TgBotConfig.WithStreaming(negative) throws" {
            Expect.throws
                (fun () -> (TgBotConfig.create "123456789:TEST-fake-token").WithStreaming(TimeSpan.FromSeconds -1.0) |> ignore)
                "a negative coalescing interval is nonsensical and must be refused the same way a zero one is"
        }

        test "TgBotConfig.WithStreaming(positive) still works — the guard only refuses zero/negative" {
            let config =
                (TgBotConfig.create "123456789:TEST-fake-token").WithStreaming(TimeSpan.FromSeconds 0.25)

            Expect.equal config.Common.Streaming (Some(TimeSpan.FromSeconds 0.25)) "a small but strictly positive interval is still accepted unchanged"
        }

        test "TgWebhookConfig.WithStreaming(TimeSpan.Zero) throws, mirroring TgBotConfig's own guard" {
            Expect.throws
                (fun () ->
                    TgWebhookConfig
                        .create("123456789:TEST-fake-token", "https://example.test/ignored", "s3cret")
                        .WithStreaming(TimeSpan.Zero)
                    |> ignore)
                "the webhook façade shares the SAME `CommonConfig.withStreaming` guard as long polling"
        }

        test "TgWebhookConfig.WithStreaming(negative) throws, mirroring TgBotConfig's own guard" {
            Expect.throws
                (fun () ->
                    TgWebhookConfig
                        .create("123456789:TEST-fake-token", "https://example.test/ignored", "s3cret")
                        .WithStreaming(TimeSpan.FromSeconds -2.0)
                    |> ignore)
                "the webhook façade shares the SAME `CommonConfig.withStreaming` guard as long polling"
        }
    ]
