<!-- SPECKIT START -->
## Active feature: 005-maf-bridge (extends 001+002+003+004)

**Current plan** (read for full context — structure, tech, constraints, the MAF-surface mapping):
`specs/005-maf-bridge/plan.md`
Supporting artifacts: `spec.md` (+ Clarifications), `research.md` (decisions D1–D13 + probe-confirmed MAF
1.13.0 facts + the mapping table), `data-model.md`, `contracts/maf-bridge.md`, `quickstart.md`.
Done slices: `specs/001-inline-keyboard-hooks/`, `specs/002-llm-tool-router/`,
`specs/003-tool-router-extensions/`, `specs/004-a2ui-renderer/`.

**What 005 adds (additive on 001–004, FR-019 — earlier APIs/tests untouched)**: the **flagship** — a NEW F#
leaf `TgLLM.Maf` that bridges a **Microsoft Agent Framework (MAF)** agent onto Telegram as a **direct,
in-process, HITL-first** bridge. US1 (P1): a run's pending tool approval → one owner-scoped single-use
message with **Approve/Reject** (rides the hardened slice-3 tool-button engine: deferred-ack + watchdog +
edit-in-place + durable `IBindingStore`, NO new engine); a tap resumes the agent; edit-in-place shows the
outcome / next approval. US2 (P2): project MAF `AIFunction`s → the `IToolRegistry`/manifest in one call.
US3 (P3): minimal non-streaming agent-as-bot (incoming text → `RunAsync` → reply). **Core stays
MAF-agnostic** — MAF lives ONLY in the leaf (Principle III / FR-018).

**Load-bearing design (research.md, probe-grounded)**: ⚠ the approval content types are
**`ToolApprovalRequestContent` / `ToolApprovalResponseContent`** (`Microsoft.Extensions.AI.Abstractions`
10.6.0) — NOT the `FunctionApproval*` names the Learn tutorial still shows (docs lag; trust the binaries).
Detect = scan `AgentResponse.Messages[*].Contents`; resume = `req.CreateResponse(bool,?reason)` →
`ChatMessage(User,[..])` → `RunAsync(same AgentSession)`. Each approval → two slice-3 `ToolButton`s
(`maf-approve`/`maf-reject`) whose structured arg is a compact **approval descriptor** `{Chat;RequestId;Tool}`;
an in-memory **pending-approval table** keyed `(chat,RequestId)` holds the live request + owner scope +
tokens with atomic consume (at-most-once). **Durability caveat (honest)**: binding is durable but the
pending table + `AgentSession` are in-memory → a post-restart tap deterministically lands in the **stale
path** (`IMafObserver.OnStaleDecision`), never a resume, never a silent drop. Owner = host-configurable
`OwnerScope` (explicit > message-sender > private-chat peer > `Anyone`) — NOT a hard-coded "initiator".
Approval body renders default (tool name + args, **plain text** — no parse-mode escaping hazard) or via an
optional host **formatter** (renames labels/body, cannot add decisions). The one additive **Core seam**:
`AgentEvent.MessageReceived` + config-time `CommonConfig.OnMessage` (+ a NEW small `IMessageObserver` —
growing `IHookObserver` is breaking), routed through the SAME per-chat dispatcher lane as taps (serial
`AgentSession` access for free), mapped in the shared `Mapping.toAgentEvent` so BOTH transports get it with
no transport code; the leaf **wraps startup** (`Maf.startPolling`/`startWebhook`) since `OnMessage` is
config-time. **D12**: the WHOLE bridge surface (both F# and C#-clean APIs) lives in the leaf — the leaf
references the façade (leaf → façade → core, reverse of A2UI); a MAF entry point in `TgLLM.CSharp` was
rejected (would drag `AIAgent` onto a MAF-agnostic package).

**Tech snapshot**: F# core (+ the one MAF-agnostic seam) + new F# leaf `TgLLM.Maf` (refs `TgLLM.FSharp` +
`Microsoft.Agents.AI` **pinned EXACT 1.13.0** → `Microsoft.Extensions.AI` 10.6.0 transitively; MAF/
`JsonElement` confined to the leaf, NOT Core). `Task`/`ValueTask` only (MAF is Task-based; note
`CreateSessionAsync : ValueTask`). Single-target `net10.0` (net8 in CI/backlog). In-memory session only
(durable session deferred). MAF is preview — **re-run the reflection probe before the first
`dotnet add package` and at any bump** (rename churn is live).

**Testing**: TDD mandatory; Expecto + FsCheck (F#), xUnit v3 + FsCheck.Xunit.v3 (C#). A hand-rolled
**`ScriptedAgent : AIAgent`** (override `RunCoreAsync` — the one protected abstract every public `RunAsync`
funnels through) drives the loop offline against the existing `FakeBotApiServer` — no live model/network.
Property tests for the PURE parts: descriptor serialize/parse round-trip, `AIFunction`→registry projection
field mapping, owner-scope resolution order, default rendering. `dotnet build`/`dotnet test` are the final
oracle (fslangmcp `check` can diverge — FsLangMCP#100). Comprehensive Principle VII sweep at Polish
(comments cite code/MAF/Bot API only, never FR/SC/US/research-D#/spec files — the recurring slip).

**Governance**: `.specify/memory/constitution.md` (v1.0.0) is binding. Use `fslangmcp` for semantic F#
queries (MAF grounding uses the Microsoft Learn MCP + a reflection probe, not fslangmcp). Discussion in
Russian; all code/docs/comments in English.
<!-- SPECKIT END -->
