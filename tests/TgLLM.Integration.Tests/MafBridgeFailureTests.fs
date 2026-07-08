/// Acceptance for the MAF bridge's resume-failure path: a scripted throw on resume is surfaced via
/// `IMafObserver.OnResumeFailed` and the approval message is edited to a failure note; no button on
/// that message can resume the agent again afterward — a sibling tap lands on the stale path
/// instead, since the pending entry was consumed (and the run abandoned) before the resume ever
/// threw.
module TgLLM.Integration.Tests.MafBridgeFailureTests

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

type private RecordingObserver() =
    let resumeFailures = ResizeArray<ApprovalDescriptor * exn>()

    member _.ResumeFailures: (ApprovalDescriptor * exn) list = List.ofSeq resumeFailures

    interface IMafObserver with
        member _.OnStaleDecision(_descriptor) = ()
        member _.OnMalformedDecision(_raw) = ()
        member _.OnResumeFailed(descriptor, error) = resumeFailures.Add(descriptor, error)
        member _.OnEmptyTurn(_chat) = ()
        member _.OnInvalidOutput(_chat, _error) = ()
        member _.OnProjectionProblem(_problem) = ()

[<Tests>]
let mafBridgeFailureTests =
    testList "MafBridgeFailure" [

        testCaseAsync "a resume that throws is surfaced via OnResumeFailed and edits the message to a failure note; no button can resume it again"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 7201L
                    let failure = InvalidOperationException "the agent's tool backend is unreachable"

                    let agent = ScriptedAgent [ PausesFor("req-1", "send_email", []); Throws failure ]
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

                    // No explicit owner was passed to StartRun, so RunOwner.resolve's private-chat-
                    // peer default applies (Bot API fact: chat.id IS the peer's user id for a
                    // private chat) — the tapping user must equal the chat id to be in scope.
                    do! deliverTap server 1 "q-fail-1" approveToken chat 1 chat
                    do! pollUntil 5000 (fun () -> server.RequestsFor "editMessageText" |> List.isEmpty |> not)

                    Expect.equal observer.ResumeFailures.Length 1 "the resume failure was surfaced exactly once"
                    let failedDescriptor, failedError = observer.ResumeFailures[0]
                    Expect.equal failedDescriptor.RequestId "req-1" "the surfaced descriptor names the request that failed to resume"
                    Expect.equal failedError.Message failure.Message "the surfaced error is the one the agent threw"

                    let editBody = (server.RequestsFor "editMessageText").Head.Body |> Option.get
                    let noteText = editBody |> field "text" |> asString
                    Expect.stringContains noteText "send_email" "the failure note names the tool that could not complete"

                    // No button on this message can resume the agent again: the sibling (Reject)
                    // binding is still routable, but its pending entry was already consumed before
                    // the resume attempt threw, so this tap lands on the stale path — never a
                    // second `RunAsync` call.
                    do! deliverTap server 2 "q-fail-sibling" rejectToken chat 1 chat
                    do! pollUntil 5000 (fun () -> server.RequestsFor "answerCallbackQuery" |> List.length >= 2)

                    Expect.equal agent.RunCount 2 "exactly the initial turn plus the one failed resume attempt — the sibling tap never runs a third turn"
                    Expect.equal (List.length (server.RequestsFor "editMessageText")) 1 "the sibling tap produces no further edit"
                }
                |> Async.AwaitTask
        }
    ]
