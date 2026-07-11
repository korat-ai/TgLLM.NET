/// Proves the durable session-store backends `MafDurableResumeTests.fs` exercises with an
/// `InMemorySessionStore` are actually INTERCHANGEABLE — the same restart-resume shape, driven
/// against `TgLLM.Persistence.FileSessionStore` and `TgLLM.Persistence.LiteDb.LiteDbSessionStore` in
/// turn, produces the SAME observable outcome, and swapping the backend changes nothing about how
/// the bridge behaves. Also proves the opt-in default: with NO session store configured at all, a
/// restart loses the in-flight approval, exactly like the pre-durable-session bridge always did.
/// Mirrors `MafDurableResumeTests.fs`'s own restart-simulation shape throughout: the BINDING store
/// stays a shared `InMemoryBindingStore` across the restart (so the tapped button still routes) and
/// only the SESSION store backend varies.
module TgLLM.Integration.Tests.MafDurableStoreFamilyTests

open System
open System.IO
open System.Text.Json.Nodes
open System.Threading.Tasks
open Expecto
open FSharp.UMX
open TgLLM.Core
open TgLLM.FSharp
open TgLLM.Persistence
open TgLLM.Persistence.LiteDb
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

let private tempPath (extension: string) : string =
    Path.Combine(Path.GetTempPath(), $"tgllm-maf-durable-store-family-tests-{Guid.NewGuid()}.{extension}")

/// Same shared-stores restart shape as `MafDurableResumeTests.fs`'s own `pollingConfig`.
let private pollingConfig (server: FakeBotApiServer) (bindingStore: IBindingStore) (sessionStore: ISessionStore) : TgBotConfig =
    (TgBotConfig.create "123456789:TEST-fake-token")
        .WithBaseUrl(server.BaseUrl)
        .WithTools(ToolRegistry.create ())
        .WithBindingStore(bindingStore)
        .WithSessionStore(sessionStore)

/// One durable session-store backend under test: how to open an instance over a path, and how to
/// release whatever exclusive handle (if any) it holds before a SECOND instance reopens the SAME
/// path — the file backend holds none (`DisposeBeforeReopen` is a no-op), the LiteDB backend holds
/// an exclusive `LiteDatabase` handle that MUST be released first, mirroring
/// `LifecycleTests.fs`'s own `(store1 :?> IDisposable).Dispose()` restart pattern for
/// `LiteDbBindingStore`.
[<NoComparison; NoEquality>]
type private SessionBackend =
    { Name: string
      Open: string -> ISessionStore
      DisposeBeforeReopen: ISessionStore -> unit }

let private fileBackend: SessionBackend =
    { Name = "File"
      Open = fun path -> FileSessionStore.OpenAt path :> ISessionStore
      DisposeBeforeReopen = fun _ -> () }

let private liteDbBackend: SessionBackend =
    { Name = "LiteDb"
      Open = fun path -> LiteDbSessionStore.OpenAt path :> ISessionStore
      DisposeBeforeReopen = fun store -> (store :?> IDisposable).Dispose() }

/// Drives one full "raise an approval, restart, tap Approve" cycle against `backend`, over a shared
/// `InMemoryBindingStore` across the restart (so the tapped button still routes — only the SESSION
/// store backend is under test) and a BRAND-NEW store instance opened over the SAME `path` for the
/// post-restart bridge (nothing in-memory carries over; only what reached `path` does). Returns the
/// edited message's own outcome text so callers can compare backends for identical behavior.
let private runRestartResume (server: FakeBotApiServer) (chat: int64) (path: string) (backend: SessionBackend) : Task<string> =
    task {
        let bindingStore = InMemoryBindingStore() :> IBindingStore
        let sessionStore1 = backend.Open path

        let agent1 = ScriptedAgent [ PausesFor("req-1", "send_email", [ "toAddr", box "alice@example.com" ]) ]
        let! bridge1 = Maf.startPolling (pollingConfig server bindingStore sessionStore1) agent1
        do! bridge1.StartRun(UMX.tag<chatId> chat, "Email alice that the deploy is done.")

        let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
        let approveToken = callbackDataAt 0 0 sent

        do! (bridge1 :> IAsyncDisposable).DisposeAsync().AsTask()
        backend.DisposeBeforeReopen sessionStore1

        // "Restart": a brand-new agent + tool registry, a brand-new session-store instance over the
        // SAME path, but the SAME (shared) binding store — only the durable SESSION backend varies.
        let sessionStore2 = backend.Open path
        let resumes = ResizeArray<string * bool>()

        let agent2 =
            ScriptedAgent(
                [ RepliesWith "Email sent to alice@example.com." ],
                onResume = (fun (reqId, approved) -> resumes.Add(reqId, approved))
            )

        use! bridge2 = Maf.startPolling (pollingConfig server bindingStore sessionStore2) agent2

        do! deliverTap server 1 $"q-family-{backend.Name}" approveToken chat 1 chat
        do! pollUntil 15000 (fun () -> server.RequestsFor "editMessageText" |> List.isEmpty |> not)

        Expect.equal resumes.Count 1 $"the post-restart agent was resumed exactly once ({backend.Name})"
        Expect.equal resumes[0] ("req-1", true) $"resumed with the PERSISTED request id and Approved = true ({backend.Name})"
        Expect.equal agent2.RunCount 1 $"the post-restart agent's only turn is this resume ({backend.Name})"

        let editBody = (server.RequestsFor "editMessageText").Head.Body |> Option.get
        Expect.equal (editBody |> field "message_id" |> asInt64) 1L $"the SAME pre-restart message is edited in place ({backend.Name})"
        Expect.equal (List.length (server.RequestsFor "sendMessage")) 1 $"resuming after a restart never sends a NEW message ({backend.Name})"

        return editBody |> field "text" |> asString
    }

[<Tests>]
let mafDurableStoreFamilyTests =
    testList "MafBridge durable session-store backends are interchangeable" [

        testCaseAsync "File backend: an approval raised before a restart resumes after reopening a BRAND-NEW FileSessionStore over the SAME path"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let path = tempPath "json"

                    try
                        let! outcome = runRestartResume server 9401L path fileBackend
                        Expect.stringContains outcome "approved" "the outcome says it was approved"
                        Expect.stringContains outcome "Email sent to alice@example.com." "the outcome carries the post-restart agent's own reply"
                    finally
                        File.Delete path
                }
                |> Async.AwaitTask
        }

        testCaseAsync "LiteDb backend: an approval raised before a restart resumes after disposing store1 and reopening a BRAND-NEW LiteDbSessionStore over the SAME path"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let path = tempPath "db"

                    try
                        let! outcome = runRestartResume server 9402L path liteDbBackend
                        Expect.stringContains outcome "approved" "the outcome says it was approved"
                        Expect.stringContains outcome "Email sent to alice@example.com." "the outcome carries the post-restart agent's own reply"
                    finally
                        File.Delete path
                }
                |> Async.AwaitTask
        }

        testCaseAsync "File and LiteDb backends are interchangeable — restarting over either produces the IDENTICAL observable outcome"
        <| async {
            do!
                task {
                    use! fileServer = FakeBotApiServer.start ()
                    use! liteServer = FakeBotApiServer.start ()
                    let filePath = tempPath "json"
                    let litePath = tempPath "db"

                    try
                        let! fileOutcome = runRestartResume fileServer 9403L filePath fileBackend
                        let! liteOutcome = runRestartResume liteServer 9403L litePath liteDbBackend
                        Expect.equal liteOutcome fileOutcome "swapping the durable session-store backend changes nothing about the observable outcome"
                    finally
                        File.Delete filePath
                        File.Delete litePath
                }
                |> Async.AwaitTask
        }

        testCaseAsync "no durable session store configured: a post-restart tap lands stale — the opt-in default is OFF, byte-identical to the pre-feature bridge"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 9404L
                    let bindingStore = InMemoryBindingStore() :> IBindingStore

                    // Deliberately no `.WithSessionStore(...)` call at all — the opt-in default. A
                    // FRESH `ToolRegistry` every call (mirroring `pollingConfig`'s own per-call
                    // construction) — reusing one instance across both bridges would trip the
                    // double-attach guard (`BridgeBuild.build`'s own doc comment), since the SAME
                    // registry would still carry the first bridge's `maf-approve`/`maf-reject`.
                    let noSessionStoreConfig () : TgBotConfig =
                        (TgBotConfig.create "123456789:TEST-fake-token")
                            .WithBaseUrl(server.BaseUrl)
                            .WithTools(ToolRegistry.create ())
                            .WithBindingStore(bindingStore)

                    let agent1 = ScriptedAgent [ PausesFor("req-1", "send_email", []) ]
                    let! bridge1 = Maf.startPolling (noSessionStoreConfig ()) agent1
                    do! bridge1.StartRun(UMX.tag<chatId> chat, "Email alice.")

                    let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                    let approveToken = callbackDataAt 0 0 sent

                    do! (bridge1 :> IAsyncDisposable).DisposeAsync().AsTask()

                    let staleDecisions = ResizeArray<ApprovalDescriptor>()

                    let observer =
                        { new IMafObserver with
                            member _.OnStaleDecision(descriptor) = staleDecisions.Add descriptor
                            member _.OnMalformedDecision(_raw) = ()
                            member _.OnResumeFailed(_descriptor, _error) = ()
                            member _.OnEmptyTurn(_chat) = ()
                            member _.OnInvalidOutput(_chat, _error) = ()
                            member _.OnProjectionProblem(_problem) = ()
                            member _.OnTurnFailed(_chat, _error) = () }

                    let options: MafBridgeOptions = { MafBridgeOptions.defaults with Observer = ValueSome observer }
                    let resumes = ResizeArray<string * bool>()
                    let agent2 = ScriptedAgent([ RepliesWith "unexpected" ], onResume = (fun (r, a) -> resumes.Add(r, a)))
                    use! bridge2 = Maf.startPollingWith options (noSessionStoreConfig ()) agent2

                    do! deliverTap server 1 "q-family-no-store" approveToken chat 1 chat
                    do! pollUntil 15000 (fun () -> staleDecisions.Count > 0)

                    Expect.equal staleDecisions.Count 1 "with no durable session store, nothing survives the restart for this chat — the tap lands stale"
                    Expect.equal staleDecisions[0].RequestId "req-1" "the surfaced descriptor still names the pre-restart request"
                    Expect.equal resumes.Count 0 "with no durable session store, a post-restart tap never resumes the agent"
                    Expect.equal agent2.RunCount 0 "the post-restart agent never ran a turn — there was nothing durable to resume from"
                    Expect.isEmpty (server.RequestsFor "editMessageText") "a stale post-restart tap never edits the message"
                }
                |> Async.AwaitTask
        }
    ]
