# Contract: Tool Router public surface (F# + C#)

**Feature**: `002-llm-tool-router` | **Date**: 2026-07-06

Additive to slice 1: the slice-1 `Button.on` / `KeyboardBuilder` hook API keeps working (FR-012).
This adds a tool registry, a neutral plan, plan-sending, the durable store option, and the richer
`PressContext` reactions. No F# idiom leaks into the C# surface (canary from slice 1 still applies).

## F# façade (`TgLLM.FSharp`)

```fsharp
// Register named tools (host code). A tool is `PressContext -> Task`; the bound arg is on `ctx.Arg`.
module Tool =
    val define : name: string -> handler: (PressContext -> Task<'a>) -> struct (string * Tool)

type ToolRegistry =
    static member create : unit -> ToolRegistry
    member Register : name: string * handler: (PressContext -> Task<'a>) -> ToolRegistry   // fluent
    // build a neutral plan
module Plan =
    val tool : label: string -> toolName: string -> ToolKeyboard row helper           // ToolButton
    val toolWithArg : label: string -> toolName: string -> arg: string -> (…)          // ToolButton + arg
    val url : label: string -> url: string -> (…)                                      // UrlButton
    val rows : PlanButton list list -> Result<ToolKeyboard, ToolError>

type TgBotConfig with
    member WithTools : ToolRegistry -> TgBotConfig
    member WithBindingStore : IBindingStore -> TgBotConfig     // e.g. a file store from TgLLM.Persistence

type TgBot with
    /// Send a keyboard built from a neutral plan; presses route to the registered tools.
    member SendKeyboardPlan : chat: ChatId * text: MessageText * plan: ToolKeyboard -> Task<MessageId>
```

Canonical F# usage:
```fsharp
let tools =
    ToolRegistry.create()
        .Register("approve", fun ctx -> ctx.EditTextAsync $"Approved by {ctx.User.FirstName}")
        .Register("reject",  fun ctx -> task { ctx.Answer("Rejected", alert = true) })

use! bot = TgBot.startPolling ((TgBotConfig.create botToken).WithTools tools)

let plan =
    Plan.rows [ [ Plan.tool "Approve" "approve"; Plan.tool "Reject" "reject" ]
                [ Plan.url "Docs" "https://example.test/docs" ] ]
match plan with
| Ok p -> let! _ = bot.SendKeyboardPlan(chat, MessageText.unsafe "Deploy?", p) in ()
| Error e -> eprintfn "%A" e
```

## C# façade (`TgLLM.CSharp`) — idiomatic, no F# leakage

```csharp
public sealed class ToolRegistry
{
    public ToolRegistry Register(string name, Func<PressContext, Task> handler);   // add or replace
}

public sealed class PlanBuilder
{
    public PlanBuilder Row(Action<PlanRowBuilder> configure);
    public KeyboardPlan Build();   // throws TgKeyboardException on invalid layout / URL
}
public sealed class PlanRowBuilder
{
    public PlanRowBuilder Tool(string label, string toolName, string? arg = null);
    public PlanRowBuilder Url(string label, string url);
}

public sealed class TelegramAgentOptions   // + existing
{
    public ToolRegistry? Tools { get; init; }
    public IBindingStore? BindingStore { get; init; }
}

public sealed class TelegramAgent          // + existing
{
    public Task<long> SendKeyboardPlanAsync(long chatId, string text, KeyboardPlan plan, CancellationToken ct = default);
}

public sealed class PressContext           // + existing (ButtonLabel/Chat/User/MessageId/ReplyTextAsync)
{
    public string? Arg { get; }
    public Task EditTextAsync(string text);
    public Task EditKeyboardAsync(KeyboardPlan plan);
    public void Answer(string text, bool alert = false);
}
```

Canonical C# usage:
```csharp
var tools = new ToolRegistry()
    .Register("approve", ctx => ctx.EditTextAsync($"Approved by {ctx.User.FirstName}"))
    .Register("reject",  ctx => { ctx.Answer("Rejected", alert: true); return Task.CompletedTask; });

await using var agent = await TelegramAgent.StartPollingAsync(
    new TelegramAgentOptions { BotToken = botToken, Tools = tools });

var plan = new PlanBuilder()
    .Row(r => r.Tool("Approve", "approve").Tool("Reject", "reject"))
    .Row(r => r.Url("Docs", "https://example.test/docs"))
    .Build();
await agent.SendKeyboardPlanAsync(chatId, "Deploy?", plan);
```

## Durable store (from `TgLLM.Persistence`)

```fsharp
// F#: a JSON-on-disk binding store
type FileBindingStore =
    static member openAt : path: string -> FileBindingStore   // loads existing bindings; IBindingStore
```
```csharp
// C#
var store = FileBindingStore.OpenAt("bindings.json");
// options: new TelegramAgentOptions { BotToken = ..., Tools = tools, BindingStore = store }
```

## Behavioural contract checklist (drives tests)

| Guarantee | Requirement | Test |
|-----------|-------------|------|
| Plan taps invoke the exact tool + arg, no glue | FR-002/003/004, SC-001/002 | ToolPlan property + integration |
| Unknown tool → ack, no run, surfaced | FR-005, SC-005 | integration |
| Edit pressed message in place | FR-006, SC-003 | integration (edit recorded, no new message) |
| Toast/alert on deferred-ack path | FR-007 | integration (answerCallbackQuery carries text/alert) |
| Bindings survive restart (file store) | FR-008, SC-004 | integration across simulated restart |
| URL button opens link, invokes no tool | FR-009, SC-006 | integration (no callback query / no tool) |
| Both façades, both transports | FR-010, SC-007 | integration matrix |
| Slice-1 hook API + tests unaffected | FR-012 | slice-1 suite stays green |
