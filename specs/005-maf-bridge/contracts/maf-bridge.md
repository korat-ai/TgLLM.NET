# Public Surface Delta: MAF Bridge

The additive public API for slice 005. Slice-1/2/3/4 signatures are unchanged (FR-019). MAF types
(`AIAgent`, `AgentSession`, `ToolApprovalRequestContent`, `AIFunction`, …) appear ONLY in `TgLLM.Maf`
signatures — never in Core or either façade (FR-018). Indicative signatures — finalized via TDD.

## Core seam (`TgLLM.Core` — additive, MAF-agnostic)

```fsharp
/// The message-side sibling of ButtonPress, produced by the SAME shared transport mapping both
/// long polling and webhooks already use — neither transport gains code of its own.
type IncomingMessage =
    { Chat: ChatId; Sender: EndUser; MessageId: MessageId; Text: string }

type AgentEvent =
    | ButtonPressed of ButtonPress
    | AckOnly of queryId: CallbackQueryId
    | MessageReceived of IncomingMessage          // NEW case; existing cases untouched

/// Runs on the message's chat's dispatcher lane — serialized with that chat's button presses.
type MessageHandler = IncomingMessage -> CancellationToken -> Task

/// NEW small interface (IHookObserver is host-implementable, so it cannot grow members additively).
type IMessageObserver =
    abstract OnMessageFailed: message: IncomingMessage * error: exn -> unit

// UpdateProcessor gains two OPTIONAL constructor parameters; every existing call site compiles
// unchanged and behaves byte-identically when they are omitted:
//   ?onMessage: MessageHandler, ?messageObserver: IMessageObserver
```

## Façade deltas (MAF-agnostic on both sides)

```fsharp
// TgLLM.FSharp — CommonConfig gains one field + the lockstep fluent methods on BOTH configs:
type CommonConfig = { (* existing fields *) ; OnMessage: MessageHandler option }
// TgBotConfig.WithOnMessage : MessageHandler -> TgBotConfig
// TgWebhookConfig.WithOnMessage : MessageHandler -> TgWebhookConfig
```

```csharp
// TgLLM.CSharp — TelegramAgentOptions gains one property (BCL delegate + DTO, no F# idioms, no MAF):
public sealed class IncomingMessageInfo
{
    public long ChatId { get; }
    public long SenderId { get; }
    public string SenderFirstName { get; }
    public string? SenderUsername { get; }
    public long MessageId { get; }
    public string Text { get; }
}

public sealed class TelegramAgentOptions
{
    // existing members unchanged
    public Func<IncomingMessageInfo, CancellationToken, Task>? OnMessage { get; set; }
}
```

## The leaf: `TgLLM.Maf` (F# surface)

```fsharp
/// Formatter override for the approval message; the default renders tool name + arguments as plain
/// text with fixed Approve/Reject labels. A formatter renames/rewrites; it cannot add decisions.
type ApprovalPrompt   = { Tool: string; Arguments: (string * string) list; Chat: ChatId }
type ApprovalRender   = { Body: string; ApproveLabel: string; RejectLabel: string }
type ApprovalFormatter = ApprovalPrompt -> ApprovalRender

/// The bridge's observability seam — every surfaced condition (stale/malformed/failed decision,
/// empty turn, invalid output, projection problem) reaches ONE observer. Noop default; the start
/// functions bridge it to the bot's own logger when one is wired.
type IMafObserver =
    abstract OnStaleDecision: descriptor: ApprovalDescriptor -> unit
    abstract OnMalformedDecision: raw: string -> unit
    abstract OnResumeFailed: descriptor: ApprovalDescriptor * error: exn -> unit
    abstract OnEmptyTurn: chat: ChatId -> unit
    abstract OnInvalidOutput: chat: ChatId * error: MafError -> unit
    abstract OnProjectionProblem: problem: ProjectionProblem -> unit

[<NoComparison; NoEquality>]
type MafBridgeOptions =
    { Formatter: ApprovalFormatter voption
      Observer: IMafObserver voption
      DefaultOwner: OwnerScope voption
      ApprovalExpiry: TimeSpan voption }

module Maf =
    /// Builds the bot from `config` (injecting the bridge's message handler — the handler must exist
    /// before the bot starts consuming updates, hence wrapping startup rather than attaching to a
    /// running bot), registers the internal maf-approve/maf-reject tools into the bot's Tool Router
    /// (requires .WithTools, same precondition and double-attach guard as A2ui.renderer), and returns
    /// the live bridge. `startPollingWith` takes MafBridgeOptions; the plain variant is zero-config.
    val startPolling:     config: TgBotConfig -> agent: AIAgent -> MafBridge
    val startPollingWith: options: MafBridgeOptions -> config: TgBotConfig -> agent: AIAgent -> MafBridge
    val startWebhook:     config: TgWebhookConfig -> agent: AIAgent -> MafBridge   // + …With variant

[<Sealed>]
type MafBridge =
    /// The bot the bridge built — the host uses it exactly as a hand-built TgBot (send, tools, …).
    member Bot: TgBot
    /// Host-initiated agent run in a chat. Serialized on the chat's lane with taps and incoming
    /// messages. `owner` overrides the approval owner scope for this run; omitted, the bridge infers
    /// the private-chat peer, else Anyone (message-initiated turns always default to their sender).
    member StartRun: chat: ChatId * prompt: string * ?owner: OwnerScope -> Task
    interface IAsyncDisposable   // disposes the bot it started

/// One-call tool projection — standalone (usable without any bridge or bot).
module MafTools =
    /// Registers every projectable AIFunction (name/description/JsonSchema -> registry metadata;
    /// handler invokes the function). Per-tool problems are collected AND mirrored to the observer;
    /// valid siblings still register.
    val project: registry: ToolRegistry -> functions: AIFunction seq -> ProjectionReport
```

## The leaf: `TgLLM.Maf` (C# surface — same assembly, C#-idiomatic)

```csharp
// Delegates are BCL; options are a mutable class; no FSharpFunc/FSharpOption/FSharpValueOption
// anywhere on this surface (Principle II canary applies to the leaf).
public delegate ApprovalRenderInfo ApprovalFormatter(ApprovalPromptInfo prompt);
public sealed record ApprovalPromptInfo(string Tool, IReadOnlyList<KeyValuePair<string, string>> Arguments, long ChatId);
public sealed record ApprovalRenderInfo(string Body, string ApproveLabel = "Approve", string RejectLabel = "Reject");

public sealed class MafBridgeSettings
{
    public ApprovalFormatter? Formatter { get; set; }
    public OwnerScope? DefaultOwner { get; set; }          // Owner.Anyone / Owner.User(id) helpers apply
    public TimeSpan? ApprovalExpiry { get; set; }
    // observer callbacks as optional BCL delegates (stale / malformed / resume-failed / empty turn /
    // invalid output / projection problem), mirroring the A2uiErrorObserver delegate pattern
    public Action<MafSurfacedEvent>? OnSurfaced { get; set; }
}

public sealed class MafTelegramBridge : IAsyncDisposable
{
    // TgBotConfig / TgWebhookConfig are already fluent and C#-callable (TgBotConfig.create(token)
    // .WithTools(...).WithBindingStore(...)), so the C# entry point reuses them directly.
    public static MafTelegramBridge StartPolling(TgBotConfig config, AIAgent agent, MafBridgeSettings? settings = null);
    public static MafTelegramBridge StartWebhook(TgWebhookConfig config, AIAgent agent, MafBridgeSettings? settings = null);

    public Task StartRunAsync(long chatId, string prompt, OwnerScope? owner = null, CancellationToken ct = default);
    public ValueTask DisposeAsync();
}

public static class MafTools
{
    // One call; report lists what registered and what was surfaced (per-tool, not all-or-nothing).
    public static ToolProjectionResult Project(ToolRegistry registry, IEnumerable<AIFunction> functions);
}
```

## Behavioral contracts (cross-façade, both transports)

- **Approval render**: a run that pauses on an approval-required tool produces exactly ONE message in
  the conversation's chat — default body (tool name + arguments, plain text) or the formatter's body —
  with one `[Approve][Reject]` row of owner-scoped, single-use tool buttons.
- **Tap → resume**: an in-scope tap acks within the spinner budget (deferred-ack + watchdog), resumes
  the agent with the matching approve/reject exactly once, and edits the SAME message in place to the
  outcome — or to the next request's fresh buttons when the turn immediately pauses again. Message
  count does not grow with decision steps.
- **Refusals**: an out-of-scope tap is refused with the denied notice and never resumes; a repeat tap on
  a decided approval is refused (single-use + pending-table consume) and never resumes a second time.
- **Text turn**: an incoming user text message is answered by the agent's reply in the same chat;
  messages and taps for one chat are processed one at a time in arrival order (same dispatcher lane) —
  identical under long polling and webhooks, per the shared mapping.
- **Surfacing (never silent)**: stale/unknown decisions (including every post-restart decision — the
  binding is durable, the session is not), malformed decision payloads, resume failures (message left
  with NO live buttons), empty turns, and over-long/invalid output all reach the observer; none crash
  the run loop or the bot.
- **Projection parity**: every projected tool appears in `ToolRegistry.ManifestJson()` with the SAME
  name, description, and parameter schema as its `AIFunction` declaration; unprojectable declarations
  are surfaced while valid siblings register.
- **No idiom leak**: the leaf's C# surface and the C# façade delta expose no
  `FSharpOption`/`FSharpValueOption`/`FSharpFunc` (canary test, as in slices 1–4).
- **Core untouched**: `TgLLM.Core` and both façades carry no MAF dependency; a `MessageReceived` event
  with no `OnMessage` wired is a no-op; slice-1/2/3/4 tests stay green unchanged.
