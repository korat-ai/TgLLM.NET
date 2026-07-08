# Quickstart: MAF Bridge — Approve Your Agent's Tool Calls from Telegram

Design-time usage of slice 005: a Microsoft Agent Framework (MAF) agent whose consequential tool calls
are approved or rejected with Telegram buttons — no button or callback plumbing written by the host.
(Shipped user docs live in `docs/`, updated during implementation.)

## 1. Author the agent (plain MAF — the host's own code)

```fsharp
open System
open System.ComponentModel
open Microsoft.Extensions.AI

// Tool methods live on a class so AIFunctionFactory can reflect parameter names and [<Description>]
// attributes into the tool's JSON schema — a let-bound F# function compiles to FSharpFunc, whose
// MethodInfo carries neither.
type MailTools() =
    [<Description "Send an email to a recipient.">]
    member _.SendEmail
        ([<Description "Recipient address">] toAddr: string,
         [<Description "Message body">] body: string) : string =
        $"Sent to {toAddr}."

let mailTools = MailTools()

// The explicit Func wrap is required: F# method groups do not auto-coerce to System.Delegate.
// ApprovalRequiredAIFunction is what makes the agent PAUSE and ask a human before executing.
let sendEmailTool = AIFunctionFactory.Create(Func<string, string, string>(mailTools.SendEmail), name = "send_email")
let approvalRequired = ApprovalRequiredAIFunction(sendEmailTool) :> AITool

let agent =
    chatClient.AsAIAgent(                       // any IChatClient backend (OpenAI, Azure, local, ...)
        instructions = "You are a helpful assistant.",
        tools = [| approvalRequired |])
```

## 2. Wire the bridge (F#)

```fsharp
open TgLLM.FSharp
open TgLLM.Maf

let tools = ToolRegistry.create ()             // the bridge registers maf-approve / maf-reject here
let store = LiteDbBindingStore.OpenAt "maf-bindings.db"   // durable: taps survive a restart

let bridge =
    Maf.startPolling
        (TgBotConfig.create(token).WithTools(tools).WithBindingStore(store))
        agent

// That's it. bridge.Bot is a regular TgBot for anything else the host wants to send.
```

## 3. The text turn (automatic)

A person writes to the bot; the bridge runs one agent turn and replies in the same chat. Messages in
one chat are answered one at a time, in order — the conversation keeps its context across turns.

```text
user:  What can you do?
bot:   I can draft and send emails for you. …
```

## 4. The approval loop (the point of the slice)

```text
user:  Email alice@example.com that the deploy is done.
bot:   Approval required: send_email            ← ONE message, buttons scoped to the sender
       toAddr: "alice@example.com"
       body: "The deploy is done."
       [ Approve ]  [ Reject ]
```

- The tap is acknowledged immediately (the agent's continuation may take seconds).
- **Approve** → the agent executes the tool and the SAME message is edited in place:

```text
bot:   ✔ send_email approved — Email sent to alice@example.com.
```

- **Reject** → the agent skips the tool; the same message shows the rejection instead.
- Someone else tapping → refused with "This button isn't for you." — the agent is not resumed.
- A second tap on a decided approval → refused; the decision took effect exactly once.
- A further approval in the same turn → the same message gets the next request's fresh buttons.

## 5. Host-initiated runs and custom rendering (optional)

```fsharp
// Start a run in a chat on the host's initiative; the peer owns the approvals in a private chat.
do! bridge.StartRun(chatId, "Prepare the weekly report and email it to the team.")

// Redact arguments / localize labels with a formatter (zero-config default shown in step 4):
let options =
    { Formatter = ValueSome(fun p -> { Body = $"Allow {p.Tool}?"; ApproveLabel = "Да"; RejectLabel = "Нет" })
      Observer = ValueNone; DefaultOwner = ValueNone; ApprovalExpiry = ValueSome(TimeSpan.FromMinutes 10.) }

let bridge = Maf.startPollingWith options (TgBotConfig.create(token).WithTools(tools)) agent
```

## 6. Tool projection — one call, no double descriptions

```fsharp
// The agent's declared tools (name, description, JSON schema) become the library's registry tools and
// appear in ManifestJson() verbatim; a broken declaration is reported, its siblings still register.
let report = MafTools.project tools [ sendEmailTool; searchTool ]
// report.Registered = ["send_email"; "search"]; report.Problems = []
```

## 7. C# — the same, idiomatically

```csharp
var tools = ToolRegistry.create();
var config = TgBotConfig.create(token).WithTools(tools).WithBindingStore(store);

await using var bridge = MafTelegramBridge.StartPolling(config, agent, new MafBridgeSettings
{
    ApprovalExpiry = TimeSpan.FromMinutes(10),
    Formatter = p => new ApprovalRenderInfo($"Allow {p.Tool}?"),
    OnSurfaced = e => logger.LogWarning("MAF bridge: {Event}", e),   // stale/malformed/failed — never silent
});

await bridge.StartRunAsync(chatId, "Prepare the weekly report and email it to the team.");
MafTools.Project(tools, new[] { sendEmailTool, searchTool });
```

## 8. What survives what (honest limits)

- Buttons ride the **durable binding store**: a tap on a pre-restart approval message still routes, is
  acknowledged, owner-checked — and is then surfaced as **stale** (the agent's session is in-memory
  this release, so a pre-restart run can no longer be resumed). Nothing is silently dropped.
- The reply path is non-streaming: one turn, one reply message. Streaming with coalesced edits is a
  later slice.

## What you get

- An approval-requiring MAF agent becomes a working Telegram approval bot with **zero button plumbing**:
  the host supplies the agent and the chat.
- Decisions are owner-scoped, single-use, immediately acknowledged, and applied exactly once; outcomes
  arrive by editing the same message — the chat doesn't fill with per-step messages.
- The agent's tool declarations and the bot's tool manifest agree by construction.
- Everything that cannot be honored — stale, malformed, failed, empty — is surfaced to the host.
