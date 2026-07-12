/// An offline, deterministic `AIAgent` double for the bridge's integration tests — no live model,
/// no network. Overrides the ONE protected core every public `AIAgent.RunAsync`/`CreateSessionAsync`
/// overload funnels into (`RunCoreAsync`/`CreateSessionCoreAsync`), per the resolved 1.13.0
/// binaries' own shape: those public members are non-virtual wrappers over these two.
module TgLLM.Integration.Tests.MafScriptedAgent

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Agents.AI
open Microsoft.Extensions.AI

/// One step of a scripted conversation, popped in order by each `RunCoreAsync` call.
/// `NoComparison`: `PausesFor`'s `obj` argument values don't satisfy F#'s structural-comparison
/// constraint (`obj` has no `IComparable`); nothing in these tests ever compares two steps anyway.
[<NoComparison>]
type ScriptedStep =
    /// The agent's turn ends with a plain text reply.
    | RepliesWith of text: string
    /// The agent pauses, asking for approval of one tool call.
    | PausesFor of requestId: string * toolName: string * args: (string * (obj | null)) list
    /// The agent pauses, asking for approval of SEVERAL tool calls in the SAME turn — one
    /// `ToolApprovalRequestContent` per entry, all in one `AgentResponse`. Distinct from returning
    /// several `PausesFor` steps in a row: those would each be a SEPARATE `RunCoreAsync` call
    /// (i.e. separate turns/resumes), while this is ALL of them raised by a SINGLE call, exactly
    /// the shape a resume (or an initial turn) that raises multiple pending requests at once needs.
    | PausesForMany of requests: (string * string * (string * (obj | null)) list) list
    /// The agent pauses, asking for approval of one tool call whose `ToolCall` is NOT a
    /// `FunctionCallContent` — the resolved 10.6.0 binaries' `ToolCallContent` has other concrete
    /// subtypes (`CodeInterpreterToolCallContent`, `McpServerToolCallContent`, etc.); this uses
    /// `CodeInterpreterToolCallContent` (a single-string `callId` constructor) as a stand-in for any
    /// of them. Exercises `ApprovalDetection`'s own fallback branch (an unexpected `ToolCall`
    /// subtype still yields a usable prompt, via `other.CallId`) and, downstream, `Bridge.fs`'s
    /// `toPersistedDto` — which must SKIP such a pending approval at persist time rather than crash
    /// the whole record on a hard `:?> FunctionCallContent` cast.
    | PausesForNonFunctionCall of requestId: string * callId: string
    /// The agent's turn ends with no text and no approval — an empty turn.
    | EndsEmpty
    /// The agent throws while producing this turn (or resuming into it).
    | Throws of error: exn
    /// Delays this long BEFORE producing `step` — simulates a slow model/tool-backend turn (e.g.
    /// a resume that takes longer than the deferred-ack watchdog budget), so a test can assert the
    /// tap is still acked promptly while the continuation is still running.
    | Delayed of delay: TimeSpan * step: ScriptedStep
    /// A STREAMING-only step (`RunCoreStreamingAsync`, never `RunCoreAsync`): yields one text update
    /// per `(delta, advanceClockBy)` pair, in order, advancing the agent's own `advanceClock`
    /// callback by `advanceClockBy` immediately before that delta is handed back from the
    /// enumerator's own `MoveNextAsync` — the SAME instant a real bridge loop would next read its
    /// clock for a coalescing/pacing decision. After every scripted delta, `final` streams via the
    /// SAME one-shot `runStep` + `AgentResponse.ToAgentResponseUpdates()` conversion every OTHER
    /// step already uses, so a `PausesFor`/`EndsEmpty`/etc. still works as this step's own ending.
    | StreamsThen of steps: (string * TimeSpan) list * final: ScriptedStep
    /// A STREAMING-only `final` step (like `EndsEmpty`, never popped by `RunCoreAsync`): blocks the
    /// enumerator's own `MoveNextAsync` indefinitely until the REAL, cancellable `CancellationToken`
    /// is cancelled, then throws `OperationCanceledException` — the SAME shape a genuine streaming
    /// backend's own enumerator would raise when a graceful shutdown (`MafBridge.DisposeAsync`'s own
    /// `cts.Cancel()`) cancels an in-flight turn mid-stream. That real token is `RunCoreStreamingAsync`'s
    /// OWN `ct` parameter — NOT the one `GetAsyncEnumerator` receives (`ScriptedUpdateSequence`'s own
    /// doc comment: empirically, against the resolved 1.13.0 binaries, that one is never cancellable).
    /// Lets a test drive `bridge.DisposeAsync()` while a `StreamsThen` step is still "in progress" and
    /// assert the cancellation is absorbed gracefully rather than treated as a stream failure.
    | HangsUntilCancelled

/// `AgentSession` has only a protected constructor — the script itself (not the session) drives
/// what a turn returns, so the only state this subclass carries is an identifying `Nonce`: a fresh
/// one per `CreateSessionCoreAsync` call, or a caller-supplied one when restoring from a serialized
/// element (`DeserializeSessionCoreAsync`) — round-tripping it is how a test tells a restored
/// session apart from a freshly created one.
type ScriptedSession(?nonce: string) =
    inherit AgentSession()
    let nonce = defaultArg nonce (Guid.NewGuid().ToString())
    member _.Nonce: string = nonce

let private toArguments (args: (string * (obj | null)) list) : IDictionary<string, obj | null> =
    let dict = Dictionary<string, obj | null>()
    for name, value in args do
        dict[name] <- value

    dict :> IDictionary<string, obj | null>

/// A one-shot `IAsyncEnumerable<AgentResponseUpdate>` in TWO stages: `immediate` (already-built,
/// synchronous — every `StreamsThen` delta) yields first, one item at a time; ONLY once `immediate`
/// is exhausted does `deferred` ever run (awaited on THAT `MoveNextAsync` call, never earlier) to
/// produce the rest (every `StreamsThen` `final` step's own conversion, or the WHOLE item list for
/// an ordinary layer-1 step). This ordering matters: `deferred` may itself throw (a scripted
/// `Throws` step) or await a real delay (`Delayed`) — running it eagerly, up front, would surface
/// that throw/delay BEFORE any already-available delta ever reached the caller, unlike a real
/// streaming backend (or `MafDurableCoverageTests`'s own probe-confirmed mid-stream-throw shape),
/// where already-produced deltas are always observed first. Mirrors the probe's own proven-out
/// `ArrayAsyncEnumerable`/`ArrayAsyncEnumerator` (`scratchpad/durableprobe/ScriptedChatClient.fs`)
/// for the enumerator shape itself. `advance` is invoked once per item (from EITHER stage),
/// immediately before that item is handed back — the SAME instant a real bridge loop would next
/// read its own clock for a coalescing decision — with each item's own paired `TimeSpan`
/// (`TimeSpan.Zero` for every non-`StreamsThen` item, so a no-op `advance` keeps every existing
/// scripted-agent test byte-identical).
///
/// `deferred` does NOT receive a `CancellationToken` from `GetAsyncEnumerator` — empirically
/// confirmed (against the resolved 1.13.0 binaries) that the token the BRIDGE passes to
/// `.GetAsyncEnumerator(cts.Token)` on the PUBLIC `AIAgent.RunStreamingAsync`'s own returned
/// enumerable never reaches a custom `RunCoreStreamingAsync` override's own returned
/// `IAsyncEnumerable`'s `GetAsyncEnumerator` — the token observed there has
/// `CanBeCanceled = false` regardless of what the caller passed in. The REAL, cancellable token
/// only ever reaches `RunCoreStreamingAsync`'s OWN `ct` parameter (see `HangsUntilCancelled`'s own
/// handling in `RunCoreStreamingAsync`, below, which captures THAT one via closure instead).
type private ScriptedUpdateSequence
    (immediate: (AgentResponseUpdate * TimeSpan) list, deferred: unit -> Task<(AgentResponseUpdate * TimeSpan) list>, advance: TimeSpan -> unit) =
    interface IAsyncEnumerable<AgentResponseUpdate> with
        member _.GetAsyncEnumerator(_ct: CancellationToken) =
            let mutable pending = immediate
            let mutable rest: (AgentResponseUpdate * TimeSpan)[] = [||]
            let mutable restIndex = -1
            let mutable current = Unchecked.defaultof<AgentResponseUpdate> // never read before the first successful MoveNextAsync sets it

            { new IAsyncEnumerator<AgentResponseUpdate> with
                member _.Current = current

                member _.MoveNextAsync() =
                    ValueTask<bool>(
                        task {
                            match pending with
                            | (update, span) :: more ->
                                pending <- more
                                advance span
                                current <- update
                                return true
                            | [] ->
                                if restIndex < 0 then
                                    let! built = deferred ()
                                    rest <- Array.ofList built

                                restIndex <- restIndex + 1

                                if restIndex < rest.Length then
                                    let update, span = rest[restIndex]
                                    advance span
                                    current <- update
                                    return true
                                else
                                    return false
                        }
                    )

                member _.DisposeAsync() = ValueTask.CompletedTask }

/// `steps` is consumed as a queue, one per `RunCoreAsync` call (an initial `StartRun`/incoming
/// message call, and one further call per resumed decision). `onResume` — if supplied — is invoked
/// with `(requestId, approved)` whenever the incoming messages carry a `ToolApprovalResponseContent`,
/// BEFORE the next step is popped, so a test can assert exactly what the bridge resumed with.
/// `failCreateSessionCount` — if supplied — makes the FIRST that-many calls to
/// `CreateSessionCoreAsync` throw (a scripted "session backend unreachable" turn); every call
/// after that succeeds normally. Lets a test simulate a session-creation failure that recovers on
/// retry, independent of anything in `steps` (which only governs `RunCoreAsync`).
/// `advanceClock` — if supplied — is invoked by `RunCoreStreamingAsync` for a `StreamsThen` step,
/// once per scripted delta, immediately before that delta is handed back from the enumerator's own
/// `MoveNextAsync` (mirrors the `let mutable now = ...` / `let clock: Clock = fun () -> now` pattern
/// `MafDurableCoverageTests.fs`/`MafDurableLifecycleTests.fs` already use for expiry tests — a test
/// passes `fun span -> now <- now + span` here, closing over the SAME mutable cell its own
/// `TgBotConfig.WithClock`/`TgWebhookConfig.WithClock` reads). Omitted (the default, a no-op),
/// every non-streaming test and every streaming test that never uses `StreamsThen` is unaffected.
type ScriptedAgent(steps: ScriptedStep list, ?onResume: string * bool -> unit, ?failCreateSessionCount: int, ?advanceClock: TimeSpan -> unit) =
    inherit AIAgent()

    let queue = Queue<ScriptedStep>(steps)
    let onResume = defaultArg onResume (fun _ -> ())
    let failCreateSessionCount = defaultArg failCreateSessionCount 0
    let advanceClock = defaultArg advanceClock ignore
    let runCount = ref 0
    let createSessionAttempts = ref 0

    /// Produces one `AgentResponse` for `step`, recursing through `Delayed`'s wrapped step after
    /// awaiting its delay — kept separate from `RunCoreAsync` itself so `Delayed` can wrap ANY
    /// other step (including, in principle, another `Delayed`) without duplicating the match.
    /// `StreamsThen` has no non-streaming shape of its own (`RunCoreStreamingAsync`, below, is the
    /// only caller that ever pops one off the queue) — reaching this branch means the scripted queue
    /// and `bot.Streaming`/the turn path that actually ran are out of step with each other, a
    /// scripting mistake in the TEST itself, not a runtime condition to handle gracefully.
    let rec runStep (step: ScriptedStep) : Task<AgentResponse> =
        task {
            match step with
            | RepliesWith text -> return AgentResponse(ChatMessage(ChatRole.Assistant, text))
            | EndsEmpty -> return AgentResponse(ChatMessage(ChatRole.Assistant, ""))
            | Throws error -> return raise error
            | PausesFor(requestId, toolName, args) ->
                let call = FunctionCallContent("call-1", toolName, toArguments args)
                let request = ToolApprovalRequestContent(requestId, call)
                let contents = ResizeArray<AIContent>[ request :> AIContent ]
                return AgentResponse(ChatMessage(ChatRole.Assistant, contents))
            | PausesForNonFunctionCall(requestId, callId) ->
                let call = CodeInterpreterToolCallContent(callId)
                let request = ToolApprovalRequestContent(requestId, call)
                let contents = ResizeArray<AIContent>[ request :> AIContent ]
                return AgentResponse(ChatMessage(ChatRole.Assistant, contents))
            | PausesForMany requests ->
                let contents =
                    ResizeArray<AIContent>
                        [ for requestId, toolName, args in requests ->
                              let call = FunctionCallContent($"call-{requestId}", toolName, toArguments args)
                              ToolApprovalRequestContent(requestId, call) :> AIContent ]

                return AgentResponse(ChatMessage(ChatRole.Assistant, contents))
            | Delayed(delay, inner) ->
                do! Task.Delay delay
                return! runStep inner
            | StreamsThen(_, _) ->
                return failwith "StreamsThen is a streaming-only ScriptedStep — RunCoreAsync (the non-streaming path) popped one off the queue; check bot.Streaming/the queue's own ordering for this test"
            | HangsUntilCancelled ->
                return failwith "HangsUntilCancelled is a streaming-only ScriptedStep — RunCoreAsync (the non-streaming path) popped one off the queue; check bot.Streaming/the queue's own ordering for this test"
        }

    /// How many turns (`RunCoreAsync` calls) this agent has actually processed — lets a test assert
    /// no extra resume happened beyond what it expected.
    member _.RunCount: int = runCount.Value

    /// The `Nonce` of every `ScriptedSession` this agent's `RunCoreAsync` has actually been called
    /// with, in call order — lets a test prove WHICH live session backed a given turn, e.g. that a
    /// resume ran on the SAME session object a prior turn started on, rather than a fresh,
    /// same-shaped stand-in.
    member val SeenSessionNonces = ResizeArray<string>() with get

    override this.RunCoreAsync
        (
            messages: ChatMessage seq,
            session: AgentSession,
            _options: AgentRunOptions,
            _ct: CancellationToken
        ) : Task<AgentResponse> =
        task {
            runCount.Value <- runCount.Value + 1

            match box session with
            | :? ScriptedSession as s -> this.SeenSessionNonces.Add s.Nonce
            | _ -> ()

            for message in messages do
                for content in message.Contents do
                    match content with
                    | :? ToolApprovalResponseContent as response -> onResume (response.RequestId, response.Approved)
                    | _ -> ()

            if queue.Count = 0 then
                return AgentResponse(ChatMessage(ChatRole.Assistant, ""))
            else
                return! runStep (queue.Dequeue())
        }

    override _.CreateSessionCoreAsync(_ct: CancellationToken) : ValueTask<AgentSession> =
        createSessionAttempts.Value <- createSessionAttempts.Value + 1

        if createSessionAttempts.Value <= failCreateSessionCount then
            ValueTask.FromException<AgentSession>(InvalidOperationException "scripted CreateSession failure")
        else
            ValueTask<AgentSession>(ScriptedSession() :> AgentSession)

    override _.SerializeSessionCoreAsync
        (session: AgentSession, _options: JsonSerializerOptions, _ct: CancellationToken)
        : ValueTask<JsonElement> =
        let scripted = session :?> ScriptedSession
        let payload = Dictionary<string, string>()
        payload["nonce"] <- scripted.Nonce
        ValueTask<JsonElement>(JsonSerializer.SerializeToElement payload)

    /// Reads back the `"nonce"` string property `SerializeSessionCoreAsync` wrote. Any other shape
    /// — missing property, wrong `ValueKind`, or a `null` string value — is treated as a corrupt or
    /// foreign persisted session and throws, rather than fabricating a fresh session silently.
    override _.DeserializeSessionCoreAsync
        (element: JsonElement, _options: JsonSerializerOptions, _ct: CancellationToken)
        : ValueTask<AgentSession> =
        let nonce =
            match element.TryGetProperty "nonce" with
            | true, prop when prop.ValueKind = JsonValueKind.String -> Option.ofObj (prop.GetString())
            | _ -> None

        match nonce with
        | Some nonce -> ValueTask<AgentSession>(ScriptedSession(nonce) :> AgentSession)
        | None -> raise (InvalidOperationException "unrecognized scripted session shape")

    /// Streaming counterpart to `RunCoreAsync` — same bookkeeping (run count, session nonce,
    /// `onResume` scan) and the SAME "one call pops one step off the queue" contract, so a streaming
    /// call still counts as one turn against it. This method's OWN body — including that bookkeeping
    /// — only starts running once the caller actually enumerates the result (`AIAgent`'s own public
    /// `RunStreamingAsync` wraps this override inside ITS OWN lazy async-iterator method), so a test
    /// that wants to assert `RunCount`/`SeenSessionNonces` for this turn must await at least the
    /// first `MoveNextAsync` first — calling `agent.RunStreamingAsync(...)` alone does not run it,
    /// exactly like a real streaming backend would not open a connection before being asked to.
    override this.RunCoreStreamingAsync
        (
            messages: ChatMessage seq,
            session: AgentSession,
            _options: AgentRunOptions,
            ct: CancellationToken
        ) : IAsyncEnumerable<AgentResponseUpdate> =
        runCount.Value <- runCount.Value + 1

        match box session with
        | :? ScriptedSession as s -> this.SeenSessionNonces.Add s.Nonce
        | _ -> ()

        for message in messages do
            for content in message.Contents do
                match content with
                | :? ToolApprovalResponseContent as response -> onResume (response.RequestId, response.Approved)
                | _ -> ()

        // Layer 1 (every ScriptedStep this leaf already knows how to run): the SAME response-shape
        // logic `runStep` already builds for the non-streaming path, converted into a one-shot
        // update batch via `AgentResponse.ToAgentResponseUpdates()`, each paired with `TimeSpan.Zero`
        // (nothing to advance the clock by).
        let layerOne (step: ScriptedStep) : Task<(AgentResponseUpdate * TimeSpan) list> =
            task {
                let! response = runStep step
                return [ for update in response.ToAgentResponseUpdates() -> update, TimeSpan.Zero ]
            }

        // `immediate` (already-built, synchronous — nothing here can throw or await) yields BEFORE
        // `deferred` ever runs — see `ScriptedUpdateSequence`'s own doc comment for why this two-stage
        // split matters for a `StreamsThen` whose `final` step is `Throws`/`Delayed`.
        let immediate, deferred =
            if queue.Count = 0 then
                [], fun () -> layerOne EndsEmpty
            else
                match queue.Dequeue() with
                | StreamsThen(deltas, EndsEmpty) ->
                    // Special-cased, unlike every OTHER `final` step: `AgentResponse(ChatMessage(_,
                    // "")).ToAgentResponseUpdates()` always yields exactly ONE update (an empty-text
                    // one — confirmed against the resolved 1.13.0 binaries, never zero), so routing
                    // `EndsEmpty` through the SAME `layerOne` conversion every other step uses would
                    // hand the caller a PHANTOM trailing update after `deltas` are exhausted — never
                    // a genuinely silent `MoveNextAsync() = false`. Harmless for every scripted step
                    // that leaves a LIVE message behind (the phantom's own empty delta is a no-op
                    // against an unchanged `ReplyCoalescer`, so it never triggers a further edit) —
                    // but a bridge turn whose live message VANISHED (`EditNotFound`) mid-stream reads
                    // "one more update, any update, even an empty one" as its own cue to send a FRESH
                    // recovery message immediately (`Bridge.fs`'s own `None, _ when not
                    // (String.IsNullOrWhiteSpace coalescer.RunningText)` arm doesn't gate on
                    // `IsDue`), so the phantom would mask exactly the "nothing further ever arrives
                    // after a vanish" scenario a test needs to reach. `EndsEmpty` as a `StreamsThen`
                    // final step means "the stream has NOTHING more to give" — `deferred` returning an
                    // empty list directly, with no conversion at all, is what actually models that.
                    let deltaItems =
                        [ for delta, advanceBy in deltas -> AgentResponseUpdate(Nullable ChatRole.Assistant, delta), advanceBy ]

                    deltaItems, fun () -> Task.FromResult []
                | StreamsThen(deltas, HangsUntilCancelled) ->
                    let deltaItems =
                        [ for delta, advanceBy in deltas -> AgentResponseUpdate(Nullable ChatRole.Assistant, delta), advanceBy ]

                    // Blocks for real, on `ct` — `RunCoreStreamingAsync`'s OWN parameter above,
                    // captured here by closure. NOT a token threaded through
                    // `ScriptedUpdateSequence`/`GetAsyncEnumerator`: empirically confirmed (see that
                    // type's own doc comment) that the token reaching `GetAsyncEnumerator` is never
                    // the real, cancellable one — only THIS parameter is. `Task.Delay` with an
                    // infinite timeout only ever completes by THROWING once `ct` is cancelled, the
                    // same shape a genuine cancelled streaming call would raise.
                    let hang () : Task<(AgentResponseUpdate * TimeSpan) list> =
                        task {
                            do! Task.Delay(Timeout.InfiniteTimeSpan, ct)
                            return failwith "unreachable: an infinite-timeout Task.Delay only ever completes by throwing on cancellation"
                        }

                    deltaItems, hang
                | StreamsThen(deltas, final) ->
                    let deltaItems =
                        [ for delta, advanceBy in deltas -> AgentResponseUpdate(Nullable ChatRole.Assistant, delta), advanceBy ]

                    deltaItems, fun () -> layerOne final
                | other -> [], fun () -> layerOne other

        ScriptedUpdateSequence(immediate, deferred, advanceClock) :> IAsyncEnumerable<AgentResponseUpdate>
