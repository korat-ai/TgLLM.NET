# Quickstart: LLM Tool Router

Let an LLM agent drive an interactive keyboard **at runtime** — the host registers named tools once,
the LLM decides which buttons to show and which tool each maps to, and the library routes taps to the
tools. Tools can edit the pressed message in place and show a toast/alert; bindings can survive a
restart. Builds on the slice-1 quickstart; the slice-1 `Button.on` / `KeyboardBuilder` API still works.

## 1. Register your tools (host code, once)

```fsharp
let tools =
    ToolRegistry.create()
        .Register("approve", fun ctx -> ctx.EditTextAsync $"Approved by {ctx.User.FirstName}")
        .Register("reject",  fun ctx -> task { ctx.Answer("Rejected", alert = true) })
```
```csharp
var tools = new ToolRegistry()
    .Register("approve", ctx => ctx.EditTextAsync($"Approved by {ctx.User.FirstName}"))
    .Register("reject",  ctx => { ctx.Answer("Rejected", alert: true); return Task.CompletedTask; });
```

## 2. Turn an LLM decision into a keyboard (at runtime)

Your LLM produces which buttons to show and which registered tool (+ optional string arg) each
triggers. Map that decision into the neutral plan — the library ships no vendor parsers:

```fsharp
let plan =
    Plan.rows [ [ Plan.tool "Approve" "approve"; Plan.tool "Reject" "reject" ]
                [ Plan.url "Docs" "https://example.test/docs" ] ]
match plan with
| Ok p -> let! _ = bot.SendKeyboardPlan(chat, MessageText.unsafe "Deploy?", p) in ()
| Error e -> eprintfn "invalid plan: %A" e
```
```csharp
var plan = new PlanBuilder()
    .Row(r => r.Tool("Approve", "approve").Tool("Reject", "reject"))
    .Row(r => r.Url("Docs", "https://example.test/docs"))
    .Build();
await agent.SendKeyboardPlanAsync(chatId, "Deploy?", plan);
```

## 3. Wire it up

```fsharp
use! bot = TgBot.startPolling ((TgBotConfig.create botToken).WithTools tools)   // or startWebhook
```
```csharp
await using var agent = await TelegramAgent.StartPollingAsync(
    new TelegramAgentOptions { BotToken = botToken, Tools = tools });
```

## 4. (Optional) Survive restarts

Add a durable binding store so taps on keyboards sent before a restart still route:

```fsharp
let store = FileBindingStore.openAt "bindings.json"
use! bot = TgBot.startPolling ((TgBotConfig.create botToken).WithTools(tools).WithBindingStore store)
```
```csharp
var store = FileBindingStore.OpenAt("bindings.json");
await using var agent = await TelegramAgent.StartPollingAsync(
    new TelegramAgentOptions { BotToken = botToken, Tools = tools, BindingStore = store });
```

## What you can rely on

- **Right tool, every tap** — the tapped button invokes exactly its bound tool with its argument.
- **Edit in place** — a tool changes the pressed message's text/keyboard instead of spamming new ones.
- **Toast/alert** — a tool can show the user a short notification or a blocking alert.
- **Unknown tool is safe** — a tap on a button whose tool is unregistered is acknowledged and logged,
  never crashes the bot.
- **URL buttons** open a link client-side (no tool, no round-trip).
- **Both transports, both languages** — identical tool code over long polling and webhooks, in F# and C#.
- **Restart-safe** (with a durable store) — outstanding keyboards keep working after a restart.
