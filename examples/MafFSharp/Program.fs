/// Example: a Microsoft Agent Framework (MAF) agent whose tool call needs a human's approval —
/// approved or rejected with Telegram buttons, with no button/callback plumbing written here.
/// `DemoAgent` below is SCRIPTED (offline, deterministic) rather than backed by a live chat model,
/// so this example needs no API key: it emits the exact same `ToolApprovalRequestContent` a real
/// MAF tool-approval loop produces — built from `AIFunctionFactory.Create`/`ApprovalRequiredAIFunction`
/// wrapping a chat-model-backed `AIAgent`, per `docs/quickstart.md`'s "Author the agent" step — just
/// without a model deciding when to call it.
///
/// Also wires a DURABLE session store (`TgLLM.Persistence.FileSessionStore`, wrapped in
/// `ObfuscatingSessionStore` below) and a DURABLE binding store (`TgLLM.Persistence.FileBindingStore`)
/// via `TgBotConfig.WithSessionStore`/`WithBindingStore` — both are required for a pending approval to
/// survive a process restart: the tap's button routes via the binding store, and resuming the agent
/// rehydrates its conversation and still-pending approvals via the session store. With both wired, an
/// approval message shown BEFORE a restart is still honored AFTER one — the tap resumes the agent and
/// edits that same message in place, exactly as if the process had never gone down.
///
/// Also turns on live streaming (`TgBotConfig.WithStreaming()`) — both turns below now arrive as
/// several deltas, paced by a real `Task.Delay` (see `DelayedUpdateSequence` below) rather than one
/// shot, so the reply visibly edits in place in Telegram instead of appearing all at once. The
/// approval turn keeps its narration on the SAME live message once the buttons are added — see that
/// turn's own comment below for why this differs from the non-streaming path.
///
/// Set `BOT_TOKEN` and `CHAT_ID` (a private chat), then `dotnet run`.
module MafFSharp.Program

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Agents.AI
open Microsoft.Extensions.AI
open FSharp.UMX
open TgLLM.Core
open TgLLM.FSharp
open TgLLM.Maf
open TgLLM.Persistence

let private requireEnv (name: string) : string =
    match Environment.GetEnvironmentVariable name |> Option.ofObj with
    | Some value -> value
    | None -> failwith $"environment variable {name} is required"

/// A reversible byte transform over `SessionRecord.Payload`, demonstrating the AT-REST SEAM an
/// `ISessionStore` decorator sits in — this is NOT encryption. XOR with a single fixed byte is
/// trivially reversible by anyone reading this file; it only proves the seam exists: `Save`
/// transforms the payload before handing it to `inner`, `TryGet` reverses the SAME transform on the
/// way back out, and `Remove`/`EvictIdle` need no transform at all since neither one touches
/// `Payload`. A host that wants REAL at-rest protection for persisted conversation content plugs an
/// encrypting store in EXACTLY this position — e.g. a decorator that calls into
/// `System.Security.Cryptography.AesGcm`, or simply an encrypted disk/volume or a database with
/// transparent data encryption underneath `FileSessionStore`/`LiteDbSessionStore`. `TgLLM.Core.
/// ISessionStore` is the seam; the library itself adds no cryptography of its own.
type private ObfuscatingSessionStore(inner: ISessionStore) =
    let obfuscationKey = 0x5Auy

    let transform (payload: byte[]) : byte[] = payload |> Array.map (fun b -> b ^^^ obfuscationKey)

    interface ISessionStore with
        member _.Save(chat: ChatId, record: SessionRecord, ct: CancellationToken) : ValueTask =
            inner.Save(chat, { record with Payload = transform record.Payload }, ct)

        member _.TryGet(chat: ChatId, ct: CancellationToken) : ValueTask<SessionRecord voption> =
            ValueTask<SessionRecord voption>(
                task {
                    match! inner.TryGet(chat, ct) with
                    | ValueNone -> return ValueNone
                    | ValueSome record -> return ValueSome { record with Payload = transform record.Payload }
                }
            )

        member _.Remove(chat: ChatId, ct: CancellationToken) : ValueTask = inner.Remove(chat, ct)

        member _.EvictIdle(olderThan: DateTimeOffset) : ValueTask<int> = inner.EvictIdle(olderThan)

/// `AgentSession` has only a protected constructor — `Turn` is the ONLY state this subclass carries:
/// which scripted step `DemoAgent.RunCoreAsync` is on for THIS chat's conversation. Kept on the
/// session (not a field on `DemoAgent` itself, unlike an earlier version of this example) precisely
/// so it round-trips through `SerializeSessionCoreAsync`/`DeserializeSessionCoreAsync` below — a real
/// `AIAgent`'s own conversation state lives the same way, on the session, which is exactly what a
/// durable `ISessionStore` persists and restores.
type private DemoSession(?turn: int) =
    inherit AgentSession()
    member val Turn = defaultArg turn 0 with get, set

/// Safely downcasts a MAF-supplied session to `DemoSession`. `AIAgent`'s own `RunCoreAsync`/
/// `SerializeSessionCoreAsync` declare their session parameter nullable — `Option.ofObj` wraps that
/// boundary explicitly (Always-Rule 5) rather than downcasting a possibly-null reference directly.
let private asDemoSession (session: AgentSession | null) : DemoSession =
    match session |> Option.ofObj with
    | Some(:? DemoSession as demoSession) -> demoSession
    | _ -> invalidOp "DemoAgent requires a DemoSession — CreateSessionCoreAsync/DeserializeSessionCoreAsync always produce one."

/// A minimal, hand-rolled `IAsyncEnumerable<AgentResponseUpdate>` — the shape
/// `AIAgent.RunCoreStreamingAsync` must return — that yields each `(update, delay)` pair in order,
/// awaiting a REAL `Task.Delay` immediately before handing each one back. Unlike this repo's own test
/// double for the same override (`tests/TgLLM.Integration.Tests/MafScriptedAgent.fs`'s
/// `ScriptedUpdateSequence`), which advances a fake clock deterministically for assertions, this
/// example drives an ACTUAL bot token/chat — the delay is real wall-clock time, which is what makes
/// `bot.Streaming`'s live edit-in-place visible when you run this against Telegram.
type private DelayedUpdateSequence(items: (AgentResponseUpdate * TimeSpan) list) =
    interface IAsyncEnumerable<AgentResponseUpdate> with
        member _.GetAsyncEnumerator(_ct: CancellationToken) =
            let mutable pending = items
            let mutable current = Unchecked.defaultof<AgentResponseUpdate>

            { new IAsyncEnumerator<AgentResponseUpdate> with
                member _.Current = current

                member _.MoveNextAsync() =
                    ValueTask<bool>(
                        task {
                            match pending with
                            | (update, delay) :: rest ->
                                pending <- rest
                                do! Task.Delay delay
                                current <- update
                                return true
                            | [] -> return false
                        }
                    )

                member _.DisposeAsync() = ValueTask.CompletedTask }

/// One streamed text delta, carrying no other content — `AgentResponseUpdate`'s own
/// `(role, content: string)` constructor, the same one used for a normal assistant chunk.
let private textDelta (text: string) : AgentResponseUpdate = AgentResponseUpdate(Nullable ChatRole.Assistant, text)

/// The pacing between scripted deltas below — comfortably straddles the bot's own default 1.5s
/// coalescing interval (`TgBotConfig.WithStreaming()`'s own default,
/// `TgLLM.Maf.StreamingDefaults.defaultCoalesceInterval`) so a live edit is actually observable
/// mid-stream, not just the initial send and the final flush.
let private pace = TimeSpan.FromMilliseconds 500.

/// One text reply, then one tool-approval pause, then (once resumed) a confirmation reply — enough
/// to walk both an ordinary text turn and the approval loop in a single run, with no live model.
type private DemoAgent() =
    inherit AIAgent()

    override _.RunCoreAsync
        (
            _messages: ChatMessage seq,
            session: AgentSession | null,
            _options: AgentRunOptions,
            _ct: CancellationToken
        ) : Task<AgentResponse> =
        let demoSession = asDemoSession session
        demoSession.Turn <- demoSession.Turn + 1

        match demoSession.Turn with
        | 1 -> Task.FromResult(AgentResponse(ChatMessage(ChatRole.Assistant, "I can draft and send emails for you.")))
        | 2 ->
            let args = Dictionary<string, obj | null>()
            args["toAddr"] <- box "alice@example.com"
            args["body"] <- box "The deploy is done."
            let call = FunctionCallContent("call-1", "send_email", args)
            let request = ToolApprovalRequestContent("req-1", call)
            let contents = ResizeArray<AIContent> [ request :> AIContent ]
            Task.FromResult(AgentResponse(ChatMessage(ChatRole.Assistant, contents)))
        | _ -> Task.FromResult(AgentResponse(ChatMessage(ChatRole.Assistant, "Sent to alice@example.com.")))

    override _.CreateSessionCoreAsync(_ct: CancellationToken) : ValueTask<AgentSession> =
        ValueTask<AgentSession>(DemoSession() :> AgentSession)

    /// Real (if trivial) session serialization — required for the durable session store wired into
    /// `main` below to have anything meaningful to persist. `{"turn": N}` is the whole wire shape.
    override _.SerializeSessionCoreAsync
        (session: AgentSession, _options: JsonSerializerOptions, _ct: CancellationToken)
        : ValueTask<JsonElement> =
        let demoSession = asDemoSession session
        ValueTask<JsonElement>(JsonSerializer.SerializeToElement {| turn = demoSession.Turn |})

    /// The read counterpart to `SerializeSessionCoreAsync` — any other shape (missing property, wrong
    /// `ValueKind`) is a corrupt or foreign record, so it throws rather than fabricating a fresh
    /// session silently; `Bridge.fs`'s own `restoreOrCreate` catches exactly this and falls back to a
    /// brand-new session, reporting the failure via `IMafSessionObserver.OnSessionRestoreFailed`.
    override _.DeserializeSessionCoreAsync
        (element: JsonElement, _options: JsonSerializerOptions, _ct: CancellationToken)
        : ValueTask<AgentSession> =
        match element.TryGetProperty "turn" with
        | true, prop when prop.ValueKind = JsonValueKind.Number -> ValueTask<AgentSession>(DemoSession(prop.GetInt32()) :> AgentSession)
        | _ -> raise (InvalidOperationException "unrecognized demo session shape")

    /// Streaming counterpart to `RunCoreAsync` above — the SAME scripted turns (`Turn`, shared via
    /// the same `DemoSession`, still drives which one runs), but each reply arrives as several
    /// deltas paced by `pace` rather than in one shot, so `bot.Streaming`'s live edit-in-place is
    /// actually visible against Telegram: the FIRST delta sends a new message immediately (never
    /// gated), and later deltas edit that SAME message in place once the coalescing interval clears.
    /// A reply long enough to exceed Telegram's 4096-character per-message cap would instead spill
    /// into a NEW message at a whitespace-preferred boundary and keep live-editing there
    /// (`TgLLM.Maf.MessageSplitting.split`, `TgLLM.Maf.ReplyCoalescer`) — neither scripted reply here
    /// is long enough to trigger that, but the mechanism driving it is the same one this override
    /// feeds.
    override _.RunCoreStreamingAsync
        (
            _messages: ChatMessage seq,
            session: AgentSession,
            _options: AgentRunOptions,
            _ct: CancellationToken
        ) : Collections.Generic.IAsyncEnumerable<AgentResponseUpdate> =
        let demoSession = asDemoSession session
        demoSession.Turn <- demoSession.Turn + 1

        match demoSession.Turn with
        | 1 ->
            DelayedUpdateSequence
                [ textDelta "I can draft ", pace
                  textDelta "and send emails ", pace
                  textDelta "for you — ", pace
                  textDelta "coordinating drafts, ", pace
                  textDelta "checking recipients, ", pace
                  textDelta "and confirming before anything goes out.", pace ]
            :> IAsyncEnumerable<AgentResponseUpdate>
        | 2 ->
            // Narration streams first, then this SAME turn pauses for approval. The bridge keeps
            // this narration on the live message and ADDS the Approve/Reject buttons to it — unlike
            // the non-streaming path, whose own preamble (`response.Text` alongside a detected
            // approval) is dropped rather than sent as a separate message first
            // (`processInitialResponse`'s own doc comment in `src/TgLLM.Maf/Bridge.fs`).
            let args = Dictionary<string, obj | null>()
            args["toAddr"] <- box "alice@example.com"
            args["body"] <- box "The deploy is done."
            let call = FunctionCallContent("call-1", "send_email", args)
            let request = ToolApprovalRequestContent("req-1", call)

            let approvalDelta =
                AgentResponseUpdate(Nullable ChatRole.Assistant, ResizeArray<AIContent> [ request :> AIContent ])

            DelayedUpdateSequence
                [ textDelta "Drafting that email now", pace
                  textDelta "...", pace
                  approvalDelta, pace ]
            :> IAsyncEnumerable<AgentResponseUpdate>
        | _ ->
            DelayedUpdateSequence [ textDelta "Sent to alice@example.com.", TimeSpan.Zero ]
            :> IAsyncEnumerable<AgentResponseUpdate>

[<EntryPoint>]
let main _ =
    task {
        let botToken = requireEnv "BOT_TOKEN"
        let chat: ChatId = UMX.tag<chatId> (int64 (requireEnv "CHAT_ID"))

        // Maf.startPolling requires a Tool Router (.WithTools) — it registers its own internal
        // maf-approve/maf-reject tools into this SAME registry.
        let tools = ToolRegistry.create ()

        // Durable binding store: the approval message's own [Approve][Reject] buttons still route
        // to maf-approve/maf-reject after a restart. Durable session store (wrapped in
        // `ObfuscatingSessionStore` above): the agent's conversation and still-pending approvals for
        // THIS chat are rehydrated on the next turn. Both are required together — with only one of
        // the two durable, a post-restart tap either can't route (no binding store) or routes but
        // finds nothing to resume (no session store). With both wired, an approval message shown
        // BEFORE a process restart is still honored AFTER it: the tap resumes the agent and edits
        // that same message in place, exactly as if the process had never gone down.
        let bindingStore = FileBindingStore.openAt "bindings.json"
        let sessionStore = ObfuscatingSessionStore(FileSessionStore.OpenAt "sessions.json")

        let config =
            (TgBotConfig.create botToken)
                .WithTools(tools)
                .WithBindingStore(bindingStore)
                .WithSessionStore(sessionStore)
                .WithStreaming() // live edit-in-place at the default 1.5s cadence — see `DelayedUpdateSequence` above

        use! bridge = Maf.startPolling config (DemoAgent())

        // A text turn: the agent answers a plain question, streamed live — watch the message edit
        // in place in Telegram as `DemoAgent.RunCoreStreamingAsync`'s deltas arrive, rather than
        // appearing all at once.
        do! bridge.StartRun(chat, "What can you do?")

        // The approval loop: the agent's next turn streams a little narration, then pauses on
        // `send_email`. The bridge sends ONE owner-scoped [Approve][Reject] message that KEEPS the
        // narration already shown; tapping it resumes the agent and edits that same message in
        // place to the outcome.
        do! bridge.StartRun(chat, "Email alice@example.com that the deploy is done.")

        printfn "MAF bridge running (long polling). Approve/Reject the pending message in Telegram. Ctrl+C to stop."
        do! Task.Delay Timeout.InfiniteTimeSpan
        return 0
    }
    |> fun run -> run.GetAwaiter().GetResult()
