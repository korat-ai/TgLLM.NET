// Example: a Microsoft Agent Framework (MAF) agent whose tool call needs a human's approval —
// approved or rejected with Telegram buttons, with no button/callback plumbing written here, via
// the C# surface (MafTelegramBridge). DemoAgent below is SCRIPTED (offline, deterministic) rather
// than backed by a live chat model, so this example needs no API key: it emits the exact same
// ToolApprovalRequestContent a real MAF tool-approval loop produces — built from
// AIFunctionFactory.Create/ApprovalRequiredAIFunction wrapping a chat-model-backed AIAgent, per
// docs/quickstart.md's "Author the agent" step — just without a model deciding when to call it.
// Set BOT_TOKEN and CHAT_ID (a private chat), then `dotnet run`.
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TgLLM.FSharp;
using TgLLM.Maf;

static string RequireEnv(string name) =>
    Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"environment variable {name} is required");

var botToken = RequireEnv("BOT_TOKEN");
var chatId = long.Parse(RequireEnv("CHAT_ID"));

// MafTelegramBridge.StartPollingAsync requires a Tool Router (.WithTools) — it registers its own
// internal maf-approve/maf-reject tools into this SAME registry.
var tools = ToolRegistry.create();
var config = TgBotConfig.create(botToken).WithTools(tools);

await using var bridge = await MafTelegramBridge.StartPollingAsync(config, new DemoAgent());

// A text turn: the agent answers a plain question with plain text — no buttons involved.
await bridge.StartRunAsync(chatId, "What can you do?");

// The approval loop: the agent's next turn pauses on send_email. The bridge sends ONE owner-scoped
// [Approve][Reject] message; tapping it resumes the agent and edits that same message in place to
// the outcome.
await bridge.StartRunAsync(chatId, "Email alice@example.com that the deploy is done.");

Console.WriteLine("MAF bridge running (long polling). Approve/Reject the pending message in Telegram. Ctrl+C to stop.");
await Task.Delay(Timeout.InfiniteTimeSpan);

// One text reply, then one tool-approval pause, then (once resumed) a confirmation reply — enough
// to walk both an ordinary text turn and the approval loop in a single run, with no live model.
sealed class DemoSession : AgentSession
{
    // AgentSession has only a protected constructor — a trivial subclass with no state of its own
    // is the whole seam; DemoAgent below (not the session) drives what a turn returns.
}

sealed class DemoAgent : AIAgent
{
    private int _turn;

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        CancellationToken cancellationToken)
    {
        _turn++;

        switch (_turn)
        {
            case 1:
                return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, "I can draft and send emails for you.")));
            case 2:
                var arguments = new Dictionary<string, object?>
                {
                    ["toAddr"] = "alice@example.com",
                    ["body"] = "The deploy is done.",
                };
                var call = new FunctionCallContent("call-1", "send_email", arguments);
                var request = new ToolApprovalRequestContent("req-1", call);
                return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, new List<AIContent> { request })));
            default:
                return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, "Sent to alice@example.com.")));
        }
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
        new(new DemoSession());

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session, JsonSerializerOptions? jsonSerializerOptions, CancellationToken cancellationToken) =>
        throw new NotSupportedException("DemoAgent does not persist sessions across a restart — this example only.");

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedSession, JsonSerializerOptions? jsonSerializerOptions, CancellationToken cancellationToken) =>
        throw new NotSupportedException("DemoAgent does not persist sessions across a restart — this example only.");

    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("DemoAgent replies in one turn; no streaming in this example.");
}
