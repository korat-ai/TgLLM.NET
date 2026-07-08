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
    /// The agent's turn ends with no text and no approval — an empty turn.
    | EndsEmpty
    /// The agent throws while producing this turn (or resuming into it).
    | Throws of error: exn
    /// Delays this long BEFORE producing `step` — simulates a slow model/tool-backend turn (e.g.
    /// a resume that takes longer than the deferred-ack watchdog budget), so a test can assert the
    /// tap is still acked promptly while the continuation is still running.
    | Delayed of delay: TimeSpan * step: ScriptedStep

/// `AgentSession` has only a protected constructor — a trivial subclass with no state of its own
/// is the whole seam; the script itself (not the session) drives what a turn returns.
type private ScriptedSession() =
    inherit AgentSession()

let private toArguments (args: (string * (obj | null)) list) : IDictionary<string, obj | null> =
    let dict = Dictionary<string, obj | null>()
    for name, value in args do
        dict[name] <- value

    dict :> IDictionary<string, obj | null>

/// `steps` is consumed as a queue, one per `RunCoreAsync` call (an initial `StartRun`/incoming
/// message call, and one further call per resumed decision). `onResume` — if supplied — is invoked
/// with `(requestId, approved)` whenever the incoming messages carry a `ToolApprovalResponseContent`,
/// BEFORE the next step is popped, so a test can assert exactly what the bridge resumed with.
type ScriptedAgent(steps: ScriptedStep list, ?onResume: string * bool -> unit) =
    inherit AIAgent()

    let queue = Queue<ScriptedStep>(steps)
    let onResume = defaultArg onResume (fun _ -> ())
    let runCount = ref 0

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
            | Delayed(delay, inner) ->
                do! Task.Delay delay
                return! runStep inner
        }

    /// How many turns (`RunCoreAsync` calls) this agent has actually processed — lets a test assert
    /// no extra resume happened beyond what it expected.
    member _.RunCount: int = runCount.Value

    override _.RunCoreAsync
        (
            messages: ChatMessage seq,
            _session: AgentSession,
            _options: AgentRunOptions,
            _ct: CancellationToken
        ) : Task<AgentResponse> =
        task {
            runCount.Value <- runCount.Value + 1

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
        ValueTask<AgentSession>(ScriptedSession() :> AgentSession)

    override _.SerializeSessionCoreAsync
        (_session: AgentSession, _options: JsonSerializerOptions, _ct: CancellationToken)
        : ValueTask<JsonElement> =
        raise (NotSupportedException "ScriptedAgent does not support session serialization — not needed by the bridge this release.")

    override _.DeserializeSessionCoreAsync
        (_element: JsonElement, _options: JsonSerializerOptions, _ct: CancellationToken)
        : ValueTask<AgentSession> =
        raise (NotSupportedException "ScriptedAgent does not support session deserialization — not needed by the bridge this release.")

    override _.RunCoreStreamingAsync
        (
            _messages: ChatMessage seq,
            _session: AgentSession,
            _options: AgentRunOptions,
            _ct: CancellationToken
        ) : IAsyncEnumerable<AgentResponseUpdate> =
        raise (NotSupportedException "ScriptedAgent does not support streaming — the bridge drives one non-streaming turn at a time.")
