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
// Also turns on live streaming (TgBotConfig.WithStreaming()) — both turns below now arrive as
// several deltas, paced by a real Task.Delay (see DemoAgent.StreamDeltasAsync below) rather than
// one shot, so the reply visibly edits in place in Telegram instead of appearing all at once. The
// approval turn keeps its narration on the SAME live message once the buttons are added — see that
// turn's own comment below for why this differs from the non-streaming path.
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
var config = TgBotConfig.create(botToken)
    .WithTools(tools)
    .WithBindingStore(bindingStore)
    .WithSessionStore(sessionStore)
    .WithStreaming(); // live edit-in-place at the default 1.5s cadence — see DemoAgent.StreamDeltasAsync below

await using var bridge = await MafTelegramBridge.StartPollingAsync(config, new DemoAgent());

// A text turn: the agent answers a plain question, streamed live — watch the message edit in place
// in Telegram as DemoAgent.RunCoreStreamingAsync's deltas arrive, rather than appearing all at once.
await bridge.StartRunAsync(chatId, "What can you do?");

// The approval loop: the agent's next turn streams a little narration, then pauses on send_email.
// The bridge sends ONE owner-scoped [Approve][Reject] message that KEEPS the narration already
// shown; tapping it resumes the agent and edits that same message in place to the outcome.
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

    // The pacing between scripted deltas below — comfortably straddles the bot's own default 1.5s
    // coalescing interval (TgBotConfig.WithStreaming()'s own default,
    // TgLLM.Maf.StreamingDefaults.defaultCoalesceInterval) so a live edit is actually observable
    // mid-stream, not just the initial send and the final flush.
    private static readonly TimeSpan Pace = TimeSpan.FromMilliseconds(500);

    // Streaming counterpart to RunCoreAsync above — the SAME scripted turns (Turn, shared via the
    // same DemoSession, still drives which one runs), but each reply arrives as several deltas
    // paced by Pace rather than in one shot, so bot.Streaming's live edit-in-place is actually
    // visible against Telegram: the FIRST delta sends a new message immediately (never gated), and
    // later deltas edit that SAME message in place once the coalescing interval clears. A reply
    // long enough to exceed Telegram's 4096-character per-message cap would instead spill into a
    // NEW message at a whitespace-preferred boundary and keep live-editing there
    // (TgLLM.Maf.MessageSplitting.split, TgLLM.Maf.ReplyCoalescer) — neither scripted reply here is
    // long enough to trigger that, but the mechanism driving it is the same one this override feeds.
    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
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

        return demoSession.Turn switch
        {
            1 => StreamDeltasAsync(new (AgentResponseUpdate Update, TimeSpan Delay)[]
            {
                (TextDelta("I can draft "), Pace),
                (TextDelta("and send emails "), Pace),
                (TextDelta("for you — "), Pace),
                (TextDelta("coordinating drafts, "), Pace),
                (TextDelta("checking recipients, "), Pace),
                (TextDelta("and confirming before anything goes out."), Pace),
            }),
            // Narration streams first, then this SAME turn pauses for approval. The bridge keeps
            // this narration on the live message and ADDS the Approve/Reject buttons to it — unlike
            // the non-streaming path, whose own preamble (response.Text alongside a detected
            // approval) is dropped rather than sent as a separate message first
            // (processInitialResponse's own doc comment in src/TgLLM.Maf/Bridge.fs).
            2 => StreamDeltasAsync(new (AgentResponseUpdate Update, TimeSpan Delay)[]
            {
                (TextDelta("Drafting that email now"), Pace),
                (TextDelta("..."), Pace),
                (ApprovalDelta(), Pace),
            }),
            _ => StreamDeltasAsync(new (AgentResponseUpdate Update, TimeSpan Delay)[]
            {
                (TextDelta("Sent to alice@example.com."), TimeSpan.Zero),
            }),
        };
    }

    // One streamed text delta, carrying no other content — AgentResponseUpdate's own
    // (role, content: string) constructor, the same one used for a normal assistant chunk.
    private static AgentResponseUpdate TextDelta(string text) => new(ChatRole.Assistant, text);

    private static AgentResponseUpdate ApprovalDelta()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["toAddr"] = "alice@example.com",
            ["body"] = "The deploy is done.",
        };
        var call = new FunctionCallContent("call-1", "send_email", arguments);
        var request = new ToolApprovalRequestContent("req-1", call);
        return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent> { request });
    }

    // Yields each (update, delay) pair in order, awaiting a REAL Task.Delay immediately before
    // handing each one back — a real bot token/chat drives this example, so the delay is real
    // wall-clock time, which is what makes bot.Streaming's live edit-in-place visible when you run
    // this against Telegram. C#'s async iterator (yield return inside async IAsyncEnumerable<T>)
    // needs no hand-rolled enumerator type, unlike this repo's F# examples/tests for the same shape.
    private static async IAsyncEnumerable<AgentResponseUpdate> StreamDeltasAsync(
        (AgentResponseUpdate Update, TimeSpan Delay)[] items)
    {
        foreach (var (update, delay) in items)
        {
            await Task.Delay(delay);
            yield return update;
        }
    }
}
