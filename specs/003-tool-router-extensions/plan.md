# Implementation Plan: Tool Router Extensions

**Branch**: `003-tool-router-extensions` | **Date**: 2026-07-06 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `specs/003-tool-router-extensions/spec.md`

## Summary

Four additive extensions on the green slice-1/2 library, all riding the fact that a binding is a
**server-side record** we can enrich: (US1) **press authorization** — a keyboard can be scoped to an
owner, and a non-owner tap on a tool button is refused; (US2) a neutral **tool manifest** the registry
emits for LLM function-calling, plus **structured arguments** (an opaque, possibly-JSON payload that
supersedes the slice-2 string); (US3) **WebApp and CopyText** buttons; (US4) **lifecycle & reliability**
— binding expiry/eviction, idle per-chat reclaim, at-most-once redelivery, soft edit-error handling,
and a second durable store (embedded SQLite). The server-side binding record evolves **once** (owner +
expiry + single-use), backward-compatible with slice-2 records. Everything is additive and opt-in; the
slice-1 hook API and slice-2 Tool Router API — and their tests — are untouched (FR-019).

This plan also **folds in reliability findings** from an adversarial review of slices 1–2. The
confirmed availability/data-corruption blockers (run-loop supervision + polling retry; per-chat tracker
keying; the decorative ack-policy claim; the C# façade doc/token gaps; the dead-keyboard guard) are
fixed as a separate hardening pass on the same branch. The **architectural** findings are designed into
this slice: deferred-ack-at-enqueue and off-loop acking (US4), `deliver` failure-ordering/orphan
compensation (US2/US4), a C#-facing `IBindingStore` adapter to stop the `FSharpOption` leak on the
store surface (US2), acking dropped non-canonical callback queries (US4), TTL/eviction (US4), and the
missing property tests (across US test tasks).

## Technical Context

<!-- Extends slices 1–2; stack is fixed. New design decisions are captured in research.md. -->

**Language/Version**: F# core + F# façade + adapters; C# façade. .NET — single-target `net10.0` for
now (as slices 1–2; the `net8.0;net10.0` shipping matrix stays a CI/backlog item).

**Primary Dependencies**: Telegram.Bot 22.10.1 (WebApp/CopyText buttons, edit/answer — behind ports);
FSharp.Core; FSharp.UMX; System.Text.Json (façades — structured-arg serialization + manifest emission;
NOT added to Core). **Microsoft.Data.Sqlite** (new SQLite store leaf project only). No LLM-vendor SDKs
(the manifest is neutral, FR-004).

**Storage**: The binding-store seam gains a second durable backend — an **embedded SQLite** store in a
new leaf project `TgLLM.Persistence.Sqlite`; the in-memory default and the slice-2 file store are
unchanged. The binding record evolves once (owner + expiry + single-use); slice-2 records still load
(FR-017). Stores support expiry-based eviction.

**Testing**: Expecto + FsCheck (F#); xUnit v3 + FsCheck.Xunit.v3 (C#). New property tests: owner match,
manifest emission (every registered tool present, neutral shape), structured-payload round-trip, expiry
decision (clock injected, not ambient), at-most-once dedup — plus the review's property gaps (duplicate
input-token consumption, `validate` Ok ⇒ `plan` Ok, non-canonical token rejection, URL passthrough).
Integration tests (against the fake Bot API server, extended for WebApp/CopyText and per-chat message
ids): owner auth, WebApp/CopyText client-side buttons, expiry/eviction/at-most-once/soft-edit, SQLite
restart (SC-010) — under both transports and both façades.

**Target Platform**: Cross-platform .NET library (NuGet).

**Project Type**: Library (extends the slice-1/2 multi-project solution; one new leaf project).

**Performance Goals**: Owner check + resolution stay O(1) per tap; ack within the SC-003 spinner budget
even when a slow tool is queued behind another (US4 moves acking to enqueue-time / off the ingestion
loop, closing a review finding); idle per-chat resources reclaimed across ≥1000 short-lived chats
(SC-007) without dropping/reordering in-flight presses.

**Constraints**: Owner scope is enforceable **only on tool (callback) buttons** — URL/WebApp/CopyText
are client-side, no callback reaches the bot (spec Clarifications). WebApp URLs MUST be https and are
launch-only this slice; CopyText text ≤ Telegram's limit (256 chars). At-most-once is per
`callback_query.id` (redelivery), not per user re-tap; single-use bindings are the opt-in confirm-once
mode. Expiry uses an **injected clock** (no ambient `DateTime.Now` in Core). The structured payload is
carried **opaquely** — Core keeps `Arg : string option` (reinterpreted as a possibly-JSON payload); the
façades own (de)serialization, so Core stays System.Text.Json-free and slice-2 string args keep working.

**Scale/Scope**: PoC — single bot. In scope: owner auth, neutral manifest, structured payload, WebApp/
CopyText, expiry/eviction, at-most-once, SQLite store. Out of scope (backlog): net8 CI leg, NuGet
publish, CS1591 coverage, live-Telegram smoke; WebApp postback handling; a networked (Redis) store.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| # | Principle | Gate | Status |
|---|-----------|------|--------|
| I | Test-First + property tests | Owner match / manifest / payload round-trip / expiry / dedup TDD'd with FsCheck; the review's property gaps added; integration for each US | ✅ PASS |
| II | F# core + dual idiomatic APIs | Auth/manifest/structured-args/new buttons in both façades; a **C#-facing `IBindingStore` adapter** removes the `FSharpOption` leak on the store surface; canary extended to walk store members | ✅ PASS |
| III | Layered architecture | Owner/manifest/expiry/dedup logic in Core (IO-agnostic; clock injected); SQLite IO in a NEW leaf project, NOT in Core | ✅ PASS |
| IV | Both transports | All capabilities ride the same `IUpdateSource`/`UpdateProcessor`; SC-011 runs over both; dropped-callback ack fixed in both transports | ✅ PASS |
| V | Vendor-grounded | WebApp (`web_app`/https), CopyText (`copy_text`/256), owner-vs-`from`, `message_id` per-chat, edit-error strings verified against core.telegram.org | ✅ PASS |
| VI | Task-based concurrency | `Task`/`ValueTask` throughout; eviction/dedup via timers/bounded sets; polling retry/backoff honors `ct`; no `Async` | ✅ PASS |
| VII | English, current docs | quickstart + examples updated with the feature; docs cite code/vendor only (no spec refs) | ✅ PASS |
| VIII | Open-source excellence | Auth/manifest examples added; README/CHANGELOG updated; every commit release-ready | ✅ PASS |

**Initial gate**: PASS. **Post-design gate**: PASS — additive design; slice-1/2 tests remain green
(FR-019). One justified structural addition (SQLite leaf project) tracked below.

## Project Structure

### Documentation (this feature)

```text
specs/003-tool-router-extensions/
├── plan.md            # This file
├── spec.md            # Feature spec (with Clarifications)
├── research.md        # Phase 0 decisions + Bot API facts (owner/webapp/copytext/dedup/eviction)
├── data-model.md      # Phase 1 domain model (binding evolution, manifest, button DU, owner scope)
├── quickstart.md      # Phase 1 usage (auth + manifest + structured args + new buttons + SQLite)
├── contracts/tool-router-extensions.md   # Phase 1 public surface delta (F# + C#)
└── checklists/requirements.md
```

### Source Code (delta on the slice-1/2 solution)

```text
src/
├── TgLLM.Core/          # + OwnerScope (Anyone | User); ToolBinding evolves (owner, expiry, single-use)
│                        #   read-compatible with slice-2; PlanButton/RegisteredButton DU + WebApp/CopyText;
│                        #   ToolManifest + emission from IToolRegistry (name/description/JSON-Schema);
│                        #   optional tool metadata (description/argSchema); expiry decision (clock injected);
│                        #   at-most-once dedup (bounded seen-set by callback_query id); binding eviction seam;
│                        #   ToolKeyboardOps.deliver failure-ordering fix (remove-old only after send + compensate);
│                        #   owner check in the resolve/route step (tool buttons only)
├── TgLLM.Persistence/          # (file store) + expiry-aware eviction on the store seam
├── TgLLM.Persistence.Sqlite/   # NEW (F#): SqliteBindingStore : IBindingStore (Microsoft.Data.Sqlite).
│                               #   Deps: Core, Microsoft.Data.Sqlite. Isolated so file-store users don't inherit it.
├── TgLLM.BotApi/        # + WebApp/CopyText button mapping; edit-error classification (not-modified/not-found);
│                        #   ack (or ack-only event) for dropped non-canonical callback queries; message_id per-chat
├── TgLLM.Webhooks/      # + same dropped-callback ack path (parity with long polling)
├── TgLLM.FSharp/        # + owner-scoped send; Plan.webApp/copyText; Plan.toolWith<'T> (structured);
│                        #   ctx.GetArg<'T>(); manifest emission; expiry/single-use options; idle-eviction config
└── TgLLM.CSharp/        # + same surface; C#-facing IBindingStore adapter (nullable ToolBinding? / DTOs);
                         #   PlanRowBuilder.WebApp/.CopyText/.Tool<T>; manifest export; owner scoping

tests/
├── TgLLM.Core.Tests/            # + property tests: owner match, manifest, payload round-trip, expiry, dedup,
│                                #   + review gaps (dup-token, validate⇒plan, non-canonical reject, url passthrough)
├── TgLLM.Persistence.Tests/     # (file) + backward-compat load of slice-2 records; expiry eviction
├── TgLLM.Persistence.Sqlite.Tests/  # NEW: round-trip, restart persistence (SC-010), slice-2 record read
├── TgLLM.Integration.Tests/     # + owner auth (US1), WebApp/CopyText (US3), expiry/eviction/at-most-once/
│                                #   soft-edit (US4), manifest (US2), both transports; slice-1/2 stay green
└── TgLLM.CSharp.Tests/          # + C# auth/manifest/structured-args/new buttons; canary extended (store members)

examples/
└── ToolRouterFSharp / ToolRouterCSharp   # extended: owner-scoped keyboard + emitted manifest + SQLite store
```

**Structure Decision**: Extend the slice-1/2 solution; add exactly one src project
(`TgLLM.Persistence.Sqlite`) plus its test project. Everything else is additive edits to existing
projects. The slice-1/2 public API and tests are preserved (FR-019).

## Complexity Tracking

> One justified structural addition; otherwise no Constitution Check violations.

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| New leaf project `TgLLM.Persistence.Sqlite` (not folding SQLite into `TgLLM.Persistence`) | A durable backend with a native dependency (`Microsoft.Data.Sqlite`) must be isolated so file-store-only consumers don't transitively inherit it — the same layering rationale that keeps IO out of Core | Putting `SqliteBindingStore` in the existing `TgLLM.Persistence` project would force the SQLite dependency onto every file-store user; a single "kitchen-sink" persistence project couples unrelated backends |
