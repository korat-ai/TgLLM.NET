/// Acceptance for the MAF bridge's streaming turn (`bot.Streaming = Some interval`): a reply that
/// arrives as several increments shows live, paced to the coalescing cadence, and degrades to a
/// single message for an ordinary one-shot reply; a reply long enough to exceed Telegram's per
/// message length cap spills into a new message the instant it would overflow, continuing to
/// live-edit from there. `MafTextTurnTests.fs`/`MafBridgeApprovalTests.fs`/etc. cover the SAME
/// scenarios with `bot.Streaming = None` — this file never touches that config, so their own
/// unchanged pass is the "streaming-off parity" gate.
module TgLLM.Integration.Tests.MafStreamingTurnTests

open System
open System.Text.Json.Nodes
open System.Threading.Tasks
open Expecto
open TgLLM.Core
open TgLLM.FSharp
open TgLLM.Integration.Tests.FakeBotApiServer
open TgLLM.Integration.Tests.MafScriptedAgent

let private field (key: string) (node: JsonNode) : JsonNode =
    match node.[key] |> Option.ofObj with
    | Some c -> c
    | None -> failwithf "expected JSON field '%s' in %s" key (node.ToJsonString())

let private asString (node: JsonNode) : string = node.AsValue().GetValue<string>()
let private asInt64 (node: JsonNode) : int64 = node.AsValue().GetValue<int64>()

let private pollUntil (ms: int) (predicate: unit -> bool) : Task =
    task {
        let mutable tries = 0

        while not (predicate ()) && tries < ms / 10 do
            do! Task.Delay 10
            tries <- tries + 1

        if not (predicate ()) then
            failtest "timed out waiting for the expected request"
    }

let private deliverText (server: FakeBotApiServer) (updateId: int) (chat: int64) (messageId: int) (userId: int64) (firstName: string) (text: string) : unit =
    server.EnqueueResult("getUpdates", TelegramJson.batch [ TelegramJson.textMessageUpdate updateId chat messageId userId firstName text ])

/// Starts a streaming-configured bridge over `server`, sharing `clock` between the bot's own
/// `WithClock` and the scripted agent's own `advanceClock` callback (mirrors
/// `MafDurableCoverageTests.fs`'s own shared-clock pattern) so a `StreamsThen` step's clock-advance
/// and this bot's own `IsDue`/`MarkEmitted` reads agree on "now".
let private startStreamingBridge (server: FakeBotApiServer) (agent: ScriptedAgent) (clock: Clock) : Task<TgLLM.Maf.MafBridge> =
    let tools = ToolRegistry.create ()

    let config =
        (TgBotConfig.create "123456789:TEST-fake-token")
            .WithBaseUrl(server.BaseUrl)
            .WithTools(tools)
            .WithClock(clock)
            .WithStreaming() // the built-in default cadence (1.5s)

    TgLLM.Maf.Maf.startPolling config agent

[<Tests>]
let mafStreamingTurnTests =
    testList "MafBridge streaming turn" [

        testCaseAsync "a reply that streams across several paced deltas shows one initial send plus edits at the coalescing cadence, ending on the complete text"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9700L
                    let mutable now = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                    let clock: Clock = fun () -> now

                    let steps =
                        [ "Hello", TimeSpan.Zero // first content-bearing delta — sent immediately, no cadence gate
                          ", world", TimeSpan.FromSeconds 2.0 // past the 1.5s default interval since the send — due
                          "!", TimeSpan.FromSeconds 0.5 ] // only 0.5s after the last emit — NOT due yet

                    let agent =
                        ScriptedAgent([ StreamsThen(steps, EndsEmpty) ], advanceClock = (fun span -> now <- now + span))

                    use! bridge = startStreamingBridge server agent clock
                    ignore bridge

                    deliverText server 1 chat 5 4300L "Nadia" "Hi agent"
                    do! pollUntil 15000 (fun () -> (server.RequestsFor "editMessageText") |> List.length >= 2)

                    let sends = server.RequestsFor "sendMessage"
                    let edits = server.RequestsFor "editMessageText"

                    Expect.equal (List.length sends) 1 "exactly one initial send for the whole turn"
                    Expect.equal ((sends.Head.Body |> Option.get) |> field "text" |> asString) "Hello" "the initial send carries only the first delta — sent before the SECOND delta ever arrived"

                    Expect.equal (List.length edits) 2 "one edit for the delta that cleared the cadence gate, plus the mandatory final flush for the delta that never got its own periodic tick"
                    Expect.equal ((edits[0].Body |> Option.get) |> field "text" |> asString) "Hello, world" "the FIRST edit reflects the two deltas coalesced together, before the third delta ever arrived"
                    Expect.equal ((edits[1].Body |> Option.get) |> field "text" |> asString) "Hello, world!" "the LAST edit (the mandatory final flush) is the complete concatenated text"

                    for edit in edits do
                        Expect.equal (edit.Body |> Option.get |> field "message_id" |> asInt64) 1L "every edit targets the SAME (originally sent) message — this reply never spills"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "an ordinary one-shot streamed reply degrades to a single send and no edit at all"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9701L
                    let mutable now = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                    let clock: Clock = fun () -> now

                    let agent = ScriptedAgent([ RepliesWith "Hello back!" ], advanceClock = (fun span -> now <- now + span))
                    use! bridge = startStreamingBridge server agent clock
                    ignore bridge

                    deliverText server 1 chat 5 4301L "Nadia" "Hi agent"
                    do! pollUntil 15000 (fun () -> (server.RequestsFor "sendMessage") |> List.isEmpty |> not)

                    // Nothing ever turns up an edit for this turn — assert after a short, deterministic
                    // settle rather than racing the bridge's own (already-complete) turn.
                    do! Task.Delay 200

                    let sends = server.RequestsFor "sendMessage"
                    let edits = server.RequestsFor "editMessageText"

                    Expect.equal (List.length sends) 1 "the complete reply is carried by the ONE send"
                    Expect.equal ((sends.Head.Body |> Option.get) |> field "text" |> asString) "Hello back!" "the send carries the complete reply verbatim"
                    Expect.isEmpty edits "the single send already shows the complete text — the finalize step's own mandatory flush has nothing pending, so it is never an actual edit call"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a reply long enough to exceed the per-message cap spills into a second message immediately, continuing to live-edit from there"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9702L
                    let mutable now = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                    let clock: Clock = fun () -> now

                    // The FIRST delta (4090 chars, ending in a single space) fits under the cap on its
                    // own and is sent as an ordinary message. The SECOND delta then pushes the running
                    // total past MessageText.MaxLength (4096) — the overflow is only discovered once
                    // this delta arrives, exercising the "retire an EXISTING message" rollover branch
                    // (MessageSplitting.split's own whitespace-preferred boundary lands on the single
                    // space already in the first delta).
                    let firstChunk = String.replicate 4089 "a" + " "
                    let overflowTail = "spilled over the cap"

                    let steps =
                        [ firstChunk, TimeSpan.Zero
                          overflowTail, TimeSpan.Zero ] // arrives immediately after — no cadence gate on a rollover

                    let agent =
                        ScriptedAgent([ StreamsThen(steps, EndsEmpty) ], advanceClock = (fun span -> now <- now + span))

                    use! bridge = startStreamingBridge server agent clock
                    ignore bridge

                    deliverText server 1 chat 5 4302L "Nadia" "Hi agent"
                    do! pollUntil 15000 (fun () -> (server.RequestsFor "sendMessage") |> List.length >= 2)

                    let sends = server.RequestsFor "sendMessage"
                    let edits = server.RequestsFor "editMessageText"

                    Expect.equal (List.length sends) 2 "the first message's own initial send, plus the rollover's immediate second send"
                    Expect.equal ((sends[0].Body |> Option.get) |> field "text" |> asString) (String.replicate 4089 "a") "the FIRST message's initial send is the first delta, trailing whitespace trimmed"
                    Expect.equal ((sends[1].Body |> Option.get) |> field "text" |> asString) overflowTail "the SECOND message's own send carries exactly the overflow tail, leading whitespace trimmed at the split boundary"

                    Expect.equal (List.length edits) 1 "exactly one mandatory retiring edit — the FIRST message's own final content, at the point the overflow was discovered"
                    let retiringEdit = edits.Head.Body |> Option.get
                    Expect.equal (retiringEdit |> field "text" |> asString) (String.replicate 4089 "a") "the retiring edit's own text is the SAME as the first message's own content — the split boundary landed on the space already there"
                    Expect.equal (retiringEdit |> field "message_id" |> asInt64) 1L "the retiring edit targets the FIRST message — the rollover's own send is a NEW message, never a second edit"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a single delta that already exceeds the cap with no whitespace anywhere falls back to a hard cut at exactly the cap, before any message has ever been sent"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9703L
                    let mutable now = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                    let clock: Clock = fun () -> now

                    // ONE unbroken 4200-char token, with NO earlier message ever having been sent — the
                    // very FIRST update the stream ever produces already overflows on its own, so both
                    // halves of the split reach the wire via ordinary SENDS (never an edit): the head
                    // chunk becomes the first-ever message, the tail becomes the second.
                    // MessageSplitting.split's own hard-cut fallback (exactly maxLen characters) decides
                    // the boundary, since there is no whitespace anywhere in the search window.
                    let unbroken = String.replicate 4200 "x"

                    let agent =
                        ScriptedAgent([ StreamsThen([ unbroken, TimeSpan.Zero ], EndsEmpty) ], advanceClock = (fun span -> now <- now + span))

                    use! bridge = startStreamingBridge server agent clock
                    ignore bridge

                    deliverText server 1 chat 5 4303L "Nadia" "Hi agent"
                    do! pollUntil 15000 (fun () -> (server.RequestsFor "sendMessage") |> List.length >= 2)

                    // No amount of extra polling ever turns up an edit for this turn.
                    do! Task.Delay 200

                    let sends = server.RequestsFor "sendMessage"
                    let edits = server.RequestsFor "editMessageText"

                    Expect.equal (List.length sends) 2 "the head chunk's own send, plus the tail's own send — neither ever had an EXISTING message to edit"
                    Expect.isEmpty edits "nothing was ever sent before the overflow was discovered, so there is no earlier message for a rollover to retire with an edit"

                    let headText = (sends[0].Body |> Option.get) |> field "text" |> asString
                    let tailText = (sends[1].Body |> Option.get) |> field "text" |> asString

                    Expect.equal headText.Length 4096 "the FIRST send is cut at EXACTLY the cap — the hard-cut fallback, since there is no whitespace anywhere in the window"
                    Expect.equal tailText.Length (unbroken.Length - 4096) "the SECOND send carries exactly the remaining overflow"
                    Expect.equal (headText + tailText) unbroken "the hard cut loses no characters — the two chunks concatenate back to the original unbroken text"
                }
                |> Async.AwaitTask
        }
    ]
