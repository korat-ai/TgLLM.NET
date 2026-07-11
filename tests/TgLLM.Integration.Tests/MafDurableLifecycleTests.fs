/// Lifecycle/eviction acceptance for the durable session, mirroring `MafDurableResumeTests.fs`'s own
/// restart-simulation shape (a shared `InMemoryBindingStore` + `InMemorySessionStore` across a
/// dispose/recreate — nothing else carries over). Where `MafDurableResumeTests.fs` proves a durable
/// record correctly RESUMES an agent across a restart, this file proves it is correctly BOUNDED and
/// RETIRED: a resume failure removes the now-presumably-dead record, an approval that outlives its
/// own expiry is pruned out of what gets persisted, and a chat's record is overwritten per turn
/// rather than accumulating one entry per turn.
module TgLLM.Integration.Tests.MafDurableLifecycleTests

open System
open System.Collections.Concurrent
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

/// Same shared-stores restart shape as `MafDurableResumeTests.fs`'s own `pollingConfig`.
let private pollingConfig (server: FakeBotApiServer) (bindingStore: IBindingStore) (sessionStore: ISessionStore) : TgBotConfig =
    (TgBotConfig.create "123456789:TEST-fake-token")
        .WithBaseUrl(server.BaseUrl)
        .WithTools(ToolRegistry.create ())
        .WithBindingStore(bindingStore)
        .WithSessionStore(sessionStore)

type private RecordingObserver() =
    let staleDecisions = ResizeArray<ApprovalDescriptor>()
    let resumeFailures = ResizeArray<ApprovalDescriptor * exn>()

    member _.StaleDecisions: ApprovalDescriptor list = List.ofSeq staleDecisions
    member _.ResumeFailures: (ApprovalDescriptor * exn) list = List.ofSeq resumeFailures

    interface IMafObserver with
        member _.OnStaleDecision(descriptor) = staleDecisions.Add descriptor
        member _.OnMalformedDecision(_raw) = ()
        member _.OnResumeFailed(descriptor, error) = resumeFailures.Add(descriptor, error)
        member _.OnEmptyTurn(_chat) = ()
        member _.OnInvalidOutput(_chat, _error) = ()
        member _.OnProjectionProblem(_problem) = ()
        member _.OnTurnFailed(_chat, _error) = ()

    interface IMafSessionObserver with
        member _.OnSessionRestoreFailed(_chat, _failure) = ()
        member _.OnSessionPersistFailed(_chat, _error) = ()

/// A plain `ISessionStore` decorator that exposes how many DISTINCT chat keys it currently holds a
/// record for — `InMemorySessionStore` itself has no such accessor (it exists purely to satisfy the
/// `ISessionStore` contract), and adding one to a production type just for this one assertion isn't
/// warranted. Same `ConcurrentDictionary`-per-chat shape as `InMemorySessionStore`, so `Save`
/// overwrites in place exactly like the production store does.
type private CountingSessionStore() =
    let sessions = ConcurrentDictionary<ChatId, SessionRecord>()

    member _.Count: int = sessions.Count

    interface ISessionStore with
        member _.Save(chat: ChatId, record: SessionRecord, _ct: CancellationToken) : ValueTask =
            sessions[chat] <- record
            ValueTask.CompletedTask

        member _.TryGet(chat: ChatId, _ct: CancellationToken) : ValueTask<SessionRecord voption> =
            match sessions.TryGetValue chat with
            | true, record -> ValueTask.FromResult(ValueSome record)
            | false, _ -> ValueTask.FromResult ValueNone

        member _.Remove(chat: ChatId, _ct: CancellationToken) : ValueTask =
            sessions.TryRemove(chat) |> ignore
            ValueTask.CompletedTask

        member _.EvictIdle(_olderThan: DateTimeOffset) : ValueTask<int> = ValueTask.FromResult 0

[<Tests>]
let mafDurableLifecycleTests =
    testList "MafBridge durable-session lifecycle and eviction" [

        testCaseAsync
            "a resume failure after a restart removes the durable record; a further tap on that chat, after ANOTHER restart, lands stale — never resumed"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9301L
                    let bindingStore = InMemoryBindingStore() :> IBindingStore
                    let sessionStore = InMemorySessionStore() :> ISessionStore

                    let agent1 = ScriptedAgent [ PausesFor("req-1", "send_email", []) ]
                    let! bridge1 = Maf.startPolling (pollingConfig server bindingStore sessionStore) agent1
                    do! bridge1.StartRun(UMX.tag<chatId> chat, "Email alice.")

                    let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                    let approveToken = callbackDataAt 0 0 sent
                    let rejectToken = callbackDataAt 0 1 sent

                    do! (bridge1 :> IAsyncDisposable).DisposeAsync().AsTask()

                    let! beforeDecision = sessionStore.TryGet(UMX.tag<chatId> chat, CancellationToken.None)
                    Expect.isTrue beforeDecision.IsSome "the pre-restart turn's end-of-turn persist left a durable record for this chat"

                    // "Restart": a brand-new agent scripted to throw on the resume, over the SAME two stores.
                    let observer = RecordingObserver()
                    let options: MafBridgeOptions = { MafBridgeOptions.defaults with Observer = ValueSome(observer :> IMafObserver) }
                    let failure = InvalidOperationException "the agent's tool backend is unreachable"
                    let agent2 = ScriptedAgent [ Throws failure ]
                    use! bridge2 = Maf.startPollingWith options (pollingConfig server bindingStore sessionStore) agent2

                    do! deliverTap server 1 "q-lifecycle-fail" approveToken chat 1 chat
                    do! pollUntil 15000 (fun () -> observer.ResumeFailures |> List.isEmpty |> not)

                    Expect.equal observer.ResumeFailures.Length 1 "the post-restart resume failure was surfaced exactly once"
                    let failedDescriptor, _ = observer.ResumeFailures[0]
                    Expect.equal failedDescriptor.RequestId "req-1" "the surfaced descriptor names the request that failed to resume"

                    do!
                        pollUntil 15000 (fun () ->
                            match (sessionStore.TryGet(UMX.tag<chatId> chat, CancellationToken.None)).AsTask().GetAwaiter().GetResult() with
                            | ValueNone -> true
                            | ValueSome _ -> false)

                    let! afterFailure = sessionStore.TryGet(UMX.tag<chatId> chat, CancellationToken.None)
                    Expect.equal afterFailure ValueNone "a resume failure removes this chat's now-presumably-dead durable record"

                    do! (bridge2 :> IAsyncDisposable).DisposeAsync().AsTask()

                    // SECOND restart: nothing durable remains for this chat, so a tap on the sibling
                    // (never-consumed) Reject binding still ROUTES — the engine's own binding is
                    // untouched by the earlier consume/removal — but finds no pending entry to resume.
                    let resumes = ResizeArray<string * bool>()
                    let agent3 = ScriptedAgent([ RepliesWith "unexpected" ], onResume = (fun (r, a) -> resumes.Add(r, a)))
                    use! bridge3 = Maf.startPollingWith options (pollingConfig server bindingStore sessionStore) agent3

                    do! deliverTap server 2 "q-lifecycle-stale" rejectToken chat 1 chat
                    do! pollUntil 15000 (fun () -> observer.StaleDecisions |> List.isEmpty |> not)

                    Expect.equal observer.StaleDecisions.Length 1 "the further tap for this chat lands on the stale path, not a resume"
                    Expect.equal observer.StaleDecisions[0].RequestId "req-1" "the surfaced descriptor still names req-1 — nothing durable ever resurrected it"
                    Expect.equal resumes.Count 0 "the further tap never resumes the agent"
                    Expect.equal agent3.RunCount 0 "the post-second-restart agent never ran a turn for a tap with nothing durable behind it"
                }
                |> Async.AwaitTask
        }

        testCaseAsync
            "an approval that outlives its own expiry is pruned from the NEXT end-of-turn persist; tapping it after a restart lands stale, not resumed"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9302L
                    let bindingStore = InMemoryBindingStore() :> IBindingStore
                    let sessionStore = InMemorySessionStore() :> ISessionStore
                    let mutable now = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                    let clock: Clock = fun () -> now

                    let options: MafBridgeOptions =
                        { MafBridgeOptions.defaults with
                            ApprovalExpiry = ValueSome(TimeSpan.FromMinutes 5.0) }

                    let agent1 = ScriptedAgent [ PausesFor("req-1", "send_email", []); RepliesWith "still working on it" ]
                    let config1 = (pollingConfig server bindingStore sessionStore).WithClock clock
                    use! bridge1 = Maf.startPollingWith options config1 agent1

                    do! bridge1.StartRun(UMX.tag<chatId> chat, "Email alice.")

                    let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                    let approveToken = callbackDataAt 0 0 sent

                    // Past req-1's own 5-minute expiry — a SECOND turn's end-of-turn persist now
                    // snapshots `pendingApprovals` WITHOUT it (`Bridge.fs`'s `persistConversation`
                    // filters by `Expiry.isLive`), even though the in-memory entry itself is
                    // untouched (nothing decided it) and the FIRST message's buttons still stand.
                    now <- now.AddMinutes 6.0
                    do! bridge1.StartRun(UMX.tag<chatId> chat, "Anything else?")
                    do! pollUntil 15000 (fun () -> (server.RequestsFor "sendMessage").Length >= 2)

                    // Rewound before the SAME binding's own send-time `ExpiresAt` (stamped from the
                    // SAME clock, at the SAME instant as the approval's) — isolating the ONE thing
                    // this test is about (the durable record no longer carries req-1) from the
                    // Tool Router's OWN, separate binding-level expiry, which would otherwise refuse
                    // the tap before it ever reached the MAF leaf at all.
                    now <- now.AddMinutes -5.0

                    do! (bridge1 :> IAsyncDisposable).DisposeAsync().AsTask()

                    let observer = RecordingObserver()
                    let restartOptions: MafBridgeOptions = { MafBridgeOptions.defaults with Observer = ValueSome(observer :> IMafObserver) }
                    let resumes = ResizeArray<string * bool>()
                    let agent2 = ScriptedAgent([ RepliesWith "unexpected" ], onResume = (fun (r, a) -> resumes.Add(r, a)))
                    let config2 = (pollingConfig server bindingStore sessionStore).WithClock clock
                    use! bridge2 = Maf.startPollingWith restartOptions config2 agent2

                    do! deliverTap server 1 "q-lifecycle-pruned" approveToken chat 1 chat
                    do! pollUntil 15000 (fun () -> observer.StaleDecisions |> List.isEmpty |> not)

                    Expect.equal observer.StaleDecisions.Length 1 "the pruned-then-restored approval is unresolvable — the tap lands stale"
                    Expect.equal observer.StaleDecisions[0].RequestId "req-1" "the surfaced descriptor names the pruned request"
                    Expect.equal resumes.Count 0 "an expired-and-pruned approval is never resumed, even though its OWN binding still routes"
                    Expect.equal agent2.RunCount 0 "the post-restart agent never ran a turn for a tap with nothing pending behind it"
                }
                |> Async.AwaitTask
        }

        testCaseAsync
            "an expired CHAINED approval lands stale on a tap — WITHOUT any restart — rather than resuming (EditKeyboardPlan carries no expiry of its own, so the peek chain must enforce it)"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9304L
                    let bindingStore = InMemoryBindingStore() :> IBindingStore
                    let sessionStore = InMemorySessionStore() :> ISessionStore
                    let mutable now = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                    let clock: Clock = fun () -> now

                    let observer = RecordingObserver()
                    let resumes = ResizeArray<string * bool>()

                    let options: MafBridgeOptions =
                        { MafBridgeOptions.defaults with
                            ApprovalExpiry = ValueSome(TimeSpan.FromMinutes 5.0)
                            Observer = ValueSome(observer :> IMafObserver) }

                    // req-2 is what THIS test is about — a CHAINED approval, raised by the resume
                    // below, never by an initial turn (`sendNewApproval`'s own `SendKeyboardPlan` DOES
                    // carry a real, Tool-Router-level expiry; `sendChainedApproval`'s own
                    // `EditKeyboardPlan` cannot — see its doc comment — so req-2's ONLY expiry
                    // enforcement, before this fix, is the persist filter, never the tap/resume path
                    // itself).
                    let agent =
                        ScriptedAgent(
                            [ PausesFor("req-1", "send_email", []); PausesFor("req-2", "send_sms", []) ],
                            onResume = (fun (r, a) -> resumes.Add(r, a))
                        )

                    let config = (pollingConfig server bindingStore sessionStore).WithClock clock
                    use! bridge = Maf.startPollingWith options config agent

                    do! bridge.StartRun(UMX.tag<chatId> chat, "Notify the team.")

                    let firstSend = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                    let approveToken1 = callbackDataAt 0 0 firstSend

                    // Approving req-1 (still well within its own expiry) resumes the agent, which
                    // raises the CHAINED req-2 onto the SAME message — stamped, per `sendChainedApproval`,
                    // with `ExpiresAt = bot.Clock() + expiry` at THIS instant.
                    do! deliverTap server 1 "q-chain-expiry-1" approveToken1 chat 1 chat
                    do! pollUntil 15000 (fun () -> server.RequestsFor "editMessageText" |> List.isEmpty |> not)

                    Expect.equal resumes.Count 1 "req-1's own resume ran exactly once"
                    Expect.equal resumes[0] ("req-1", true) "req-1 was approved"

                    let chainedBody = (server.RequestsFor "editMessageText").Head.Body |> Option.get
                    let approveToken2 = callbackDataAt 0 0 chainedBody

                    // Past req-2's own 5-minute expiry — NO restart happens here at all: this is the
                    // SAME bridge, the SAME in-memory `pendingApprovals` entry for req-2, untouched by
                    // anything except the clock.
                    now <- now.AddMinutes 6.0

                    do! deliverTap server 2 "q-chain-expiry-2" approveToken2 chat 1 chat
                    do! pollUntil 15000 (fun () -> observer.StaleDecisions |> List.isEmpty |> not)

                    Expect.equal observer.StaleDecisions.Length 1 "the expired chained approval lands on the stale path"
                    Expect.equal observer.StaleDecisions[0].RequestId "req-2" "the surfaced descriptor names the expired chained request"
                    Expect.equal resumes.Count 1 "req-2 is NEVER resumed once its own expiry has passed — no second resume was added"
                    Expect.equal agent.RunCount 2 "the agent never ran a further turn for the expired tap — still just the initial turn plus req-1's own resume"

                    Expect.equal
                        (List.length (server.RequestsFor "editMessageText"))
                        1
                        "the expired tap never touches the message again — no outcome/failure edit for a decision that was refused as stale"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "two successive StartRun turns for one chat leave exactly ONE durable record — overwrite-per-turn, not one row per turn"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9303L
                    let bindingStore = InMemoryBindingStore() :> IBindingStore
                    let sessionStore = CountingSessionStore()

                    let agent = ScriptedAgent [ RepliesWith "first"; RepliesWith "second" ]
                    use! bridge = Maf.startPolling (pollingConfig server bindingStore (sessionStore :> ISessionStore)) agent

                    do! bridge.StartRun(UMX.tag<chatId> chat, "hi")
                    do! pollUntil 15000 (fun () -> (server.RequestsFor "sendMessage").Length >= 1)

                    do! bridge.StartRun(UMX.tag<chatId> chat, "hi again")
                    do! pollUntil 15000 (fun () -> (server.RequestsFor "sendMessage").Length >= 2)

                    Expect.equal sessionStore.Count 1 "two turns for the SAME chat leave exactly one durable record, not one per turn"

                    let! record = (sessionStore :> ISessionStore).TryGet(UMX.tag<chatId> chat, CancellationToken.None)
                    Expect.isTrue record.IsSome "the chat's single record still resolves after both turns"
                }
                |> Async.AwaitTask
        }
    ]
