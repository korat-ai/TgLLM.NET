/// Additional restart-simulation coverage for the durable session, filling in shapes
/// `MafDurableResumeTests.fs`/`MafDurableLifecycleTests.fs`/`MafDurableReliabilityTests.fs` don't
/// themselves drive: that a resumed turn actually runs on the RESTORED session object (not merely a
/// same-shaped fresh one), that two approvals raised together in one turn are decided independently
/// across their own separate restarts, that `HandleIncomingMessage` — not just a tap or a
/// pre-restart `StartRun` — can be the very first post-restart touch that restores a chat, that an
/// idle record removed by `ISessionStore.EvictIdle` behaves exactly like a never-persisted one on
/// the next tap, and that a host-initiated run and a decision tap racing for the SAME chat right
/// after a restart never restore or resume more than once. Mirrors those files' own shared shape
/// throughout: a shared `InMemoryBindingStore` + `InMemorySessionStore` across a dispose/recreate —
/// nothing else carries over between a chat's "before" and "after" bridge.
module TgLLM.Integration.Tests.MafDurableCoverageTests

open System
open System.Threading
open System.Threading.Tasks
open System.Text.Json.Nodes
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

/// Delivers ONE incoming plain-text message — the `HandleIncomingMessage` path, distinct from a
/// tap or a host-initiated `StartRun`.
let private deliverText (server: FakeBotApiServer) (updateId: int) (chat: int64) (messageId: int) (userId: int64) (text: string) : Task =
    server.EnqueueResult("getUpdates", TelegramJson.batch [ TelegramJson.textMessageUpdate updateId chat messageId userId "Tester" text ])
    Task.CompletedTask

/// Same shared-stores restart shape as `MafDurableResumeTests.fs`'s own `pollingConfig`.
let private pollingConfig (server: FakeBotApiServer) (bindingStore: IBindingStore) (sessionStore: ISessionStore) : TgBotConfig =
    (TgBotConfig.create "123456789:TEST-fake-token")
        .WithBaseUrl(server.BaseUrl)
        .WithTools(ToolRegistry.create ())
        .WithBindingStore(bindingStore)
        .WithSessionStore(sessionStore)

/// Records `OnStaleDecision`/`OnResumeFailed` — the two signals a tap with nothing durably
/// resumable behind it, or a resume that itself throws, actually produces.
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

    interface IMafSessionObserver with
        member _.OnSessionRestoreFailed(_chat, _failure) = ()
        member _.OnSessionPersistFailed(_chat, _error) = ()

[<Tests>]
let mafDurableCoverageTests =
    testList "MafBridge durable-session additional restart coverage" [

        testCaseAsync "a post-restart resume runs on the RESTORED session object itself, not a fresh same-shaped stand-in"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9600L
                    let bindingStore = InMemoryBindingStore() :> IBindingStore
                    let sessionStore = InMemorySessionStore() :> ISessionStore

                    let agent1 = ScriptedAgent [ PausesFor("req-1", "send_email", [ "toAddr", box "alice@example.com" ]) ]
                    let! bridge1 = Maf.startPolling (pollingConfig server bindingStore sessionStore) agent1
                    do! bridge1.StartRun(UMX.tag<chatId> chat, "Email alice that the deploy is done.")

                    Expect.equal agent1.SeenSessionNonces.Count 1 "the pre-restart turn ran on exactly one live session"
                    let preRestartNonce = agent1.SeenSessionNonces[0]

                    let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                    let approveToken = callbackDataAt 0 0 sent

                    do! (bridge1 :> IAsyncDisposable).DisposeAsync().AsTask()

                    let agent2 = ScriptedAgent [ RepliesWith "Email sent to alice@example.com." ]
                    use! bridge2 = Maf.startPolling (pollingConfig server bindingStore sessionStore) agent2

                    // Sanity-check this test's own discriminating power BEFORE relying on it: a
                    // freshly minted session's own nonce (`ScriptedSession`'s default — a new Guid
                    // per instance) must differ from the pre-restart one. If it didn't, the equality
                    // assertion below would pass even for a bug that resumed on a brand-new session
                    // instead of the restored one — this proves that can't happen here.
                    let! freshSession = agent2.CreateSessionAsync()
                    let freshNonce = (freshSession :?> ScriptedSession).Nonce
                    Expect.notEqual freshNonce preRestartNonce "a freshly created session's own nonce differs from the pre-restart one, so the assertion below actually discriminates"

                    do! deliverTap server 1 "q-continuity-approve" approveToken chat 1 chat
                    do! pollUntil 15000 (fun () -> server.RequestsFor "editMessageText" |> List.isEmpty |> not)

                    Expect.equal agent2.SeenSessionNonces.Count 1 "the post-restart resume ran on exactly one live session"

                    Expect.equal
                        agent2.SeenSessionNonces[0]
                        preRestartNonce
                        "the resumed turn ran on the SAME session the pre-restart turn started on — the restored live session, never a fresh stand-in"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "two approvals raised together in ONE turn are independently decidable across their OWN separate restarts — deciding one never resurrects or consumes the other"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9601L
                    let bindingStore = InMemoryBindingStore() :> IBindingStore
                    let sessionStore = InMemorySessionStore() :> ISessionStore

                    let agent1 = ScriptedAgent [ PausesForMany [ ("req-a", "send_email", []); ("req-b", "send_sms", []) ] ]
                    let! bridge1 = Maf.startPolling (pollingConfig server bindingStore sessionStore) agent1
                    do! bridge1.StartRun(UMX.tag<chatId> chat, "Notify the team both ways.")

                    Expect.equal (List.length (server.RequestsFor "sendMessage")) 2 "both approvals from the initial turn each get their own message"
                    let initialSends = server.RequestsFor "sendMessage"
                    let sendA = initialSends.[0].Body |> Option.get
                    let sendB = initialSends.[1].Body |> Option.get
                    let approveTokenA = callbackDataAt 0 0 sendA
                    let approveTokenB = callbackDataAt 0 0 sendB

                    do! (bridge1 :> IAsyncDisposable).DisposeAsync().AsTask()

                    // --- FIRST restart: decide req-a only. ---
                    let resumesA = ResizeArray<string * bool>()
                    let agent2 = ScriptedAgent([ RepliesWith "req-a done" ], onResume = (fun (r, a) -> resumesA.Add(r, a)))
                    let! bridge2 = Maf.startPolling (pollingConfig server bindingStore sessionStore) agent2

                    do! deliverTap server 1 "q-multi-a" approveTokenA chat 1 chat
                    do! pollUntil 15000 (fun () -> server.RequestsFor "editMessageText" |> List.isEmpty |> not)

                    Expect.equal resumesA.Count 1 "req-a resumed exactly once"
                    Expect.equal resumesA[0] ("req-a", true) "resumed with req-a's own persisted request id"

                    let! recordAfterA = sessionStore.TryGet(UMX.tag<chatId> chat, CancellationToken.None)

                    match recordAfterA with
                    | ValueNone -> failtest "expected a persisted durable record for this chat after deciding req-a"
                    | ValueSome stored ->
                        match SessionEnvelope.decodeAndValidate SessionEnvelope.currentMafVersion stored.Payload with
                        | Error err -> failwithf "expected a well-formed persisted envelope, got %A" err
                        | Ok env ->
                            let requestIds = env.Approvals |> Array.map (fun a -> a.RequestId) |> Array.toList
                            Expect.isFalse (List.contains "req-a" requestIds) "req-a was consumed — no longer carried in the re-persisted record"
                            Expect.contains requestIds "req-b" "req-b is STILL carried in the re-persisted record — untouched by req-a's own decision"

                    do! (bridge2 :> IAsyncDisposable).DisposeAsync().AsTask()

                    // --- SECOND restart, over the SAME two stores: decide req-b. ---
                    let resumesB = ResizeArray<string * bool>()
                    let agent3 = ScriptedAgent([ RepliesWith "req-b done" ], onResume = (fun (r, a) -> resumesB.Add(r, a)))
                    use! bridge3 = Maf.startPolling (pollingConfig server bindingStore sessionStore) agent3

                    do! deliverTap server 2 "q-multi-b" approveTokenB chat 2 chat
                    do! pollUntil 15000 (fun () -> server.RequestsFor "editMessageText" |> List.length >= 2)

                    Expect.equal resumesB.Count 1 "req-b resumed exactly once, on its own later restart"

                    Expect.equal
                        resumesB[0]
                        ("req-b", true)
                        "resumed with req-b's own persisted request id — never confused with req-a's, already decided a restart ago"

                    Expect.equal resumesA.Count 1 "req-a's own resume count is untouched by req-b's later, separate decision"

                    let secondEdit = (server.RequestsFor "editMessageText").[1].Body |> Option.get
                    Expect.equal (secondEdit |> field "message_id" |> fun n -> n.AsValue().GetValue<int64>()) 2L "req-b's own edit targets ITS OWN message, not req-a's"
                    Expect.stringContains (secondEdit |> field "text" |> asString) "req-b done" "the second restart's own agent reply concludes req-b's decision"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "an incoming TEXT message is the first post-restart touch: it restores the session AND rehydrates the pending approval, which then resumes normally on a later tap"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9602L
                    let bindingStore = InMemoryBindingStore() :> IBindingStore
                    let sessionStore = InMemorySessionStore() :> ISessionStore

                    let agent1 = ScriptedAgent [ PausesFor("req-1", "send_email", [ "toAddr", box "alice@example.com" ]) ]
                    let! bridge1 = Maf.startPolling (pollingConfig server bindingStore sessionStore) agent1
                    do! bridge1.StartRun(UMX.tag<chatId> chat, "Email alice that the deploy is done.")

                    let preRestartNonce = agent1.SeenSessionNonces[0]
                    let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                    let approveToken = callbackDataAt 0 0 sent

                    do! (bridge1 :> IAsyncDisposable).DisposeAsync().AsTask()

                    let resumes = ResizeArray<string * bool>()

                    let agent2 =
                        ScriptedAgent(
                            [ RepliesWith "Still here."; RepliesWith "Email sent to alice@example.com." ],
                            onResume = (fun (r, a) -> resumes.Add(r, a))
                        )

                    use! bridge2 = Maf.startPolling (pollingConfig server bindingStore sessionStore) agent2

                    // The FIRST post-restart touch for this chat is an incoming TEXT message — never
                    // a tap, never a host-initiated StartRun — so this exercises
                    // `HandleIncomingMessage`'s own restore call.
                    do! deliverText server 1 chat 5 chat "Still there"
                    do! pollUntil 15000 (fun () -> (server.RequestsFor "sendMessage").Length >= 2)

                    Expect.equal agent2.SeenSessionNonces.Count 1 "the text turn ran on exactly one live session"

                    Expect.equal
                        agent2.SeenSessionNonces[0]
                        preRestartNonce
                        "the incoming-text restore path restored the SAME pre-restart session"

                    let textReply = (server.RequestsFor "sendMessage") |> List.last |> fun r -> r.Body |> Option.get
                    Expect.equal (textReply |> field "text" |> asString) "Still here." "the text turn's own reply reached the wire, on the restored session"

                    // The pending approval was rehydrated as a SIDE EFFECT of the text turn's own
                    // restore — tapping it now must resume normally, proving the text-restore path
                    // rehydrated the pending approval, not just the session.
                    do! deliverTap server 2 "q-text-restore-approve" approveToken chat 1 chat
                    do! pollUntil 15000 (fun () -> server.RequestsFor "editMessageText" |> List.isEmpty |> not)

                    Expect.equal resumes.Count 1 "the rehydrated approval resumed exactly once"
                    Expect.equal resumes[0] ("req-1", true) "resumed with the persisted request id and Approved = true"

                    Expect.equal agent2.SeenSessionNonces.Count 2 "the resume ran a second turn, still on a live session"

                    Expect.equal
                        agent2.SeenSessionNonces[1]
                        preRestartNonce
                        "the resume still runs on the SAME restored session — the text turn's own restore, never a second, different one"

                    let editBody = (server.RequestsFor "editMessageText").Head.Body |> Option.get
                    Expect.stringContains (editBody |> field "text" |> asString) "approved" "the outcome says it was approved"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "an idle record evicted directly on the store behaves exactly like one that was never persisted — the post-restart tap lands stale, never resumed"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9603L
                    let bindingStore = InMemoryBindingStore() :> IBindingStore
                    let sessionStore = InMemorySessionStore() :> ISessionStore
                    let mutable now = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                    let clock: Clock = fun () -> now

                    let agent1 = ScriptedAgent [ PausesFor("req-1", "send_email", []) ]
                    let config1 = (pollingConfig server bindingStore sessionStore).WithClock clock
                    let! bridge1 = Maf.startPolling config1 agent1
                    do! bridge1.StartRun(UMX.tag<chatId> chat, "Email alice.")

                    let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                    let approveToken = callbackDataAt 0 0 sent

                    let! beforeEviction = sessionStore.TryGet(UMX.tag<chatId> chat, CancellationToken.None)

                    match beforeEviction with
                    | ValueSome record -> Expect.equal record.LastActivityAt now "the record's own LastActivityAt is stamped from the SAME clock this test controls"
                    | ValueNone -> failtest "expected a persisted durable record for this chat before eviction"

                    do! (bridge1 :> IAsyncDisposable).DisposeAsync().AsTask()

                    // Simulate the idle-session sweeper having already run, directly on the shared
                    // store — deterministic, independent of `SessionEvictionSweeper`'s own
                    // interval/timing (already unit-tested on its own).
                    now <- now.AddHours 2.0
                    let! removed = sessionStore.EvictIdle now
                    Expect.equal removed 1 "the idle record for this chat was evicted"

                    let! afterEviction = sessionStore.TryGet(UMX.tag<chatId> chat, CancellationToken.None)
                    Expect.equal afterEviction ValueNone "the evicted record is gone from the store"

                    // "Restart", over the now-evicted store.
                    let observer = RecordingObserver()
                    let options: MafBridgeOptions = { MafBridgeOptions.defaults with Observer = ValueSome(observer :> IMafObserver) }
                    let resumes = ResizeArray<string * bool>()
                    let agent2 = ScriptedAgent([ RepliesWith "unexpected" ], onResume = (fun (r, a) -> resumes.Add(r, a)))
                    let config2 = (pollingConfig server bindingStore sessionStore).WithClock clock
                    use! bridge2 = Maf.startPollingWith options config2 agent2

                    do! deliverTap server 1 "q-evicted-stale" approveToken chat 1 chat
                    do! pollUntil 15000 (fun () -> not (List.isEmpty observer.StaleDecisions))

                    Expect.equal (List.length observer.StaleDecisions) 1 "the tap lands on the stale path — nothing durable survived the eviction"
                    Expect.equal observer.StaleDecisions[0].RequestId "req-1" "the surfaced descriptor names the evicted request"
                    Expect.equal resumes.Count 0 "the evicted approval is never resumed"
                    Expect.equal agent2.RunCount 0 "the post-restart agent never ran a turn for a tap with nothing durable behind it"
                    Expect.isEmpty (server.RequestsFor "editMessageText") "the stale tap never edits the message"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "post-restart, a host StartRun and the decision tap for the SAME chat fired without awaiting the first: the restore happens exactly once, and the tapped approval resumes at most once"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9604L
                    let bindingStore = InMemoryBindingStore() :> IBindingStore
                    let sessionStore = InMemorySessionStore() :> ISessionStore

                    let agent1 = ScriptedAgent [ PausesFor("req-1", "send_email", []) ]
                    let! bridge1 = Maf.startPolling (pollingConfig server bindingStore sessionStore) agent1
                    do! bridge1.StartRun(UMX.tag<chatId> chat, "Email alice.")

                    let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                    let approveToken = callbackDataAt 0 0 sent

                    do! (bridge1 :> IAsyncDisposable).DisposeAsync().AsTask()

                    let resumes = ResizeArray<string * bool>()

                    let agent2 =
                        ScriptedAgent(
                            [ RepliesWith "concurrent hello"; RepliesWith "req-1 resumed" ],
                            onResume = (fun (r, a) -> resumes.Add(r, a))
                        )

                    use! bridge2 = Maf.startPolling (pollingConfig server bindingStore sessionStore) agent2

                    // Fire a host-initiated StartRun and the decision tap for the SAME chat WITHOUT
                    // awaiting the first — both contend for this chat's own restore/session, on
                    // `MafBridge`'s own per-chat lock.
                    let startRunTask = bridge2.StartRun(UMX.tag<chatId> chat, "Anything new?")
                    do! deliverTap server 1 "q-concurrent-tap" approveToken chat 1 chat

                    do!
                        pollUntil 15000 (fun () ->
                            resumes.Count >= 1 && (server.RequestsFor "sendMessage" |> List.length) >= 2)

                    do! startRunTask

                    Expect.equal agent2.RunCount 2 "exactly two turns ran — the host-initiated run and the tap's own resume, neither more nor less"
                    Expect.equal resumes.Count 1 "the tapped approval resumed exactly once, despite racing a concurrent StartRun for the same chat"
                    Expect.equal resumes[0] ("req-1", true) "resumed with the persisted request id and Approved = true"

                    let distinctNonces = agent2.SeenSessionNonces |> Seq.distinct |> Seq.toList
                    Expect.equal agent2.SeenSessionNonces.Count 2 "both turns actually ran, each against a live session"

                    Expect.equal
                        (List.length distinctNonces)
                        1
                        "both turns ran on the SAME restored session — the chat's own lock meant the restore happened exactly once, never raced into two different sessions"

                    Expect.equal
                        (List.length (server.RequestsFor "sendMessage"))
                        2
                        "the pre-restart send plus the concurrent StartRun's own new send — no extra, duplicated send from a double-restore"
                }
                |> Async.AwaitTask
        }
    ]
