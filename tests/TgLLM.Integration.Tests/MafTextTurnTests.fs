/// Acceptance for the MAF bridge's text turn (the Core message seam wired via `Maf.startPolling`'s
/// own `config.WithOnMessage`): an incoming user text message reaches the agent and its reply
/// lands back in the same chat; two messages in one chat are answered in arrival order on that
/// chat's own lane; a turn that instead raises an approval hands off to the SAME approval flow
/// `MafBridgeApprovalTests.fs` exercises for a host-initiated run, owner-scoped to the message's
/// own sender (a message-initiated turn defaults to `User sender`, never the bridge's
/// host-initiated default — see `RunOwner.resolve`'s own doc comment).
module TgLLM.Integration.Tests.MafTextTurnTests

open System.Text.Json.Nodes
open System.Threading.Tasks
open Expecto
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

let private deliverTexts (server: FakeBotApiServer) (updates: (int * int64 * int * int64 * string * string) list) : unit =
    let json = updates |> List.map (fun (updateId, chat, messageId, userId, firstName, text) -> TelegramJson.textMessageUpdate updateId chat messageId userId firstName text)
    server.EnqueueResult("getUpdates", TelegramJson.batch json)

let private deliverTap (server: FakeBotApiServer) (updateId: int) (queryId: string) (token: string) (chat: int64) (messageId: int) (userId: int64) : unit =
    server.EnqueueResult("getUpdates", TelegramJson.batch [ TelegramJson.callbackQueryUpdate updateId queryId token chat messageId userId "Tester" ])

let private startBridge (server: FakeBotApiServer) (agent: ScriptedAgent) : Task<TgLLM.Maf.MafBridge> =
    let tools = TgLLM.FSharp.ToolRegistry.create ()
    let config = (TgLLM.FSharp.TgBotConfig.create "123456789:TEST-fake-token").WithBaseUrl(server.BaseUrl).WithTools(tools)
    TgLLM.Maf.Maf.startPolling config agent

let private startBridgeWith
    (server: FakeBotApiServer)
    (agent: ScriptedAgent)
    (options: TgLLM.Maf.MafBridgeOptions)
    : Task<TgLLM.Maf.MafBridge> =
    let tools = TgLLM.FSharp.ToolRegistry.create ()
    let config = (TgLLM.FSharp.TgBotConfig.create "123456789:TEST-fake-token").WithBaseUrl(server.BaseUrl).WithTools(tools)
    TgLLM.Maf.Maf.startPollingWith options config agent

[<Tests>]
let mafTextTurnTests =
    testList "MafBridge text turn" [

        testCaseAsync "an incoming text message reaches the agent and its reply lands in the same chat"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9001L

                    let agent = ScriptedAgent [ RepliesWith "Hello back!" ]
                    use! bridge = startBridge server agent
                    ignore bridge

                    deliverText server 1 chat 5 4242L "Nadia" "Hi agent"
                    do! pollUntil 5000 (fun () -> server.RequestsFor "sendMessage" |> List.isEmpty |> not)

                    let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                    Expect.equal (sent |> field "chat_id" |> asInt64) chat "the reply targets the SAME chat the message came from"
                    Expect.equal (sent |> field "text" |> asString) "Hello back!" "the agent's reply text is sent verbatim"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "two messages in one chat are answered in arrival order, on that chat's own lane"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9002L

                    let agent = ScriptedAgent [ RepliesWith "First."; RepliesWith "Second." ]
                    use! bridge = startBridge server agent
                    ignore bridge

                    deliverTexts
                        server
                        [ (1, chat, 5, 4243L, "Nadia", "one")
                          (2, chat, 6, 4243L, "Nadia", "two") ]

                    do! pollUntil 5000 (fun () -> server.RequestsFor "sendMessage" |> List.length >= 2)

                    match server.RequestsFor "sendMessage" with
                    | [ first; second ] ->
                        Expect.equal (first.Body |> Option.get |> field "text" |> asString) "First." "the FIRST message's reply is sent first"
                        Expect.equal (second.Body |> Option.get |> field "text" |> asString) "Second." "the SECOND message's reply follows, in arrival order"
                    | other -> failwithf "expected exactly two sendMessage calls, got %d" (List.length other)
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a turn that raises an approval hands off to the SAME decision-tool flow as a host-initiated run, owner-scoped to the message's own sender"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9003L
                    let senderId = 4244L

                    let resumes = ResizeArray<string * bool>()

                    let agent =
                        ScriptedAgent(
                            [ PausesFor("req-1", "send_email", [ "toAddr", box "bob@example.com" ])
                              RepliesWith "Sent." ],
                            onResume = (fun (reqId, approved) -> resumes.Add(reqId, approved))
                        )

                    use! bridge = startBridge server agent
                    ignore bridge

                    deliverText server 1 chat 5 senderId "Nadia" "Email bob about the deploy"
                    do! pollUntil 5000 (fun () -> server.RequestsFor "sendMessage" |> List.isEmpty |> not)

                    let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                    Expect.stringContains (sent |> field "text" |> asString) "send_email" "the message-initiated turn's approval renders the pending tool call"
                    let approveToken = callbackDataAt 0 0 sent

                    // The tapping user is the SAME sender who sent the original text message —
                    // RunOwner.resolve's message-initiated default (`User sender`) is what makes
                    // this tap in-scope; a different user tapping the same button is covered by
                    // MafBridgeRefusalTests' out-of-scope case.
                    deliverTap server 2 "q-text-approve" approveToken chat 1 senderId
                    do! pollUntil 5000 (fun () -> server.RequestsFor "editMessageText" |> List.isEmpty |> not)

                    Expect.equal resumes.Count 1 "the agent was resumed exactly once, via the SAME maf-approve decision-tool path"
                    Expect.equal resumes[0] ("req-1", true) "resumed with the tapped request id and Approved = true"

                    let outcome = (server.RequestsFor "editMessageText").Head.Body |> Option.get |> field "text" |> asString
                    Expect.stringContains outcome "Sent." "the outcome carries the agent's own concluding reply"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "an agent that throws during a TEXT turn is surfaced via OnTurnFailed — never silent, and the chat's lock is released for the NEXT turn"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9004L
                    let senderId = 4245L
                    let failure = System.InvalidOperationException "the model backend is unreachable"

                    let turnFailures = ResizeArray<TgLLM.Core.ChatId * exn>()

                    let observer =
                        { new TgLLM.Maf.IMafObserver with
                            member _.OnStaleDecision(_) = ()
                            member _.OnMalformedDecision(_) = ()
                            member _.OnResumeFailed(_, _) = ()
                            member _.OnEmptyTurn(_) = ()
                            member _.OnInvalidOutput(_, _) = ()
                            member _.OnProjectionProblem(_) = ()
                            member _.OnTurnFailed(chat, error) = turnFailures.Add(chat, error) }

                    let agent = ScriptedAgent [ Throws failure; RepliesWith "recovered" ]

                    let options: TgLLM.Maf.MafBridgeOptions =
                        { TgLLM.Maf.MafBridgeOptions.defaults with
                            Observer = ValueSome observer }

                    use! bridge = startBridgeWith server agent options
                    ignore bridge

                    deliverText server 1 chat 5 senderId "Nadia" "Hi agent"
                    do! pollUntil 5000 (fun () -> turnFailures.Count > 0)

                    Expect.equal turnFailures.Count 1 "the throw during the text turn was surfaced exactly once"
                    let failedChat, failedError = turnFailures[0]
                    Expect.equal (FSharp.UMX.UMX.untag failedChat: int64) chat "surfaced for the right chat"
                    Expect.equal failedError.Message failure.Message "the surfaced error is the one the agent threw"
                    Expect.isEmpty (server.RequestsFor "sendMessage") "no reply was ever sent for the failed turn"

                    // The chat's own lock was still released normally — a SECOND message on the
                    // SAME chat still reaches the agent and gets a reply.
                    deliverText server 2 chat 6 senderId "Nadia" "Try again"
                    do! pollUntil 5000 (fun () -> server.RequestsFor "sendMessage" |> List.isEmpty |> not)

                    let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                    Expect.equal (sent |> field "text" |> asString) "recovered" "the chat recovered on the NEXT turn — its lock was not left held"
                }
                |> Async.AwaitTask
        }
    ]
