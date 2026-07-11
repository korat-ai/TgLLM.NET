// Example: a Microsoft Agent Framework (MAF) agent whose tool call needs a human's approval —
// approved or rejected with Telegram buttons, with no button/callback plumbing written here, via
// the C# surface (MafTelegramBridge). DemoAgent below is SCRIPTED (offline, deterministic) rather
// than backed by a live chat model, so this example needs no API key: it emits the exact same
// ToolApprovalRequestContent a real MAF tool-approval loop produces — built from
// AIFunctionFactory.Create/ApprovalRequiredAIFunction wrapping a chat-model-backed AIAgent, per
// docs/quickstart.md's "Author the agent" step — just without a model deciding when to call it.
//
// Also wires a DURABLE session store (TgLLM.Persistence.FileSessionStore) and a DURABLE binding
// store (TgLLM.Persistence.FileBindingStore) via TgBotConfig.WithSessionStore/WithBindingStore —
// both are required for a pending approval to survive a process restart: the tap's button routes
// via the binding store, and resuming the agent rehydrates its conversation and still-pending
// approvals via the session store. With both wired, an approval message shown BEFORE a process
// restart is still honored AFTER it: the tap resumes the agent and edits that same message in
// place, exactly as if the process had never gone down.
//
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
using TgLLM.Persistence;

static string RequireEnv(string name) =>
    Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"environment variable {name} is required");

var botToken = RequireEnv("BOT_TOKEN");
var chatId = long.Parse(RequireEnv("CHAT_ID"));

// MafTelegramBridge.StartPollingAsync requires a Tool Router (.WithTools) — it registers its own
// internal maf-approve/maf-reject tools into this SAME registry.
var tools = ToolRegistry.create();
var bindingStore = FileBindingStore.openAt("bindings.json");
var sessionStore = FileSessionStore.OpenAt("sessions.json");
var config = TgBotConfig.create(botToken).WithTools(tools).WithBindingStore(bindingStore).WithSessionStore(sessionStore);

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
    // AgentSession has only a protected constructor — Turn is the ONLY state this subclass carries:
    // which scripted step DemoAgent.RunCoreAsync is on for THIS chat's conversation. Kept on the
    // session (not a field on DemoAgent itself, unlike an earlier version of this example)
    // precisely so it round-trips through SerializeSessionCoreAsync/DeserializeSessionCoreAsync
    // below — a real AIAgent's own conversation state lives the same way, on the session, which is
    // exactly what a durable ISessionStore persists and restores.
    public int Turn { get; set; }
}

sealed class DemoAgent : AIAgent
{
    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        CancellationToken cancellationToken)
    {
        if (session is not DemoSession demoSession)
        {
            throw new InvalidOperationException(
                "DemoAgent requires a DemoSession — CreateSessionCoreAsync/DeserializeSessionCoreAsync always produce one.");
        }

        demoSession.Turn++;

        switch (demoSession.Turn)
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

    // Real (if trivial) session serialization — required for the durable session store wired in
    // above to have anything meaningful to persist. {"turn": N} is the whole wire shape.
    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session, JsonSerializerOptions? jsonSerializerOptions, CancellationToken cancellationToken) =>
        new(JsonSerializer.SerializeToElement(new { turn = ((DemoSession)session).Turn }));

    // The read counterpart to SerializeSessionCoreAsync — any other shape (missing property, wrong
    // ValueKind) is a corrupt or foreign record, so it throws rather than fabricating a fresh
    // session silently; Bridge.fs's own restoreOrCreate catches exactly this and falls back to a
    // brand-new session, reporting the failure via IMafSessionObserver.OnSessionRestoreFailed.
    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedSession, JsonSerializerOptions? jsonSerializerOptions, CancellationToken cancellationToken)
    {
        if (serializedSession.TryGetProperty("turn", out var turnProperty) && turnProperty.ValueKind == JsonValueKind.Number)
        {
            return new(new DemoSession { Turn = turnProperty.GetInt32() });
        }

        throw new InvalidOperationException("unrecognized demo session shape");
    }

    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("DemoAgent replies in one turn; no streaming in this example.");
}
