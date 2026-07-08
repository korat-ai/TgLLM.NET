# Research & Decisions: MAF Bridge — HITL Approval as Telegram Buttons

Phase 0 decisions for slice 005. Each is **Decision / Rationale / Alternatives rejected**. MAF facts are
grounded TWO ways (Principle V): (1) Microsoft Learn (tutorial + API reference pages for
`Microsoft.Agents.AI` 1.13.0 / `Microsoft.Extensions.AI` 10.6.0) and (2) a **reflection probe** over the
actual resolved 1.13.0 binaries (`dotnet add package Microsoft.Agents.AI --version 1.13.0`, then
enumerate the real public surface). Where the two disagree, the probe wins — every divergence from the
previously-assumed names is flagged inline as **⚠ DIVERGENCE**. Telegram facts are grounded against
core.telegram.org (unchanged from slices 1–4).

## D1 — Package pin: `Microsoft.Agents.AI` 1.13.0 exact, MAF confined to the leaf

**Decision**: `TgLLM.Maf` references `Microsoft.Agents.AI` pinned to **exactly `1.13.0`** (no floating
range). Probe-confirmed transitive closure: `Microsoft.Agents.AI.Abstractions 1.13.0`,
`Microsoft.Extensions.AI 10.6.0`, `Microsoft.Extensions.AI.Abstractions 10.6.0` (informational versions
`1.13.0+7ca73c06…` / `10.6.0+c8437bc2…`). No other project — Core, either façade, transports — gains any
MAF or `Microsoft.Extensions.AI` dependency.

**Rationale**: MAF renames types between releases (D2's ⚠ flags are the live proof, found *within days*
of the previous verification). An exact pin makes an upgrade a deliberate, test-gated feature; the leaf
is the blast shield (FR-018). `Microsoft.Extensions.AI.Abstractions` is where the approval content types
actually live, so its version rides the MAF pin and is never referenced directly.

**Alternatives rejected**: A floating `1.13.*` — silently absorbs renames. Referencing
`Microsoft.Extensions.AI.Abstractions` directly from the leaf "for clarity" — invites a version skew
against what `Microsoft.Agents.AI` was compiled for; the transitive resolution is the compatible one.

## D2 — Approval detect + resume (probe-confirmed mechanism)

**Decision**: The bridge drives one non-streaming turn per user input and inspects the response for
pending approvals:

1. **Run**: `agent.RunAsync(message, session, ct) : Task<AgentResponse>` — `AgentResponse` carries
   `Messages : IList<ChatMessage>` and `Text : string` (concatenated assistant text).
2. **Detect**: scan `response.Messages → m.Contents → items of Microsoft.Extensions.AI.`
   **`ToolApprovalRequestContent`** (sealed, base `InputRequestContent`). Each carries
   `RequestId : string` (inherited; "the unique identifier that correlates this request with its
   corresponding response") and `ToolCall : ToolCallContent` — in practice a `FunctionCallContent`
   (`: ToolCallContent`) with `Name : string` and `Arguments : IDictionary<string, object?>`, which the
   default rendering (D7) displays.
3. **Resume**: `request.CreateResponse(approved, ?reason) : ToolApprovalResponseContent`, wrapped in
   `ChatMessage(ChatRole.User, [ responseContent ])`, passed back via `agent.RunAsync(approvalMessage,
   session, ct)` with the **same session** — the agent continues from where it paused. The next response
   is inspected again (loop until no pending requests remain).

The tool-side prerequisite is the host's job (the host supplies the configured agent): wrapping an
`AIFunction` in `Microsoft.Extensions.AI.ApprovalRequiredAIFunction(innerFunction)` marks it
approval-required; the bridge only *consumes* the resulting pause.

**⚠ DIVERGENCE (from the assumed facts, found by the probe)**:

- The approval content types are **`ToolApprovalRequestContent` / `ToolApprovalResponseContent`**
  (namespace `Microsoft.Extensions.AI`, assembly `Microsoft.Extensions.AI.Abstractions` 10.6.0). The
  previously-assumed names `FunctionApprovalRequestContent` / `FunctionApprovalResponseContent` **do not
  exist in the resolved binaries**. The Learn *tutorial* page (agents/tools/tool-approval) still shows
  the old names in its C# snippets — the tutorial lags the rename; the Learn *API reference*
  (`view=net-11.0-pp`, package 10.6.0/10.7.0) and the 1.13.0 `Microsoft.Agents.AI` reference both agree
  with the probe. Trust the probe + API reference, not the tutorial snippet.
- The request's function call is exposed as **`ToolCall : ToolCallContent`**, not `FunctionCall :
  FunctionCallContent` — the bridge pattern-matches the `FunctionCallContent` subtype to read
  `Name`/`Arguments`.
- The response type is **`AgentResponse`** (with `AgentResponse<'T>`), not `AgentRunResponse`; there is
  **no `UserInputRequests` helper property** on the .NET response (the probe found no such member
  anywhere in the four assemblies) — scanning `Messages[*].Contents` is the mechanism, matching the
  current C# tutorial's own `SelectMany(x => x.Contents).OfType<…>()` shape.
- `CreateResponse(bool)` was assumed correct and **is** confirmed — with an additional optional
  `reason : string` parameter.

**Rationale**: This is the vendor's documented direct-agent HITL loop; the bridge adds no protocol of its
own. Multiple `ToolApprovalRequestContent` items in one response are each rendered as their own decision
message (D5), matching the spec's assumption; 1.13.0 also ships a `ToolApprovalAgent` middleware that
queues requests one-at-a-time and supports "don't ask again" rules — a host MAY wrap its agent with it,
and the bridge is indifferent (it renders whatever pending requests a response carries).

**Alternatives rejected**: Consuming `RunStreamingAsync` and folding updates — streaming is explicitly
deferred; `AgentResponseExtensions.ToAgentResponseAsync` exists for later. Building on workflow
`RequestInfoEvent`s — the workflow layer is the deferred wizard slice, not this MVP.

## D3 — Conversation state: one in-memory `AgentSession` per chat

**Decision**: The conversation/session type in 1.13.0 is **`AgentSession`** (abstract, in
`Microsoft.Agents.AI.Abstractions`; probe-confirmed — the assumed name was right). The bridge holds one
`AgentSession` per chat, created lazily on the chat's first turn via
`agent.CreateSessionAsync(ct) : ValueTask<AgentSession>` (note: **`ValueTask`**, not `Task`), stored in
an in-memory per-chat map inside the bridge. All access to a chat's session happens on that chat's
dispatcher lane (D8), so the session is never touched concurrently — MAF documents no thread-safety
guarantee for a session, and none is needed under per-chat serialization.

Durability: `AIAgent.SerializeSessionAsync(session) : ValueTask<JsonElement>` /
`DeserializeSessionAsync(JsonElement)` exist in 1.13.0 and are the seam a future durable-session slice
will use. **This release does not use them**: a process restart loses every session (the spec's declared
in-memory scope); the observable consequence is D4's stale-decision path, not a crash.

**Rationale**: One session per chat is exactly the spec's Conversation entity (one exchange ↔ one chat);
lazy creation means an idle chat costs nothing. Sharing the bot's per-chat lane gives the "serialized per
chat" guarantee for free instead of introducing a second locking scheme.

**Alternatives rejected**: One session per user across chats — breaks the cross-chat-safety rule
(slice 4): replies/approvals must address the conversation's own chat. Serializing the session into the
binding store to survive restarts — a whole design of its own (explicitly deferred backlog).

## D4 — The approval descriptor and the pending-approval table (the durability caveat)

**Decision**: When a run pauses, the bridge records each pending request in an **in-memory
pending-approval table** keyed by `(chat, RequestId)`, holding the live `ToolApprovalRequestContent`
(needed for `CreateResponse`), the approval message's `MessageId` (for edit-in-place), the resolved owner
scope, and both buttons' `CallbackToken`s. What is serialized into each button's `ToolBinding.Arg`
(slice-2 structured argument, stored server-side in the binding store — not in Telegram's 64-byte
`callback_data`) is a compact, self-describing **approval descriptor**:
`{ Chat; RequestId; Tool }` — the chat id, the MAF request id, and the tool name for human-readable
stale/failure reports.

A tap deserializes the descriptor and looks up `(Chat, RequestId)` in the table:

- **hit** → this decision is delivered: remove the entry (atomically, before the resume starts — the
  decision takes effect at most once even if a sibling tap races in behind it on the same lane), then
  resume (D2) and edit the message in place (D5).
- **miss** → the decision is **stale**: the run ended, the process restarted (table and session are both
  in-memory), or the entry was already consumed by the sibling button. Surfaced via
  `IMafObserver.OnStaleDecision descriptor` — the descriptor is still perfectly readable (which tool,
  which request, which chat), mirroring the A2UI leaf's readable stale-surface reports. Never silently
  dropped; the tap is still acked by the engine as always.

**Durability caveat, stated honestly**: the *binding* is durable (`IBindingStore` — a pre-restart
Approve button still routes, is deduped, owner-checked, and acked by the hardened engine), but the
*pending request and session it belongs to are not*. A post-restart tap therefore can never resume its
run — it deterministically lands in the stale path above. That is the designed degradation for this
release, and exactly what the spec's stale/unknown edge case prescribes; durable sessions are the
deferred slice.

**Rationale**: The MAF `ToolApprovalRequestContent` object itself is not usefully serializable for
resume purposes — even though `ToolApprovalResponseContent` has a public reconstructing constructor, a
resume without the live in-memory `AgentSession` is impossible, so persisting the request buys nothing
this release. A compact descriptor + in-memory table keeps the tap payload small, keeps MAF types out of
the stored bytes (the binding store stays MAF-agnostic — it stores an opaque string), and makes the
stale report informative.

**Alternatives rejected**: Serializing the whole request (tool call arguments included) into `Arg` —
larger stored payload, MAF-shaped JSON in a MAF-agnostic store, still can't resume post-restart. A
random opaque token as `Arg` with all meaning in the table — loses the readable stale report after
restart (the exact case where readability matters most).

## D5 — Approval → slice-3 tool buttons: `maf-approve` / `maf-reject`, no new engine

**Decision**: The bridge registers two internal Tool Router tools, **`maf-approve`** and
**`maf-reject`**, into the bot's own `ToolRegistry` (same shape as slice 4's single `a2ui-action` tool,
including the double-attach guard). One pending approval renders as ONE message sent via the existing
`TgBot.SendKeyboardPlan` with:

- one keyboard row `[Approve][Reject]` — two `ToolButton`s whose structured arg is the SAME serialized
  descriptor (D4); the tapped tool's *name* is the decision, so the descriptor carries no decision field;
- `owner = <resolved owner scope>` (D6), `deniedNotice` default, `singleUse = true`, and an optional
  keyboard expiry (host-configurable; expired taps fall into the engine's existing stale path).

The tool handler (per press) parses the descriptor, consumes the pending entry (D4), then resumes the
agent (D2) — a multi-second operation that runs INSIDE the tool, so the slice-3 **deferred-ack +
watchdog** acks the tap within the spinner budget while the continuation is still running, with zero new
code. When the continuation completes, the handler **edits the approval message in place**: outcome text
(approved → the turn's resulting text or a completion note; rejected → a rejection note) via
`ctx.EditTextAsync`, or — when the SAME turn immediately raises the next approval — a fresh
`[Approve][Reject]` keyboard for the new request on the same message via the bot-level
`EditKeyboardPlan` path (the A2UI leaf's established mechanism for replacing text + keyboard together).

Single-use semantics across the *pair*: `SingleUse = true` consumes the *tapped* binding at resolve
time (slice-3 behavior); the *sibling* button's binding is removed by the handler right after the
pending entry is consumed (its token is in the table). Even if that cleanup is beaten by a concurrent
sibling tap, the sibling lands on the already-consumed table entry → stale path → refused, never a
second resume. The at-most-once guarantee therefore rests on the table's atomic consume, with binding
cleanup as hygiene.

**Failure paths** (all observable, never silent): resume throws → `IMafObserver.OnResumeFailed
(descriptor, exn)` AND the message is edited to a failure note so no live buttons survive a step that
will not complete; unparseable/absent descriptor → `OnMalformedDecision raw` (nothing actionable —
mirrors the A2UI `Nothing`/malformed split); empty turn (no text, no approval) → `OnEmptyTurn chat` and
no empty message is sent (`MessageText.create` would reject it anyway); reply over the Bot API length
limit → surfaced through the same observer rather than crashing the turn (slice-4 precedent).

**Rationale**: This reuses the *already-hardened* routing, dedup, owner-scope, expiry, deferred-ack,
edit-in-place, and durable-binding machinery — the bridge is a mapping layer plus a resume call, not a
second dispatcher. Two named tools (rather than one tool with a decision field in the arg) make the
button → decision mapping visible in the manifest and keep the descriptor minimal.

**Alternatives rejected**: A separate MAF-side callback path — duplicates everything slices 1–3 fixed.
One `maf-decision` tool with the decision inside the arg — workable, but the tapped-tool-name-is-the-
decision design removes a field and an invalid-decision parse case. Sending a NEW message per outcome —
violates the message-count success criterion; edit-in-place is the established shape.

## D6 — Owner-scope defaulting: host policy first, obvious inference second

**Decision**: Who may decide an approval reuses Core's `OwnerScope` (`Anyone | User of UserId`) with this
resolution order, applied when the run starts (FR-002/FR-005):

1. **Host-supplied scope for the run** (explicit parameter on run start / bridge option) — always wins.
2. **User-initiated turn** (the run began from an incoming text message): default `User sender` — the
   message sender owns the approvals its turn raises.
3. **Host-initiated run in a private chat**: default `User (peer)` — Bot API fact (core.telegram.org): a
   private chat's `chat.id` **is** the peer's user id (positive; groups/channels are negative), so the
   peer is knowable from the `ChatId` alone.
4. **Host-initiated run in a non-private chat with no explicit scope**: `Anyone` — no inference is
   "obvious" here, and the spec's clarification forbids hard-coding a business rule; the default is
   documented and the host can always pass a scope.

Enforcement is entirely the existing engine's: `OwnerScope.isAllowed` refuses out-of-scope taps before
any tool runs, with the standard denied notice.

**Rationale**: Matches the clarified spec verbatim: policy is host-configurable, inference only where
obvious. Reusing `OwnerScope` means the refusal path, notice override, and the "unidentifiable presser is
refused under `User`" safety all come for free and stay property-tested where they already are.

**Alternatives rejected**: Hard-coding "initiator only" — explicitly rejected in clarification. A new
scope kind (e.g. role-based) — out of scope; `OwnerScope` is the library's existing vocabulary.

## D7 — Default approval rendering + optional host formatter

**Decision**: Default rendering (zero configuration): body = tool name + arguments from
`FunctionCallContent.Name` / `.Arguments` (one `key: value` line per argument, values JSON-rendered),
sent as **plain text** (no `parseMode`), with fixed labels **Approve** / **Reject**. Overridable by an
optional host **formatter**:

- F#: `ApprovalFormatter = ApprovalPrompt -> ApprovalRender` over
  `ApprovalPrompt = { Tool: string; Arguments: (string * string) list; Chat: ChatId }` and
  `ApprovalRender = { Body: string; ApproveLabel: string; RejectLabel: string }`;
- the formatter's output is validated like any other send (`MessageText`/`ButtonLabel` smart
  constructors) and an invalid render is surfaced, not thrown mid-loop.

**Rationale**: Plain text cannot be broken by arbitrary tool-argument content — no MarkdownV2 escaping
obligation for the zero-config path (an agent's tool arguments are untrusted for parse-safety, the exact
lesson of slice 4's D7; here the simplest safe answer is no parse mode at all). Showing arguments by
default serves informed consent; a host with sensitive arguments redacts via the formatter, which also
covers localization of the labels. Fixed two-button semantics stay: the formatter renames, it cannot
add/remove decisions.

**Alternatives rejected**: MarkdownV2 default with escaping — more machinery for no MVP gain; a
formatter-supplied parse mode can be a later additive option. Letting the formatter return a whole
keyboard — that's the deferred "agent plans a keyboard" backlog, not approval rendering.

## D8 — The additive Core seam: `AgentEvent.MessageReceived` + config-time `OnMessage`

**Decision**: Core (all MAF-agnostic, additive):

- `IncomingMessage = { Chat: ChatId; Sender: EndUser; MessageId: MessageId; Text: string }` — bare text +
  identity, nothing vendor-shaped.
- `AgentEvent` gains `| MessageReceived of IncomingMessage` (alongside `ButtonPressed`/`AckOnly`).
- `MessageHandler = IncomingMessage -> CancellationToken -> Task`.
- `UpdateProcessor` gains optional `?onMessage: MessageHandler` and optional
  `?messageObserver: IMessageObserver`. A `MessageReceived` event is routed through
  **`dispatcher.Enqueue(msg.Chat, work)`** — the SAME per-chat lane as taps — so messages and taps for
  one chat are processed one at a time in arrival order, and the bridge's per-chat session (D3) is never
  accessed concurrently. Handler exceptions are caught in the work thunk and reported via
  `IMessageObserver.OnMessageFailed(msg, exn)` (a NEW, small interface with a Noop default; the façade
  bridges it to `ILogger` exactly like `IHookObserver`). With no `onMessage` wired, a `MessageReceived`
  event is a no-op — slice-1/2/3/4 behavior is byte-identical.
- `Mapping.toAgentEvent` (the pure mapping BOTH transports share) additionally maps an `Update` carrying
  a user text `Message` to `MessageReceived`; all other update kinds keep being skipped. Because both
  transports share this one mapping, **neither transport needs code of its own** — the same way the
  `AckOnly` fix "fell out" in slice 3.

Façade (F#): `CommonConfig.OnMessage : MessageHandler option` (+ `withOnMessage` /
`.WithOnMessage(...)` on both `TgBotConfig` and `TgWebhookConfig`, keeping the two transports in
lockstep). Façade (C#): `TelegramAgentOptions.OnMessage` as a BCL delegate. Both are host-facing and
MAF-free — a host can answer text messages with no MAF at all.

Because `OnMessage` is **config-time** (a bot starts consuming updates the instant it starts — a
late-bound handler would race the first message), the MAF leaf cannot attach to an already-running bot
the way `A2ui.renderer` does for taps. The leaf therefore **wraps bot startup**: `Maf.startPolling
config agent …` composes `config.WithOnMessage(bridge handler)`, starts the bot, and registers
`maf-approve`/`maf-reject` into the bot's tool registry before returning — the host never sees the
two-phase wiring, and no approval keyboard can exist before its tools are registered.

**Rationale**: Extending the existing `IHookObserver` instead was rejected as **breaking**: it is a
public interface hosts may implement; adding a member invalidates existing implementors, violating the
additive guarantee — a separate tiny interface with a Noop default is the additive shape. Carrying
`Sender` (not just chat + text) is load-bearing for D6's owner default. Routing through the existing
dispatcher is what makes the serialization requirement a property of the architecture rather than of
bridge code.

**Alternatives rejected**: A late-settable message-handler slot on `TgBot` — adds mutable public state
and a "message arrived before the handler was set" hole; config-time wiring has no such window. Mapping
every message kind (media, edits, channel posts) — scope creep; user TEXT messages are the spec's
requirement, everything else keeps the established "skipped, not guessed at" policy.

## D9 — `AIFunction` → `IToolRegistry` projection (one call, per-tool surfacing)

**Decision**: `MafTools.project (registry: ToolRegistry) (functions: AIFunction seq) : ProjectionReport`
— a single call (usable standalone, without any bridge/bot) that, per function:

| `AIFunction` member (probe-confirmed) | Registry destination |
|---|---|
| `Name : string` (from `AITool`) | `ToolName.create` → the registry key; invalid (empty-after-trim) → a surfaced `ProjectionProblem`, tool skipped |
| `Description : string` (from `AITool`, may be empty) | `ToolMetadata.Description` (empty → `None`) |
| `JsonSchema : JsonElement` (from `AIFunctionDeclaration`) | `.GetRawText()` → `ToolMetadata.ArgSchema` — the registry carries schema text verbatim (its documented contract), so the manifest reports the SAME schema MAF holds |
| the function itself | a registered `Tool` whose handler parses the button's structured arg (JSON object) into `AIFunctionArguments` (probe: `: IDictionary<string, object?>`, public ctor from a dictionary) and calls `f.InvokeAsync(args, ct) : ValueTask<obj?>`, replying the JSON-serialized result in the chat |

Valid siblings register even when one declaration fails (per-tool `Result`, collected into the report and
mirrored to the observer) — the A2UI "supported siblings still render" policy applied to projection. A
projected name colliding with an existing registration follows `IToolRegistry.Register`'s documented
add-or-replace semantics; a duplicate WITHIN one projected set is surfaced as a problem (an agent
declaring two tools with one name is a broken declaration, not a re-registration).

**Rationale**: The two shapes agree almost field-for-field (`ToolManifestEntry` is
name/description/parameters); the projection makes them agree *by construction* and kills drift-prone
double descriptions. `GetRawText()` keeps Core's "opaque schema text, never parsed" contract intact — no
JSON dependency moves into Core.

**Alternatives rejected**: Manifest-only projection (no invokable handler) — the registry's contract is
that registered tools route; a manifest entry that can't be pressed is a broken tool. All-or-nothing
projection — one bad declaration would veto an otherwise-valid tool surface.

## D10 — F# ↔ MAF interop rules (leaf-boundary discipline)

**Decision**: All MAF adaptation happens inside `TgLLM.Maf`:

- **Nullability**: MAF surfaces are nullable-annotated C# (`ChatClientAgent` ctor's
  `instructions = null` etc.); every inbound `string`/object crossing into bridge logic goes through
  `Option.ofObj` immediately; nothing MAF-nullable escapes the leaf.
- **ValueTask**: `CreateSessionAsync` / `SerializeSessionAsync` / `AIFunction.InvokeAsync` return
  `ValueTask<_>` — awaited directly inside `task { }`, never converted through `Async`.
- **Collections**: `ChatMessage(ChatRole.User, contents)` takes `IList<AIContent>`; F# builds a
  `ResizeArray`/array, upcast at the call. `IList<AITool>` likewise where a host composes tools.
- **Delegates**: MAF option types take BCL `Func<…>` (e.g. `ToolApprovalAgentOptions.AutoApprovalRules`);
  F# lambdas do NOT auto-coerce to `Func` in those positions — wrap explicitly
  (`System.Func<_,_>(fun … -> …)`). The MVP bridge itself needs none, but the rule is recorded for the
  leaf.
- **`JsonElement`**: appears only in the leaf (`AIFunction.JsonSchema`, session serialization);
  Core/façades keep seeing opaque `string`.
- **Task-only**: MAF is Task/ValueTask-based end to end, so Principle VI holds with zero `Async<'T>`
  anywhere in the slice.

**Rationale**: These are the standing F#-against-C#-framework rules (the Orleans/A2UI shape); writing
them down here is what keeps review mechanical.

## D11 — Testing: a scripted `AIAgent`, not a mocked framework

**Decision**: Integration tests use a hand-rolled **`ScriptedAgent`** deriving `AIAgent` and overriding
the probe-confirmed protected core (this is the complete override surface in 1.13.0):

- `RunCoreAsync(messages, session, options, ct) : Task<AgentResponse>` — pops the next scripted step:
  either *reply with text*, or *pause with a `ToolApprovalRequestContent(requestId, FunctionCallContent
  (callId, name, args))`*; when the incoming `messages` carry a `ToolApprovalResponseContent`, the script
  asserts the expected `RequestId`/`Approved` and yields the follow-up step (result text, a further
  approval, an empty turn, or a throw — each edge case is one script).
- `CreateSessionCoreAsync` → a trivial `AgentSession` subclass; `SerializeSessionCoreAsync` /
  `DeserializeSessionCoreAsync` → not needed by the bridge this release (throw
  `NotSupportedException` in the stub); `RunCoreStreamingAsync` → not consumed (throw).

No live model, no network: the Telegram side is the existing `FakeBotApiServer`; the MAF side is the
script. Pure-logic property tests (Expecto + FsCheck) target the descriptor serialize/parse round-trip,
the projection field mapping (D9), the owner-scope resolution order (D6), and the default rendering
(D7) — none of which need an agent at all.

**Rationale**: `AIAgent`'s public `RunAsync` overloads are non-virtual wrappers over ONE protected
abstract — overriding `RunCoreAsync` intercepts every public entry uniformly, which a mock of the public
surface cannot. The stub is deterministic, offline, and survives MAF preview churn better than
interaction-based mocks.

**Alternatives rejected**: Mocking `IChatClient` under a real `ChatClientAgent` — couples tests to
`ChatClientAgent`'s internal function-invocation pipeline (preview behavior, e.g.
`EnableNonApprovalRequiredFunctionBypassing`), which is MAF's to test, not ours. Live-model smoke tests —
standing deferred backlog.

## D12 — Packaging: the whole bridge surface lives in the leaf, dual-idiomatic

**Decision**: `TgLLM.Maf` references `TgLLM.FSharp` (leaf → façade → core; note this is the *reverse* of
the A2UI leaf, which sits UNDER the façade — the bridge needs `TgBot`, so it sits above). The leaf itself
ships **both** an idiomatic F# surface (module functions, `voption`, records) and a C#-clean surface
(sealed class + static factories, BCL delegates, nullable annotations — the Principle II canary applies
to the leaf's public API). The façades gain ONLY the MAF-agnostic Core-seam wiring from D8
(`CommonConfig.OnMessage` / `TelegramAgentOptions.OnMessage`).

This refines the plan's structure sketch (which placed "bridge entry points" in the façades): putting a
MAF entry point into `TgLLM.CSharp` would force `AIAgent` onto that façade's public API and drag the
pinned MAF dependency into a package that must stay MAF-agnostic — the A2UI precedent (façade references
the leaf) was harmless only because that leaf's dependency is `System.Text.Json`. A C# host references
`TgLLM.Maf` alongside `TgLLM.CSharp`; an F# host references it alongside `TgLLM.FSharp`.

**Rationale**: FR-018's letter ("the bridge and all framework types MUST live only in the new leaf") and
the blast-shield argument from D1 — a MAF rename must never be able to break a consumer who didn't opt
into MAF.

**Alternatives rejected**: A second companion package (`TgLLM.Maf.CSharp`) — packaging overhead for a
handful of overloads; one dual-surface leaf matches the library's existing dual-façade discipline.
Mirroring the A2UI reference direction — impossible; the bridge consumes the façade, not vice versa.

## D13 — Positioning: a direct in-process bridge; AG-UI is MAF's native protocol, A2UI is adjacent

**Decision (note, not code)**: MAF speaks **AG-UI natively** (Microsoft ships a first-class AG-UI
integration with HITL approval middleware — Learn: integrations/ag-ui); Google's **A2UI** (slice 004) is
an adjacent, different protocol. This slice deliberately bridges MAF **directly, in-process**
(`AIAgent.RunAsync` ↔ Tool Router), with no protocol hop: fewer moving parts, exact typing, and the
approval loop rides `ToolApprovalRequestContent` natively. A future event-stream agent-UI foundation
leaf (which could consume AG-UI and would then serve MAF *and* non-MAF agents over HTTP) remains the
declared backlog vision — this MVP does not build it.

## MAF surface → library mapping (probe-confirmed signatures)

| MAF surface (1.13.0 / M.E.AI 10.6.0) | Library counterpart | Decision |
|---|---|---|
| `AIAgent.RunAsync(string, AgentSession, AgentRunOptions, ct) : Task<AgentResponse>` | one conversation turn on the chat's dispatcher lane | D2, D8 |
| `AgentResponse.Messages[*].Contents` containing `ToolApprovalRequestContent` | one Telegram message: body (D7) + `[Approve][Reject]` tool buttons | D2, D5 |
| `ToolApprovalRequestContent.RequestId` | pending-table key half + descriptor field | D4 |
| `ToolApprovalRequestContent.ToolCall :?> FunctionCallContent` (`Name`, `Arguments`) | default approval body; `Tool` field of the descriptor | D7, D4 |
| `request.CreateResponse(approved, ?reason)` → `ChatMessage(ChatRole.User, [content])` → `RunAsync(…, session)` | the `maf-approve`/`maf-reject` tool handler's resume | D2, D5 |
| `AIAgent.CreateSessionAsync(ct) : ValueTask<AgentSession>` | per-chat in-memory conversation state | D3 |
| `AIAgent.SerializeSessionAsync / DeserializeSessionAsync : … JsonElement …` | (deferred durable-session seam; unused this release) | D3, D4 |
| `ApprovalRequiredAIFunction(inner)` (host-side) | what makes a tool pause; the bridge only consumes the pause | D2 |
| `AIFunction.Name / Description / JsonSchema : JsonElement` | `ToolName` / `ToolMetadata.Description` / `ToolMetadata.ArgSchema` (manifest parity) | D9 |
| `AIFunction.InvokeAsync(AIFunctionArguments, ct) : ValueTask<obj?>` | the projected tool's registered handler | D9 |
| `AIFunctionFactory.Create(Delegate, …)` (host-side) | how hosts author the functions they project | D9 |
| `ToolApprovalAgent` / `AlwaysApproveToolApprovalResponseContent` (1.13.0 middleware) | optional host-side wrapper; bridge-indifferent | D2 |
| — (no MAF type) | `AgentEvent.MessageReceived` + `CommonConfig.OnMessage` (Core seam, MAF-agnostic) | D8 |

## MAF 1.13.0 facts (grounded, probe-confirmed)

- Assemblies resolved by the 1.13.0 pin: `Microsoft.Agents.AI` 1.13.0, `Microsoft.Agents.AI.Abstractions`
  1.13.0, `Microsoft.Extensions.AI` 10.6.0, `Microsoft.Extensions.AI.Abstractions` 10.6.0.
- `AIAgent` (abstract): public non-virtual `RunAsync` overloads (`string` / `ChatMessage` /
  `IEnumerable<ChatMessage>` / typed `AgentResponse<'T>` variants), all funneling into ONE
  `protected abstract RunCoreAsync(IEnumerable<ChatMessage>, AgentSession, AgentRunOptions, ct)`;
  likewise `CreateSessionAsync` → `CreateSessionCoreAsync`, `SerializeSessionAsync` →
  `SerializeSessionCoreAsync`, `DeserializeSessionAsync` → `DeserializeSessionCoreAsync`,
  `RunStreamingAsync` → `RunCoreStreamingAsync`. `session` and `options` parameters default to `null`.
- `ChatClientAgent` (sealed): `ctor(IChatClient, ?instructions, ?name, ?description, ?tools :
  IList<AITool>, …)` and `ctor(IChatClient, ChatClientAgentOptions, …)`;
  `chatClient.AsAIAgent(…) : ChatClientAgent` extension; `ChatClientAgentSession : AgentSession` with
  `ConversationId`; history via `ChatHistoryProvider` (`InMemoryChatHistoryProvider` default machinery),
  state in `AgentSession.StateBag : AgentSessionStateBag` (JSON-serializable).
- `AgentResponse`: `Messages : IList<ChatMessage>`, `Text : string`, `AgentId`, `ResponseId`,
  `CreatedAt`, `Usage`, `RawRepresentation`; `AgentResponse<'T>.Result`.
- Approval contents (M.E.AI.Abstractions): `InputRequestContent` (base; `RequestId : string`;
  `[JsonDerivedType(typeof(ToolApprovalRequestContent), "toolApprovalRequest")]`) →
  `ToolApprovalRequestContent` (sealed; `ToolCall : ToolCallContent`;
  `CreateResponse(bool, ?reason) : ToolApprovalResponseContent`); `InputResponseContent` →
  `ToolApprovalResponseContent` (sealed; `Approved : bool`, `ToolCall`, `Reason`; public
  `[JsonConstructor] ctor(requestId, approved, toolCall)`). `FunctionCallContent : ToolCallContent`
  (`CallId`, `Name`, `Arguments : IDictionary<string, object?>`).
- Tools: `AITool` (`Name`, `Description`) → `AIFunctionDeclaration` (`JsonSchema : JsonElement`,
  `ReturnJsonSchema`) → `AIFunction` (`InvokeAsync(AIFunctionArguments = null, ct) : ValueTask<obj?>`,
  `UnderlyingMethod`, `JsonSerializerOptions`); `AIFunctionFactory.Create(Delegate | MethodInfo, …)`;
  `AIFunctionArguments : IDictionary<string, object?>` with dictionary ctors;
  `ApprovalRequiredAIFunction : DelegatingAIFunction`, `ctor(AIFunction)`.
- 1.13.0 approval middleware (available to hosts, not required by the bridge): `ToolApprovalAgent :
  DelegatingAIAgent` (queues multiple requests one-at-a-time; "always approve" rules persisted in the
  session `StateBag`), `builder.UseToolApproval(…)`, `AlwaysApproveToolApprovalResponseContent`,
  `ChatClientAgentOptions.EnableNonApprovalRequiredFunctionBypassing`.
- Docs-lag warning recorded in D2: the Learn tutorial's C# snippets for tool approval still name
  `FunctionApprovalRequestContent`/`FunctionApprovalResponseContent`; the binaries and API reference use
  `ToolApprovalRequestContent`/`ToolApprovalResponseContent`. Implementation follows the binaries.
