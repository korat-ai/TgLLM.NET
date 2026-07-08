/// Example: a Microsoft Agent Framework (MAF) agent whose tool call needs a human's approval —
/// approved or rejected with Telegram buttons, with no button/callback plumbing written here.
/// `DemoAgent` below is SCRIPTED (offline, deterministic) rather than backed by a live chat model,
/// so this example needs no API key: it emits the exact same `ToolApprovalRequestContent` a real
/// MAF tool-approval loop produces — built from `AIFunctionFactory.Create`/`ApprovalRequiredAIFunction`
/// wrapping a chat-model-backed `AIAgent`, per `docs/quickstart.md`'s "Author the agent" step — just
/// without a model deciding when to call it.
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

let private requireEnv (name: string) : string =
    match Environment.GetEnvironmentVariable name |> Option.ofObj with
    | Some value -> value
    | None -> failwith $"environment variable {name} is required"

/// `AgentSession` has only a protected constructor — a trivial subclass with no state of its own is
/// the whole seam; `DemoAgent` below (not the session) drives what a turn returns.
type private DemoSession() =
    inherit AgentSession()

/// One text reply, then one tool-approval pause, then (once resumed) a confirmation reply — enough
/// to walk both an ordinary text turn and the approval loop in a single run, with no live model.
type private DemoAgent() =
    inherit AIAgent()

    let mutable turn = 0

    override _.RunCoreAsync
        (
            _messages: ChatMessage seq,
            _session: AgentSession,
            _options: AgentRunOptions,
            _ct: CancellationToken
        ) : Task<AgentResponse> =
        turn <- turn + 1

        match turn with
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

    override _.SerializeSessionCoreAsync
        (_session: AgentSession, _options: JsonSerializerOptions, _ct: CancellationToken)
        : ValueTask<JsonElement> =
        raise (NotSupportedException "DemoAgent does not persist sessions across a restart — this example only.")

    override _.DeserializeSessionCoreAsync
        (_element: JsonElement, _options: JsonSerializerOptions, _ct: CancellationToken)
        : ValueTask<AgentSession> =
        raise (NotSupportedException "DemoAgent does not persist sessions across a restart — this example only.")

    override _.RunCoreStreamingAsync
        (
            _messages: ChatMessage seq,
            _session: AgentSession,
            _options: AgentRunOptions,
            _ct: CancellationToken
        ) : Collections.Generic.IAsyncEnumerable<AgentResponseUpdate> =
        raise (NotSupportedException "DemoAgent replies in one turn; no streaming in this example.")

[<EntryPoint>]
let main _ =
    task {
        let botToken = requireEnv "BOT_TOKEN"
        let chat: ChatId = UMX.tag<chatId> (int64 (requireEnv "CHAT_ID"))

        // Maf.startPolling requires a Tool Router (.WithTools) — it registers its own internal
        // maf-approve/maf-reject tools into this SAME registry.
        let tools = ToolRegistry.create ()
        use! bridge = Maf.startPolling ((TgBotConfig.create botToken).WithTools tools) (DemoAgent())

        // A text turn: the agent answers a plain question with plain text — no buttons involved.
        do! bridge.StartRun(chat, "What can you do?")

        // The approval loop: the agent's next turn pauses on `send_email`. The bridge sends ONE
        // owner-scoped [Approve][Reject] message; tapping it resumes the agent and edits that same
        // message in place to the outcome.
        do! bridge.StartRun(chat, "Email alice@example.com that the deploy is done.")

        printfn "MAF bridge running (long polling). Approve/Reject the pending message in Telegram. Ctrl+C to stop."
        do! Task.Delay Timeout.InfiniteTimeSpan
        return 0
    }
    |> fun run -> run.GetAwaiter().GetResult()
