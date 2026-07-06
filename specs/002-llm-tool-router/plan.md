# Implementation Plan: LLM Tool Router

**Branch**: `002-llm-tool-router` | **Date**: 2026-07-06 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `specs/002-llm-tool-router/spec.md`

## Summary

Add a **Tool Router** so an LLM agent can bind behavior to buttons at runtime through *data*, not
compiled code: the host registers named tools once; the LLM produces a neutral **keyboard plan**
(labels + tool names + optional string args); the library builds the keyboard and routes each press
to the registered tool. Because a binding is now `token → tool name + arg` (serializable), bindings
can be **persisted** (a file store) and survive a restart. Also closes capability debts from slice 1:
richer in-place reactions (edit the pressed message, toast/alert) and URL buttons. Everything is
**additive** on top of the green slice-1 library — the slice-1 hook API and its tests are untouched
(FR-012).

## Technical Context

<!-- Extends slice 1; stack is fixed. Design + Bot API facts resolved in research.md (D1–D8). -->

**Language/Version**: F# core + F# façade + adapters; C# façade. .NET — single-target `net10.0` for
now (as slice 1; `net8.0;net10.0` shipping matrix is a CI/backlog item, not this feature).

**Primary Dependencies**: Telegram.Bot 22.10.1 (edit/answer/url — behind ports); FSharp.Core;
FSharp.UMX; System.Text.Json (the new file binding store only). `Microsoft.AspNetCore.App` (webhook
glue, unchanged). No LLM-vendor SDKs (library is format-agnostic, FR-013).

**Storage**: Binding store — in-memory default (Core) + a **file-based (JSON-on-disk)** durable store
(new `TgLLM.Persistence` project). No external database (deferred).

**Testing**: Expecto + FsCheck (F#); xUnit v3 + FsCheck.Xunit.v3 (C# façade). Property tests for the
pure kernel (`ToolPlan.plan`, tool resolution). Restart persistence and edit-in-place are integration
tests against the slice-1 fake Bot API server (extended for edit/answer/url).

**Target Platform**: Cross-platform .NET library (NuGet).

**Project Type**: Library (extends the slice-1 multi-project solution; one new project).

**Performance Goals**: Ack the tap within the SC-003 budget even on the deferred-ack tool path (a
watchdog sends a default ack ~2s if a tool is slow); 100% correct tool routing over ≥100 taps (SC-002).

**Constraints**: `answerCallbackQuery` is **one-shot** → tool toasts require deferred ack (D2);
`callback_data` 1–64 bytes → args stored library-side (D4); URL taps send no callback query (D3);
edit errors (`message to edit not found` / `is not modified`) caught + surfaced, not fatal (D1).

**Scale/Scope**: PoC — single bot; string args; file persistence. Full external-DB store, structured
args, and message-media edits are out of scope.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| # | Principle | Gate | Status |
|---|-----------|------|--------|
| I | Test-First + property tests | `ToolPlan.plan` + tool resolution TDD'd with FsCheck; integration for edit/persistence/url | ✅ PASS |
| II | F# core + dual idiomatic APIs | Tool registry / plan builder / extended PressContext in both façades; slice-1 canary still guards leakage | ✅ PASS |
| III | Layered architecture | New ports in Core; file store (IO) in a NEW leaf project `TgLLM.Persistence`, NOT in Core | ✅ PASS |
| IV | Both transports | Router rides the same `IUpdateSource`/`UpdateProcessor`; SC-007 runs over both | ✅ PASS |
| V | Vendor-grounded | Edit/answer/url facts verified against core.telegram.org + Telegram's reference server (research.md) | ✅ PASS |
| VI | Task-based concurrency | `Task`/`ValueTask` throughout; watchdog via timers; no `Async` | ✅ PASS |
| VII | English, current docs | quickstart.md + examples authored with the feature; docs cite code/vendor only | ✅ PASS |
| VIII | Open-source excellence | Tool-router examples added; README/CHANGELOG updated | ✅ PASS |

**Initial gate**: PASS. **Post-design gate**: PASS — additive design; slice-1 tests remain green
(FR-012). No violations → Complexity Tracking empty.

## Project Structure

### Documentation (this feature)

```text
specs/002-llm-tool-router/
├── plan.md            # This file
├── spec.md            # Feature spec (with Clarifications)
├── research.md        # Phase 0 decisions (D1–D8) + Bot API facts
├── data-model.md      # Phase 1 domain model (additive)
├── quickstart.md      # Phase 1 usage
├── contracts/tool-router.md   # Phase 1 public surface (F# + C#)
└── checklists/requirements.md
```

### Source Code (delta on the slice-1 solution)

```text
src/
├── TgLLM.Core/          # + ToolName/ToolError, ToolKeyboard/PlanButton, ToolBinding, ToolPlan.plan,
│                        #   IToolRegistry + InMemoryToolRegistry, IBindingStore + InMemoryBindingStore,
│                        #   ToolDispatch, extended PressContext (Arg/Edit*/Answer), extended
│                        #   IBotApiClient (Edit*, AnswerCallback+text/alert), RegisteredButton → DU,
│                        #   UpdateProcessor optional ?toolDispatch (deferred-ack path + watchdog)
├── TgLLM.Persistence/   # NEW (F#): FileBindingStore (JSON on disk) : IBindingStore.  Deps: Core, STJ
├── TgLLM.BotApi/        # + URL-button mapping; Edit*/AnswerCallback(text,alert) impl; error surfacing
├── TgLLM.FSharp/        # + ToolRegistry, Plan module, WithTools/WithBindingStore, SendKeyboardPlan
└── TgLLM.CSharp/        # + ToolRegistry, PlanBuilder, KeyboardPlan, options.Tools/BindingStore, Send…

tests/
├── TgLLM.Core.Tests/         # + ToolPlan property tests, tool resolution, registry/binding-store
├── TgLLM.Persistence.Tests/  # NEW: FileBindingStore round-trip + reload-on-restart
├── TgLLM.Integration.Tests/  # + edit-in-place, toast/alert, unknown-tool, URL button, restart (SC-004),
│                             #   both-transports tool-router acceptance; slice-1 tests stay green
└── TgLLM.CSharp.Tests/       # + C# tool-router behavior; leak canary re-run

examples/
└── ToolRouterFSharp/  ToolRouterCSharp/   # NEW: LLM-style tool-router demo (both languages)
```

**Structure Decision**: Extend the slice-1 solution; add exactly one src project (`TgLLM.Persistence`,
because a file store does IO and Core must stay IO-agnostic) plus one test project. Everything else is
additive edits to existing projects. The slice-1 public API and tests are preserved (FR-012); the
Tool Router is a convenience layer, not a rewrite.

## Complexity Tracking

> No Constitution Check violations. Table intentionally empty.

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| — | — | — |
