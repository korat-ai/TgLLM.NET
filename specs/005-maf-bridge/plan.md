# Implementation Plan: MAF Bridge — HITL Approval as Telegram Buttons

**Branch**: `005-maf-bridge` | **Date**: 2026-07-08 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/005-maf-bridge/spec.md`

## Summary

Add a new F# leaf, `TgLLM.Maf`, that bridges a Microsoft Agent Framework (MAF) agent onto the existing Tool
Router so a MAF agent's human-in-the-loop tool approval becomes an owner-scoped, single-use Telegram button,
its declared tools become the library's registry/manifest tools, and a plain user text message is answered
by the agent. The bridge is built entirely on the hardened slice-1/2/3 primitives (owner-scoped single-use
buttons, deferred-ack + watchdog, edit-in-place delivery, durable binding store) — no new engine. MAF is
confined to the leaf and pinned to an exact version, so the core and both façades stay MAF-agnostic
(Principle III). One additive, MAF-agnostic core seam is required: surfacing an incoming user *text* message
(`AgentEvent.MessageReceived` + an `OnMessage` hook), routed through the same per-chat dispatcher lane as
button taps, which also gives serialized (non-concurrent) access to the agent's per-chat working state. The
approval message renders by default from the tool name + arguments with fixed Approve/Reject controls and is
overridable by an optional host formatter; who may act on an approval is a host-configurable owner policy
that reuses the library's existing `OwnerScope`. Additive on slices 001–004 — their public behavior and
tests are unchanged.

## Technical Context

**Language/Version**: F# on .NET 10 (`net10.0` single-target, matching slices 001–004; `net8.0` in the CI
matrix stays a deferred backlog item).

**Primary Dependencies**:
- New leaf `TgLLM.Maf` references `TgLLM.FSharp` (the F# façade — for `TgBot`, the Tool Router, owner-scoped
  buttons, edit-in-place, and the binding store) and `Microsoft.Agents.AI` **pinned to an exact version**
  (1.13.0 confirmed current on NuGet 2026-07-08; re-verified in research), which brings
  `Microsoft.Extensions.AI` (10.6.0) transitively.
- `System.Text.Json` (+ `FSharp.SystemTextJson` if needed) is used **only in the leaf** to serialize the
  approval descriptor into a `ToolBinding.Arg`, mirroring how the A2UI leaf isolates JSON — never in Core.
- Core and the façades gain **no** MAF or agent-framework dependency. `Telegram.Bot` stays confined to the
  transport project (unchanged).

**Storage**: Reuses the existing `IBindingStore` (InMemory / File / LiteDb) for the approval bindings — no
new store. Conversation/agent state is in-memory for this release (durable `AgentSession` deferred).

**Testing**: Expecto + FsCheck (F# leaf), xUnit v3 + FsCheck.Xunit.v3 (C# façade). Property tests for the
pure parts — the `AIFunction` → tool-registry projection, the approval-descriptor serialize/parse round-trip,
and the owner-scope decision. Integration tests drive the loop through the existing `FakeBotApiServer` with a
stubbed/fake MAF agent (a hand-rolled `AIAgent` that yields a scripted approval request then a result), so no
live model or network is needed. `dotnet build` / `dotnet test` are the final oracle (fslangmcp `check` can
diverge — FsLangMCP#100).

**Target Platform**: .NET 10, cross-platform; both Telegram transports (long polling and webhooks).

**Project Type**: Library — F# core + separate idiomatic C#/F# façade packages + a new isolated F# leaf.

**Performance Goals**: Approval buttons appear within a single message-send under normal conditions
(SC-002); no throughput target beyond correctness. Per-chat serialization bounds concurrency by design.

**Constraints**: Core stays MAF-agnostic and IO-agnostic (Principle III); the feature is additive — slices
001–004 public APIs and tests are byte-for-byte untouched (FR-019); `Task`/`ValueTask` only, MAF's
`RunAsync` is already Task-based (Principle VI); MAF types never cross the leaf boundary (Principle II — the
C# façade must not expose `FSharpFunc`/`FSharpOption`, and MAF's `Delegate`/`Func`/`JsonElement` are adapted
at the boundary).

**Scale/Scope**: One new leaf project (`TgLLM.Maf`), one additive Core seam (`AgentEvent.MessageReceived` +
`CommonConfig.OnMessage`), façade wiring in both `TgLLM.FSharp` and `TgLLM.CSharp`, one new test project plus
additions to the integration and C# test projects, and one runnable example per façade.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | How this plan satisfies it |
|-----------|--------|----------------------------|
| I — Test-First + property tests | ✅ PASS | TDD mandatory; property tests planned for the `AIFunction`→registry projection, the approval-descriptor round-trip, and the owner-scope decision. No implementation before a failing test. |
| II — F# core, dual idiomatic façades, no idiom leak | ✅ PASS | The leaf is F#; the C# façade surface exposes no `FSharpFunc`/`FSharpOption`; MAF's `Delegate`/`Func`/`JsonElement` are adapted at the boundary (Func wrappers, `Option.ofObj`). The additive Core seam is plain and MAF-agnostic. |
| III — Layered, Core IO/MAF-agnostic | ✅ PASS | MAF lives only in `TgLLM.Maf`. The one Core change carries a bare text + chat, not MAF types; dependency direction stays leaf → façade → core. |
| IV — Both transports | ✅ PASS | The message seam routes through the existing per-chat dispatcher used by both long polling and webhooks; consumer handler code is unchanged across transports. |
| V — Vendor-grounded | ✅ PASS | research.md grounds the MAF surface (`ApprovalRequiredAIFunction`, `FunctionApprovalRequestContent`, `AgentSession`, `AIFunctionFactory`, `AIFunction.JsonSchema`) against Microsoft Learn; Telegram behavior is unchanged and already grounded. |
| VI — Task/ValueTask | ✅ PASS | MAF `RunAsync`/`RunStreamingAsync` are Task-based; the bridge uses `Task`/`ValueTask` throughout; no `Async`. |
| VII — English docs, no spec refs in code | ✅ PASS | All code/docs/comments English; the recurring Principle VII sweep runs at Polish; every subagent brief carries the explicit forbid on FR/SC/US/task-ID/research-D#/spec-file/branch-slug citations. |
| VIII — Open-source excellence | ✅ PASS | Runnable examples for both façades, user docs updated in the same change, green CI, release-ready at every commit. |

**Result**: No violations. Complexity Tracking below is intentionally empty.

**Post-design re-check (after Phase 1)**: Still PASS on all eight. Phase-1 grounding *strengthened*
Principle III — research D12 confines the bridge's entire public surface (including its C#-clean API) to the
leaf, so no MAF type reaches a MAF-agnostic package, and the reflection probe (Principle V) corrected the
approval content types to the names in the actual 1.13.0 binaries (`ToolApprovalRequestContent` /
`ToolApprovalResponseContent`) before any code depends on them. No new violations introduced by the design.

## Project Structure

### Documentation (this feature)

```text
specs/005-maf-bridge/
├── plan.md              # This file (/speckit-plan command output)
├── spec.md              # Feature specification (+ Clarifications)
├── research.md          # Phase 0 output — MAF grounding + decisions
├── data-model.md        # Phase 1 output — entities → F# types
├── quickstart.md        # Phase 1 output — integrator walkthrough (F# and C#)
├── contracts/           # Phase 1 output — the leaf's public contract + façade wiring
│   └── maf-bridge.md
├── checklists/
│   └── requirements.md  # Spec quality checklist (from /speckit-specify)
└── tasks.md             # Phase 2 output (/speckit-tasks — NOT created here)
```

### Source Code (repository root)

```text
src/
├── TgLLM.Core/                 # +AgentEvent.MessageReceived, +CommonConfig.OnMessage (additive, MAF-agnostic)
├── TgLLM.FSharp/               # F# façade: +CommonConfig.OnMessage seam ONLY (MAF-agnostic; no MAF ref)
├── TgLLM.CSharp/               # C# façade: +TelegramAgentOptions.OnMessage seam ONLY (MAF-agnostic; no MAF ref)
├── TgLLM.Maf/                  # NEW F# leaf — references TgLLM.FSharp + Microsoft.Agents.AI (pinned exact).
│                               #   Holds the WHOLE bridge surface, dual-idiomatic (F# module + C#-clean class):
│                               #   approval detect/resume, AIFunction→registry projection, descriptor
│                               #   (de)serialization, owner-scope defaulting, approval rendering, observer,
│                               #   startup wrapper (Maf.startPolling/startWebhook)
├── TgLLM.BotApi/ TgLLM.Webhooks/ TgLLM.AspNetCore/   # transports (unchanged)
└── TgLLM.A2UI/ TgLLM.Persistence*/                    # earlier leaves (unchanged)

tests/
├── TgLLM.Maf.Tests/            # NEW — Expecto + FsCheck (projection, round-trip, owner-scope properties)
├── TgLLM.Integration.Tests/    # + bridge loop tests via FakeBotApiServer + stub MAF agent
└── TgLLM.CSharp.Tests/         # + C# façade bridge tests (idiom-cleanliness + a loop smoke)

examples/
├── MafFSharp/                  # NEW runnable example — F#
└── MafCSharp/                  # NEW runnable example — C#
```

**Structure Decision**: A single new isolated F# leaf (`TgLLM.Maf`) holds the ENTIRE bridge surface —
including both its idiomatic F# and its C#-clean public API — so no MAF type (`AIAgent`, `AgentSession`,
`ToolApprovalRequestContent`, …) ever reaches the core or either façade (Principle III / FR-018). The leaf
references `TgLLM.FSharp` (leaf → façade → core; this is the *reverse* of the A2UI leaf, which sits under
the façade — the bridge needs `TgBot`, so it sits above). The façades gain ONLY the additive, MAF-agnostic
message seam (`CommonConfig.OnMessage` / `TelegramAgentOptions.OnMessage`); a MAF entry point placed in
`TgLLM.CSharp` was rejected because it would force `AIAgent` onto a package that must stay MAF-free. A C#
host references `TgLLM.Maf` alongside `TgLLM.CSharp`; an F# host references it alongside `TgLLM.FSharp`.
(Refines the initial sketch per research D12.)

## Complexity Tracking

> No Constitution Check violations — this section is intentionally empty.
