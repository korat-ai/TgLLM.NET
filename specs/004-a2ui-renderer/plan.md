# Implementation Plan: A2UI Renderer for Telegram

**Branch**: `004-a2ui-renderer` | **Date**: 2026-07-07 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `specs/004-a2ui-renderer/spec.md`

## Summary

A new `TgLLM.A2UI` leaf project renders the **Telegram-representable subset** (`telegram-basic` catalog)
of Google's open A2UI protocol onto the existing Tool Router + edit-in-place, **bidirectionally**:
`createSurface`/`updateComponents` → send a message (Text → Markdown body, Button → inline keyboard,
Row/Column → layout); a Button tap → an A2UI `action` message handed to a host sink; the agent's
follow-up updates → edit-in-place; `deleteSurface` → delete. The renderer advertises only
`telegram-basic` and **surfaces** (never silently drops) any component it can't render. **Core stays
A2UI-agnostic** — A2UI lives entirely in the new leaf project (v1.0 is a candidate spec we stay aware of,
not coupled to). Everything is additive; slices 1–3 APIs and tests are untouched (FR-012).

**Key reuse**: each A2UI Button maps to a Tool Router **tool button** whose tool is an internal
`a2ui-action` handler and whose **structured argument** (slice-2 payload) is the serialized action
descriptor (`surfaceId` + `sourceComponentId` + action `name` + context bindings). A tap therefore rides
the *hardened* slice-2/3 routing, deferred-ack, edit-in-place, and durable binding store with no new
engine — the A2UI renderer is a mapping layer, not a second dispatcher.

## Technical Context

<!-- Extends slices 1–3; the engine is reused. A2UI v1.0 schema grounded against a2ui.org. -->

**Language/Version**: F# core (unchanged, A2UI-agnostic) + a new F# leaf `TgLLM.A2UI` + dual C#/F#
façades. .NET single-target `net10.0` (as before).

**Primary Dependencies**: `TgLLM.Core` (Tool Router routing, edit-in-place, binding store — reused);
`System.Text.Json` + `FSharp.SystemTextJson` (A2UI message parsing / action-message emission — in the
A2UI leaf and façades, NOT in Core). Telegram.Bot stays confined to transport. No A2UI-vendor SDK — we
implement the open spec directly. No LLM-vendor SDKs.

**Storage**: reuses the Tool Router binding store — a Button's binding (token → `a2ui-action` +
descriptor payload, + owner/expiry as available) is durable, so a tap on a pre-restart surface still
emits its `action` (FR-010). The live **surface registry** (component tree + data model per open
surface, for coalescing and re-render) is in-memory per bot; a restart loses the ability to *re-render*
a pre-restart surface on a later `updateComponents`, but a tap still routes (durable binding) — an
accepted MVP limitation, documented.

**Testing**: Expecto + FsCheck (F#); xUnit v3 + FsCheck.Xunit.v3 (C#). **Property tests for the pure
mapping**: an A2UI `telegram-basic` component tree → (message text, keyboard plan) is a pure function —
properties for layout (Row → one row, Column → stacked), Text concatenation/Markdown, Button → callback
button, and "unsupported component surfaced, supported siblings intact." Parse round-trip properties for
the A2UI message model. Integration: the full render → tap → `action` → agent update → re-render loop
over both transports and both façades.

**Target Platform**: Cross-platform .NET library (NuGet).

**Project Type**: Library (extends the slice-1/2/3 multi-project solution; one new leaf project).

**Performance Goals**: Rendering a surface is O(tree size); coalescing collapses a burst of messages for
one surface into one send/edit; taps stay O(1) through the reused router.

**Constraints**: A2UI carries no chat identity → the **host supplies the target chat** at ingest (the
renderer's ingest takes `(chat, a2uiMessage)`). One **surface = one message** `(chat, message_id)`.
`createSurface` is **create-once** (duplicate id → surfaced error). Unresolved data path → **empty
string**. Streaming is **coalesced** (whole-message send/edit, not partial). Only the `telegram-basic`
catalog renders; anything else is a surfaced error/unsupported report. Telegram Markdown escaping
(MarkdownV2 vs legacy) is a mapping concern the renderer owns (verify against core.telegram.org).

**Scale/Scope**: PoC — single bot. In scope: telegram-basic render (Text/Button/Row/Column/Divider/
Image), the tap→action→re-render loop, per-surface coalescing, catalog + unsupported-component surfacing.
Out of scope (backlog): WebApp fallback for a whole rich surface; input components + two-way binding;
concrete streaming throttle policy; nested JSON-Pointer / Collection-Scope / `List` templates; net8 CI,
NuGet, CS1591, live-Telegram smoke.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| # | Principle | Gate | Status |
|---|-----------|------|--------|
| I | Test-First + property tests | The pure component-tree → (text, keyboard) mapping + A2UI parse round-trip are property-tested with FsCheck; integration for the loop | ✅ PASS |
| II | F# core + dual idiomatic APIs | A2UI ingest + action-sink in both façades; no `FSharpOption`/`FSharpFunc` on the C# surface (canary extended) | ✅ PASS |
| III | Layered architecture | A2UI lives ONLY in the new `TgLLM.A2UI` leaf; Core stays A2UI-agnostic (FR-012), no A2UI dependency in Core | ✅ PASS |
| IV | Both transports | The renderer rides the reused `UpdateProcessor`/edit-in-place; the loop runs over long polling AND webhooks (SC-007) | ✅ PASS |
| V | Vendor-grounded | A2UI v1.0 message/component/action schema grounded against a2ui.org; Telegram Markdown/edit facts verified against core.telegram.org | ✅ PASS |
| VI | Task-based concurrency | `Task`/`ValueTask` throughout; the surface registry is a thread-safe in-memory map; no `Async` | ✅ PASS |
| VII | English, current docs | A2UI quickstart + example authored with the feature; docs cite code / A2UI / Bot API only (no spec refs) | ✅ PASS |
| VIII | Open-source excellence | An A2UI-speaking example (both languages); README/CHANGELOG updated; "A2UI renderer for Telegram" is the headline differentiator | ✅ PASS |

**Initial gate**: PASS. **Post-design gate**: PASS — additive; slice-1/2/3 tests stay green (FR-012).
One justified structural addition (the A2UI leaf project) tracked below.

## Project Structure

### Documentation (this feature)

```text
specs/004-a2ui-renderer/
├── plan.md            # This file
├── spec.md            # Feature spec (with Clarifications)
├── research.md        # Phase 0 decisions + A2UI v1.0 facts + the telegram-basic mapping table
├── data-model.md      # Phase 1 domain model (A2UI message/component model, surface registry, action msg)
├── quickstart.md      # Phase 1 usage (ingest an A2UI surface, wire the action sink)
├── contracts/a2ui-renderer.md   # Phase 1 public surface (F# + C#)
└── checklists/requirements.md
```

### Source Code (delta on the slice-1/2/3 solution)

```text
src/
├── TgLLM.Core/          # UNCHANGED — stays A2UI-agnostic (FR-012). No A2UI dependency.
├── TgLLM.A2UI/          # NEW (F#): A2UI message model + parse (createSurface/updateComponents/
│                        #   updateDataModel/deleteSurface, version "v1.0"); the `telegram-basic` catalog;
│                        #   the PURE renderer (component tree + data model → message text + keyboard plan,
│                        #   unsupported → surfaced); the surface registry (coalesce + surface↔(chat,msg));
│                        #   the A2UI `action`-message builder; the internal `a2ui-action` tool + descriptor
│                        #   payload. Deps: TgLLM.Core, System.Text.Json, FSharp.SystemTextJson.
├── TgLLM.FSharp/        # + A2UI façade: ingest `(chat, a2uiMessage)`, wire the host action sink
└── TgLLM.CSharp/        # + A2UI façade equivalent (no F# idioms on the surface)

tests/
├── TgLLM.A2UI.Tests/           # NEW: FsCheck property tests for the pure mapping + parse round-trip; catalog
├── TgLLM.Integration.Tests/    # + render → tap → action → re-render loop over both transports; slice-1/2/3 green
└── TgLLM.CSharp.Tests/         # + C# A2UI façade; idiom-leak canary re-run

examples/
└── A2UIFSharp / A2UICSharp     # NEW: an A2UI-speaking demo bot (both languages)
```

**Structure Decision**: Add exactly one src project (`TgLLM.A2UI`) plus its test project and examples.
Core is untouched (A2UI-agnostic, FR-012). The renderer reuses the Tool Router routing, edit-in-place,
and durable binding store — a mapping layer, not a new engine.

## Complexity Tracking

> One justified structural addition; otherwise no Constitution Check violations.

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| New leaf project `TgLLM.A2UI` | A2UI is a specific (candidate) protocol; isolating it in a leaf keeps Core A2UI-agnostic (FR-012, Principle III) and lets the library ship without A2UI for consumers who don't want it — the same rationale that keeps IO and vendor formats out of Core | Putting A2UI in Core couples the domain kernel to a moving external spec and forces the dependency on every consumer; putting it in the façades duplicates it across C#/F# and mixes protocol logic into idiom-translation code |
