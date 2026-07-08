/// Acceptance for the MAF bridge's refusal paths: an out-of-scope tap is refused by the ENGINE
/// before the bridge ever sees it (no resume); a repeat tap on the SAME already-decided button is
/// refused by the engine's own single-use binding removal (no resume); a race on the SIBLING
/// (untapped) button after a decision already resolved lands on the bridge's own
/// `PendingApprovals.TryConsume` miss — surfaced as stale, never a second resume.
module TgLLM.Integration.Tests.MafBridgeRefusalTests

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

let private tryField (key: string) (node: JsonNode) : JsonNode option = node.[key] |> Option.ofObj

let private at (i: int) (node: JsonNode) : JsonNode =
    match node.[i] |> Option.ofObj with
    | Some c -> c
    | None -> failwithf "expected JSON index %d in %s" i (node.ToJsonString())

let private asString (node: JsonNode) : string = node.AsValue().GetValue<string>()

let private callbackDataAt (row: int) (col: int) (sendBody: JsonNode) : string =
    sendBody |> field "reply_markup" |> field "inline_keyboard" |> at row |> at col |> field "callback_data" |> asString

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

/// A recording `IMafObserver` — every surfaced condition lands in its own list, so a test can
/// assert exactly what was surfaced without inspecting a bot logger's text.
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

[<Tests>]
let mafBridgeRefusalTests =
    testList "MafBridgeRefusal" [

        testCaseAsync "a tap outside the run's configured owner scope is refused by the engine — no resume"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 7101L
                    let ownerId = 8001L
                    let otherId = 8002L

                    let agent = ScriptedAgent [ PausesFor("req-1", "send_email", []); RepliesWith "sent" ]
                    let tools = ToolRegistry.create ()
                    let config = (TgBotConfig.create "123456789:TEST-fake-token").WithBaseUrl(server.BaseUrl).WithTools(tools)
                    use! bridge = Maf.startPolling config agent

                    do! bridge.StartRun(UMX.tag<chatId> chat, "Email alice.", owner = Owner.user ownerId)

                    let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                    let approveToken = callbackDataAt 0 0 sent

                    do! deliverTap server 1 "q-outofscope" approveToken chat 1 otherId
                    do! pollUntil 5000 (fun () -> server.RequestsFor "answerCallbackQuery" |> List.isEmpty |> not)

                    match server.RequestsFor "answerCallbackQuery" with
                    | [ ack ] ->
                        let ackBody = ack.Body |> Option.get
                        Expect.equal (ackBody |> field "text" |> asString) OwnerScope.DefaultDeniedNotice "the non-owner sees the denied notice"
                    | other -> failwithf "expected exactly one answerCallbackQuery, got %d" (List.length other)

                    Expect.isEmpty (server.RequestsFor "editMessageText") "the out-of-scope tap never edits the message"
                    Expect.equal agent.RunCount 1 "only the initial StartRun turn ran — the out-of-scope tap never resumed the agent"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a chained approval's decision still enforces owner scope in the LEAF — a non-owner tap on it is refused, never resumes"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 7104L
                    let otherId = 8003L

                    let agent =
                        ScriptedAgent [
                            PausesFor("req-1", "send_email", [])
                            PausesFor("req-2", "send_sms", [])
                            RepliesWith "All done."
                        ]

                    let tools = ToolRegistry.create ()
                    let config = (TgBotConfig.create "123456789:TEST-fake-token").WithBaseUrl(server.BaseUrl).WithTools(tools)
                    use! bridge = Maf.startPolling config agent

                    // No explicit owner — RunOwner.resolve's private-chat-peer default applies, so
                    // the OWNER of both req-1 and (once chained) req-2 is the peer whose id equals
                    // `chat`.
                    do! bridge.StartRun(UMX.tag<chatId> chat, "Notify the team.")

                    let firstSend = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                    let approveToken1 = callbackDataAt 0 0 firstSend

                    // The OWNER taps Approve on req-1 — this resumes and CHAINS to req-2, rendered
                    // via `EditKeyboardPlan` on the SAME message; that replacement binding is
                    // `Anyone`-scoped AT THE ENGINE LEVEL (`TgBot.EditKeyboardPlan` accepts no
                    // owner parameter), so the engine's own binding-level owner check alone would
                    // let ANYONE resolve it.
                    do! deliverTap server 1 "q-chain-owner" approveToken1 chat 1 chat
                    do! pollUntil 5000 (fun () -> server.RequestsFor "editMessageText" |> List.isEmpty |> not)
                    Expect.equal agent.RunCount 2 "the owner's tap on req-1 resumed the agent, chaining to req-2"

                    let chainedEdit = (server.RequestsFor "editMessageText").Head.Body |> Option.get
                    let approveToken2 = callbackDataAt 0 0 chainedEdit

                    // A DIFFERENT user taps Approve on the CHAINED req-2 — the engine's own
                    // binding-level check would ALLOW this (the binding is `Anyone`), so this must
                    // be refused by the LEAF's own owner check against the pending entry's OWN
                    // `Owner` (propagated from req-1's original owner-scoped send).
                    do! deliverTap server 2 "q-chain-other" approveToken2 chat 1 otherId
                    do! pollUntil 5000 (fun () -> server.RequestsFor "answerCallbackQuery" |> List.length >= 2)

                    Expect.equal agent.RunCount 2 "the non-owner's tap on the CHAINED approval never resumed the agent a third time"
                    Expect.equal (List.length (server.RequestsFor "editMessageText")) 1 "the non-owner's tap never produced a second edit"

                    match server.RequestsFor "answerCallbackQuery" with
                    | [ _; secondAck ] ->
                        let ackBody = secondAck.Body |> Option.get
                        Expect.equal (ackBody |> field "text" |> asString) OwnerScope.DefaultDeniedNotice "the non-owner sees the denied notice"
                    | other -> failtestf "expected exactly two answerCallbackQuery calls, got %d" (List.length other)
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a second tap on the SAME already-decided button is refused by the engine's own single-use removal — no second resume"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 7102L

                    let agent = ScriptedAgent [ PausesFor("req-1", "send_email", []); RepliesWith "sent" ]
                    let tools = ToolRegistry.create ()
                    let config = (TgBotConfig.create "123456789:TEST-fake-token").WithBaseUrl(server.BaseUrl).WithTools(tools)
                    use! bridge = Maf.startPolling config agent

                    do! bridge.StartRun(UMX.tag<chatId> chat, "Email alice.")

                    let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                    let approveToken = callbackDataAt 0 0 sent

                    // No explicit owner was passed to StartRun, so RunOwner.resolve's private-chat-
                    // peer default applies (Bot API fact: chat.id IS the peer's user id for a
                    // private chat) — the tapping user must equal the chat id to be in scope.
                    do! deliverTap server 1 "q-first" approveToken chat 1 chat
                    do! pollUntil 5000 (fun () -> server.RequestsFor "editMessageText" |> List.isEmpty |> not)
                    Expect.equal agent.RunCount 2 "the first tap resumed the agent exactly once (plus the initial StartRun turn)"

                    do! deliverTap server 2 "q-repeat" approveToken chat 1 chat
                    do! pollUntil 5000 (fun () -> server.RequestsFor "answerCallbackQuery" |> List.length >= 2)

                    Expect.equal (List.length (server.RequestsFor "editMessageText")) 1 "the repeat tap never produces a second edit"
                    Expect.equal agent.RunCount 2 "the repeat tap never resumes the agent a second time"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a sibling-button race (tapping Reject after Approve already resolved) is refused as stale — never resumes"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 7103L

                    let agent = ScriptedAgent [ PausesFor("req-1", "send_email", []); RepliesWith "sent" ]
                    let observer = RecordingObserver()

                    let options: MafBridgeOptions =
                        { MafBridgeOptions.defaults with
                            Observer = ValueSome(observer :> IMafObserver) }

                    let tools = ToolRegistry.create ()
                    let config = (TgBotConfig.create "123456789:TEST-fake-token").WithBaseUrl(server.BaseUrl).WithTools(tools)
                    use! bridge = Maf.startPollingWith options config agent

                    do! bridge.StartRun(UMX.tag<chatId> chat, "Email alice.")

                    let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                    let approveToken = callbackDataAt 0 0 sent
                    let rejectToken = callbackDataAt 0 1 sent

                    do! deliverTap server 1 "q-approve-first" approveToken chat 1 chat
                    do! pollUntil 5000 (fun () -> server.RequestsFor "editMessageText" |> List.isEmpty |> not)
                    Expect.equal agent.RunCount 2 "the approve tap resumed the agent"

                    do! deliverTap server 2 "q-reject-race" rejectToken chat 1 chat
                    do! pollUntil 5000 (fun () -> not (List.isEmpty observer.StaleDecisions))

                    Expect.equal (List.length observer.StaleDecisions) 1 "the sibling tap is surfaced as exactly one stale decision"
                    Expect.equal observer.StaleDecisions[0].RequestId "req-1" "the surfaced descriptor names the already-decided request"
                    Expect.equal agent.RunCount 2 "the sibling tap never resumes the agent a second time"
                    Expect.equal (List.length (server.RequestsFor "editMessageText")) 1 "the sibling tap never produces a second edit"
                }
                |> Async.AwaitTask
        }
    ]
