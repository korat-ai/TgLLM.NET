/// Acceptance for the MAF bridge's reliability/observability sweep: every surfaced condition
/// reaches `IMafObserver`, and the run loop never crashes or sends something malformed instead.
/// Complements `MafBridgeRefusalTests.fs` (its sibling-race case already covers ONE
/// `OnStaleDecision` trigger) and `MafBridgeFailureTests.fs` (`OnResumeFailed`) with the remaining
/// conditions: a post-restart-shaped stale decision (a well-formed descriptor for a request no
/// bridge instance ever had pending), a malformed decision payload, an empty turn, and an
/// over-the-Bot-API-limit reply.
module TgLLM.Integration.Tests.MafObservabilityTests

open System.Text.Json.Nodes
open System.Threading.Tasks
open Expecto
open FSharp.UMX
open Microsoft.Extensions.AI
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

let private deliverTap (server: FakeBotApiServer) (updateId: int) (queryId: string) (token: string) (chat: int64) (messageId: int) (userId: int64) : unit =
    server.EnqueueResult("getUpdates", TelegramJson.batch [ TelegramJson.callbackQueryUpdate updateId queryId token chat messageId userId "Tester" ])

let private approveToolName: ToolName =
    match ToolName.create "maf-approve" with
    | Ok n -> n
    | Error e -> failwithf "test setup: unreachable %A" e

/// Returns the SAME fixed binding for ANY token requested — routes an arbitrary tap straight to
/// `maf-approve`'s handler with a caller-controlled `Arg`, without going through a real
/// `SendKeyboardPlan` (which could never itself produce a malformed/unknown-to-this-bridge arg).
type private FixedBindingStore(binding: ToolBinding) =
    interface IBindingStore with
        member _.Save(_bindings, _ct) = ValueTask.CompletedTask
        member _.TryGet(_token, _ct) = ValueTask.FromResult(ValueSome binding)
        member _.Remove(_tokens, _ct) = ValueTask.CompletedTask
        member _.EvictExpired(_now) = ValueTask.FromResult 0

/// Every `IMafObserver` member lands in its own recorded list — used both for the individual
/// per-condition tests below and for this file's own completeness-sweep test.
type private RecordingObserver() =
    let stale = ResizeArray<ApprovalDescriptor>()
    let malformed = ResizeArray<string>()
    let resumeFailed = ResizeArray<ApprovalDescriptor * exn>()
    let emptyTurn = ResizeArray<ChatId>()
    let invalidOutput = ResizeArray<ChatId * MafError>()
    let projectionProblem = ResizeArray<ProjectionProblem>()
    let turnFailed = ResizeArray<ChatId * exn>()

    member _.Stale: ApprovalDescriptor list = List.ofSeq stale
    member _.Malformed: string list = List.ofSeq malformed
    member _.ResumeFailed: (ApprovalDescriptor * exn) list = List.ofSeq resumeFailed
    member _.EmptyTurn: ChatId list = List.ofSeq emptyTurn
    member _.InvalidOutput: (ChatId * MafError) list = List.ofSeq invalidOutput
    member _.ProjectionProblem: ProjectionProblem list = List.ofSeq projectionProblem
    member _.TurnFailed: (ChatId * exn) list = List.ofSeq turnFailed

    interface IMafObserver with
        member _.OnStaleDecision(descriptor) = stale.Add descriptor
        member _.OnMalformedDecision(raw) = malformed.Add raw
        member _.OnResumeFailed(descriptor, error) = resumeFailed.Add(descriptor, error)
        member _.OnEmptyTurn(chat) = emptyTurn.Add chat
        member _.OnInvalidOutput(chat, error) = invalidOutput.Add(chat, error)
        member _.OnProjectionProblem(problem) = projectionProblem.Add problem
        member _.OnTurnFailed(chat, error) = turnFailed.Add(chat, error)

let private startBridgeWith (server: FakeBotApiServer) (agent: ScriptedAgent) (observer: IMafObserver) (bindingStore: IBindingStore option) : Task<MafBridge> =
    let tools = ToolRegistry.create ()

    let baseConfig =
        (TgBotConfig.create "123456789:TEST-fake-token")
            .WithBaseUrl(server.BaseUrl)
            .WithTools(tools)

    let config =
        match bindingStore with
        | Some store -> baseConfig.WithBindingStore store
        | None -> baseConfig

    let options: MafBridgeOptions =
        { MafBridgeOptions.defaults with
            Observer = ValueSome observer }

    Maf.startPollingWith options config agent

[<Tests>]
let mafObservabilityTests =
    testList "MafBridgeObservability" [

        testCaseAsync "a well-formed decision for a request NO bridge instance ever had pending (post-restart shape) is surfaced via OnStaleDecision — acked, never resumed"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 7301L

                    let ghostDescriptor: ApprovalDescriptor =
                        { Chat = chat
                          RequestId = "ghost-request"
                          Tool = "send_email" }

                    let binding =
                        ToolBinding.create (CallbackToken.generate ()) approveToolName (Some(ApprovalDescriptor.serialize ghostDescriptor))

                    let agent = ScriptedAgent []
                    let observer = RecordingObserver()
                    use! bridge = startBridgeWith server agent (observer :> IMafObserver) (Some(FixedBindingStore binding :> IBindingStore))
                    ignore bridge

                    deliverTap server 1 "q-ghost" (CallbackToken.value (CallbackToken.generate ())) chat 1 chat
                    do! pollUntil 5000 (fun () -> not (List.isEmpty observer.Stale))

                    Expect.equal (List.length observer.Stale) 1 "exactly one stale decision was surfaced"
                    Expect.equal observer.Stale[0].RequestId "ghost-request" "the descriptor is still fully readable — request id survives"
                    Expect.equal observer.Stale[0].Tool "send_email" "the descriptor is still fully readable — tool name survives"
                    Expect.equal agent.RunCount 0 "a stale decision never resumes the agent"

                    do! pollUntil 5000 (fun () -> server.RequestsFor "answerCallbackQuery" |> List.isEmpty |> not)
                    Expect.isEmpty (server.RequestsFor "editMessageText") "a stale decision never edits any message"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a decision arg that does not parse is surfaced via OnMalformedDecision — acked, never resumed"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 7302L

                    let binding = ToolBinding.create (CallbackToken.generate ()) approveToolName (Some "not valid json at all")
                    let agent = ScriptedAgent []
                    let observer = RecordingObserver()
                    use! bridge = startBridgeWith server agent (observer :> IMafObserver) (Some(FixedBindingStore binding :> IBindingStore))
                    ignore bridge

                    deliverTap server 1 "q-malformed" (CallbackToken.value (CallbackToken.generate ())) chat 1 chat
                    do! pollUntil 5000 (fun () -> not (List.isEmpty observer.Malformed))

                    Expect.equal observer.Malformed [ "not valid json at all" ] "the unparseable raw payload is surfaced verbatim"
                    Expect.equal agent.RunCount 0 "a malformed decision never resumes the agent"
                    Expect.isEmpty observer.Stale "a malformed payload is a DIFFERENT condition than a stale-but-parseable one"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a turn with no text and no approval is surfaced via OnEmptyTurn — no empty message is ever sent"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 7303L

                    let agent = ScriptedAgent [ EndsEmpty ]
                    let observer = RecordingObserver()
                    use! bridge = startBridgeWith server agent (observer :> IMafObserver) None

                    do! bridge.StartRun(UMX.tag<chatId> chat, "Do nothing in particular.")
                    do! pollUntil 5000 (fun () -> not (List.isEmpty observer.EmptyTurn))

                    Expect.equal observer.EmptyTurn [ UMX.tag<chatId> chat ] "the empty turn is surfaced for the right chat"
                    Expect.isEmpty (server.RequestsFor "sendMessage") "no message — empty or otherwise — was ever sent for an empty turn"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a reply over the Bot API length limit is surfaced via OnInvalidOutput — the turn completes without crashing"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 7304L
                    let overLongReply = String.replicate 4200 "x"

                    let agent = ScriptedAgent [ RepliesWith overLongReply ]
                    let observer = RecordingObserver()
                    use! bridge = startBridgeWith server agent (observer :> IMafObserver) None

                    do! bridge.StartRun(UMX.tag<chatId> chat, "Say something very long.")
                    do! pollUntil 5000 (fun () -> not (List.isEmpty observer.InvalidOutput))

                    match observer.InvalidOutput with
                    | [ (surfacedChat, ReplyTooLong(length, max)) ] ->
                        Expect.equal surfacedChat (UMX.tag<chatId> chat) "surfaced for the right chat"
                        Expect.equal length overLongReply.Length "the surfaced length matches the actual reply"
                        Expect.isGreaterThan length max "the reply genuinely exceeds the surfaced maximum"
                    | other -> failwithf "expected exactly one ReplyTooLong, got %A" other

                    Expect.isEmpty (server.RequestsFor "sendMessage") "the over-limit reply itself was never put on the wire"

                    // The turn didn't crash the bridge/chat lock — a SECOND, ordinary turn on the
                    // SAME chat still completes normally afterward.
                    let agent2Reply = TaskCompletionSource<unit>()
                    ignore agent2Reply
                    do! bridge.StartRun(UMX.tag<chatId> chat, "Say something short this time.")
                    do! pollUntil 5000 (fun () -> not (List.isEmpty observer.EmptyTurn))
                    Expect.equal agent.RunCount 2 "a follow-up turn on the same chat still runs — the over-limit reply didn't poison the chat's lock"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "completeness sweep: every IMafObserver member has an actual triggering path — none is dead code"
        <| async {
            do!
                task {
                    let observer = RecordingObserver()

                    // OnEmptyTurn.
                    use! emptyServer = FakeBotApiServer.start ()
                    let emptyAgent = ScriptedAgent [ EndsEmpty ]
                    use! emptyBridge = startBridgeWith emptyServer emptyAgent (observer :> IMafObserver) None
                    do! emptyBridge.StartRun(UMX.tag<chatId> 7501L, "go")
                    do! pollUntil 5000 (fun () -> not (List.isEmpty observer.EmptyTurn))

                    // OnInvalidOutput.
                    use! longServer = FakeBotApiServer.start ()
                    let longAgent = ScriptedAgent [ RepliesWith(String.replicate 4200 "x") ]
                    use! longBridge = startBridgeWith longServer longAgent (observer :> IMafObserver) None
                    do! longBridge.StartRun(UMX.tag<chatId> 7502L, "go")
                    do! pollUntil 5000 (fun () -> not (List.isEmpty observer.InvalidOutput))

                    // OnResumeFailed.
                    use! failServer = FakeBotApiServer.start ()
                    let failAgent = ScriptedAgent [ PausesFor("r-fail", "t", []); Throws(System.Exception "boom") ]
                    use! failBridge = startBridgeWith failServer failAgent (observer :> IMafObserver) None
                    do! failBridge.StartRun(UMX.tag<chatId> 7503L, "go")
                    let failSent = (failServer.RequestsFor "sendMessage").Head.Body |> Option.get
                    let failToken = callbackDataAt 0 0 failSent
                    deliverTap failServer 1 "q-sweep-fail" failToken 7503L 1 7503L
                    do! pollUntil 5000 (fun () -> not (List.isEmpty observer.ResumeFailed))

                    // OnStaleDecision (a well-formed descriptor with no pending entry — the
                    // post-restart shape) and OnMalformedDecision (a payload that fails to parse
                    // at all) — both via `FixedBindingStore`, same technique as this file's own
                    // dedicated tests above.
                    let staleDescriptor: ApprovalDescriptor =
                        { Chat = 7504L; RequestId = "r-stale"; Tool = "t" }

                    let staleBinding =
                        ToolBinding.create (CallbackToken.generate ()) approveToolName (Some(ApprovalDescriptor.serialize staleDescriptor))

                    use! staleServer = FakeBotApiServer.start ()
                    let staleAgent = ScriptedAgent []

                    use! staleBridge =
                        startBridgeWith staleServer staleAgent (observer :> IMafObserver) (Some(FixedBindingStore staleBinding :> IBindingStore))

                    deliverTap staleServer 1 "q-sweep-stale" (CallbackToken.value (CallbackToken.generate ())) 7504L 1 7504L
                    do! pollUntil 5000 (fun () -> not (List.isEmpty observer.Stale))

                    let malformedBinding = ToolBinding.create (CallbackToken.generate ()) approveToolName (Some "not json")
                    use! malformedServer = FakeBotApiServer.start ()
                    let malformedAgent = ScriptedAgent []

                    use! malformedBridge =
                        startBridgeWith malformedServer malformedAgent (observer :> IMafObserver) (Some(FixedBindingStore malformedBinding :> IBindingStore))

                    deliverTap malformedServer 1 "q-sweep-malformed" (CallbackToken.value (CallbackToken.generate ())) 7505L 1 7505L
                    do! pollUntil 5000 (fun () -> not (List.isEmpty observer.Malformed))

                    // OnProjectionProblem — MafTools.projectWith, unrelated to any bridge/bot.
                    let registry = ToolRegistry.create ()
                    let echo = System.Func<string, string>(id)
                    let badDeclaration = AIFunctionFactory.Create(echo, "", "desc", null)
                    MafTools.projectWith (observer :> IMafObserver) registry [ badDeclaration ] |> ignore

                    // OnTurnFailed — a host-initiated run whose OWN turn throws (session creation
                    // or `agent.RunAsync` itself), never reaching a reply or a pending approval.
                    use! turnFailServer = FakeBotApiServer.start ()
                    let turnFailAgent = ScriptedAgent [ Throws(System.Exception "turn boom") ]
                    use! turnFailBridge = startBridgeWith turnFailServer turnFailAgent (observer :> IMafObserver) None
                    do! turnFailBridge.StartRun(UMX.tag<chatId> 7506L, "go")
                    do! pollUntil 5000 (fun () -> not (List.isEmpty observer.TurnFailed))

                    Expect.isNonEmpty observer.Stale "OnStaleDecision has a real triggering path"
                    Expect.isNonEmpty observer.Malformed "OnMalformedDecision has a real triggering path"
                    Expect.isNonEmpty observer.ResumeFailed "OnResumeFailed has a real triggering path"
                    Expect.isNonEmpty observer.EmptyTurn "OnEmptyTurn has a real triggering path"
                    Expect.isNonEmpty observer.InvalidOutput "OnInvalidOutput has a real triggering path"
                    Expect.isNonEmpty observer.ProjectionProblem "OnProjectionProblem has a real triggering path"
                    Expect.isNonEmpty observer.TurnFailed "OnTurnFailed has a real triggering path"
                }
                |> Async.AwaitTask
        }
    ]
