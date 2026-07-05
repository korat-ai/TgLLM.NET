# Implementation Plan: Interactive Keyboards with Button Hooks (Agent PoC)

**Branch**: `001-inline-keyboard-hooks` | **Date**: 2026-07-04 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `specs/001-inline-keyboard-hooks/spec.md`

## Summary

Give bot-side agents a convenient way to send an inline keyboard to a Telegram chat and attach an
arbitrary hook to each button, routing button presses back to the right hook — over **both** long
polling and webhooks, with identical hook code. The technical approach is a **layered F# library**:
an IO-agnostic core (domain + pure kernel + ports + engine), transport adapters over **Telegram.Bot**
(behind an `IBotApiClient`/`IUpdateSource` seam), a per-chat `System.Threading.Channels` dispatcher
for ordering, an in-memory hook store behind a storage seam, and **two separate idiomatic façade
packages** (F# and C#). Correctness centers on a pure kernel (callback-token codec, keyboard
planning, routing) exhaustively covered by FsCheck property tests.

## Technical Context

<!-- Values resolved in research.md (D1–D9); no NEEDS CLARIFICATION remain. -->

**Language/Version**: F# (core + F# façade + adapters) and C# (C# façade), .NET — multi-target
`net8.0;net10.0` (see research D5). *As built (first slice): single-target `net10.0` — only that
runtime is installed locally; the `net8.0;net10.0` shipping matrix is enabled in CI where both
runtimes exist.*

**Primary Dependencies**: Telegram.Bot 22.10.1 (transport, isolated behind ports);
`Microsoft.AspNetCore.App` framework reference (webhook endpoint glue only); FSharp.Core; FSharp.UMX
(compile-time ids). No independent JSON library for the PoC (Telegram.Bot owns wire).

**Storage**: In-memory `IHookStore` (button→hook associations); behind a storage seam so a durable
store can replace it later (FR-016). No database in this slice.

**Testing**: Expecto + Expecto.FsCheck (F# core/adapters/integration); xUnit v3 + FsCheck.Xunit.v3
(C# façade). Property-based tests are MANDATORY for the pure kernel (Principle I). *As built: Expecto
10.2.3 (the 11.x line has no working `dotnet test` VSTest adapter yet); FsCheck 3.x across both stacks.*

**Target Platform**: Cross-platform .NET library (NuGet). Long polling runs anywhere; webhooks
require a public HTTPS endpoint (ports 443/80/88/8443, TLS 1.2+).

**Project Type**: Library (multi-project: core + transport adapters + two façade packages + tests +
examples).

**Performance Goals**: Ack the tapped button within 3s for ≥99% of taps (SC-003); begin hook
execution within 3s for ≥95% (SC-004); 100% correct per-button routing over ≥100 presses (SC-002).

**Constraints**: `callback_data` 1–64 bytes (drives the token+store indirection, D8); per-chat
sequential / cross-chat concurrent processing (FR-015); webhooks and getUpdates are mutually
exclusive (deleteWebhook before polling); `answerCallbackQuery` required for every press including
unknown ones (FR-007, FR-010).

**Scale/Scope**: PoC — single bot identity, in-memory store, one update kind (button presses). Idle
per-chat channel eviction and durable stores are noted follow-ups, out of scope here.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| # | Principle | Gate | Status |
|---|-----------|------|--------|
| I | Test-First + property tests | Pure kernel (token codec, `KeyboardPlan.assign`, `Routing.decide`, dispatcher ordering) is TDD'd with FsCheck; no impl before a failing test | ✅ PASS |
| II | F# core + dual idiomatic APIs | Core F#; separate `TgLLM.FSharp` + `TgLLM.CSharp` packages; UMX-erased ids, nullable-not-Option, reflection canary test guards leakage | ✅ PASS |
| III | Layered architecture | Single dependency direction; core has no HTTP/hosting/JSON dep; Telegram.Bot & ASP.NET confined to leaf adapters | ✅ PASS |
| IV | Both transports | `IUpdateSource` with long-polling + webhook adapters; identical hook code; SC-008 runs the scenario over both | ✅ PASS |
| V | Vendor-grounded | Design grounded in core.telegram.org + Microsoft Learn (research.md); Telegram.Bot absorbs wire fidelity; explicit verify-list in research D7 | ✅ PASS |
| VI | Task-based concurrency | `Task`/`ValueTask` throughout; no `Async<'T>`; Channels are `ValueTask`-based | ✅ PASS |
| VII | English, current docs | quickstart.md + examples authored with the feature; all docs/comments English; docs cite code & vendor docs only | ✅ PASS |
| VIII | Open-source excellence | Examples for both transports × both languages; README/CI/license are repo-level deliverables tracked in tasks | ✅ PASS |

**Initial gate (pre-research)**: PASS — no principle blocked the approach.
**Post-design gate (post-Phase 1)**: PASS — the design *encodes* the principles (ports for III/IV,
dual façades for II, pure kernel for I). No violations → Complexity Tracking is empty.

## Project Structure

### Documentation (this feature)

```text
specs/001-inline-keyboard-hooks/
├── plan.md              # This file
├── spec.md              # Feature spec (with Clarifications)
├── research.md          # Phase 0 decisions (D1–D9)
├── data-model.md        # Phase 1 domain model
├── quickstart.md        # Phase 1 usage guide
├── contracts/           # Phase 1 API contracts
│   ├── core-ports.md
│   ├── fsharp-facade.md
│   └── csharp-facade.md
└── checklists/
    └── requirements.md   # Spec quality checklist
```

### Source Code (repository root)

```text
src/
├── TgLLM.Core/          # F#  Domain + ports + pure kernel + UpdateProcessor + default impls
│                        #     (InMemoryHookStore, PerChatChannelDispatcher, NoopHookObserver).
│                        #     Deps: FSharp.Core, FSharp.UMX. NO HTTP/hosting/JSON.
├── TgLLM.BotApi/        # F#  Telegram.Bot-backed IBotApiClient + LongPollingUpdateSource +
│                        #     Telegram.Bot→domain mapping. Deps: Telegram.Bot, TgLLM.Core.
├── TgLLM.Webhooks/      # F#  Host-agnostic WebhookUpdateSource (Channel), secret-token check (pure),
│                        #     update parsing. Deps: Telegram.Bot, TgLLM.Core. NO ASP.NET dep.
├── TgLLM.AspNetCore/    # F#  MapTelegramWebhook endpoint glue. FrameworkReference
│                        #     Microsoft.AspNetCore.App lives ONLY here. Deps: TgLLM.Webhooks.
├── TgLLM.FSharp/        # F#  Public idiomatic F# façade (NuGet #1). Deps: Core, BotApi, Webhooks.
└── TgLLM.CSharp/        # C#  Public idiomatic C# façade (NuGet #2). Deps: Core, BotApi, Webhooks.

tests/
├── TgLLM.Core.Tests/         # F#  Expecto + FsCheck: token codec, keyboard planning, routing, ordering
├── TgLLM.CSharp.Tests/       # C#  xUnit v3 + FsCheck.Xunit.v3: façade behavior + idiom-leak canary
└── TgLLM.Integration.Tests/  # F#  Fake Bot API (Kestrel TestServer); both transports (SC-008);
                              #     failure isolation (SC-006); ordering (SC-007)

examples/
├── PollingFSharp/  ├── PollingCSharp/  ├── WebhookFSharp/  └── WebhookCSharp/
```

**Structure Decision**: Multi-project library. Project count is driven directly by the constitution,
not gratuitous: dual façades (Principle II) → two packages; transport isolation + both transports
(Principles III, IV) → separate `BotApi`/`Webhooks`/`AspNetCore` adapters behind ports; IO-agnostic
core (Principle III) → a dependency-free `TgLLM.Core`. `TgLLM.Protocol` (a wire-DTO layer) is
intentionally **absent** for the PoC because Telegram.Bot owns wire (research D1).

## Complexity Tracking

> No Constitution Check violations. This table is intentionally empty.

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| — | — | — |
