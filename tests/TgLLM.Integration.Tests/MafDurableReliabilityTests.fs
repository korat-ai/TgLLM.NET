/// Adversarial reliability acceptance for the durable-session observability seam
/// (`IMafSessionObserver`, `Bridge.fs`): every way `restoreOrCreate`/`persistConversation` can fail —
/// a corrupt record, an incompatible format/framework-version marker, a store that throws on read or
/// write, and a corrupt session payload inside an otherwise well-formed envelope — must reach
/// `OnSessionRestoreFailed`/`OnSessionPersistFailed` rather than crashing the bridge or silently
/// resurrecting garbage. Mirrors `MafDurableResumeTests.fs`'s own restart-simulation shape throughout
/// (a shared `InMemoryBindingStore` + `ISessionStore` across a dispose/recreate — nothing else
/// carries over). A sibling `testList` at the bottom (T026) checks the observer vocabulary itself
/// stays fully exercised and that the three-tier `sessionObserver` resolution `Bridge.fs` applies
/// (custom observer, then the bot's own logger, then noop) behaves as documented.
module TgLLM.Integration.Tests.MafDurableReliabilityTests

open System
open System.Reflection
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
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

/// Directly overwrites `chat`'s own durable record with arbitrary bytes — bypassing
/// `persistConversation` entirely, the same way a hand-edited record or a foreign write would reach
/// the store in practice.
let private overwriteRecord (store: ISessionStore) (chat: int64) (payload: byte[]) : Task =
    task { do! store.Save(UMX.tag<chatId> chat, { Payload = payload; LastActivityAt = DateTimeOffset.UtcNow }, CancellationToken.None) }
    :> Task

/// Records EVERY `IMafObserver`/`IMafSessionObserver` call in one object — both interfaces on the
/// SAME instance, so `MafBridgeOptions.Observer = ValueSome(this :> IMafObserver)` makes
/// `Bridge.fs`'s own `sessionObserver` resolution pick it up as the durable-session channel too
/// (the `:? IMafSessionObserver as so` branch of its three-tier match).
type private CapturingObserver() =
    let stale = ResizeArray<ApprovalDescriptor>()
    let malformed = ResizeArray<string>()
    let resumeFailed = ResizeArray<ApprovalDescriptor * exn>()
    let emptyTurn = ResizeArray<ChatId>()
    let invalidOutput = ResizeArray<ChatId * MafError>()
    let projectionProblem = ResizeArray<ProjectionProblem>()
    let turnFailed = ResizeArray<ChatId * exn>()
    let restoreFailed = ResizeArray<ChatId * SessionFailure>()
    let persistFailed = ResizeArray<ChatId * exn>()

    member _.Stale: ApprovalDescriptor list = List.ofSeq stale
    member _.RestoreFailed: (ChatId * SessionFailure) list = List.ofSeq restoreFailed
    member _.PersistFailed: (ChatId * exn) list = List.ofSeq persistFailed
    member _.TurnFailed: (ChatId * exn) list = List.ofSeq turnFailed

    interface IMafObserver with
        member _.OnStaleDecision(descriptor) = stale.Add descriptor
        member _.OnMalformedDecision(raw) = malformed.Add raw
        member _.OnResumeFailed(descriptor, error) = resumeFailed.Add(descriptor, error)
        member _.OnEmptyTurn(chat) = emptyTurn.Add chat
        member _.OnInvalidOutput(chat, error) = invalidOutput.Add(chat, error)
        member _.OnProjectionProblem(problem) = projectionProblem.Add problem
        member _.OnTurnFailed(chat, error) = turnFailed.Add(chat, error)

    interface IMafSessionObserver with
        member _.OnSessionRestoreFailed(chat, failure) = restoreFailed.Add(chat, failure)
        member _.OnSessionPersistFailed(chat, error) = persistFailed.Add(chat, error)

/// A host-supplied `IMafObserver` that deliberately does NOT also implement `IMafSessionObserver` —
/// the shape `Bridge.fs`'s own three-tier `sessionObserver` resolution falls through past, to decide
/// between the bot's own logger and a noop.
type private ObserverOnly() =
    interface IMafObserver with
        member _.OnStaleDecision(_descriptor) = ()
        member _.OnMalformedDecision(_raw) = ()
        member _.OnResumeFailed(_descriptor, _error) = ()
        member _.OnEmptyTurn(_chat) = ()
        member _.OnInvalidOutput(_chat, _error) = ()
        member _.OnProjectionProblem(_problem) = ()
        member _.OnTurnFailed(_chat, _error) = ()

type private NoopScope() =
    interface IDisposable with
        member _.Dispose() : unit = ()

/// A minimal `ILogger` fake that records `LogWarning` calls — mirrors
/// `MafCSharpSurfaceObservabilityTests.fs`'s own `RecordingLogger`.
type private RecordingLogger() =
    let warnings = ResizeArray<string>()
    member _.Warnings: string list = List.ofSeq warnings

    interface ILogger with
        member _.BeginScope<'TState when 'TState: not null>(_state: 'TState) : IDisposable = new NoopScope()
        member _.IsEnabled(_logLevel: LogLevel) : bool = true

        member _.Log<'TState>
            (
                logLevel: LogLevel,
                _eventId: EventId,
                state: 'TState,
                error: exn | null,
                formatter: Func<'TState, exn | null, string>
            ) : unit =
            match logLevel with
            | LogLevel.Warning -> warnings.Add(formatter.Invoke(state, error))
            | _ -> ()

/// A `TryGet`-only failure: `Save`/`Remove`/`EvictIdle` all forward to `inner`, so a chat's record
/// written by an earlier, undecorated bridge instance is still fully readable/writable by everything
/// EXCEPT the restore path this decorator targets.
type private ThrowingTryGetStore(inner: ISessionStore) =
    interface ISessionStore with
        member _.Save(chat, record, ct) = inner.Save(chat, record, ct)
        member _.TryGet(_chat: ChatId, _ct: CancellationToken) : ValueTask<SessionRecord voption> =
            raise (InvalidOperationException "the durable session store is unreachable for reads")
        member _.Remove(chat, ct) = inner.Remove(chat, ct)
        member _.EvictIdle(olderThan) = inner.EvictIdle(olderThan)

/// A `Save`-only failure: `TryGet`/`Remove`/`EvictIdle` all forward to `inner`.
type private ThrowingSaveStore(inner: ISessionStore) =
    interface ISessionStore with
        member _.Save(_chat: ChatId, _record: SessionRecord, _ct: CancellationToken) : ValueTask =
            raise (InvalidOperationException "the durable session store is unreachable for writes")
        member _.TryGet(chat, ct) = inner.TryGet(chat, ct)
        member _.Remove(chat, ct) = inner.Remove(chat, ct)
        member _.EvictIdle(olderThan) = inner.EvictIdle(olderThan)

type private FailOnSaveStore(inner: ISessionStore, failOnCall: int) =
    let mutable calls = 0

    interface ISessionStore with
        member _.Save(chat, record, ct) =
            let call = Interlocked.Increment(&calls)

            if call = failOnCall then
                ValueTask.FromException(InvalidOperationException "the durable claim write failed")
            else
                inner.Save(chat, record, ct)

        member _.TryGet(chat, ct) = inner.TryGet(chat, ct)
        member _.Remove(chat, ct) = inner.Remove(chat, ct)
        member _.EvictIdle(olderThan) = inner.EvictIdle(olderThan)

/// A `Remove`-only failure: `TryGet`/`Save`/`EvictIdle` all forward to `inner` — the shape a
/// read-only filesystem gives `FileSessionStore.Remove` (which itself rewrites the file), reachable
/// from `restoreOrCreate`'s own two remove-on-failure gates.
type private ThrowingRemoveStore(inner: ISessionStore) =
    interface ISessionStore with
        member _.Save(chat, record, ct) = inner.Save(chat, record, ct)
        member _.TryGet(chat, ct) = inner.TryGet(chat, ct)
        member _.Remove(_chat: ChatId, _ct: CancellationToken) : ValueTask =
            raise (InvalidOperationException "the durable session store is unreachable for removal")
        member _.EvictIdle(olderThan) = inner.EvictIdle(olderThan)

/// A TRANSIENT `TryGet` blip: the FIRST call throws, every call after that forwards to `inner` —
/// unlike `ThrowingTryGetStore` (which fails forever), this models a read that recovers on retry
/// `Save`/`Remove`/`EvictIdle` always forward to `inner`.
type private FirstCallThrowsTryGetStore(inner: ISessionStore) =
    let mutable calls = 0
    interface ISessionStore with
        member _.Save(chat, record, ct) = inner.Save(chat, record, ct)

        member _.TryGet(chat: ChatId, ct: CancellationToken) : ValueTask<SessionRecord voption> =
            calls <- calls + 1

            if calls = 1 then
                raise (InvalidOperationException "the durable session store is transiently unreachable for reads")
            else
                inner.TryGet(chat, ct)

        member _.Remove(chat, ct) = inner.Remove(chat, ct)
        member _.EvictIdle(olderThan) = inner.EvictIdle(olderThan)

/// Drives ONE "raise req-1 through bridge1, corrupt/break what bridge2 reads, restart, tap Approve"
/// cycle: `corrupt` mutates `sessionStore1`'s own record (or is a no-op, when the failure instead
/// comes from `sessionStore2` itself being a throwing decorator); `sessionStore2` is whatever store
/// bridge2 is actually given — the SAME instance for a corrupted-bytes scenario, a decorator wrapping
/// it for a store-unavailable scenario. Every T025 restore-failure test below shares this exact shape.
let private runRestoreFailureCycle
    (server: FakeBotApiServer)
    (chat: int64)
    (bindingStore: IBindingStore)
    (sessionStore1: ISessionStore)
    (corrupt: ISessionStore -> Task)
    (sessionStore2: ISessionStore)
    (tapLabel: string)
    : Task<CapturingObserver * ResizeArray<string * bool> * ScriptedAgent> =
    task {
        let agent1 = ScriptedAgent [ PausesFor("req-1", "send_email", [ "toAddr", box "alice@example.com" ]) ]
        let! bridge1 = Maf.startPolling (pollingConfig server bindingStore sessionStore1) agent1
        do! bridge1.StartRun(UMX.tag<chatId> chat, "Email alice that the deploy is done.")

        let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
        let approveToken = callbackDataAt 0 0 sent

        do! (bridge1 :> IAsyncDisposable).DisposeAsync().AsTask()
        do! corrupt sessionStore1

        let observer = CapturingObserver()
        let options: MafBridgeOptions = { MafBridgeOptions.defaults with Observer = ValueSome(observer :> IMafObserver) }
        let resumes = ResizeArray<string * bool>()
        let agent2 = ScriptedAgent([ RepliesWith "unexpected" ], onResume = (fun (r, a) -> resumes.Add(r, a)))
        use! bridge2 = Maf.startPollingWith options (pollingConfig server bindingStore sessionStore2) agent2

        do! deliverTap server 1 tapLabel approveToken chat 1 chat
        do! pollUntil 15000 (fun () -> not (List.isEmpty observer.RestoreFailed))
        do! pollUntil 15000 (fun () -> not (List.isEmpty observer.Stale))

        return observer, resumes, agent2
    }

/// The finding-1 regression shape: same "raise req-1, corrupt, restart, tap" cycle as
/// `runRestoreFailureCycle`, but ALSO drives a SECOND `StartRun` turn for the SAME chat, on the SAME
/// post-restart bridge, right after the tap — the whole point of pairing `SessionEnvelope.validate`'s
/// own approvals gate with `restoreOrCreate`'s all-or-nothing rehydration: a record shape that would
/// otherwise throw PAST both of `restoreOrCreate`'s remove-on-failure gates must still end up
/// removed, so a turn AFTER the tap — not just the tap itself — succeeds. Before either fix, the
/// second turn re-faults with `OnTurnFailed` the exact same way, forever, because the poisoned
/// record was never removed. `bridge2` is deliberately NOT `use!`-bound here (unlike
/// `runRestoreFailureCycle`) — this helper needs it to stay open past the tap for the second turn,
/// disposing it explicitly once both turns are done.
let private runPoisonedRecordCycle
    (server: FakeBotApiServer)
    (chat: int64)
    (bindingStore: IBindingStore)
    (sessionStore: ISessionStore)
    (poison: ConversationEnvelopeDto)
    (tapLabel: string)
    : Task<CapturingObserver * SessionRecord voption> =
    task {
        let agent1 = ScriptedAgent [ PausesFor("req-1", "send_email", [ "toAddr", box "alice@example.com" ]) ]
        let! bridge1 = Maf.startPolling (pollingConfig server bindingStore sessionStore) agent1
        do! bridge1.StartRun(UMX.tag<chatId> chat, "Email alice that the deploy is done.")

        let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
        let approveToken = callbackDataAt 0 0 sent

        do! (bridge1 :> IAsyncDisposable).DisposeAsync().AsTask()
        do! overwriteRecord sessionStore chat (SessionEnvelope.encode poison)

        let observer = CapturingObserver()
        let options: MafBridgeOptions = { MafBridgeOptions.defaults with Observer = ValueSome(observer :> IMafObserver) }
        let agent2 = ScriptedAgent [ RepliesWith "unexpected"; RepliesWith "second turn ok" ]
        let! bridge2 = Maf.startPollingWith options (pollingConfig server bindingStore sessionStore) agent2

        do! deliverTap server 1 tapLabel approveToken chat 1 chat
        do! pollUntil 15000 (fun () -> not (List.isEmpty observer.RestoreFailed))
        do! pollUntil 15000 (fun () -> not (List.isEmpty observer.Stale))

        // Snapshotted BETWEEN the tap and the second turn below — the second turn's own end-of-turn
        // persist writes a FRESH, valid record back for this chat regardless, which would otherwise
        // mask whether the tap itself actually removed the poisoned one.
        let! afterTap = sessionStore.TryGet(UMX.tag<chatId> chat, CancellationToken.None)

        // The key regression: the poisoned record must have been REMOVED by the tap above — a
        // SECOND turn for this chat, on the SAME bridge, must succeed rather than re-fault the exact
        // same way (`OnTurnFailed`) every time, forever, off a record that never gets cleaned up.
        do! bridge2.StartRun(UMX.tag<chatId> chat, "Still there?")
        do! pollUntil 15000 (fun () -> (server.RequestsFor "sendMessage").Length >= 2)

        do! (bridge2 :> IAsyncDisposable).DisposeAsync().AsTask()

        return observer, afterTap
    }

[<Tests>]
let mafDurablePoisonedRecordRecoveryTests =
    testList "MafBridge durable-session poisoned-record recovery (a poisoned record must never brick a chat)" [

        testCaseAsync "Approvals: null — the tap lands stale on removal, and a SECOND turn for this chat still succeeds"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9807L
                    let bindingStore = InMemoryBindingStore() :> IBindingStore
                    let sessionStore = InMemorySessionStore() :> ISessionStore

                    let poison: ConversationEnvelopeDto =
                        { Format = SessionEnvelope.CurrentFormat
                          MafVersion = SessionEnvelope.currentMafVersion
                          MeaiVersion = SessionEnvelope.currentMeaiVersion
                          SessionJson = "{}"
                          Approvals = Unchecked.defaultof<PersistedApprovalDto[]> }

                    let! observer, afterTap = runPoisonedRecordCycle server chat bindingStore sessionStore poison "q-poison-null-approvals"

                    match observer.RestoreFailed with
                    | [ (surfacedChat, CorruptRecord _) ] -> Expect.equal surfacedChat (UMX.tag<chatId> chat) "surfaced for the right chat"
                    | other -> failwithf "expected exactly one CorruptRecord restore failure, got %A" other

                    Expect.equal (List.length observer.Stale) 1 "the tap lands on the stale path — a null Approvals array is never resumed"

                    Expect.equal
                        afterTap
                        ValueNone
                        "the poisoned record is removed BY THE TAP ITSELF — a null Approvals array never bricks the chat (checked before the second turn's own re-persist would otherwise mask this)"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "an approval with a null RequestId — the tap lands stale on removal, and a SECOND turn for this chat still succeeds"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9808L
                    let bindingStore = InMemoryBindingStore() :> IBindingStore
                    let sessionStore = InMemorySessionStore() :> ISessionStore

                    let poisonedApproval: PersistedApprovalDto =
                        { RequestId = Unchecked.defaultof<string>
                          CallId = "call-1"
                          Tool = "send_email"
                          ArgumentsJson = null
                          OwnerUserId = Nullable()
                          MessageId = 1L
                          ExpiresAt = Nullable() }

                    let poison: ConversationEnvelopeDto =
                        { Format = SessionEnvelope.CurrentFormat
                          MafVersion = SessionEnvelope.currentMafVersion
                          MeaiVersion = SessionEnvelope.currentMeaiVersion
                          SessionJson = "{}"
                          Approvals = [| poisonedApproval |] }

                    let! observer, afterTap = runPoisonedRecordCycle server chat bindingStore sessionStore poison "q-poison-null-requestid"

                    match observer.RestoreFailed with
                    | [ (surfacedChat, CorruptRecord _) ] -> Expect.equal surfacedChat (UMX.tag<chatId> chat) "surfaced for the right chat"
                    | other -> failwithf "expected exactly one CorruptRecord restore failure, got %A" other

                    Expect.equal (List.length observer.Stale) 1 "the tap lands on the stale path — a null-RequestId approval is never resumed"

                    Expect.equal
                        afterTap
                        ValueNone
                        "the poisoned record is removed BY THE TAP ITSELF — a null-RequestId approval never bricks the chat (checked before the second turn's own re-persist would otherwise mask this)"
                }
                |> Async.AwaitTask
        }

        testCaseAsync
            "ArgumentsJson holding valid-but-non-object JSON — passes validate, throws inside rehydration, still lands stale on removal, and a SECOND turn for this chat still succeeds"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9809L
                    let bindingStore = InMemoryBindingStore() :> IBindingStore
                    let sessionStore = InMemorySessionStore() :> ISessionStore

                    let poisonedApproval: PersistedApprovalDto =
                        { RequestId = "req-1"
                          CallId = "call-1"
                          Tool = "send_email"
                          ArgumentsJson = "[1,2,3]"
                          OwnerUserId = Nullable()
                          MessageId = 1L
                          ExpiresAt = Nullable() }

                    let poison: ConversationEnvelopeDto =
                        { Format = SessionEnvelope.CurrentFormat
                          MafVersion = SessionEnvelope.currentMafVersion
                          MeaiVersion = SessionEnvelope.currentMeaiVersion
                          // A scripted session payload that itself deserializes successfully — this
                          // scenario is about the APPROVAL rehydration throwing, not the session's own.
                          SessionJson = """{"nonce":"poisoned-arguments-json"}"""
                          Approvals = [| poisonedApproval |] }

                    let! observer, afterTap = runPoisonedRecordCycle server chat bindingStore sessionStore poison "q-poison-nonobject-args"

                    match observer.RestoreFailed with
                    | [ (surfacedChat, CorruptRecord _) ] -> Expect.equal surfacedChat (UMX.tag<chatId> chat) "surfaced for the right chat"
                    | other -> failwithf "expected exactly one CorruptRecord restore failure, got %A" other

                    Expect.equal (List.length observer.Stale) 1 "the tap lands on the stale path — a rehydration failure is never resumed"

                    Expect.equal
                        afterTap
                        ValueNone
                        "the poisoned record is removed BY THE TAP ITSELF — a non-object ArgumentsJson never bricks the chat, even though \
                         it passes validate (checked before the second turn's own re-persist would otherwise mask this)"
                }
                |> Async.AwaitTask
        }
    ]

[<Tests>]
let mafDurableReliabilityTests =
    testList "MafBridge durable-session reliability under an adversarial restore/persist" [

        testCaseAsync "corrupt record: garbage bytes overwrite a valid record — OnSessionRestoreFailed(CorruptRecord _), the record is removed, and the tap lands stale on a fresh session"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9801L
                    let bindingStore = InMemoryBindingStore() :> IBindingStore
                    let sessionStore = InMemorySessionStore() :> ISessionStore
                    let corrupt (store: ISessionStore) : Task = overwriteRecord store chat [| 0uy; 1uy; 2uy |]

                    let! observer, resumes, agent2 =
                        runRestoreFailureCycle server chat bindingStore sessionStore corrupt sessionStore "q-reliability-corrupt"

                    match observer.RestoreFailed with
                    | [ (surfacedChat, CorruptRecord _) ] -> Expect.equal surfacedChat (UMX.tag<chatId> chat) "surfaced for the right chat"
                    | other -> failwithf "expected exactly one CorruptRecord restore failure, got %A" other

                    Expect.equal (List.length observer.Stale) 1 "the tap lands on the stale path — a corrupt record is never resumed"
                    Expect.equal resumes.Count 0 "a corrupt record's decision is never resumed"
                    Expect.equal agent2.RunCount 0 "the post-restart agent never ran a turn for a tap with a corrupt durable record behind it"

                    let! afterFailure = sessionStore.TryGet(UMX.tag<chatId> chat, CancellationToken.None)
                    Expect.equal afterFailure ValueNone "the corrupt record is removed after the failed restore, not retried forever"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "incompatible format: a Format marker this build no longer understands — OnSessionRestoreFailed(IncompatibleFormat _), removed, tap lands stale"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9802L
                    let bindingStore = InMemoryBindingStore() :> IBindingStore
                    let sessionStore = InMemorySessionStore() :> ISessionStore

                    let corrupt (store: ISessionStore) : Task =
                        let envelope: ConversationEnvelopeDto =
                            { Format = 999
                              MafVersion = SessionEnvelope.currentMafVersion
                              MeaiVersion = SessionEnvelope.currentMeaiVersion
                              SessionJson = "{}"
                              Approvals = [||] }

                        overwriteRecord store chat (SessionEnvelope.encode envelope)

                    let! observer, resumes, agent2 =
                        runRestoreFailureCycle server chat bindingStore sessionStore corrupt sessionStore "q-reliability-format"

                    match observer.RestoreFailed with
                    | [ (surfacedChat, IncompatibleFormat(found, expected)) ] ->
                        Expect.equal surfacedChat (UMX.tag<chatId> chat) "surfaced for the right chat"
                        Expect.equal found 999 "the surfaced found-format matches the record's own marker"
                        Expect.equal expected SessionEnvelope.CurrentFormat "the surfaced expected-format matches this build's own current format"
                    | other -> failwithf "expected exactly one IncompatibleFormat restore failure, got %A" other

                    Expect.equal (List.length observer.Stale) 1 "the tap lands on the stale path — an incompatible-format record is never resumed"
                    Expect.equal resumes.Count 0 "an incompatible-format record's decision is never resumed"
                    Expect.equal agent2.RunCount 0 "the post-restart agent never ran a turn for a tap with an incompatible-format record behind it"

                    let! afterFailure = sessionStore.TryGet(UMX.tag<chatId> chat, CancellationToken.None)
                    Expect.equal afterFailure ValueNone "the incompatible-format record is removed after the failed restore"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "incompatible MAF version: a MafVersion marker whose major.minor differs from this build's own — OnSessionRestoreFailed(IncompatibleMafVersion _), removed, tap lands stale"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9803L
                    let bindingStore = InMemoryBindingStore() :> IBindingStore
                    let sessionStore = InMemorySessionStore() :> ISessionStore

                    let corrupt (store: ISessionStore) : Task =
                        let envelope: ConversationEnvelopeDto =
                            { Format = SessionEnvelope.CurrentFormat
                              MafVersion = "2.0.0.0"
                              MeaiVersion = SessionEnvelope.currentMeaiVersion
                              SessionJson = "{}"
                              Approvals = [||] }

                        overwriteRecord store chat (SessionEnvelope.encode envelope)

                    let! observer, resumes, agent2 =
                        runRestoreFailureCycle server chat bindingStore sessionStore corrupt sessionStore "q-reliability-mafversion"

                    match observer.RestoreFailed with
                    | [ (surfacedChat, IncompatibleMafVersion(found, running)) ] ->
                        Expect.equal surfacedChat (UMX.tag<chatId> chat) "surfaced for the right chat"
                        Expect.equal found "2.0.0.0" "the surfaced found-version matches the record's own marker"
                        Expect.equal running SessionEnvelope.currentMafVersion "the surfaced running-version matches this build's own MAF assembly version"
                    | other -> failwithf "expected exactly one IncompatibleMafVersion restore failure, got %A" other

                    Expect.equal (List.length observer.Stale) 1 "the tap lands on the stale path — an incompatible-version record is never resumed"
                    Expect.equal resumes.Count 0 "an incompatible-version record's decision is never resumed"
                    Expect.equal agent2.RunCount 0 "the post-restart agent never ran a turn for a tap with an incompatible-version record behind it"

                    let! afterFailure = sessionStore.TryGet(UMX.tag<chatId> chat, CancellationToken.None)
                    Expect.equal afterFailure ValueNone "the incompatible-version record is removed after the failed restore"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "store throws on load: TryGet unreachable — OnSessionRestoreFailed(StoreUnavailable _), tap lands stale on a fresh session"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9804L
                    let bindingStore = InMemoryBindingStore() :> IBindingStore
                    let sessionStore = InMemorySessionStore() :> ISessionStore
                    let sessionStore2 = ThrowingTryGetStore(sessionStore) :> ISessionStore
                    let corrupt (_store: ISessionStore) : Task = Task.CompletedTask

                    let! observer, resumes, agent2 =
                        runRestoreFailureCycle server chat bindingStore sessionStore corrupt sessionStore2 "q-reliability-load"

                    match observer.RestoreFailed with
                    | [ (surfacedChat, StoreUnavailable _) ] -> Expect.equal surfacedChat (UMX.tag<chatId> chat) "surfaced for the right chat"
                    | other -> failwithf "expected exactly one StoreUnavailable restore failure, got %A" other

                    Expect.equal (List.length observer.Stale) 1 "the tap lands on the stale path — an unreadable store is never resumed"
                    Expect.equal resumes.Count 0 "an unreadable store's decision is never resumed"
                    Expect.equal agent2.RunCount 0 "the post-restart agent never ran a turn for a tap with an unreadable durable store behind it"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "store throws on save: OnSessionPersistFailed fires, but the turn's own approval message is still delivered — the turn is otherwise unaffected"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9805L
                    let bindingStore = InMemoryBindingStore() :> IBindingStore
                    let sessionStore = ThrowingSaveStore(InMemorySessionStore() :> ISessionStore) :> ISessionStore

                    let observer = CapturingObserver()
                    let options: MafBridgeOptions = { MafBridgeOptions.defaults with Observer = ValueSome(observer :> IMafObserver) }
                    let agent = ScriptedAgent [ PausesFor("req-1", "send_email", [ "toAddr", box "alice@example.com" ]) ]
                    use! bridge = Maf.startPollingWith options (pollingConfig server bindingStore sessionStore) agent

                    do! bridge.StartRun(UMX.tag<chatId> chat, "Email alice that the deploy is done.")
                    do! pollUntil 15000 (fun () -> not (List.isEmpty observer.PersistFailed))

                    Expect.equal (List.length observer.PersistFailed) 1 "the failed durable save is surfaced exactly once"
                    let surfacedChat, _ = observer.PersistFailed[0]
                    Expect.equal surfacedChat (UMX.tag<chatId> chat) "surfaced for the right chat"

                    Expect.equal (List.length (server.RequestsFor "sendMessage")) 1 "the turn's own approval message was still delivered despite the durable save failing"
                    let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                    Expect.isNonEmpty (callbackDataAt 0 0 sent) "the delivered message still carries a real Approve keyboard button"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a decision is not resumed until its durable consumption has been saved"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9811L
                    let bindingStore = InMemoryBindingStore() :> IBindingStore
                    let innerStore = InMemorySessionStore() :> ISessionStore
                    let sessionStore = FailOnSaveStore(innerStore, 2) :> ISessionStore

                    let agent1 = ScriptedAgent [ PausesFor("req-1", "send_email", []) ]
                    let! bridge1 = Maf.startPolling (pollingConfig server bindingStore sessionStore) agent1
                    do! bridge1.StartRun(UMX.tag<chatId> chat, "Send the email.")

                    let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                    let approveToken = callbackDataAt 0 0 sent
                    let rejectToken = callbackDataAt 0 1 sent
                    do! (bridge1 :> IAsyncDisposable).DisposeAsync().AsTask()

                    let observer = CapturingObserver()
                    let options = { MafBridgeOptions.defaults with Observer = ValueSome(observer :> IMafObserver) }
                    let agent2 = ScriptedAgent [ RepliesWith "Done." ]
                    use! bridge2 = Maf.startPollingWith options (pollingConfig server bindingStore sessionStore) agent2

                    do! deliverTap server 1 "q-claim-save-fails" approveToken chat 1 chat
                    do! pollUntil 15000 (fun () -> not (List.isEmpty observer.PersistFailed))

                    Expect.equal agent2.RunCount 0 "the agent is not resumed while the consumed decision is absent from durable state"
                    Expect.isEmpty (server.RequestsFor "editMessageText") "the approval stays visible after the failed claim write"

                    do! deliverTap server 2 "q-claim-save-retry" rejectToken chat 1 chat
                    do! pollUntil 15000 (fun () -> server.RequestsFor "editMessageText" |> List.isEmpty |> not)

                    Expect.equal agent2.RunCount 1 "a later sibling tap retries the durable claim and resumes once it succeeds"
                }
                |> Async.AwaitTask
        }

        testCaseAsync
            "a pending approval whose ToolCall is NOT a FunctionCallContent: the persist SKIPS it rather than failing the whole record — the session and its FunctionCallContent siblings still persist, and OnSessionPersistFailed never fires"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9810L
                    let bindingStore = InMemoryBindingStore() :> IBindingStore
                    let sessionStore = InMemorySessionStore() :> ISessionStore

                    let observer = CapturingObserver()
                    let options: MafBridgeOptions = { MafBridgeOptions.defaults with Observer = ValueSome(observer :> IMafObserver) }

                    let agent =
                        ScriptedAgent
                            [ PausesFor("req-1", "send_email", [ "toAddr", box "alice@example.com" ])
                              PausesForNonFunctionCall("req-2", "call-2") ]

                    use! bridge = Maf.startPollingWith options (pollingConfig server bindingStore sessionStore) agent

                    // Turn 1: req-1 (a normal FunctionCallContent approval) is raised and persisted —
                    // this succeeds even BEFORE the fix, since `toPersistedDto`'s hard cast only ever
                    // sees FunctionCallContent-shaped entries so far.
                    do! bridge.StartRun(UMX.tag<chatId> chat, "Email alice that the deploy is done.")
                    do! pollUntil 15000 (fun () -> (server.RequestsFor "sendMessage").Length >= 1)

                    // Turn 2: req-2 (a NON-FunctionCallContent approval) is raised ALONGSIDE the
                    // still-pending req-1 — the end-of-turn persist this turn triggers now has to
                    // snapshot BOTH. Before the fix, `toPersistedDto`'s `:?> FunctionCallContent` cast
                    // throws on req-2, failing the WHOLE persist (session + req-1 included) and firing
                    // `OnSessionPersistFailed`. After the fix, req-2 is silently skipped and the
                    // record — session + req-1 — still persists cleanly.
                    do! bridge.StartRun(UMX.tag<chatId> chat, "Also run the interpreter tool.")
                    do! pollUntil 15000 (fun () -> (server.RequestsFor "sendMessage").Length >= 2)

                    // Give a failing persist a moment to have reported itself, if it was going to —
                    // there is no positive event to poll for on the SUCCESS path.
                    do! Task.Delay 200

                    Expect.isEmpty observer.PersistFailed "the persist succeeds despite req-2's non-FunctionCallContent ToolCall — the whole record is never failed over one skippable approval"

                    let! record = sessionStore.TryGet(UMX.tag<chatId> chat, CancellationToken.None)

                    match record with
                    | ValueNone -> failtest "expected a persisted durable record for this chat"
                    | ValueSome stored ->
                        match SessionEnvelope.decodeAndValidate SessionEnvelope.currentMafVersion stored.Payload with
                        | Error err -> failwithf "expected a well-formed persisted envelope, got %A" err
                        | Ok env ->
                            let requestIds = env.Approvals |> Array.map (fun a -> a.RequestId) |> Array.toList
                            Expect.contains requestIds "req-1" "req-1 (a FunctionCallContent sibling) is still written to the persisted record"
                            Expect.isFalse (List.contains "req-2" requestIds) "req-2 (non-FunctionCallContent) is skipped, not written — it degrades to stale post-restart rather than corrupting the whole record"
                }
                |> Async.AwaitTask
        }

        testCaseAsync
            "store throws on Remove during a corrupt-record restore: the guarded removal reports OnSessionPersistFailed, but the turn still succeeds on a fresh session rather than dying misclassified as OnTurnFailed"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9812L
                    let bindingStore = InMemoryBindingStore() :> IBindingStore
                    let sessionStore = InMemorySessionStore() :> ISessionStore

                    let agent1 = ScriptedAgent [ PausesFor("req-1", "send_email", []) ]
                    let! bridge1 = Maf.startPolling (pollingConfig server bindingStore sessionStore) agent1
                    do! bridge1.StartRun(UMX.tag<chatId> chat, "Email alice.")
                    do! (bridge1 :> IAsyncDisposable).DisposeAsync().AsTask()

                    do! overwriteRecord sessionStore chat [| 0uy; 1uy; 2uy |]

                    let observer = CapturingObserver()
                    let options: MafBridgeOptions = { MafBridgeOptions.defaults with Observer = ValueSome(observer :> IMafObserver) }
                    let sessionStore2 = ThrowingRemoveStore(sessionStore) :> ISessionStore
                    let agent2 = ScriptedAgent [ RepliesWith "still here" ]
                    use! bridge2 = Maf.startPollingWith options (pollingConfig server bindingStore sessionStore2) agent2

                    // A PLAIN StartRun — not a decision tap — is the shape that exposes this defect
                    // most directly: `HandleDecision`'s own pre-peek block already wraps its
                    // `conversations.GetOrCreate` call in a try/with (reporting `StoreUnavailable` on
                    // ANY factory fault), but `StartRun`'s own outer try/with has no such special
                    // case — an unguarded `store.Remove` throw deep inside `restoreOrCreate` reaches
                    // it as an ordinary fault and is reported as `OnTurnFailed`, with NO reply ever
                    // sent for this turn. Polls for a SECOND `sendMessage` (bridge1's own req-1
                    // approval keyboard is already the first) — the turn actually completing, not
                    // merely bridge1's earlier send still sitting in the log.
                    do! bridge2.StartRun(UMX.tag<chatId> chat, "Still there?")
                    do! pollUntil 15000 (fun () -> (server.RequestsFor "sendMessage").Length >= 2)

                    match observer.RestoreFailed with
                    | [ (surfacedChat, CorruptRecord _) ] -> Expect.equal surfacedChat (UMX.tag<chatId> chat) "the restore failure is surfaced for the right chat"
                    | other -> failwithf "expected exactly one CorruptRecord restore failure, got %A" other

                    match observer.PersistFailed with
                    | [ (surfacedChat, _) ] -> Expect.equal surfacedChat (UMX.tag<chatId> chat) "the guarded removal's own failure is surfaced for the right chat"
                    | other -> failwithf "expected exactly one persist (removal) failure, got %A" other

                    Expect.isEmpty observer.TurnFailed "the turn itself succeeds — a store that cannot remove a corrupt record must never misclassify the WHOLE turn as OnTurnFailed"

                    Expect.equal (List.length (server.RequestsFor "sendMessage")) 2 "the turn's own reply was still delivered, on a fresh session, despite the store's Remove failing"
                    let sent = (server.RequestsFor "sendMessage") |> List.last |> fun r -> r.Body |> Option.get
                    Expect.stringContains (sent |> field "text" |> asString) "still here" "the fresh session's own reply reached the wire"
                }
                |> Async.AwaitTask
        }

        testCaseAsync
            "a transient TryGet blip: a tap mid-blip faults and lands stale rather than silently caching a fresh session, and a LATER turn — once the store recovers — retries the restore and finds the ORIGINAL record, req-1 included, still intact"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9813L
                    let bindingStore = InMemoryBindingStore() :> IBindingStore
                    let sessionStore = InMemorySessionStore() :> ISessionStore

                    let agent1 = ScriptedAgent [ PausesFor("req-1", "send_email", [ "toAddr", box "alice@example.com" ]) ]
                    let! bridge1 = Maf.startPolling (pollingConfig server bindingStore sessionStore) agent1
                    do! bridge1.StartRun(UMX.tag<chatId> chat, "Email alice that the deploy is done.")

                    let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                    let approveToken = callbackDataAt 0 0 sent

                    do! (bridge1 :> IAsyncDisposable).DisposeAsync().AsTask()

                    let observer = CapturingObserver()
                    let options: MafBridgeOptions = { MafBridgeOptions.defaults with Observer = ValueSome(observer :> IMafObserver) }
                    let sessionStore2 = FirstCallThrowsTryGetStore(sessionStore) :> ISessionStore
                    let resumes = ResizeArray<string * bool>()
                    let agent2 = ScriptedAgent([ RepliesWith "still here after the blip" ], onResume = (fun (r, a) -> resumes.Add(r, a)))
                    use! bridge2 = Maf.startPollingWith options (pollingConfig server bindingStore sessionStore2) agent2

                    // The ONLY tap this test drives — `sendNewApproval` sends every fresh approval
                    // `singleUse = true`, so the Tool Router itself consumes the underlying BINDING
                    // on the first ALLOWED press regardless of what `HandleDecision` goes on to decide
                    // (`UpdateProcessor.processPress`'s own doc comment: "consume on the first ALLOWED
                    // attempt, not consume on success") — a genuinely SECOND tap on this SAME token
                    // could never reach the tool again, blip or no blip. The store's `TryGet` throws
                    // on this ONE call (its one-shot blip). The restore must fault, report it, and
                    // land stale — NEVER silently cache a fresh, approval-less session for this chat
                    // (the risk being: `Conversations`' own `Lazy<Task<AgentSession>>`
                    // would otherwise cache that fresh session for the rest of the process's life, and
                    // the NEXT persist would overwrite the still-intact record).
                    do! deliverTap server 1 "q-blip-1" approveToken chat 1 chat
                    do! pollUntil 15000 (fun () -> not (List.isEmpty observer.RestoreFailed))
                    do! pollUntil 15000 (fun () -> not (List.isEmpty observer.Stale))

                    match observer.RestoreFailed with
                    | [ (surfacedChat, StoreUnavailable _) ] ->
                        Expect.equal surfacedChat (UMX.tag<chatId> chat) "surfaced for the right chat"
                    | other ->
                        failwithf
                            "expected EXACTLY one StoreUnavailable restore failure (a second entry here means the transient-blip fault was reported TWICE — once by restoreOrCreate itself, once more by HandleDecision's own pre-peek catch, which must now swallow instead), got %A"
                            other

                    Expect.equal observer.Stale.Length 1 "the tap — mid-blip — lands on the stale path"
                    Expect.equal resumes.Count 0 "nothing resumes on the faulted tap"

                    // A LATER, host-initiated turn for the SAME chat — the shape that actually proves
                    // the record survived: `conversations.GetOrCreate` was evicted on the tap's own
                    // fault (`Conversations.GetOrCreate`'s own doc comment), so THIS call retries
                    // `restoreOrCreate` rather than reusing a cached fresh session. The store's
                    // `TryGet` now succeeds (the blip was one-shot) — the ORIGINAL record, with req-1
                    // still pending inside it, is what this turn actually restores.
                    do! bridge2.StartRun(UMX.tag<chatId> chat, "Still there?")
                    do! pollUntil 15000 (fun () -> (server.RequestsFor "sendMessage").Length >= 2)

                    Expect.isEmpty observer.TurnFailed "the later turn succeeds — a resolved blip is never treated as an ongoing failure"

                    Expect.equal
                        (List.length observer.RestoreFailed)
                        1
                        "still exactly one restore failure overall — the later turn's own restore SUCCEEDS this time, it does not fail again"

                    let laterReply = (server.RequestsFor "sendMessage") |> List.last |> fun r -> r.Body |> Option.get
                    Expect.stringContains (laterReply |> field "text" |> asString) "still here after the blip" "the later turn's own reply reached the wire, on the properly-restored session"

                    // The KEY regression: the record this later turn re-persists must still carry
                    // req-1 — proving the ORIGINAL record (not a fresh, approval-less stand-in) is
                    // what actually got restored and carried forward.
                    let! record = sessionStore.TryGet(UMX.tag<chatId> chat, CancellationToken.None)

                    match record with
                    | ValueNone -> failtest "expected a persisted durable record for this chat"
                    | ValueSome stored ->
                        match SessionEnvelope.decodeAndValidate SessionEnvelope.currentMafVersion stored.Payload with
                        | Error err -> failwithf "expected a well-formed persisted envelope, got %A" err
                        | Ok env ->
                            let requestIds = env.Approvals |> Array.map (fun a -> a.RequestId) |> Array.toList

                            Expect.contains
                                requestIds
                                "req-1"
                                "req-1 is still carried in the record the later turn re-persisted — the ORIGINAL restored record, never silently replaced by a fresh one"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "corrupt session JSON inside a well-formed envelope: format/version both pass, DeserializeSessionAsync itself throws — OnSessionRestoreFailed(CorruptRecord _), removed, tap lands stale"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9806L
                    let bindingStore = InMemoryBindingStore() :> IBindingStore
                    let sessionStore = InMemorySessionStore() :> ISessionStore

                    let corrupt (store: ISessionStore) : Task =
                        let envelope: ConversationEnvelopeDto =
                            { Format = SessionEnvelope.CurrentFormat
                              MafVersion = SessionEnvelope.currentMafVersion
                              MeaiVersion = SessionEnvelope.currentMeaiVersion
                              SessionJson = """{"not":"a nonce"}"""
                              Approvals = [||] }

                        overwriteRecord store chat (SessionEnvelope.encode envelope)

                    let! observer, resumes, agent2 =
                        runRestoreFailureCycle server chat bindingStore sessionStore corrupt sessionStore "q-reliability-sessionjson"

                    match observer.RestoreFailed with
                    | [ (surfacedChat, CorruptRecord _) ] -> Expect.equal surfacedChat (UMX.tag<chatId> chat) "surfaced for the right chat"
                    | other -> failwithf "expected exactly one CorruptRecord restore failure, got %A" other

                    Expect.equal (List.length observer.Stale) 1 "the tap lands on the stale path — a session payload the agent itself rejects is never resumed"
                    Expect.equal resumes.Count 0 "a rejected session payload's decision is never resumed"
                    Expect.equal agent2.RunCount 0 "the post-restart agent never ran a turn for a tap with a corrupt session payload behind it"

                    let! afterFailure = sessionStore.TryGet(UMX.tag<chatId> chat, CancellationToken.None)
                    Expect.equal afterFailure ValueNone "the record is removed once the agent itself rejects the session payload it carries"
                }
                |> Async.AwaitTask
        }
    ]

[<Tests>]
let mafDurableReliabilityConsistencyTests =
    testList "MafBridge durable-session observer consistency" [

        testCase "IMafSessionObserver exposes exactly OnSessionRestoreFailed and OnSessionPersistFailed — a new member here needs its own triggering test in this file"
        <| fun _ ->
            let memberNames =
                typeof<IMafSessionObserver>.GetMethods()
                |> Array.map (fun (m: MethodInfo) -> m.Name)
                |> Array.sort

            Expect.equal
                memberNames
                [| "OnSessionPersistFailed"; "OnSessionRestoreFailed" |]
                "IMafSessionObserver's member set changed — add a triggering test above (or below) for any new member"

        testCaseAsync "completeness sweep: BOTH IMafSessionObserver members have a real triggering path in this suite"
        <| async {
            do!
                task {
                    let observer = CapturingObserver()

                    // OnSessionRestoreFailed — a corrupt record, the simplest of this file's own
                    // restore-failure shapes.
                    use! restoreServer = FakeBotApiServer.start ()
                    let restoreChat = 9901L
                    let restoreBindingStore = InMemoryBindingStore() :> IBindingStore
                    let restoreSessionStore = InMemorySessionStore() :> ISessionStore
                    let restoreAgent1 = ScriptedAgent [ PausesFor("req-1", "send_email", []) ]
                    let! restoreBridge1 = Maf.startPolling (pollingConfig restoreServer restoreBindingStore restoreSessionStore) restoreAgent1
                    do! restoreBridge1.StartRun(UMX.tag<chatId> restoreChat, "Email alice.")
                    let restoreSent = (restoreServer.RequestsFor "sendMessage").Head.Body |> Option.get
                    let restoreToken = callbackDataAt 0 0 restoreSent
                    do! (restoreBridge1 :> IAsyncDisposable).DisposeAsync().AsTask()
                    do! overwriteRecord restoreSessionStore restoreChat [| 9uy; 9uy; 9uy |]

                    let restoreOptions: MafBridgeOptions =
                        { MafBridgeOptions.defaults with
                            Observer = ValueSome(observer :> IMafObserver) }

                    let restoreAgent2 = ScriptedAgent [ RepliesWith "unexpected" ]

                    use! restoreBridge2 =
                        Maf.startPollingWith restoreOptions (pollingConfig restoreServer restoreBindingStore restoreSessionStore) restoreAgent2

                    do! deliverTap restoreServer 1 "q-sweep-restore" restoreToken restoreChat 1 restoreChat
                    do! pollUntil 15000 (fun () -> not (List.isEmpty observer.RestoreFailed))

                    // OnSessionPersistFailed — a store whose Save throws.
                    use! persistServer = FakeBotApiServer.start ()
                    let persistChat = 9902L
                    let persistBindingStore = InMemoryBindingStore() :> IBindingStore
                    let persistSessionStore = ThrowingSaveStore(InMemorySessionStore() :> ISessionStore) :> ISessionStore

                    let persistOptions: MafBridgeOptions =
                        { MafBridgeOptions.defaults with
                            Observer = ValueSome(observer :> IMafObserver) }

                    let persistAgent = ScriptedAgent [ RepliesWith "hi" ]
                    use! persistBridge = Maf.startPollingWith persistOptions (pollingConfig persistServer persistBindingStore persistSessionStore) persistAgent
                    do! persistBridge.StartRun(UMX.tag<chatId> persistChat, "hi")
                    do! pollUntil 15000 (fun () -> not (List.isEmpty observer.PersistFailed))

                    Expect.isNonEmpty observer.RestoreFailed "OnSessionRestoreFailed has a real triggering path"
                    Expect.isNonEmpty observer.PersistFailed "OnSessionPersistFailed has a real triggering path"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "three-tier resolution: a CUSTOM IMafObserver with no IMafSessionObserver, plus a wired logger, still gets a session-restore failure logged"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9903L
                    let bindingStore = InMemoryBindingStore() :> IBindingStore
                    let sessionStore = InMemorySessionStore() :> ISessionStore

                    let agent1 = ScriptedAgent [ PausesFor("req-1", "send_email", []) ]
                    let! bridge1 = Maf.startPolling (pollingConfig server bindingStore sessionStore) agent1
                    do! bridge1.StartRun(UMX.tag<chatId> chat, "Email alice.")

                    let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                    let approveToken = callbackDataAt 0 0 sent
                    do! (bridge1 :> IAsyncDisposable).DisposeAsync().AsTask()
                    do! overwriteRecord sessionStore chat [| 0uy; 1uy; 2uy |]

                    let logger = RecordingLogger()

                    let options: MafBridgeOptions =
                        { MafBridgeOptions.defaults with
                            Observer = ValueSome(ObserverOnly() :> IMafObserver) }

                    let config = (pollingConfig server bindingStore sessionStore).WithLogger logger
                    let agent2 = ScriptedAgent [ RepliesWith "unexpected" ]
                    use! bridge2 = Maf.startPollingWith options config agent2

                    do! deliverTap server 1 "q-tier-logged" approveToken chat 1 chat
                    do! pollUntil 15000 (fun () -> logger.Warnings |> List.exists (fun w -> w.Contains "restoring the durable session"))

                    Expect.isTrue
                        (logger.Warnings |> List.exists (fun w -> w.Contains "restoring the durable session"))
                        "a custom IMafObserver with no session channel, plus a wired logger, still gets the restore failure logged — the bot's own logger fallback"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "three-tier resolution: the SAME custom observer, with NO logger wired, drops a session-restore failure silently — never crashes the tap"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9904L
                    let bindingStore = InMemoryBindingStore() :> IBindingStore
                    let sessionStore = InMemorySessionStore() :> ISessionStore

                    let agent1 = ScriptedAgent [ PausesFor("req-1", "send_email", []) ]
                    let! bridge1 = Maf.startPolling (pollingConfig server bindingStore sessionStore) agent1
                    do! bridge1.StartRun(UMX.tag<chatId> chat, "Email alice.")

                    let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                    let approveToken = callbackDataAt 0 0 sent
                    do! (bridge1 :> IAsyncDisposable).DisposeAsync().AsTask()
                    do! overwriteRecord sessionStore chat [| 0uy; 1uy; 2uy |]

                    let options: MafBridgeOptions =
                        { MafBridgeOptions.defaults with
                            Observer = ValueSome(ObserverOnly() :> IMafObserver) }
                    // Deliberately no `.WithLogger(...)` on this second config — the noop tier.

                    let agent2 = ScriptedAgent [ RepliesWith "unexpected" ]
                    use! bridge2 = Maf.startPollingWith options (pollingConfig server bindingStore sessionStore) agent2

                    do! deliverTap server 1 "q-tier-silent" approveToken chat 1 chat
                    do! pollUntil 15000 (fun () -> server.RequestsFor "answerCallbackQuery" |> List.isEmpty |> not)

                    Expect.isEmpty
                        (server.RequestsFor "editMessageText")
                        "with no session-observer channel wired at all, the corrupt-record tap still lands stale, not resumed — no message is ever edited, and the tap itself is acked without crashing the bridge"
                }
                |> Async.AwaitTask
        }
    ]
