/// Restart-simulation acceptance for the durable session: a HITL approval sent by ONE `MafBridge`
/// process is decided by a DIFFERENT one, sharing nothing but the two durable stores — an
/// `InMemoryBindingStore` (so the tap still ROUTES to `maf-approve`/`maf-reject`, exactly like
/// `RestartPersistenceTests.fs`'s own restart harness) AND an `InMemorySessionStore` (so the agent
/// conversation + its still-pending approvals actually RESUME). The agent, tools, and bridge itself
/// are all rebuilt from scratch for the "post-restart" half — nothing in-process survives; only what
/// reached the two stores does.
module TgLLM.Integration.Tests.MafDurableResumeTests

open System
open System.Net.Http
open System.Text
open System.Text.Json.Nodes
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Logging
open Expecto
open FSharp.UMX
open TgLLM.Core
open TgLLM.FSharp
open TgLLM.AspNetCore
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

/// One shared pair of durable stores + a fresh `ToolRegistry`/config for ONE bridge instance — every
/// test builds a "before" bridge, disposes it, then builds an "after" bridge with THIS SAME function
/// over the SAME two store instances (nothing else carries over), exactly mirroring
/// `RestartPersistenceTests.fs`'s own restart shape (there: `IBindingStore` only; here: also
/// `ISessionStore`, since durable resume needs both — the binding routes the tap, the session record
/// resumes the agent).
let private pollingConfig (server: FakeBotApiServer) (bindingStore: IBindingStore) (sessionStore: ISessionStore) : TgBotConfig =
    (TgBotConfig.create "123456789:TEST-fake-token")
        .WithBaseUrl(server.BaseUrl)
        .WithTools(ToolRegistry.create ())
        .WithBindingStore(bindingStore)
        .WithSessionStore(sessionStore)

/// Same pattern as `MafWebhookBridgeTests.fs`'s own `startWebhookHost` — hosts the webhook-receiving
/// endpoint on a loopback port.
let private startWebhookHost (source: TgLLM.Webhooks.WebhookUpdateSource) (secret: string) : Task<WebApplication> =
    task {
        let builder = WebApplication.CreateBuilder()
        builder.WebHost.UseUrls "http://127.0.0.1:0" |> ignore
        builder.Logging.ClearProviders() |> ignore
        let app = builder.Build()
        app.MapTelegramWebhook("/telegram/webhook", source, secret) |> ignore
        do! app.StartAsync()
        return app
    }

let private post (http: HttpClient) (url: string) (json: string) (secret: string) : Task<HttpResponseMessage> =
    let request = new HttpRequestMessage(HttpMethod.Post, url)
    request.Content <- new StringContent(json, Encoding.UTF8, "application/json")
    request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", secret)
    http.SendAsync request

type private RecordingSessionObserver() =
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
let mafDurableResumeTests =
    testList "MafBridge durable resume across a restart" [

        testCaseAsync "Approve after a restart resumes the agent and edits the SAME pre-restart message"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 8001L
                    let bindingStore = InMemoryBindingStore() :> IBindingStore
                    let sessionStore = InMemorySessionStore() :> ISessionStore

                    let agent1 = ScriptedAgent [ PausesFor("req-1", "send_email", [ "toAddr", box "alice@example.com" ]) ]
                    let! bridge1 = Maf.startPolling (pollingConfig server bindingStore sessionStore) agent1
                    do! bridge1.StartRun(UMX.tag<chatId> chat, "Email alice that the deploy is done.")

                    let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                    let approveToken = callbackDataAt 0 0 sent

                    do! (bridge1 :> IAsyncDisposable).DisposeAsync().AsTask()

                    // "Restart": a brand-new agent instance and tool registry, over the SAME two stores.
                    let resumes = ResizeArray<string * bool>()

                    let agent2 =
                        ScriptedAgent(
                            [ RepliesWith "Email sent to alice@example.com." ],
                            onResume = (fun (reqId, approved) -> resumes.Add(reqId, approved))
                        )

                    use! bridge2 = Maf.startPolling (pollingConfig server bindingStore sessionStore) agent2

                    do! deliverTap server 1 "q-durable-approve" approveToken chat 1 chat
                    do! pollUntil 15000 (fun () -> server.RequestsFor "editMessageText" |> List.isEmpty |> not)

                    Expect.equal resumes.Count 1 "the post-restart agent was resumed exactly once"
                    Expect.equal resumes[0] ("req-1", true) "resumed with the PERSISTED request id and Approved = true"
                    Expect.equal agent2.RunCount 1 "the post-restart agent's only turn is this resume — StartRun never ran on it"

                    let editBody = (server.RequestsFor "editMessageText").Head.Body |> Option.get
                    Expect.equal (editBody |> field "message_id" |> asInt64) 1L "the SAME pre-restart message is edited in place — no new message"
                    Expect.equal (List.length (server.RequestsFor "sendMessage")) 1 "resuming after a restart never sends a NEW message"

                    let outcome = editBody |> field "text" |> asString
                    Expect.stringContains outcome "approved" "the outcome says it was approved"
                    Expect.stringContains outcome "Email sent to alice@example.com." "the outcome carries the POST-restart agent's own reply text"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "Reject after a restart resumes the agent with Approved = false and edits to the rejection"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 8002L
                    let bindingStore = InMemoryBindingStore() :> IBindingStore
                    let sessionStore = InMemorySessionStore() :> ISessionStore

                    let agent1 = ScriptedAgent [ PausesFor("req-1", "delete_record", []) ]
                    let! bridge1 = Maf.startPolling (pollingConfig server bindingStore sessionStore) agent1
                    do! bridge1.StartRun(UMX.tag<chatId> chat, "Delete the record.")

                    let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                    let rejectToken = callbackDataAt 0 1 sent

                    do! (bridge1 :> IAsyncDisposable).DisposeAsync().AsTask()

                    let resumes = ResizeArray<string * bool>()
                    let agent2 = ScriptedAgent([ RepliesWith "" ], onResume = (fun (reqId, approved) -> resumes.Add(reqId, approved)))
                    use! bridge2 = Maf.startPolling (pollingConfig server bindingStore sessionStore) agent2

                    do! deliverTap server 1 "q-durable-reject" rejectToken chat 1 chat
                    do! pollUntil 15000 (fun () -> server.RequestsFor "editMessageText" |> List.isEmpty |> not)

                    Expect.equal resumes.Count 1 "the post-restart agent was resumed exactly once"
                    Expect.equal resumes[0] ("req-1", false) "resumed with the PERSISTED request id and Approved = false"

                    let editBody = (server.RequestsFor "editMessageText").Head.Body |> Option.get
                    Expect.equal (editBody |> field "message_id" |> asInt64) 1L "the SAME pre-restart message is edited in place"
                    let outcome = editBody |> field "text" |> asString
                    Expect.stringContains outcome "rejected" "the outcome says it was rejected"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a chained approval raised by a post-restart resume is itself persisted — it survives a SECOND restart"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 8003L
                    let bindingStore = InMemoryBindingStore() :> IBindingStore
                    let sessionStore = InMemorySessionStore() :> ISessionStore

                    // --- Before ANY restart: raise req-1. ---
                    let agent1 = ScriptedAgent [ PausesFor("req-1", "send_email", []) ]
                    let! bridge1 = Maf.startPolling (pollingConfig server bindingStore sessionStore) agent1
                    do! bridge1.StartRun(UMX.tag<chatId> chat, "Notify the team.")

                    let firstSend = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                    let approveToken1 = callbackDataAt 0 0 firstSend

                    do! (bridge1 :> IAsyncDisposable).DisposeAsync().AsTask()

                    // --- FIRST restart: approving req-1 raises a CHAINED req-2, itself persisted. ---
                    let resumes2 = ResizeArray<string * bool>()
                    let agent2 = ScriptedAgent([ PausesFor("req-2", "send_sms", []) ], onResume = (fun (r, a) -> resumes2.Add(r, a)))
                    let! bridge2 = Maf.startPolling (pollingConfig server bindingStore sessionStore) agent2

                    do! deliverTap server 1 "q-chain-restart-1" approveToken1 chat 1 chat
                    do! pollUntil 15000 (fun () -> server.RequestsFor "editMessageText" |> List.isEmpty |> not)

                    Expect.equal resumes2[0] ("req-1", true) "the first restart's resume decided req-1"
                    Expect.equal (List.length (server.RequestsFor "sendMessage")) 1 "the chained req-2 reuses the SAME message — no new send"

                    let chainedBody = (server.RequestsFor "editMessageText").Head.Body |> Option.get
                    Expect.equal (chainedBody |> field "message_id" |> asInt64) 1L "req-2 was chained onto the SAME message as req-1"
                    Expect.stringContains (chainedBody |> field "text" |> asString) "send_sms" "the chained edit shows req-2's own prompt"
                    let approveToken2 = callbackDataAt 0 0 chainedBody

                    do! (bridge2 :> IAsyncDisposable).DisposeAsync().AsTask()

                    // --- SECOND restart: a THIRD bridge, over the SAME two stores, resolves req-2. ---
                    let resumes3 = ResizeArray<string * bool>()
                    let agent3 = ScriptedAgent([ RepliesWith "Both emails sent." ], onResume = (fun (r, a) -> resumes3.Add(r, a)))
                    use! bridge3 = Maf.startPolling (pollingConfig server bindingStore sessionStore) agent3

                    do! deliverTap server 2 "q-chain-restart-2" approveToken2 chat 1 chat
                    do! pollUntil 15000 (fun () -> server.RequestsFor "editMessageText" |> List.length >= 2)

                    Expect.equal resumes3.Count 1 "the SECOND restart's resume decided req-2 exactly once"
                    Expect.equal resumes3[0] ("req-2", true) "resumed with req-2's OWN persisted request id"

                    let finalBody = (server.RequestsFor "editMessageText") |> List.last |> fun r -> r.Body |> Option.get
                    Expect.equal (finalBody |> field "message_id" |> asInt64) 1L "still the SAME original message, across BOTH restarts"
                    Expect.stringContains (finalBody |> field "text" |> asString) "Both emails sent." "the turn concludes with the THIRD agent's own reply"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "the SAME durable resume ALSO works over the WEBHOOK transport, driven by a real HTTP POST"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let secret = "s3cret-maf-durable-webhook"
                    let chat = 8004L
                    let bindingStore = InMemoryBindingStore() :> IBindingStore
                    let sessionStore = InMemorySessionStore() :> ISessionStore

                    let webhookConfig (tools: ToolRegistry) : TgWebhookConfig =
                        TgWebhookConfig
                            .create("123456789:TEST-fake-token", "https://example.test/ignored", secret)
                            .WithBaseUrl(server.BaseUrl)
                            .WithTools(tools)
                            .WithBindingStore(bindingStore)
                            .WithSessionStore(sessionStore)

                    let agent1 = ScriptedAgent [ PausesFor("req-1", "send_email", [ "toAddr", box "alice@example.com" ]) ]
                    let! bridge1 = Maf.startWebhook (webhookConfig (ToolRegistry.create ())) agent1
                    do! bridge1.StartRun(UMX.tag<chatId> chat, "Email alice that the deploy is done.")

                    let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                    let approveToken = callbackDataAt 0 0 sent

                    do! (bridge1 :> IAsyncDisposable).DisposeAsync().AsTask()

                    // "Restart", over the webhook transport this time: a brand-new agent + tool
                    // registry, the SAME two stores, and the tap delivered as a real HTTP POST rather
                    // than a scripted `getUpdates` payload — persist/restore sits below the transport
                    // on the per-chat lane, so this is the SAME resume path `MafBridgeApprovalTests.fs`'s
                    // polling-transport durable-resume test above exercises, proven transport-agnostic.
                    let resumes = ResizeArray<string * bool>()

                    let agent2 =
                        ScriptedAgent(
                            [ RepliesWith "Email sent to alice@example.com." ],
                            onResume = (fun (reqId, approved) -> resumes.Add(reqId, approved))
                        )

                    use! bridge2 = Maf.startWebhook (webhookConfig (ToolRegistry.create ())) agent2
                    use! host = startWebhookHost bridge2.Bot.WebhookSource secret
                    let webhookUrl = (Seq.head host.Urls).TrimEnd('/') + "/telegram/webhook"
                    use http = new HttpClient()

                    let updateJson = TelegramJson.callbackQueryUpdate 1 "q-durable-webhook-approve" approveToken chat 1 chat "Tester"
                    let! response = post http webhookUrl updateJson secret
                    Expect.equal (int response.StatusCode) 200 "the webhook delivery is accepted"

                    do! pollUntil 15000 (fun () -> server.RequestsFor "editMessageText" |> List.isEmpty |> not)

                    Expect.equal resumes.Count 1 "the agent was resumed exactly once, over the webhook transport, after a restart"
                    Expect.equal resumes[0] ("req-1", true) "resumed with the persisted request id and Approved = true"

                    let editBody = (server.RequestsFor "editMessageText").Head.Body |> Option.get
                    let outcome = editBody |> field "text" |> asString
                    Expect.stringContains outcome "approved" "the outcome says it was approved"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a non-owner's tap after a restart is refused (the persisted OwnerUserId still applies) — never resumes"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 8005L
                    let ownerId = 9001L
                    let otherId = 9002L
                    let bindingStore = InMemoryBindingStore() :> IBindingStore
                    let sessionStore = InMemorySessionStore() :> ISessionStore

                    let agent1 = ScriptedAgent [ PausesFor("req-1", "send_email", []) ]
                    let! bridge1 = Maf.startPolling (pollingConfig server bindingStore sessionStore) agent1
                    do! bridge1.StartRun(UMX.tag<chatId> chat, "Email alice.", owner = Owner.user ownerId)

                    let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                    let approveToken = callbackDataAt 0 0 sent

                    do! (bridge1 :> IAsyncDisposable).DisposeAsync().AsTask()

                    let resumes = ResizeArray<string * bool>()
                    let agent2 = ScriptedAgent([ RepliesWith "sent" ], onResume = (fun (r, a) -> resumes.Add(r, a)))
                    use! bridge2 = Maf.startPolling (pollingConfig server bindingStore sessionStore) agent2

                    // A DIFFERENT user taps first, post-restart.
                    do! deliverTap server 1 "q-durable-nonowner" approveToken chat 1 otherId
                    do! pollUntil 15000 (fun () -> server.RequestsFor "answerCallbackQuery" |> List.isEmpty |> not)

                    Expect.equal resumes.Count 0 "the non-owner's tap never resumed the agent"
                    Expect.equal agent2.RunCount 0 "the post-restart agent never ran a turn for a refused tap"
                    Expect.isEmpty (server.RequestsFor "editMessageText") "the non-owner's tap never edits the message"

                    match server.RequestsFor "answerCallbackQuery" with
                    | [ ack ] ->
                        let ackBody = ack.Body |> Option.get
                        Expect.equal (ackBody |> field "text" |> asString) OwnerScope.DefaultDeniedNotice "the non-owner sees the denied notice"
                    | other -> failwithf "expected exactly one answerCallbackQuery, got %d" (List.length other)

                    // The OWNER's own tap, right after, still resumes — the non-owner's refused tap
                    // never consumed the entry the restore rehydrated.
                    do! deliverTap server 2 "q-durable-owner" approveToken chat 1 ownerId
                    do! pollUntil 15000 (fun () -> server.RequestsFor "editMessageText" |> List.isEmpty |> not)

                    Expect.equal resumes.Count 1 "the owner's OWN tap, after the earlier refusal, resumes normally"
                    Expect.equal resumes[0] ("req-1", true) "resumed with the persisted request id and Approved = true"
                }
                |> Async.AwaitTask
        }

        testCaseAsync "a decision already consumed BEFORE a restart is not resumable after it — the persist excludes it, so the sibling button lands stale"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()
                    let chat = 8006L
                    let bindingStore = InMemoryBindingStore() :> IBindingStore
                    let sessionStore = InMemorySessionStore() :> ISessionStore

                    // --- Before the restart: decide req-1 (Approve) — this CONSUMES it, and the
                    // end-of-turn persist that follows snapshots `pendingApprovals` WITHOUT it. ---
                    let resumes1 = ResizeArray<string * bool>()
                    let agent1 = ScriptedAgent([ PausesFor("req-1", "send_email", []); RepliesWith "sent" ], onResume = (fun (r, a) -> resumes1.Add(r, a)))
                    let! bridge1 = Maf.startPolling (pollingConfig server bindingStore sessionStore) agent1
                    do! bridge1.StartRun(UMX.tag<chatId> chat, "Email alice.")

                    let sent = (server.RequestsFor "sendMessage").Head.Body |> Option.get
                    let approveToken = callbackDataAt 0 0 sent
                    // The SIBLING (never-pressed) button's own binding is untouched by consuming
                    // Approve's — its ENGINE-level binding is still live post-restart, so a tap on IT
                    // still reaches `HandleDecision`, unlike a re-tap of the ALREADY-consumed Approve
                    // token (refused earlier by the engine's own single-use removal — see
                    // `MafBridgeRefusalTests.fs`'s equivalent same-token case).
                    let rejectToken = callbackDataAt 0 1 sent

                    do! deliverTap server 1 "q-durable-consume" approveToken chat 1 chat
                    do! pollUntil 15000 (fun () -> server.RequestsFor "editMessageText" |> List.isEmpty |> not)
                    Expect.equal resumes1[0] ("req-1", true) "req-1 was decided (and consumed) BEFORE any restart"

                    do! (bridge1 :> IAsyncDisposable).DisposeAsync().AsTask()

                    // --- Restart: the sibling Reject button's tap restores the (now approval-less)
                    // record and must land on the stale path — never a second resume. ---
                    let observer = RecordingSessionObserver()
                    let options: MafBridgeOptions = { MafBridgeOptions.defaults with Observer = ValueSome(observer :> IMafObserver) }
                    let resumes2 = ResizeArray<string * bool>()
                    let agent2 = ScriptedAgent([ RepliesWith "unexpected" ], onResume = (fun (r, a) -> resumes2.Add(r, a)))
                    use! bridge2 = Maf.startPollingWith options (pollingConfig server bindingStore sessionStore) agent2

                    do! deliverTap server 2 "q-durable-stale" rejectToken chat 1 chat
                    do! pollUntil 15000 (fun () -> not (List.isEmpty observer.StaleDecisions))

                    Expect.equal (List.length observer.StaleDecisions) 1 "the sibling tap on the already-consumed decision is surfaced as stale"
                    Expect.equal observer.StaleDecisions[0].RequestId "req-1" "the surfaced descriptor names the already-decided request"
                    Expect.equal resumes2.Count 0 "the already-consumed decision is NOT resumed a second time, post-restart"
                    Expect.equal agent2.RunCount 0 "the post-restart agent never ran a turn for a stale tap"
                    Expect.equal (List.length (server.RequestsFor "editMessageText")) 1 "the stale tap never produces a second edit — only the PRE-restart decision's own edit exists"
                }
                |> Async.AwaitTask
        }
    ]
