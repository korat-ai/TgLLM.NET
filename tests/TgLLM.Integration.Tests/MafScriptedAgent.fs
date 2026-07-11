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

/// `steps` is consumed as a queue, one per `RunCoreAsync` call (an initial `StartRun`/incoming
/// message call, and one further call per resumed decision). `onResume` — if supplied — is invoked
/// with `(requestId, approved)` whenever the incoming messages carry a `ToolApprovalResponseContent`,
/// BEFORE the next step is popped, so a test can assert exactly what the bridge resumed with.
/// `failCreateSessionCount` — if supplied — makes the FIRST that-many calls to
/// `CreateSessionCoreAsync` throw (a scripted "session backend unreachable" turn); every call
/// after that succeeds normally. Lets a test simulate a session-creation failure that recovers on
/// retry, independent of anything in `steps` (which only governs `RunCoreAsync`).
type ScriptedAgent(steps: ScriptedStep list, ?onResume: string * bool -> unit, ?failCreateSessionCount: int) =
    inherit AIAgent()

    let queue = Queue<ScriptedStep>(steps)
    let onResume = defaultArg onResume (fun _ -> ())
    let failCreateSessionCount = defaultArg failCreateSessionCount 0
    let runCount = ref 0
    let createSessionAttempts = ref 0

    /// Produces one `AgentResponse` for `step`, recursing through `Delayed`'s wrapped step after
    /// awaiting its delay — kept separate from `RunCoreAsync` itself so `Delayed` can wrap ANY
    /// other step (including, in principle, another `Delayed`) without duplicating the match.
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

    override _.RunCoreStreamingAsync
        (
            _messages: ChatMessage seq,
            _session: AgentSession,
            _options: AgentRunOptions,
            _ct: CancellationToken
        ) : IAsyncEnumerable<AgentResponseUpdate> =
        raise (NotSupportedException "ScriptedAgent does not support streaming — the bridge drives one non-streaming turn at a time.")
