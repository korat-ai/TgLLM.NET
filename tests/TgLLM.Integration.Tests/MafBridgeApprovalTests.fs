/// Acceptance for the MAF bridge's approve/reject happy paths, over `FakeBotApiServer` and a
/// `ScriptedAgent`: one message with `[Approve][Reject]`; Approve resumes with `true` and edits the
/// SAME message to the outcome; Reject resumes with `false` and edits to the rejection; a further
/// approval in the same turn gets fresh buttons on the SAME message; a host formatter overrides the
/// zero-config default rendering.
///
/// Every tap below is delivered from a user whose id EQUALS the chat id: `RunOwner.resolve`'s
/// private-chat-peer default (Bot API fact — a private chat's `chat.id` IS the peer's user id)
/// applies to every `StartRun` here, since none of them pass an explicit `owner` — so the tapping
/// user must be that SAME peer to be in scope. `MafBridgeRefusalTests.fs` covers a tap from a
/// DIFFERENT user being refused.
module TgLLM.Integration.Tests.MafBridgeApprovalTests

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

let private startBridge (server: FakeBotApiServer) (agent: ScriptedAgent) : Task<MafBridge> =
    let tools = ToolRegistry.create ()
    let config = (TgBotConfig.create "123456789:TEST-fake-token").WithBaseUrl(server.BaseUrl).WithTools(tools)
    Maf.startPolling config agent

let private startBridgeWith (server: FakeBotApiServer) (agent: ScriptedAgent) (options: MafBridgeOptions) : Task<MafBridge> =
    let tools = ToolRegistry.create ()
    let config = (TgBotConfig.create "123456789:TEST-fake-token").WithBaseUrl(server.BaseUrl).WithTools(tools)
    Maf.startPollingWith options config agent

[<Tests>]
let mafBridgeApprovalTests =
    testList "MafBridgeApproval" [

        testCaseAsync "Approve resumes true and edits the SAME message to the outcome; no new message is sent"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 7001L

                    let resumes = ResizeArray<string * bool>()

                    let agent =
                        ScriptedAgent(
                            [ PausesFor("req-1", "send_email", [ "toAddr", box "alice@example.com" ])
                              RepliesWith "Email sent to alice@example.com." ],
                            onResume = (fun (reqId, approved) -> resumes.Add(reqId, approved))
                        )

                    use! bridge = startBridge server agent
                    do! bridge.StartRun(UMX.tag<chatId> chat, "Email alice that the deploy is done.")

                    Expect.equal (List.length (server.RequestsFor "sendMessage")) 1 "the pending approval sends exactly one message"
                    let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                    let body = sent |> field "text" |> asString
                    Expect.stringContains body "send_email" "the default render shows the tool name"
                    Expect.stringContains body "alice@example.com" "the default render shows the arguments"
                    Expect.isNone (tryField "parse_mode" sent) "the approval prompt is sent as plain text — no parse mode"

                    Expect.equal (buttonTextAt 0 0 sent) "Approve" "the first button is the default Approve label"
                    Expect.equal (buttonTextAt 0 1 sent) "Reject" "the second button is the default Reject label"
                    let approveToken = callbackDataAt 0 0 sent

                    do! deliverTap server 1 "q-approve-1" approveToken chat 1 chat
                    do! pollUntil 5000 (fun () -> server.RequestsFor "editMessageText" |> List.isEmpty |> not)

                    Expect.equal (List.length (server.RequestsFor "sendMessage")) 1 "approving never sends a NEW message"
                    Expect.equal resumes.Count 1 "the agent was resumed exactly once"
                    Expect.equal resumes[0] ("req-1", true) "the agent was resumed with the tapped request id and Approved = true"

                    match server.RequestsFor "editMessageText" with
                    | [ edit ] ->
                        let editBody = edit.Body |> Option.get
                        Expect.equal (editBody |> field "chat_id" |> asInt64) chat "the edit targets the same chat"
                        Expect.equal (editBody |> field "message_id" |> asInt64) 1L "the edit targets the SAME (originally sent) message"
                        let outcome = editBody |> field "text" |> asString
                        Expect.stringContains outcome "send_email" "the outcome mentions the tool"
                        Expect.stringContains outcome "approved" "the outcome says it was approved"
                        Expect.stringContains outcome "Email sent to alice@example.com." "the outcome carries the agent's own reply text"
                    | other -> failwithf "expected exactly one editMessageText call, got %d" (List.length other)
                }
                |> Async.AwaitTask
        }

        testCaseAsync "Reject resumes false and edits the SAME message to the rejection"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 7002L
                    let resumes = ResizeArray<string * bool>()

                    let agent =
                        ScriptedAgent(
                            [ PausesFor("req-1", "delete_record", [])
                              RepliesWith "" ],
                            onResume = (fun (reqId, approved) -> resumes.Add(reqId, approved))
                        )

                    use! bridge = startBridge server agent
                    do! bridge.StartRun(UMX.tag<chatId> chat, "Delete the record.")

                    let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                    let rejectToken = callbackDataAt 0 1 sent

                    do! deliverTap server 1 "q-reject-1" rejectToken chat 1 chat
                    do! pollUntil 5000 (fun () -> server.RequestsFor "editMessageText" |> List.isEmpty |> not)

                    Expect.equal (List.length (server.RequestsFor "sendMessage")) 1 "rejecting never sends a NEW message"
                    Expect.equal resumes[0] ("req-1", false) "the agent was resumed with Approved = false"

                    let editBody = (server.RequestsFor "editMessageText").Head.Body |> Option.get
                    let outcome = editBody |> field "text" |> asString
                    Expect.stringContains outcome "rejected" "the outcome says it was rejected"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a further approval in the same turn gets fresh buttons on the SAME message — message count never grows"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 7003L

                    let agent =
                        ScriptedAgent(
                            [ PausesFor("req-1", "send_email", [])
                              PausesFor("req-2", "send_sms", [])
                              RepliesWith "All done." ]
                        )

                    use! bridge = startBridge server agent
                    do! bridge.StartRun(UMX.tag<chatId> chat, "Notify the team.")

                    Expect.equal (List.length (server.RequestsFor "sendMessage")) 1 "the first approval sends exactly one message"
                    let firstSend = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                    let approveToken1 = callbackDataAt 0 0 firstSend

                    do! deliverTap server 1 "q-chain-1" approveToken1 chat 1 chat
                    do! pollUntil 5000 (fun () -> server.RequestsFor "editMessageText" |> List.isEmpty |> not)

                    Expect.equal (List.length (server.RequestsFor "sendMessage")) 1 "the chained request reuses the SAME message — no new send"

                    let chainedEdits = server.RequestsFor "editMessageText"
                    Expect.equal (List.length chainedEdits) 1 "the chained request edits text+keyboard together (TgBot.EditKeyboardPlan)"
                    let chainedBody = chainedEdits.Head.Body |> Option.get
                    Expect.equal (chainedBody |> field "message_id" |> asInt64) 1L "the chained edit targets the SAME message"
                    Expect.stringContains (chainedBody |> field "text" |> asString) "send_sms" "the chained edit shows the NEXT request's own prompt"
                    let approveToken2 = callbackDataAt 0 0 chainedBody

                    do! deliverTap server 2 "q-chain-2" approveToken2 chat 1 chat
                    do! pollUntil 5000 (fun () -> server.RequestsFor "editMessageText" |> List.length >= 2)

                    Expect.equal (List.length (server.RequestsFor "sendMessage")) 1 "still exactly one message across the whole chained approval"
                    let finalEdit = (server.RequestsFor "editMessageText") |> List.last
                    let finalBody = finalEdit.Body |> Option.get
                    Expect.stringContains (finalBody |> field "text" |> asString) "All done." "the final edit shows the turn's concluding reply"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a host formatter overrides the body/labels; the zero-config default is used when none is set"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 7004L

                    let formatter: ApprovalFormatter =
                        fun prompt ->
                            { Body = $"Allow {prompt.Tool}?"
                              ApproveLabel = "Да"
                              RejectLabel = "Нет" }

                    let options: MafBridgeOptions =
                        { MafBridgeOptions.defaults with
                            Formatter = ValueSome formatter }

                    let agent = ScriptedAgent [ PausesFor("req-1", "send_email", []) ]
                    use! bridge = startBridgeWith server agent options
                    do! bridge.StartRun(UMX.tag<chatId> chat, "Email alice.")

                    let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                    Expect.equal (sent |> field "text" |> asString) "Allow send_email?" "the formatter's body was used verbatim"
                    Expect.equal (buttonTextAt 0 0 sent) "Да" "the formatter's Approve label was used"
                    Expect.equal (buttonTextAt 0 1 sent) "Нет" "the formatter's Reject label was used"
                }
                |> Async.AwaitTask
        }
    ]
