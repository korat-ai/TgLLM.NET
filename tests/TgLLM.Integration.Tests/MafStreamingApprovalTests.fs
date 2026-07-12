/// Acceptance for a streaming turn (`bot.Streaming = Some interval`) that ends in a tool-approval
/// request: the decision buttons land on the CURRENT (last) live message, and that message's own
/// shown narration is KEPT rather than replaced by the ordinary tool-call prompt text — deliberately
/// unlike the non-streaming bridge, whose own preamble is dropped in favor of the approval prompt
/// (`Bridge.fs`'s `processInitialResponse`, own doc comment). A turn that has already spilled across
/// more than one message (`MafStreamingTurnTests.fs` covers the spill mechanics on their own) keeps
/// the decision on the LAST message only, carrying ONLY that message's own slice — earlier messages
/// stay exactly as their own rollover already finalized them. Resuming a decision made on a streamed
/// message goes through the SAME `HandleDecision`/`agent.RunAsync` path `MafBridgeApprovalTests.fs`/
/// `MafBridgeRefusalTests.fs` already cover for the non-streaming case — this file's own resume tests
/// mirror those, over a streamed-approval message instead of a freshly-sent one.
module TgLLM.Integration.Tests.MafStreamingApprovalTests

open System
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

let private deliverTap (server: FakeBotApiServer) (updateId: int) (queryId: string) (token: string) (chat: int64) (messageId: int) (userId: int64) : Task =
    server.EnqueueResult("getUpdates", TelegramJson.batch [ TelegramJson.callbackQueryUpdate updateId queryId token chat messageId userId "Tester" ])
    Task.CompletedTask

/// A recording `IMafObserver` — every stale decision lands in its own list, mirroring
/// `MafBridgeRefusalTests.fs`'s own copy for the identical purpose.
type private RecordingObserver() =
    let staleDecisions = ResizeArray<ApprovalDescriptor>()

    member _.StaleDecisions: ApprovalDescriptor list = List.ofSeq staleDecisions

    interface IMafObserver with
        member _.OnStaleDecision(descriptor) = staleDecisions.Add descriptor
        member _.OnMalformedDecision(_raw) = ()
        member _.OnResumeFailed(_descriptor, _error) = ()
        member _.OnEmptyTurn(_chat) = ()
        member _.OnInvalidOutput(_chat, _error) = ()
        member _.OnProjectionProblem(_problem) = ()
        member _.OnTurnFailed(_chat, _error) = ()

/// Starts a streaming-configured bridge over `server`, sharing `clock` between the bot's own
/// `WithClock` and the scripted agent's own `advanceClock` callback — mirrors
/// `MafStreamingTurnTests.fs`'s own helper of the same shape (kept as a local copy rather than a
/// shared one, matching this project's own convention of a private per-file helper set).
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

[<Tests>]
let mafStreamingApprovalTests =
    testList "MafBridge streaming approval" [

        testCaseAsync "a streamed reply that ends in an approval keeps its own narration and gains the decision buttons on the SAME message"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9800L
                    let mutable now = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                    let clock: Clock = fun () -> now

                    let agent =
                        ScriptedAgent(
                            [ StreamsThen(
                                  [ "Let me check that for you", TimeSpan.Zero ],
                                  PausesFor("req-1", "send_email", [ "toAddr", box "alice@example.com" ])
                              ) ],
                            advanceClock = (fun span -> now <- now + span)
                        )

                    use! bridge = startStreamingBridge server agent clock
                    do! bridge.StartRun(UMX.tag<chatId> chat, "Email alice that the deploy is done.")

                    do! pollUntil 15000 (fun () -> server.RequestsFor "editMessageText" |> List.isEmpty |> not)

                    let sends = server.RequestsFor "sendMessage"
                    let edits = server.RequestsFor "editMessageText"

                    Expect.equal (List.length sends) 1 "exactly one message exists for the whole turn — the approval never sends a second one"
                    Expect.equal (List.length edits) 1 "the approval is rendered by editing the SAME message, not a fresh send"

                    let editBody = edits.Head.Body |> Option.get
                    Expect.equal (editBody |> field "message_id" |> asInt64) 1L "the edit targets the ORIGINAL streamed message"

                    let text = editBody |> field "text" |> asString
                    Expect.stringContains text "Let me check that for you" "the narration already shown survives the approval edit"
                    Expect.isFalse (text.Contains "Approval required") "the tool-call prompt text never replaces the kept narration"

                    Expect.equal (buttonTextAt 0 0 editBody) "Approve" "the first button is the default Approve label"
                    Expect.equal (buttonTextAt 0 1 editBody) "Reject" "the second button is the default Reject label"

                    // The decision was recorded against THIS message: tapping its own Approve button
                    // resumes normally and edits THIS SAME message to the outcome.
                    let approveToken = callbackDataAt 0 0 editBody
                    do! deliverTap server 1 "q-streamed-approval" approveToken chat 1 chat
                    do! pollUntil 15000 (fun () -> server.RequestsFor "editMessageText" |> List.length >= 2)

                    let outcomeEdit = (server.RequestsFor "editMessageText") |> List.last |> fun r -> r.Body |> Option.get
                    Expect.equal (outcomeEdit |> field "message_id" |> asInt64) 1L "the pending decision was recorded against the streamed message — the resume edits THAT message"
                    Expect.stringContains (outcomeEdit |> field "text" |> asString) "approved" "the resume concluded normally"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "Reject on a streamed approval message resumes with Approved = false and edits to the rejection"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9801L
                    let mutable now = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                    let clock: Clock = fun () -> now
                    let resumes = ResizeArray<string * bool>()

                    let agent =
                        ScriptedAgent(
                            [ StreamsThen([ "Checking the record", TimeSpan.Zero ], PausesFor("req-1", "delete_record", []))
                              RepliesWith "" ],
                            onResume = (fun (reqId, approved) -> resumes.Add(reqId, approved)),
                            advanceClock = (fun span -> now <- now + span)
                        )

                    use! bridge = startStreamingBridge server agent clock
                    do! bridge.StartRun(UMX.tag<chatId> chat, "Delete the record.")
                    do! pollUntil 15000 (fun () -> server.RequestsFor "editMessageText" |> List.isEmpty |> not)

                    let approvalEdit = (server.RequestsFor "editMessageText").Head.Body |> Option.get
                    let rejectToken = callbackDataAt 0 1 approvalEdit

                    do! deliverTap server 1 "q-streamed-reject" rejectToken chat 1 chat
                    do! pollUntil 15000 (fun () -> server.RequestsFor "editMessageText" |> List.length >= 2)

                    Expect.equal resumes.Count 1 "the agent was resumed exactly once"
                    Expect.equal resumes[0] ("req-1", false) "the agent was resumed with Approved = false"

                    let outcomeEdit = (server.RequestsFor "editMessageText") |> List.last |> fun r -> r.Body |> Option.get
                    Expect.stringContains (outcomeEdit |> field "text" |> asString) "rejected" "the outcome says it was rejected"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a non-owner tap on a streamed approval message is refused by the leaf's own owner check — no resume"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9802L
                    let ownerId = 8100L
                    let otherId = 8101L
                    let mutable now = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                    let clock: Clock = fun () -> now

                    let agent =
                        ScriptedAgent(
                            [ StreamsThen([ "Checking", TimeSpan.Zero ], PausesFor("req-1", "send_email", [])) ],
                            advanceClock = (fun span -> now <- now + span)
                        )

                    use! bridge = startStreamingBridge server agent clock
                    do! bridge.StartRun(UMX.tag<chatId> chat, "Email alice.", owner = Owner.user ownerId)
                    do! pollUntil 15000 (fun () -> server.RequestsFor "editMessageText" |> List.isEmpty |> not)

                    let approvalEdit = (server.RequestsFor "editMessageText").Head.Body |> Option.get
                    let approveToken = callbackDataAt 0 0 approvalEdit

                    do! deliverTap server 1 "q-streamed-nonowner" approveToken chat 1 otherId
                    do! pollUntil 15000 (fun () -> server.RequestsFor "answerCallbackQuery" |> List.isEmpty |> not)

                    match server.RequestsFor "answerCallbackQuery" with
                    | [ ack ] ->
                        let ackBody = ack.Body |> Option.get
                        Expect.equal (ackBody |> field "text" |> asString) OwnerScope.DefaultDeniedNotice "the non-owner sees the denied notice"
                    | other -> failwithf "expected exactly one answerCallbackQuery, got %d" (List.length other)

                    Expect.equal (List.length (server.RequestsFor "editMessageText")) 1 "the refused tap never produces a second edit"
                    Expect.equal agent.RunCount 1 "only the initial streamed turn ran — the non-owner's tap never resumed the agent"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a second tap on the SAME streamed approval message is refused as stale — no second resume"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9803L
                    let mutable now = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                    let clock: Clock = fun () -> now
                    let observer = RecordingObserver()

                    let options: MafBridgeOptions =
                        { MafBridgeOptions.defaults with
                            Observer = ValueSome(observer :> IMafObserver) }

                    let agent =
                        ScriptedAgent(
                            [ StreamsThen([ "Checking", TimeSpan.Zero ], PausesFor("req-1", "send_email", []))
                              RepliesWith "sent" ],
                            advanceClock = (fun span -> now <- now + span)
                        )

                    use! bridge = startStreamingBridgeWith server agent clock options
                    do! bridge.StartRun(UMX.tag<chatId> chat, "Email alice.")
                    do! pollUntil 15000 (fun () -> server.RequestsFor "editMessageText" |> List.isEmpty |> not)

                    let approvalEdit = (server.RequestsFor "editMessageText").Head.Body |> Option.get
                    let approveToken = callbackDataAt 0 0 approvalEdit

                    do! deliverTap server 1 "q-streamed-first" approveToken chat 1 chat
                    do! pollUntil 15000 (fun () -> server.RequestsFor "editMessageText" |> List.length >= 2)
                    Expect.equal agent.RunCount 2 "the first tap resumed the agent exactly once (plus the initial streamed turn)"

                    do! deliverTap server 2 "q-streamed-repeat" approveToken chat 1 chat
                    do! pollUntil 15000 (fun () -> not (List.isEmpty observer.StaleDecisions))

                    Expect.equal (List.length observer.StaleDecisions) 1 "the repeat tap is surfaced as exactly one stale decision"
                    Expect.equal observer.StaleDecisions[0].RequestId "req-1" "the surfaced descriptor names the already-decided request"
                    Expect.equal agent.RunCount 2 "the repeat tap never resumes the agent a second time"
                    Expect.equal (List.length (server.RequestsFor "editMessageText")) 2 "the repeat tap never produces a further edit"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a spilled streamed turn that ends in an approval lands the decision on the LAST message only — the first message stays finalized plain text"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9804L
                    let mutable now = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                    let clock: Clock = fun () -> now

                    // Same construction as MafStreamingTurnTests.fs's own spill test: a first delta
                    // that fits under the cap on its own, ending in a single space, then a short tail
                    // that pushes the running total past MessageText.MaxLength.
                    let firstChunk = String.replicate 4089 "a" + " "
                    let overflowTail = "spilled over the cap"

                    let steps = [ firstChunk, TimeSpan.Zero; overflowTail, TimeSpan.Zero ]

                    let agent =
                        ScriptedAgent(
                            [ StreamsThen(steps, PausesFor("req-1", "send_email", [])) ],
                            advanceClock = (fun span -> now <- now + span)
                        )

                    use! bridge = startStreamingBridge server agent clock
                    do! bridge.StartRun(UMX.tag<chatId> chat, "Email alice.")

                    do! pollUntil 15000 (fun () -> server.RequestsFor "editMessageText" |> List.length >= 2)

                    let sends = server.RequestsFor "sendMessage"
                    let edits = server.RequestsFor "editMessageText"

                    Expect.equal (List.length sends) 2 "the first message's own initial send, plus the rollover's immediate second send"
                    Expect.equal (List.length edits) 2 "the rollover's retiring edit on the first message, plus the approval's edit on the second"

                    let retiringEdit = edits[0].Body |> Option.get
                    Expect.equal (retiringEdit |> field "message_id" |> asInt64) 1L "the FIRST message's own retiring edit"

                    Expect.equal
                        (retiringEdit |> field "text" |> asString)
                        (String.replicate 4089 "a")
                        "the first message is finalized at the split boundary and never touched again by the approval"

                    let approvalEdit = edits[1].Body |> Option.get
                    Expect.equal (approvalEdit |> field "message_id" |> asInt64) 2L "the approval lands on the LAST (second) message, not the first"

                    let approvalText = approvalEdit |> field "text" |> asString

                    Expect.equal
                        approvalText
                        overflowTail
                        "the approval edit keeps ONLY the last message's own slice — never the whole accumulated reply"

                    Expect.isFalse (approvalText.Contains "aaaa") "the last message's own approval body does not contain the first message's own text"

                    Expect.equal (buttonTextAt 0 0 approvalEdit) "Approve" "the last message gains the decision buttons"
                    Expect.equal (buttonTextAt 0 1 approvalEdit) "Reject" "the last message gains the decision buttons"

                    // Approve, and confirm the SAME resume guarantees hold for the spilled case too.
                    let approveToken = callbackDataAt 0 0 approvalEdit
                    do! deliverTap server 1 "q-spilled-approve" approveToken chat 2 chat
                    do! pollUntil 15000 (fun () -> server.RequestsFor "editMessageText" |> List.length >= 3)

                    let outcomeEdit = (server.RequestsFor "editMessageText") |> List.last |> fun r -> r.Body |> Option.get
                    Expect.equal (outcomeEdit |> field "message_id" |> asInt64) 2L "the resume concludes on the SAME (last) message the decision was recorded against"
                    Expect.stringContains (outcomeEdit |> field "text" |> asString) "approved" "the resume concluded normally"
                    Expect.equal (List.length (server.RequestsFor "sendMessage")) 2 "the resume never sends a new message"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a streamed turn that is ONLY an approval (no narration ever streamed) still sends the decision and can be resumed"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9806L
                    let mutable now = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                    let clock: Clock = fun () -> now
                    let resumes = ResizeArray<string * bool>()

                    // A bare PausesFor — never wrapped in StreamsThen, so nothing is ever appended to
                    // the coalescer's own running text; `currentMessage` stays `None` for the whole
                    // loop, exactly the "no narration at all" shape F1 exercises.
                    let agent =
                        ScriptedAgent(
                            [ PausesFor("req-1", "send_email", [ "toAddr", box "alice@example.com" ])
                              RepliesWith "Email sent to alice@example.com." ],
                            onResume = (fun (reqId, approved) -> resumes.Add(reqId, approved)),
                            advanceClock = (fun span -> now <- now + span)
                        )

                    use! bridge = startStreamingBridge server agent clock
                    do! bridge.StartRun(UMX.tag<chatId> chat, "Email alice that the deploy is done.")

                    do! pollUntil 15000 (fun () -> server.RequestsFor "sendMessage" |> List.isEmpty |> not)

                    let sends = server.RequestsFor "sendMessage"
                    Expect.equal (List.length sends) 1 "the bare approval (no narration) still sends exactly one message — it is never silently dropped"

                    let sentBody = sends.Head.Body |> Option.get
                    Expect.equal (buttonTextAt 0 0 sentBody) "Approve" "the first button is the default Approve label"
                    Expect.equal (buttonTextAt 0 1 sentBody) "Reject" "the second button is the default Reject label"

                    let approveToken = callbackDataAt 0 0 sentBody
                    do! deliverTap server 1 "q-streamed-bare-approval" approveToken chat 1 chat
                    do! pollUntil 15000 (fun () -> server.RequestsFor "editMessageText" |> List.isEmpty |> not)

                    Expect.equal resumes.Count 1 "the agent was resumed exactly once — the session was not left mid-turn"
                    Expect.equal resumes[0] ("req-1", true) "the agent was resumed with Approved = true"

                    let outcomeEdit = (server.RequestsFor "editMessageText") |> List.last |> fun r -> r.Body |> Option.get
                    Expect.stringContains (outcomeEdit |> field "text" |> asString) "approved" "the resume concluded normally"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "narration followed by two simultaneous tool calls repurposes the current message for the FIRST and sends the rest as their own fresh messages"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9805L
                    let mutable now = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                    let clock: Clock = fun () -> now

                    let agent =
                        ScriptedAgent(
                            [ StreamsThen(
                                  [ "Notifying the team now", TimeSpan.Zero ],
                                  PausesForMany [ ("req-2", "send_sms", []); ("req-3", "post_slack", []) ]
                              ) ],
                            advanceClock = (fun span -> now <- now + span)
                        )

                    use! bridge = startStreamingBridge server agent clock
                    do! bridge.StartRun(UMX.tag<chatId> chat, "Notify everyone.")

                    do! pollUntil 15000 (fun () -> server.RequestsFor "sendMessage" |> List.length >= 2)

                    let sends = server.RequestsFor "sendMessage"
                    let edits = server.RequestsFor "editMessageText"

                    Expect.equal (List.length sends) 2 "the initial narration send, plus req-3's own fresh message"
                    Expect.equal (List.length edits) 1 "req-2 is chained onto the SAME (current) message — the only edit for this turn"

                    let chainedEdit = edits.Head.Body |> Option.get
                    Expect.equal (chainedEdit |> field "message_id" |> asInt64) 1L "req-2 repurposes the CURRENT (narration) message, exactly like a single-approval turn"

                    let chainedText = chainedEdit |> field "text" |> asString
                    Expect.stringContains chainedText "Notifying the team now" "req-2's own edit keeps the narration already shown"
                    Expect.isFalse (chainedText.Contains "send_sms") "the kept-narration edit shows the narration only — not req-2's own tool-call prompt text"

                    let freshSend = sends[1].Body |> Option.get
                    let freshText = freshSend |> field "text" |> asString
                    Expect.stringContains freshText "post_slack" "req-3's own fresh message shows its OWN prompt, via the unchanged sendNewApproval path"
                    Expect.isFalse (freshText.Contains "Notifying the team now") "req-3's fresh message is a plain new approval, not a kept-narration edit"
                }
                |> Async.AwaitTask
        }
    ]
