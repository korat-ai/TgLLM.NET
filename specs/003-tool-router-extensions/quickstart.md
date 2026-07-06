# Quickstart: Tool Router Extensions

Design-time usage of slice 003's additions. All snippets are additive on the slice-2 Tool Router; a host
using none of them keeps slice-2 behavior. (Shipped user docs live in `docs/quickstart.md`, updated
during implementation.)

## 1. Owner-scoped keyboard (US1)

Only the user the keyboard was meant for can press its tool buttons.

```fsharp
// F# — scope a keyboard to the user whose message triggered it
let plan =
    ToolKeyboard.create [ [ Plan.tool "Approve" "approve"; Plan.tool "Reject" "reject" ] ]
do! bot.SendKeyboardPlan(chat = chatId, text = "Deploy?", plan = plan, owner = Owner.user msg.From.Id)
// A different user tapping "Approve" gets a toast ("This button isn't for you") and no tool runs.
```

```csharp
// C#
await agent.SendKeyboardPlanAsync(chatId, "Deploy?", plan, owner: Owner.User(msg.From.Id));
```

## 2. Emit a tool manifest for the LLM (US2)

Register tools with descriptions/schemas once; hand the neutral manifest to your model's
function-calling API.

```fsharp
let registry =
    ToolRegistry.create()
    |> fun r -> r.Register("approve", approveTool, description = "Approve the pending deploy",
                           argSchema = """{ "type":"object","properties":{"env":{"type":"string"}} }""")
    |> fun r -> r.Register("reject",  rejectTool,  description = "Reject the pending deploy")

let toolsJson = registry.ManifestJson()   // [{ "name":"approve","description":"...","parameters":{...} }, ...]
// → drop toolsJson into your OpenAI/Anthropic "tools" request (a trivial key rename if needed).
```

## 3. Structured arguments (US2)

Bind a typed payload to a button; read it back typed in the tool.

```fsharp
let plan =
    ToolKeyboard.create [ [ Plan.toolWith "Ship v2" "ship" {| version = "2.0"; canary = true |} ] ]

let shipTool (ctx: PressContext) = task {
    let req = ctx.GetArg<{| version: string; canary: bool |}>()   // deserialized from the binding
    do! deploy req.version req.canary
}
// A slice-2 string arg still works: Plan.toolWithArg "..." "tool" "raw-id"  →  ctx.Arg
```

## 4. WebApp and CopyText buttons (US3)

```fsharp
let plan =
    ToolKeyboard.create
        [ [ Plan.webApp   "Open form"   "https://app.example.com/form" ]     // launches a Mini App
          [ Plan.copyText "Copy token"  "ghp_xxx...(≤256 chars)" ]           // copies to clipboard
          [ Plan.tool     "Done"        "finish" ] ]                          // still routes server-side
// WebApp/CopyText taps are handled entirely by the client — no tool runs for them.
```

## 5. Expiry, confirm-once, and a durable LiteDB store (US4)

```fsharp
let plan =
    ToolKeyboard.create
        [ [ Plan.tool "Confirm" "confirm" |> Plan.expiring (TimeSpan.FromMinutes 10.) |> Plan.singleUse ] ]
// After 10 min, or after the first successful press, a tap resolves as unknown (ack, no tool).

// Bindings survive restart in LiteDB (interchangeable with the in-memory and file stores):
let store = LiteDbBindingStore.OpenAt "bindings.db"
let config = TgBotConfig.create(token).WithTools(registry).WithBindingStore(store)
                                       .WithIdleChatEviction(TimeSpan.FromMinutes 30.)
```

```csharp
// C# — same store
var options = new TelegramAgentOptions(token)
{
    Tools = registry,
    BindingStore = LiteDbBindingStore.OpenAt("bindings.db"),
};
```

## What you get

- A group keyboard whose buttons only its owner can press.
- The same registry that *routes* taps also *describes itself* to your LLM.
- Buttons that carry rich, typed arguments — not just short strings.
- Mini-App-launch and copy-to-clipboard buttons beside your tool buttons.
- Bindings that expire, can be confirm-once, survive restart in LiteDB, and don't grow unbounded.
