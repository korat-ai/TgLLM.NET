/// Reliability acceptance for the MAF bridge's streaming turn (`bot.Streaming = Some interval`):
/// Telegram's own rate limit (429, with and without a `Retry-After` hint) is absorbed at two tiers
/// — a non-blocking mid-stream back-off (`ReplyCoalescer.NotifyRateLimited`) and a bounded, blocking
/// retry for MANDATORY emits (`guardedEmit`, shared by the final flush, a split's retiring edit, and
/// a kept-narration approval render); a mid-stream throw is surfaced without stranding the chat for
/// its NEXT turn; an initial-send failure never reaches finalize/persist at all; a completed streamed
/// turn persists exactly like the SAME scripted turn would via the non-streaming path; an
/// empty/whitespace-only stream never sends a blank placeholder message. `MafStreamingTurnTests.fs`/
/// `MafStreamingApprovalTests.fs` cover the HAPPY-path streaming shapes on their own — this file is
/// specifically about failure/edge-case absorption.
module TgLLM.Integration.Tests.MafStreamingReliabilityTests

open System
open System.Threading
open System.Text.Json.Nodes
open System.Threading.Tasks
open Expecto
open FSharp.UMX
open TgLLM.Core
open TgLLM.FSharp
open TgLLM.Maf
open TgLLM.Integration.Tests.FakeBotApiServer
open TgLLM.Integration.Tests.MafScriptedAgent

let private field (key: string) (node: JsonNode) : JsonNode =
    match node.[key] |> Option.ofObj with
    | Some c -> c
    | None -> failwithf "expected JSON field '%s' in %s" key (node.ToJsonString())

let private at (i: int) (node: JsonNode) : JsonNode =
    match node.[i] |> Option.ofObj with
    | Some c -> c
    | None -> failwithf "expected JSON index %d in %s" i (node.ToJsonString())

let private asString (node: JsonNode) : string = node.AsValue().GetValue<string>()
let private asInt64 (node: JsonNode) : int64 = node.AsValue().GetValue<int64>()

let private callbackDataAt (row: int) (col: int) (sendBody: JsonNode) : string =
    sendBody |> field "reply_markup" |> field "inline_keyboard" |> at row |> at col |> field "callback_data" |> asString

let private buttonTextAt (row: int) (col: int) (sendBody: JsonNode) : string =
    sendBody |> field "reply_markup" |> field "inline_keyboard" |> at row |> at col |> field "text" |> asString

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

let private deliverTap (server: FakeBotApiServer) (updateId: int) (queryId: string) (token: string) (chat: int64) (messageId: int) (userId: int64) : Task =
    server.EnqueueResult("getUpdates", TelegramJson.batch [ TelegramJson.callbackQueryUpdate updateId queryId token chat messageId userId "Tester" ])
    Task.CompletedTask

/// Records every condition this file's tests need to assert on — a superset of the smaller
/// per-file `RecordingObserver`s elsewhere in this suite, since reliability tests need to see
/// several DIFFERENT channels (empty turns, turn failures, invalid output, AND the streaming-only
/// `OnStreamFailed`) rather than just one.
type private RecordingObserver() =
    let emptyTurns = ResizeArray<ChatId>()
    let turnFailures = ResizeArray<ChatId * exn>()
    let invalidOutputs = ResizeArray<ChatId * MafError>()
    let streamFailures = ResizeArray<ChatId * MessageId * exn>()

    member _.EmptyTurns: ChatId list = List.ofSeq emptyTurns
    member _.TurnFailures: (ChatId * exn) list = List.ofSeq turnFailures
    member _.InvalidOutputs: (ChatId * MafError) list = List.ofSeq invalidOutputs
    member _.StreamFailures: (ChatId * MessageId * exn) list = List.ofSeq streamFailures

    interface IMafObserver with
        member _.OnStaleDecision(_descriptor) = ()
        member _.OnMalformedDecision(_raw) = ()
        member _.OnResumeFailed(_descriptor, _error) = ()
        member _.OnEmptyTurn(chat) = emptyTurns.Add chat
        member _.OnInvalidOutput(chat, error) = invalidOutputs.Add(chat, error)
        member _.OnProjectionProblem(_problem) = ()
        member _.OnTurnFailed(chat, error) = turnFailures.Add(chat, error)

    interface IMafSessionObserver with
        member _.OnSessionRestoreFailed(_chat, _failure) = ()
        member _.OnSessionPersistFailed(_chat, _error) = ()

    interface IMafStreamingObserver with
        member _.OnStreamFailed(chat, liveMessage, error) = streamFailures.Add(chat, liveMessage, error)

/// Same shared-clock harness as `MafStreamingTurnTests.fs`/`MafStreamingApprovalTests.fs`'s own
/// `startStreamingBridge`, plus an explicit `options` parameter (mirrors
/// `MafStreamingApprovalTests.fs`'s own `…With` variant) so a test can wire the `RecordingObserver`
/// above.
let private startStreamingBridgeWith
    (server: FakeBotApiServer)
    (agent: ScriptedAgent)
    (clock: Clock)
    (options: MafBridgeOptions)
    : Task<MafBridge> =
    let tools = ToolRegistry.create ()

    let config =
        (TgBotConfig.create "123456789:TEST-fake-token")
            .WithBaseUrl(server.BaseUrl)
            .WithTools(tools)
            .WithClock(clock)
            .WithStreaming() // the built-in default cadence (1.5s)

    Maf.startPollingWith options config agent

let private startStreamingBridge (server: FakeBotApiServer) (agent: ScriptedAgent) (clock: Clock) : Task<MafBridge> =
    startStreamingBridgeWith server agent clock MafBridgeOptions.defaults

/// Same shape as `pollingConfig` in `MafDurableCoverageTests.fs`, plus `.WithStreaming()` — for
/// T029's durable-persist-parity comparison, which needs ONE bridge with streaming ON and a SEPARATE
/// one with it left OFF, both wired against session stores of the SAME concrete type.
let private streamingSessionConfig (server: FakeBotApiServer) (sessionStore: ISessionStore) : TgBotConfig =
    (TgBotConfig.create "123456789:TEST-fake-token")
        .WithBaseUrl(server.BaseUrl)
        .WithTools(ToolRegistry.create ())
        .WithSessionStore(sessionStore)
        .WithStreaming()

let private plainSessionConfig (server: FakeBotApiServer) (sessionStore: ISessionStore) : TgBotConfig =
    (TgBotConfig.create "123456789:TEST-fake-token")
        .WithBaseUrl(server.BaseUrl)
        .WithTools(ToolRegistry.create ())
        .WithSessionStore(sessionStore)

[<Tests>]
let mafStreamingReliabilityTests =
    testList "MafBridge streaming reliability" [

        testCaseAsync "a mid-stream 429 WITH a retry_after hint is absorbed: the edit is retried on a LATER tick, never raised to the observer, and the reply still completes"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9900L
                    let mutable now = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                    let clock: Clock = fun () -> now
                    let observer = RecordingObserver()
                    let options: MafBridgeOptions = { MafBridgeOptions.defaults with Observer = ValueSome(observer :> IMafObserver) }

                    // "Hello" sent immediately; ", world" arrives 2.0s later — past the 1.5s default
                    // interval, so an edit is attempted and hits the canned 429; "!" arrives 65s after
                    // THAT, well past the rate-limited gate (now + max(1.5, 61) = 63s from the failed
                    // attempt), so its own tick retries the SAME pending text and succeeds. The
                    // retry_after hint (61s) is deliberately ABOVE Telegram.Bot's own vendor-level
                    // `RetryThreshold` (60, `TelegramBotClientOptions.RetryThreshold`'s own default) —
                    // below that threshold, Telegram.Bot's OWN internal auto-retry (a real, blocking
                    // `Task.Delay` inside the vendor client, underneath this leaf's own code) would
                    // absorb the 429 itself before this leaf's own `NotifyRateLimited` ever saw it,
                    // proving the WRONG tier. Above the threshold, the vendor throws immediately with
                    // no delay of its own, and this leaf's own back-off (driven entirely by the virtual
                    // clock above, never a real sleep) is what is actually under test.
                    let steps =
                        [ "Hello", TimeSpan.Zero
                          ", world", TimeSpan.FromSeconds 2.0
                          "!", TimeSpan.FromSeconds 65.0 ]

                    let agent =
                        ScriptedAgent([ StreamsThen(steps, EndsEmpty) ], advanceClock = (fun span -> now <- now + span))

                    server.EnqueueError("editMessageText", 429, "Too Many Requests", retryAfterSeconds = 61)

                    use! bridge = startStreamingBridgeWith server agent clock options
                    ignore bridge

                    deliverText server 1 chat 5 4900L "Nadia" "Hi agent"
                    do! pollUntil 15000 (fun () -> (server.RequestsFor "editMessageText") |> List.length >= 2)

                    let edits = server.RequestsFor "editMessageText"
                    Expect.equal (List.length edits) 2 "one failed attempt, plus the retry that actually reached the wire — no THIRD edit, since the retry's own text already matches the final reply"

                    let lastEdit = edits |> List.last |> fun r -> r.Body |> Option.get
                    Expect.equal (lastEdit |> field "text" |> asString) "Hello, world!" "the retried edit carries the complete text, unchanged and un-dropped by the rate limit"
                    Expect.equal (lastEdit |> field "message_id" |> asInt64) 1L "the retry targets the SAME message the rate-limited attempt did"

                    Expect.isEmpty observer.InvalidOutputs "a 429 absorbed by the coalescer's own back-off never reaches OnInvalidOutput"
                    Expect.isEmpty observer.TurnFailures "the turn completed normally — never OnTurnFailed"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a mid-stream 429 with NO retry_after hint is absorbed exactly the same way, with no NullReferenceException anywhere"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9901L
                    let mutable now = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                    let clock: Clock = fun () -> now
                    let observer = RecordingObserver()
                    let options: MafBridgeOptions = { MafBridgeOptions.defaults with Observer = ValueSome(observer :> IMafObserver) }

                    let steps =
                        [ "Hello", TimeSpan.Zero
                          ", world", TimeSpan.FromSeconds 2.0
                          "!", TimeSpan.FromSeconds 2.0 ]

                    let agent =
                        ScriptedAgent([ StreamsThen(steps, EndsEmpty) ], advanceClock = (fun span -> now <- now + span))

                    // No `retryAfterSeconds` at all — the canned error body carries no `parameters`
                    // field, so `ex.Parameters`/`ex.Parameters.RetryAfter` are both null on the F#
                    // side; `NotifyRateLimited` still falls back to the ordinary interval.
                    server.EnqueueError("editMessageText", 429, "Too Many Requests")

                    use! bridge = startStreamingBridgeWith server agent clock options
                    ignore bridge

                    deliverText server 1 chat 5 4901L "Nadia" "Hi agent"
                    do! pollUntil 15000 (fun () -> (server.RequestsFor "editMessageText") |> List.length >= 2)

                    let edits = server.RequestsFor "editMessageText"
                    let lastEdit = edits |> List.last |> fun r -> r.Body |> Option.get
                    Expect.equal (lastEdit |> field "text" |> asString) "Hello, world!" "absorption completes the SAME way with no retry_after hint at all"

                    Expect.isEmpty observer.InvalidOutputs "a 429 with no retry_after hint is STILL absorbed silently — never OnInvalidOutput"
                    Expect.isEmpty observer.TurnFailures "no exception (NullReferenceException or otherwise) ever escaped to OnTurnFailed"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a 429 at the end-of-stream mandatory final flush is absorbed by guardedEmit's own bounded retry — the reply still completes"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9902L
                    let mutable now = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                    let clock: Clock = fun () -> now
                    let observer = RecordingObserver()
                    let options: MafBridgeOptions = { MafBridgeOptions.defaults with Observer = ValueSome(observer :> IMafObserver) }

                    // The second delta arrives only 0.5s after the first — inside the 1.5s default
                    // interval — so NO mid-stream edit is ever attempted; the ONLY editMessageText
                    // call for this whole turn is the end-of-stream mandatory final flush.
                    let steps = [ "Hello", TimeSpan.Zero; ", world", TimeSpan.FromSeconds 0.5 ]

                    let agent =
                        ScriptedAgent([ StreamsThen(steps, EndsEmpty) ], advanceClock = (fun span -> now <- now + span))

                    // NO `retryAfterSeconds` — the canned error carries no `parameters.retry_after` at
                    // all, so Telegram.Bot's OWN vendor-level auto-retry never engages (its own
                    // decompiled condition, `Parameters?.RetryAfter <= RetryThreshold`, is false when
                    // `RetryAfter` is null) and the `ApiRequestException` reaches THIS leaf's own
                    // `guardedEmit` on the very first attempt — the tier actually under test here.
                    // `guardedEmit`'s own retry delay defaults to a real one-second `Task.Delay` in
                    // that case (`Option.defaultValue 1`, `Bridge.fs`), accepted here since the stream
                    // has already ended by the time this runs.
                    server.EnqueueError("editMessageText", 429, "Too Many Requests")

                    use! bridge = startStreamingBridgeWith server agent clock options
                    ignore bridge

                    deliverText server 1 chat 5 4902L "Nadia" "Hi agent"
                    do! pollUntil 15000 (fun () -> (server.RequestsFor "editMessageText") |> List.length >= 2)

                    let edits = server.RequestsFor "editMessageText"
                    Expect.equal (List.length edits) 2 "one failed attempt, plus guardedEmit's own successful retry"

                    let lastEdit = edits |> List.last |> fun r -> r.Body |> Option.get
                    Expect.equal (lastEdit |> field "text" |> asString) "Hello, world" "the retried final flush carries the complete reply"

                    Expect.isEmpty observer.InvalidOutputs "guardedEmit's own successful retry never reaches OnInvalidOutput"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a 429 with a retry_after > 60s at a guardedEmit site is CLAMPED to a small wait — the chat lock is never held for the full hint"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9950L
                    let mutable now = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                    let clock: Clock = fun () -> now
                    let observer = RecordingObserver()
                    let options: MafBridgeOptions = { MafBridgeOptions.defaults with Observer = ValueSome(observer :> IMafObserver) }

                    // Same construction as the end-of-stream mandatory-final-flush test above: the
                    // ONLY editMessageText call for this whole turn is guardedEmit's own mandatory
                    // final flush. `retryAfterSeconds = 61` is deliberately ABOVE Telegram.Bot's own
                    // vendor-level `RetryThreshold` (60) so the vendor's own internal auto-retry never
                    // absorbs it first — the `ApiRequestException` reaches THIS leaf's `guardedEmit`
                    // carrying a real 61-second hint, exactly the shape every 429 `guardedEmit` itself
                    // ever actually sees (Telegram.Bot already absorbs anything <= 60s internally).
                    // `guardedEmit`'s own `Task.Delay` is a REAL wall-clock wait — unlike the mid-stream
                    // `NotifyRateLimited` tier, it is NOT gated by the virtual `clock`/`advanceClock`
                    // above — so an un-clamped wait here would really block for 61 real seconds and
                    // this test would time out against `pollUntil`'s own budget.
                    let steps = [ "Hello", TimeSpan.Zero; ", world", TimeSpan.FromSeconds 0.5 ]

                    let agent =
                        ScriptedAgent([ StreamsThen(steps, EndsEmpty) ], advanceClock = (fun span -> now <- now + span))

                    server.EnqueueError("editMessageText", 429, "Too Many Requests", retryAfterSeconds = 61)

                    use! bridge = startStreamingBridgeWith server agent clock options
                    ignore bridge

                    let stopwatch = System.Diagnostics.Stopwatch.StartNew()
                    deliverText server 1 chat 5 4950L "Nadia" "Hi agent"
                    do! pollUntil 20000 (fun () -> (server.RequestsFor "editMessageText") |> List.length >= 2)
                    stopwatch.Stop()

                    Expect.isLessThan
                        stopwatch.Elapsed.TotalSeconds
                        20.0
                        "the retry was clamped to a small wait, not the full 61-second hint — the turn completed well under it"

                    let edits = server.RequestsFor "editMessageText"
                    Expect.equal (List.length edits) 2 "one failed attempt, plus guardedEmit's own successful (clamped) retry"

                    let lastEdit = edits |> List.last |> fun r -> r.Body |> Option.get
                    Expect.equal (lastEdit |> field "text" |> asString) "Hello, world" "the retried final flush carries the complete reply"

                    Expect.isEmpty observer.InvalidOutputs "guardedEmit's own successful retry never reaches OnInvalidOutput"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "EditNotFound at the end-of-stream mandatory final flush is reported via OnInvalidOutput, not silently swallowed"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9921L
                    let mutable now = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                    let clock: Clock = fun () -> now
                    let observer = RecordingObserver()
                    let options: MafBridgeOptions = { MafBridgeOptions.defaults with Observer = ValueSome(observer :> IMafObserver) }

                    // Same construction as the 429-at-final-flush test above: the second delta arrives
                    // inside the 1.5s default interval, so the ONLY editMessageText call for this whole
                    // turn is the end-of-stream mandatory final flush — and `guardedEmit` classifies
                    // THIS canned error as `Ok EditNotFound` (a successful HTTP call, never an
                    // exception), not `Error _`.
                    let steps = [ "Hello", TimeSpan.Zero; ", world", TimeSpan.FromSeconds 0.5 ]

                    let agent =
                        ScriptedAgent([ StreamsThen(steps, EndsEmpty) ], advanceClock = (fun span -> now <- now + span))

                    server.EnqueueError("editMessageText", 400, "Bad Request: message to edit not found")

                    use! bridge = startStreamingBridgeWith server agent clock options
                    ignore bridge

                    deliverText server 1 chat 5 4921L "Nadia" "Hi agent"
                    do! pollUntil 5000 (fun () -> not (List.isEmpty observer.InvalidOutputs))

                    Expect.equal (List.length observer.InvalidOutputs) 1 "the vanished final-flush target is reported exactly once — not silently dropped"

                    match observer.InvalidOutputs.Head with
                    | _, DeliveryFailed detail -> Expect.stringContains detail "no longer exists" "the reported error names the vanished message as the cause"
                    | other -> failwithf "expected DeliveryFailed, got %A" other

                    Expect.equal (List.length (server.RequestsFor "editMessageText")) 1 "the vanished target is reported on the FIRST attempt — EditNotFound is not itself retried"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a 429 at a split's retiring edit is absorbed by guardedEmit's own bounded retry"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9903L
                    let mutable now = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                    let clock: Clock = fun () -> now
                    let observer = RecordingObserver()
                    let options: MafBridgeOptions = { MafBridgeOptions.defaults with Observer = ValueSome(observer :> IMafObserver) }

                    // Same construction as `MafStreamingTurnTests.fs`'s own spill test: a first delta
                    // under the cap on its own, then a short tail that pushes the running total past
                    // the cap — the ONLY editMessageText call in this whole turn is the rollover's own
                    // mandatory retiring edit.
                    let firstChunk = String.replicate 4089 "a" + " "
                    let overflowTail = "spilled over the cap"
                    let steps = [ firstChunk, TimeSpan.Zero; overflowTail, TimeSpan.Zero ]

                    let agent =
                        ScriptedAgent([ StreamsThen(steps, EndsEmpty) ], advanceClock = (fun span -> now <- now + span))

                    // Same rationale as the final-flush test above: no `retryAfterSeconds`, so the
                    // vendor's own auto-retry never intercepts this — `guardedEmit`'s own retry is
                    // what's actually under test.
                    server.EnqueueError("editMessageText", 429, "Too Many Requests")

                    use! bridge = startStreamingBridgeWith server agent clock options
                    ignore bridge

                    deliverText server 1 chat 5 4903L "Nadia" "Hi agent"
                    do! pollUntil 15000 (fun () -> (server.RequestsFor "sendMessage") |> List.length >= 2)

                    let edits = server.RequestsFor "editMessageText"
                    Expect.equal (List.length edits) 2 "one failed retiring-edit attempt, plus guardedEmit's own successful retry"

                    let lastEdit = edits |> List.last |> fun r -> r.Body |> Option.get
                    Expect.equal (lastEdit |> field "message_id" |> asInt64) 1L "the retried edit still targets the FIRST (retiring) message"
                    Expect.equal (lastEdit |> field "text" |> asString) (String.replicate 4089 "a") "the retried edit still carries the first message's own finalized content"

                    let sends = server.RequestsFor "sendMessage"
                    Expect.equal (List.length sends) 2 "the rollover's own second message still sends normally once the retiring edit succeeds"
                    Expect.equal ((sends[1].Body |> Option.get) |> field "text" |> asString) overflowTail "the overflow still reaches the SECOND message, unaffected by the first message's own rate-limited retry"

                    Expect.isEmpty observer.InvalidOutputs "guardedEmit's own successful retry never reaches OnInvalidOutput"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "EditNotFound on the LAST live message of an already-spilled turn, with nothing further arriving, recovers only the missing tail — never re-sends the first (already-finalized) message"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9920L
                    let mutable now = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                    let clock: Clock = fun () -> now
                    let observer = RecordingObserver()
                    let options: MafBridgeOptions = { MafBridgeOptions.defaults with Observer = ValueSome(observer :> IMafObserver) }

                    // Same spill construction as the tests above: a first delta under the cap on its
                    // own (finalized into message 1 by the rollover's retiring edit), then a short tail
                    // that overflows into a SECOND message. A THIRD delta then arrives 2.0s later — past
                    // the default 1.5s coalescing interval — so message 2's own coalescer is due for a
                    // mid-stream edit; that edit is answered with `EditNotFound` (the fake server's
                    // SECOND canned `editMessageText` response — the FIRST, unqueued, is the ordinary
                    // successful retiring edit for message 1). Nothing further ever arrives after that
                    // (`EndsEmpty`), so the stream ends with `currentMessage = None` and no chance for
                    // the ordinary "a later split still rolls to a fresh send" recovery to ever fire.
                    let firstChunk = String.replicate 4089 "a" + " "
                    let overflowTail = "spilled over the cap"
                    let vanishedTail = " and vanished"

                    let steps =
                        [ firstChunk, TimeSpan.Zero
                          overflowTail, TimeSpan.Zero
                          vanishedTail, TimeSpan.FromSeconds 2.0 ]

                    let agent =
                        ScriptedAgent([ StreamsThen(steps, EndsEmpty) ], advanceClock = (fun span -> now <- now + span))

                    // The retiring edit (message 1's own finalize) must succeed ordinarily — only the
                    // SECOND editMessageText call (message 2's own mid-stream edit) is the vanish.
                    server.EnqueueResult("editMessageText", $"""{{"message_id":1,"date":0,"chat":{{"id":{chat},"type":"private"}}}}""")
                    server.EnqueueError("editMessageText", 400, "Bad Request: message to edit not found")

                    use! bridge = startStreamingBridgeWith server agent clock options
                    ignore bridge

                    deliverText server 1 chat 5 4920L "Nadia" "Hi agent"
                    do! pollUntil 15000 (fun () -> (server.RequestsFor "sendMessage") |> List.length >= 3)
                    do! Task.Delay 300

                    let sends = server.RequestsFor "sendMessage"
                    let edits = server.RequestsFor "editMessageText"

                    Expect.equal (List.length sends) 3 "message 1's initial send, message 2's rollover send, and the recovery send for the missing tail"
                    Expect.equal (List.length edits) 2 "message 1's own retiring edit, plus the vanish-triggering mid-stream edit attempt on message 2"

                    Expect.equal
                        ((sends[0].Body |> Option.get) |> field "text" |> asString)
                        (String.replicate 4089 "a")
                        "message 1's own content, as originally finalized by the rollover"

                    let recoverySend = sends[2].Body |> Option.get
                    let recoveryText = recoverySend |> field "text" |> asString

                    Expect.equal recoveryText (overflowTail + vanishedTail) "the recovery send carries ONLY message 2's own missing tail, not the whole turn"
                    Expect.isFalse (recoveryText.Contains "aaaa") "the recovery send never duplicates message 1's own already-finalized content"

                    let reportedVanish =
                        observer.InvalidOutputs
                        |> List.exists (fun (_, err) ->
                            match err with
                            | DeliveryFailed detail -> detail.Contains "no longer exists"
                            | _ -> false)

                    Expect.isTrue reportedVanish "the vanish itself is still reported via OnInvalidOutput, exactly as an ordinary mid-stream EditNotFound already is"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a 429 at the kept-narration approval render is absorbed by guardedEmit's own bounded retry, exactly like the other two mandatory emits"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9904L
                    let mutable now = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                    let clock: Clock = fun () -> now
                    let observer = RecordingObserver()
                    let options: MafBridgeOptions = { MafBridgeOptions.defaults with Observer = ValueSome(observer :> IMafObserver) }

                    let agent =
                        ScriptedAgent(
                            [ StreamsThen([ "Let me check that for you", TimeSpan.Zero ], PausesFor("req-1", "send_email", [])) ],
                            advanceClock = (fun span -> now <- now + span)
                        )

                    // Same rationale again: no `retryAfterSeconds`, so the vendor's own auto-retry
                    // never intercepts this — this is the ONE call site (`sendChainedApprovalCore`'s
                    // own `bot.EditKeyboardPlan` call, `Bridge.fs`) that must ITSELF route through
                    // `guardedEmit` for this test to pass; a direct, unwrapped call would surface the
                    // very first 429 straight to `OnInvalidOutput` with no retry at all.
                    server.EnqueueError("editMessageText", 429, "Too Many Requests")

                    use! bridge = startStreamingBridgeWith server agent clock options
                    do! bridge.StartRun(UMX.tag<chatId> chat, "Email alice that the deploy is done.")

                    do! pollUntil 15000 (fun () -> (server.RequestsFor "editMessageText") |> List.length >= 2)

                    let edits = server.RequestsFor "editMessageText"
                    Expect.equal (List.length edits) 2 "one failed approval-render attempt, plus guardedEmit's own successful retry"

                    let lastEdit = edits |> List.last |> fun r -> r.Body |> Option.get
                    Expect.equal (lastEdit |> field "message_id" |> asInt64) 1L "the retried edit still targets the streamed message"
                    Expect.stringContains (lastEdit |> field "text" |> asString) "Let me check that for you" "the retried render still keeps the narration already shown"
                    Expect.equal (buttonTextAt 0 0 lastEdit) "Approve" "the retried render still carries the decision buttons"

                    Expect.isEmpty observer.InvalidOutputs "guardedEmit's own successful retry never reaches OnInvalidOutput"

                    // The approval is still fully live and decidable after the retry succeeded.
                    let approveToken = callbackDataAt 0 0 lastEdit
                    do! deliverTap server 1 "q-render-retry-approve" approveToken chat 1 chat
                    do! pollUntil 15000 (fun () -> (server.RequestsFor "editMessageText") |> List.length >= 3)

                    let outcomeEdit = (server.RequestsFor "editMessageText") |> List.last |> fun r -> r.Body |> Option.get
                    Expect.stringContains (outcomeEdit |> field "text" |> asString) "approved" "the pending decision recorded against the retried render still resumes normally"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a canned 429 that never succeeds within guardedEmit's own bound surfaces via OnInvalidOutput rather than hanging the turn"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9905L
                    let mutable now = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                    let clock: Clock = fun () -> now
                    let observer = RecordingObserver()
                    let options: MafBridgeOptions = { MafBridgeOptions.defaults with Observer = ValueSome(observer :> IMafObserver) }

                    let steps = [ "Hello", TimeSpan.Zero; ", world", TimeSpan.FromSeconds 0.5 ]

                    let agent =
                        ScriptedAgent([ StreamsThen(steps, EndsEmpty) ], advanceClock = (fun span -> now <- now + span))

                    // Three consecutive 429s (no `retryAfterSeconds`, so none of them are ever
                    // absorbed by the vendor's own auto-retry) exhausts `guardedEmit 3`'s own retry
                    // budget.
                    for _ in 1..3 do
                        server.EnqueueError("editMessageText", 429, "Too Many Requests")

                    use! bridge = startStreamingBridgeWith server agent clock options
                    ignore bridge

                    deliverText server 1 chat 5 4905L "Nadia" "Hi agent"
                    do! pollUntil 15000 (fun () -> not (List.isEmpty observer.InvalidOutputs))

                    Expect.equal (List.length (server.RequestsFor "editMessageText")) 3 "every one of guardedEmit's own bounded attempts reached the wire, and none beyond that"
                    Expect.equal (List.length observer.InvalidOutputs) 1 "the exhausted retry surfaces exactly once via OnInvalidOutput"
                    Expect.isEmpty observer.TurnFailures "an exhausted mandatory-emit retry is OnInvalidOutput, never OnTurnFailed — the agent's own turn was fine"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a scripted throw with NOTHING ever sent reports OnTurnFailed"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9906L
                    let observer = RecordingObserver()
                    let options: MafBridgeOptions = { MafBridgeOptions.defaults with Observer = ValueSome(observer :> IMafObserver) }

                    let agent = ScriptedAgent [ Throws(InvalidOperationException "scripted backend failure") ]
                    use! bridge = startStreamingBridgeWith server agent (fun () -> DateTimeOffset.UtcNow) options
                    ignore bridge

                    deliverText server 1 chat 5 4906L "Nadia" "Hi agent"
                    do! pollUntil 5000 (fun () -> not (List.isEmpty observer.TurnFailures))

                    Expect.equal (List.length observer.TurnFailures) 1 "the throw is reported exactly once"
                    Expect.isEmpty (server.RequestsFor "sendMessage") "nothing was ever sent for this turn"
                    Expect.isEmpty observer.StreamFailures "no message existed, so this is OnTurnFailed, never OnStreamFailed"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a scripted throw AFTER a live message reports OnStreamFailed with a best-effort failure-note edit, and the SAME chat's next turn proceeds normally"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9907L
                    let observer = RecordingObserver()
                    let options: MafBridgeOptions = { MafBridgeOptions.defaults with Observer = ValueSome(observer :> IMafObserver) }

                    let agent =
                        ScriptedAgent(
                            [ StreamsThen([ "Working on it", TimeSpan.Zero ], Throws(InvalidOperationException "scripted mid-stream failure"))
                              RepliesWith "back to normal" ]
                        )

                    use! bridge = startStreamingBridgeWith server agent (fun () -> DateTimeOffset.UtcNow) options

                    do! bridge.StartRun(UMX.tag<chatId> chat, "First turn.")
                    do! pollUntil 5000 (fun () -> not (List.isEmpty observer.StreamFailures))

                    Expect.equal (List.length observer.StreamFailures) 1 "the mid-stream throw is reported exactly once"
                    let failedChat, failedMessage, _ = observer.StreamFailures.Head
                    Expect.equal failedChat (UMX.tag<chatId> chat) "the failure is reported for the right chat"
                    Expect.equal failedMessage (UMX.tag<messageId> 1L) "the failure names the message that was already live when the stream failed"
                    Expect.isEmpty observer.TurnFailures "a throw AFTER a live message is OnStreamFailed, never OnTurnFailed"

                    do! pollUntil 5000 (fun () -> (server.RequestsFor "editMessageText") |> List.isEmpty |> not)
                    let failureEdit = (server.RequestsFor "editMessageText").Head.Body |> Option.get
                    Expect.equal (failureEdit |> field "message_id" |> asInt64) 1L "the best-effort failure note is edited onto the SAME message that was already live"

                    Expect.equal agent.SeenSessionNonces.Count 1 "the failed turn still ran on exactly one live session"
                    let preFailureNonce = agent.SeenSessionNonces[0]

                    // Drive a SECOND turn on the SAME chat, immediately — no session repair of any
                    // kind between the two calls. The FIRST turn already sent its own "Working on it"
                    // message before it failed, so the predicate below waits for a SECOND sendMessage,
                    // not merely a non-empty list.
                    do! bridge.StartRun(UMX.tag<chatId> chat, "Second turn.")
                    do! pollUntil 5000 (fun () -> (server.RequestsFor "sendMessage") |> List.length >= 2)

                    let secondSend = (server.RequestsFor "sendMessage") |> List.last |> fun r -> r.Body |> Option.get
                    Expect.equal (secondSend |> field "text" |> asString) "back to normal" "the second turn completes ordinarily, exactly as a fresh turn on this chat would"

                    Expect.equal agent.SeenSessionNonces.Count 2 "the second turn ran on exactly one more live session"

                    Expect.equal
                        agent.SeenSessionNonces[1]
                        preFailureNonce
                        "the second turn ran on the SAME session the failed turn started on — MAF's own per-turn history commit left it untouched, so no session repair was ever needed"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "an agent-origin OperationCanceledException mid-stream (our own cancellation NOT requested) is reported via OnStreamFailed, never mistaken for a graceful shutdown and swallowed"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9926L
                    let observer = RecordingObserver()
                    let options: MafBridgeOptions = { MafBridgeOptions.defaults with Observer = ValueSome(observer :> IMafObserver) }

                    // A model backend's own HttpClient timeout surfaces as a TaskCanceledException (an
                    // OperationCanceledException subtype) from the stream — WHILE the bridge's own
                    // cancellation source is still live (no DisposeAsync). Distinct from a graceful
                    // shutdown: it must be reported exactly like any other mid-stream failure, not
                    // absorbed as if it were our own cancellation.
                    let agent =
                        ScriptedAgent [ StreamsThen([ "Working on it", TimeSpan.Zero ], Throws(TaskCanceledException "backend timeout")) ]

                    use! bridge = startStreamingBridgeWith server agent (fun () -> DateTimeOffset.UtcNow) options
                    ignore bridge

                    deliverText server 1 chat 5 4926L "Nadia" "Hi agent"
                    do! pollUntil 5000 (fun () -> not (List.isEmpty observer.StreamFailures))

                    Expect.equal (List.length observer.StreamFailures) 1 "the agent-origin cancellation is reported once, exactly as a non-cancellation mid-stream failure would be"
                    let _, failedMessage, _ = observer.StreamFailures.Head
                    Expect.equal failedMessage (UMX.tag<messageId> 1L) "the failure names the message that was already live when the stream failed"
                    Expect.isEmpty observer.TurnFailures "a failure AFTER a live message is OnStreamFailed, never OnTurnFailed"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "an EditNotFound on a spilled message's own retiring edit is reported via OnInvalidOutput, not silently swallowed"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9927L
                    let observer = RecordingObserver()
                    let options: MafBridgeOptions = { MafBridgeOptions.defaults with Observer = ValueSome(observer :> IMafObserver) }

                    // A reply that spills: the first delta fills one message (ending on a space so the
                    // split boundary lands there); the second delta pushes the running total past the
                    // cap, triggering a rollover whose RETIRING edit of the first message is answered
                    // with "message to edit not found" (the user deleted it) — the fake classifies that
                    // 400 as Ok EditNotFound, a successful HTTP call, never a thrown exception.
                    let firstChunk = String.replicate 4089 "a" + " "
                    let overflowTail = "spilled over the cap"

                    let agent =
                        ScriptedAgent [ StreamsThen([ firstChunk, TimeSpan.Zero; overflowTail, TimeSpan.Zero ], EndsEmpty) ]

                    server.EnqueueError("editMessageText", 400, "Bad Request: message to edit not found")

                    use! bridge = startStreamingBridgeWith server agent (fun () -> DateTimeOffset.UtcNow) options
                    ignore bridge

                    deliverText server 1 chat 5 4927L "Nadia" "Hi agent"
                    do! pollUntil 5000 (fun () -> (server.RequestsFor "sendMessage") |> List.length >= 2)

                    let vanishReported =
                        observer.InvalidOutputs
                        |> List.exists (fun (_, error) ->
                            match error with
                            | DeliveryFailed msg -> msg.Contains "no longer exists"
                            | _ -> false)

                    Expect.isTrue vanishReported "the retiring edit's own EditNotFound is surfaced via OnInvalidOutput, exactly as the mid-stream edit and the final flush already report theirs"
                    Expect.equal (List.length (server.RequestsFor "sendMessage")) 2 "the rollover's own new message is still sent even though retiring the first one found it gone"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a graceful shutdown mid-stream is absorbed silently — no failure-note edit, no OnStreamFailed, no OnTurnFailed"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9925L
                    let observer = RecordingObserver()
                    let options: MafBridgeOptions = { MafBridgeOptions.defaults with Observer = ValueSome(observer :> IMafObserver) }

                    // A live message goes out ("Working on it"), then the stream hangs — simulating a
                    // real backend still mid-turn — until `MafBridge.DisposeAsync`'s own `cts.Cancel()`
                    // cancels it, the same shape a routine process restart produces. Deliberately NOT
                    // `use!`-bound: this test disposes `bridge` explicitly, mid-turn, then awaits the
                    // ORIGINAL `StartRun` task — an auto-dispose at scope end on top of that would
                    // double-dispose the same bot.
                    let agent =
                        ScriptedAgent [ StreamsThen([ "Working on it", TimeSpan.Zero ], HangsUntilCancelled) ]

                    let! bridge = Maf.startPollingWith options ((TgBotConfig.create "123456789:TEST-fake-token").WithBaseUrl(server.BaseUrl).WithTools(ToolRegistry.create ()).WithStreaming()) agent

                    let runTask = bridge.StartRun(UMX.tag<chatId> chat, "First turn.")
                    do! pollUntil 5000 (fun () -> (server.RequestsFor "sendMessage") |> List.isEmpty |> not)

                    // `pollUntil` only proves the HTTP request for "Working on it" REACHED the fake
                    // server — the server records a request the instant it arrives, well before it
                    // even writes a response back (`FakeBotApiServer.fs`'s own route handler). The
                    // bridge loop itself is still awaiting that response's own deserialization at this
                    // point; disposing immediately here would interrupt THAT in-flight call instead of
                    // the intended target (the SUBSEQUENT `HangsUntilCancelled` block) and misreport as
                    // an initial-send failure. A short, deterministic settle — same idiom as
                    // `MafStreamingTurnTests.fs`'s own one-shot-reply test — lets the send actually
                    // complete and the loop reach the genuinely-blocked point before cancelling.
                    do! Task.Delay 300

                    // The turn is now genuinely stuck mid-stream, past its own first live send —
                    // cancel it, the same way a process restart would.
                    do! (bridge :> IAsyncDisposable).DisposeAsync().AsTask()
                    do! runTask

                    Expect.isEmpty observer.StreamFailures "a cancelled turn is never reported as a stream failure — it is not a genuine backend error"
                    Expect.isEmpty observer.TurnFailures "a cancelled turn is never reported as a turn failure either"
                    Expect.isEmpty observer.InvalidOutputs "a cancelled turn never reaches OnInvalidOutput"
                    Expect.isEmpty (server.RequestsFor "editMessageText") "no failure-note edit is ever attempted against the cancelled turn's own live message — the user's own partial narration survives untouched"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "when the FIRST bot.SendText for a turn throws, OnInvalidOutput(DeliveryFailed) fires, finalize/persist never run, and no edit is ever attempted"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9908L
                    let observer = RecordingObserver()
                    let options: MafBridgeOptions = { MafBridgeOptions.defaults with Observer = ValueSome(observer :> IMafObserver) }
                    let sessionStore = InMemorySessionStore() :> ISessionStore

                    let agent = ScriptedAgent [ RepliesWith "Hello back!" ]

                    let config =
                        (plainSessionConfig server sessionStore).WithStreaming()

                    server.EnqueueError("sendMessage", 400, "Bad Request: simulated failure")

                    use! bridge = Maf.startPollingWith options config agent

                    do! bridge.StartRun(UMX.tag<chatId> chat, "Hi agent")
                    do! pollUntil 5000 (fun () -> not (List.isEmpty observer.InvalidOutputs))

                    Expect.equal (List.length observer.InvalidOutputs) 1 "the initial-send failure is reported exactly once"

                    match observer.InvalidOutputs.Head with
                    | _, DeliveryFailed detail -> Expect.stringContains detail "initial send failed" "the reported error names the initial send as the failure point"
                    | other -> failwithf "expected DeliveryFailed, got %A" other

                    Expect.equal (List.length (server.RequestsFor "sendMessage")) 1 "exactly the ONE failed attempt reached the wire — no fallback second send"
                    Expect.isEmpty (server.RequestsFor "editMessageText") "no edit is ever attempted against a message that was never sent"
                    Expect.isEmpty observer.TurnFailures "an initial-send failure is OnInvalidOutput, never OnTurnFailed — the agent's own turn was fine"

                    let! persisted = sessionStore.TryGet(UMX.tag<chatId> chat, CancellationToken.None)
                    Expect.equal persisted ValueNone "finalizeStreamingTurn/persistConversation never ran for this turn — nothing was ever written to the session store"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a 429 on the INITIAL send is bounded-retried via guardedEmit and the reply still completes — contrast with the 400 case above, which still aborts"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9909L
                    let observer = RecordingObserver()
                    let options: MafBridgeOptions = { MafBridgeOptions.defaults with Observer = ValueSome(observer :> IMafObserver) }
                    let sessionStore = InMemorySessionStore() :> ISessionStore

                    let agent = ScriptedAgent [ RepliesWith "Hello back!" ]

                    let config = (plainSessionConfig server sessionStore).WithStreaming()

                    // No `retryAfterSeconds` — the canned error carries no `parameters.retry_after` at
                    // all, so Telegram.Bot's own vendor-level auto-retry never engages and the
                    // `ApiRequestException` reaches THIS leaf's own `guardedEmit` on the first attempt,
                    // the same rationale the other `guardedEmit` 429 tests above already use.
                    server.EnqueueError("sendMessage", 429, "Too Many Requests")

                    use! bridge = Maf.startPollingWith options config agent

                    do! bridge.StartRun(UMX.tag<chatId> chat, "Hi agent")
                    do! pollUntil 15000 (fun () -> (server.RequestsFor "sendMessage") |> List.length >= 2)

                    let sends = server.RequestsFor "sendMessage"

                    Expect.equal
                        (List.length sends)
                        2
                        "one failed 429 attempt, plus guardedEmit's own successful retry — unlike the 400 case above, a transient rate limit never discards the reply"

                    let lastSend = sends |> List.last |> fun r -> r.Body |> Option.get
                    Expect.equal (lastSend |> field "text" |> asString) "Hello back!" "the retried initial send still carries the complete reply"

                    Expect.isEmpty observer.InvalidOutputs "guardedEmit's own successful retry never reaches OnInvalidOutput"
                    Expect.isEmpty observer.TurnFailures "the turn completed normally — never OnTurnFailed"

                    let! persisted = sessionStore.TryGet(UMX.tag<chatId> chat, CancellationToken.None)
                    Expect.notEqual persisted ValueNone "unlike the 400 case above, the retried turn still reached finalize/persist normally"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a completed STREAMED turn persists a session + pending-approval record structurally identical to the SAME scripted turn's non-streaming persist"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let streamedChat = 9909L
                    let plainChat = 9910L
                    let sessionStore = InMemorySessionStore() :> ISessionStore

                    // An explicit, FIXED owner on both `StartRun` calls — omitting it would default
                    // (`RunOwner.resolve`, `OwnerResolution.fs`) to "the chat's own id reinterpreted as
                    // a user id" for a host-initiated run in a private chat, which would legitimately
                    // (and irrelevantly to this test) differ between the two DIFFERENT chat ids below.
                    let fixedOwner = Owner.user 4200L

                    let streamedAgent =
                        ScriptedAgent [ StreamsThen([ "Checking the deploy status", TimeSpan.Zero ], PausesFor("req-1", "send_email", [ "toAddr", box "alice@example.com" ])) ]

                    use! streamedBridge = Maf.startPolling (streamingSessionConfig server sessionStore) streamedAgent
                    do! streamedBridge.StartRun(UMX.tag<chatId> streamedChat, "Email alice that the deploy is done.", owner = fixedOwner)
                    do! pollUntil 5000 (fun () -> (server.RequestsFor "sendMessage") |> List.isEmpty |> not)

                    let plainAgent =
                        ScriptedAgent [ PausesFor("req-1", "send_email", [ "toAddr", box "alice@example.com" ]) ]

                    use! plainBridge = Maf.startPolling (plainSessionConfig server sessionStore) plainAgent
                    do! plainBridge.StartRun(UMX.tag<chatId> plainChat, "Email alice that the deploy is done.", owner = fixedOwner)
                    do! pollUntil 5000 (fun () -> (server.RequestsFor "sendMessage") |> List.length >= 2)

                    let! streamedRecord = sessionStore.TryGet(UMX.tag<chatId> streamedChat, CancellationToken.None)
                    let! plainRecord = sessionStore.TryGet(UMX.tag<chatId> plainChat, CancellationToken.None)

                    let envelope record =
                        match record with
                        | ValueSome(r: SessionRecord) ->
                            match SessionEnvelope.decodeAndValidate SessionEnvelope.currentMafVersion r.Payload with
                            | Ok env -> env
                            | Error err -> failwithf "expected a well-formed persisted envelope, got %A" err
                        | ValueNone -> failtest "expected a persisted durable record for this chat"

                    let streamedEnvelope = envelope streamedRecord
                    let plainEnvelope = envelope plainRecord

                    Expect.equal streamedEnvelope.Format plainEnvelope.Format "the streamed and non-streamed records share the SAME envelope format version"
                    Expect.equal streamedEnvelope.MafVersion plainEnvelope.MafVersion "the streamed and non-streamed records share the SAME MAF version stamp"
                    Expect.equal streamedEnvelope.MeaiVersion plainEnvelope.MeaiVersion "the streamed and non-streamed records share the SAME MEAI version stamp"

                    Expect.equal streamedEnvelope.Approvals.Length 1 "the streamed turn persisted exactly one pending approval"
                    Expect.equal plainEnvelope.Approvals.Length 1 "the non-streamed turn persisted exactly one pending approval"

                    let streamedApproval = streamedEnvelope.Approvals[0]
                    let plainApproval = plainEnvelope.Approvals[0]

                    // The persisted approval DTO carries no message BODY/text field at all — only
                    // identifiers — so this comparison is unaffected by the streaming path's own
                    // deliberately DIFFERENT rendered body (kept narration vs. the default render).
                    Expect.equal streamedApproval.RequestId plainApproval.RequestId "the SAME request id was persisted either way"
                    Expect.equal streamedApproval.CallId plainApproval.CallId "the SAME call id was persisted either way"
                    Expect.equal streamedApproval.Tool plainApproval.Tool "the SAME tool name was persisted either way"
                    Expect.equal streamedApproval.ArgumentsJson plainApproval.ArgumentsJson "the SAME arguments were persisted either way"
                    Expect.equal streamedApproval.OwnerUserId plainApproval.OwnerUserId "the SAME owner scope was persisted either way"
                    Expect.equal streamedApproval.MessageId plainApproval.MessageId "the SAME message id (each chat's own first message) was persisted either way"
                    Expect.equal streamedApproval.ExpiresAt plainApproval.ExpiresAt "the SAME expiry (none, in this test) was persisted either way"

                    // The session payload's own SHAPE, not its exact value — `ScriptedSession`'s own
                    // nonce is a fresh random Guid per session instance, so the two chats' own
                    // `SessionJson` values legitimately differ; what must match is that BOTH decode to
                    // the SAME recognizable shape.
                    use streamedDoc = System.Text.Json.JsonDocument.Parse streamedEnvelope.SessionJson
                    use plainDoc = System.Text.Json.JsonDocument.Parse plainEnvelope.SessionJson

                    let nonceKind (doc: System.Text.Json.JsonDocument) =
                        match doc.RootElement.TryGetProperty "nonce" with
                        | true, prop -> prop.ValueKind
                        | false, _ -> failtest "expected a 'nonce' property in the persisted session payload"

                    Expect.equal (nonceKind streamedDoc) (nonceKind plainDoc) "both persisted session payloads carry the SAME recognizable shape (a string 'nonce' property)"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "zero streamed updates reports OnEmptyTurn and sends nothing"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9911L
                    let observer = RecordingObserver()
                    let options: MafBridgeOptions = { MafBridgeOptions.defaults with Observer = ValueSome(observer :> IMafObserver) }

                    let agent = ScriptedAgent [ EndsEmpty ]
                    use! bridge = startStreamingBridgeWith server agent (fun () -> DateTimeOffset.UtcNow) options
                    ignore bridge

                    deliverText server 1 chat 5 4911L "Nadia" "Hi agent"
                    do! pollUntil 5000 (fun () -> not (List.isEmpty observer.EmptyTurns))

                    Expect.equal (List.length observer.EmptyTurns) 1 "the empty stream is reported exactly once"
                    Expect.isEmpty (server.RequestsFor "sendMessage") "nothing was ever sent for an empty stream"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a stream whose deltas concatenate to PURE WHITESPACE also reports OnEmptyTurn and sends nothing — never a blank placeholder message"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9912L
                    let mutable now = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                    let clock: Clock = fun () -> now
                    let observer = RecordingObserver()
                    let options: MafBridgeOptions = { MafBridgeOptions.defaults with Observer = ValueSome(observer :> IMafObserver) }

                    let steps = [ "   ", TimeSpan.Zero; "\n", TimeSpan.Zero ]

                    let agent =
                        ScriptedAgent([ StreamsThen(steps, EndsEmpty) ], advanceClock = (fun span -> now <- now + span))

                    use! bridge = startStreamingBridgeWith server agent clock options
                    ignore bridge

                    deliverText server 1 chat 5 4912L "Nadia" "Hi agent"
                    do! pollUntil 5000 (fun () -> not (List.isEmpty observer.EmptyTurns))

                    Expect.equal (List.length observer.EmptyTurns) 1 "an all-whitespace stream is reported as an empty turn"
                    Expect.isEmpty (server.RequestsFor "sendMessage") "a whitespace-only stream never sends a blank placeholder message"
                    Expect.isEmpty (server.RequestsFor "editMessageText") "a whitespace-only stream never edits anything either — nothing was ever sent to edit"
                }
                |> Async.AwaitTask
        }
    ]
