---
description: "Task list for feature 004-a2ui-renderer"
---

# Tasks: A2UI Renderer for Telegram

**Input**: Design documents from `specs/004-a2ui-renderer/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/, quickstart.md

**Tests**: MANDATORY (constitution Principle I — Test-First, NON-NEGOTIABLE). Each `[test]` task writes
failing tests that MUST fail before its paired implementation makes them pass; property tests (FsCheck)
are required for the pure mapping and the parse round-trip.

**Invariant (FR-012)**: additive on the green slice-1/2/3 library. The **slice-1/2/3 suite (286 tests)
MUST stay green** throughout; every phase re-runs the full suite. `dotnet build`/`dotnet test` are the
final oracle. **Core (`TgLLM.Core`) MUST NOT gain any A2UI dependency** — A2UI lives only in `TgLLM.A2UI`.

**PRINCIPLE VII (mandatory)**: NO code comment, XML-doc, or test-name/assertion string may cite
`FR-###`, `SC-###`, `US#`, task IDs (`T0xx`), `research D#`/`D#`, spec file names, "review finding #N",
or branch/feature slugs. Describe BEHAVIOR only; cite only code symbols, the A2UI protocol, and Telegram/
vendor docs. ("Principle N" = the constitution, allowed.)

**Organization**: grouped by user story (US1 render → US2 loop → US3 streaming → US4 catalog).

## Format: `[ID] [P?] [Story?] Description`

- **[P]**: different files, no dependency on an incomplete task
- **[Story]**: US1 / US2 / US3 / US4 (story phases only)
- **[test]**: test-first; its failing tests gate the paired implementation

---

## Phase 1: Setup

- [ ] T001 Create the new leaf project `src/TgLLM.A2UI` (F#; ProjectReference `TgLLM.Core`; PackageReference `System.Text.Json` via framework + `FSharp.SystemTextJson`) and test project `tests/TgLLM.A2UI.Tests` (Expecto + FsCheck); add both to `TgLLM.NET.sln`
- [ ] T002 [P] Create example skeletons `examples/A2UIFSharp` (F#) and `examples/A2UICSharp` (C#), added to the solution; confirm no new central package versions are needed beyond `FSharp.SystemTextJson` (already present)

**Checkpoint**: solution builds with the new empty projects; slice-1/2/3 suite still green.

---

## Phase 2: Foundational (the A2UI model + pure renderer — blocks all stories)

- [ ] T003 [P] [test] Failing FsCheck + example tests for A2UI message parsing (`createSurface`/`updateComponents`/`updateDataModel`/`deleteSurface`, `version` "v1.0"; a malformed/missing-field message → `MalformedMessage`, never a throw) in `tests/TgLLM.A2UI.Tests/A2uiParseTests.fs`
- [ ] T004 Implement the `A2uiMessage` model + `A2uiError` + a total `parse: string -> A2uiParse` (System.Text.Json) in `src/TgLLM.A2UI/A2uiMessage.fs` to green T003
- [ ] T005 [P] [test] Failing tests for `DynString` resolution (Literal → itself; Bound absolute JSON-Pointer → data-model value; unresolved path → empty string) in `tests/TgLLM.A2UI.Tests/DynStringTests.fs`
- [ ] T006 Implement `DynString` + absolute JSON-Pointer resolution against a `JsonNode` data model in `src/TgLLM.A2UI/` to green T005
- [ ] T007 [P] [test] Failing tests for MarkdownV2 escaping (every reserved char `_ * [ ] ( ) ~ ` > # + - = | { } . !` escaped; a documented subset — bold/italic/code/link — passes through) in `tests/TgLLM.A2UI.Tests/MarkdownTests.fs`
- [ ] T008 Implement the MarkdownV2 escaper/mapper in `src/TgLLM.A2UI/` to green T007 (verify the reserved-char list against core.telegram.org)
- [ ] T009 [P] [test] Failing tests for the `telegram-basic` `Catalog` (advertises the supported set; `Supports "Text"/"Button"/"Row"/"Column"/"Divider"/"Image"` true; `"Slider"/"TextField"/...` false) in `tests/TgLLM.A2UI.Tests/CatalogTests.fs`
- [ ] T010 Implement the `Catalog` (telegram-basic) + `Component`/`ComponentNode`/`ButtonAction` model + a narrower `toTelegramBasic` that maps a raw component to a node or `Unsupported name`, in `src/TgLLM.A2UI/` to green T009
- [ ] T011 [P] [test] Failing FsCheck property tests for the PURE `Renderer.render` (component tree + data model → `RenderedSurface`): Row ⇒ one keyboard row, Column ⇒ stacked rows, Text ⇒ concatenated MarkdownV2 body, `ServerEvent` Button ⇒ a tool button carrying its `ActionDescriptor`, `LocalOpenUrl` Button ⇒ a URL button, an `Unsupported` component ⇒ recorded in `Unsupported` with supported siblings intact, no `root` ⇒ nothing renders — in `tests/TgLLM.A2UI.Tests/RendererTests.fs`
- [ ] T012 Implement the pure `Renderer.render` (build the MarkdownV2 body, resolve DynStrings, build a `ToolKeyboard` where each `ServerEvent` Button becomes a tool button whose structured arg is its `ActionDescriptor`, collect `Unsupported`) in `src/TgLLM.A2UI/Renderer.fs` to green T011

**Checkpoint**: parse + pure renderer + catalog green (property-tested); Core carries no A2UI dep; slice-1/2/3 green.

---

## Phase 3: User Story 1 — Render an A2UI surface as a Telegram message (Priority: P1) 🎯 MVP

**Goal**: `createSurface`+`updateComponents` renders one Telegram message whose text + keyboard layout match the tree.

- [ ] T013 [US1] [test] Failing tests for the `SurfaceRegistry` (`Apply (chat, createSurface)` records a `LiveSurface`, produces a `SendNew` effect once `root` is present; a second `createSurface` for a live id → `DuplicateSurface`; an `updateComponents` for an unknown surface → `UnknownSurface`) in `tests/TgLLM.A2UI.Tests/SurfaceRegistryTests.fs`
- [ ] T014 [US1] Implement the thread-safe in-memory `SurfaceRegistry` (surface↔`(chat,message)`, component/data-model state, `Apply` → `RenderEffect`) in `src/TgLLM.A2UI/SurfaceRegistry.fs` to green T013
- [ ] T015 [US1] Register the internal `a2ui-action` tool once into a bot's Tool Router, and wire `A2ui.renderer bot sink` + `A2uiRenderer.Ingest(chat, json)` (parse → registry.Apply → carry out `SendNew` by building the plan via `ToolPlan.plan` and sending) in `src/TgLLM.A2UI/` + `src/TgLLM.FSharp/`
- [ ] T016 [P] [US1] Expose the C# façade `A2uiRenderer.Create(agent, sink)` + `IngestAsync` with C#-idiomatic DTOs (`A2uiAction`, `A2uiIngestResult`, `Catalog` — no `FSharpOption`/`FSharpFunc`) in `src/TgLLM.CSharp/`; extend the idiom-leak canary
- [ ] T017 [US1] [test] Integration: a `createSurface`+`updateComponents` (Text + two Buttons in a Row) sends one message with the right body + one keyboard row, over BOTH transports and BOTH façades, in `tests/TgLLM.Integration.Tests/A2uiRenderTests.fs` and a C# test

**Checkpoint**: US1 renders a static surface end-to-end; slice-1/2/3 green.

---

## Phase 4: User Story 2 — Tap → action → re-render loop (Priority: P2)

**Goal**: a Button tap emits an A2UI `action` to the host sink; the agent's update edits the message in place.

- [ ] T018 [US2] [test] Failing tests: the `a2ui-action` tool handler resolves the descriptor's context against the surface data model, builds an `A2uiAction` (timestamp from the injected `Clock`), and calls the sink; a `wantResponse` action with no `actionId` is surfaced as malformed, in `tests/TgLLM.A2UI.Tests/ActionTests.fs`
- [ ] T019 [US2] Implement the `a2ui-action` handler + `A2uiAction` builder (injected clock) + sink delivery in `src/TgLLM.A2UI/` to green T018
- [ ] T020 [US2] Carry out `updateComponents`/`updateDataModel` on a live surface as edit-in-place (`EditExisting` → reuse slice-3 edit + soft edit errors) and `deleteSurface` as delete, in `src/TgLLM.A2UI/` + façades; `LocalOpenUrl` Button → URL button (no callback)
- [ ] T021 [US2] [test] Integration: render a surface with a Button; simulate a tap → the sink receives the correct `A2uiAction`; feed the agent's `updateComponents` reply → the SAME message is edited (no new message); `deleteSurface` deletes it; a `LocalOpenUrl` button emits no action — over both transports/façades, in `tests/TgLLM.Integration.Tests/A2uiLoopTests.fs` + a C# test

**Checkpoint**: US2 full loop green; slice-1/2/3 green.

---

## Phase 5: User Story 3 — Streaming and incremental updates (Priority: P3)

**Goal**: a burst of messages for one surface coalesces into one send then edits.

- [ ] T022 [US3] [test] Failing tests: `createSurface` + two `updateComponents` for the same surface flush to exactly one message (send then edit), not three; an `updateDataModel` changing a bound value edits the message text in place, in `tests/TgLLM.Integration.Tests/A2uiStreamingTests.fs`
- [ ] T023 [US3] Implement per-surface coalescing (buffer + render-on-flush; render only when `root` is present, `NoEffect` otherwise) in `src/TgLLM.A2UI/SurfaceRegistry.fs` to green T022

**Checkpoint**: US3 coalescing green; slice-1/2/3 green.

---

## Phase 6: User Story 4 — Catalog and unsupported components (Priority: P4)

**Goal**: unknown catalog / unsupported component / malformed message are surfaced, never silently wrong.

- [ ] T024 [US4] [test] Failing tests: `createSurface` with an unknown `catalogId` → surfaced `UnknownCatalog`, nothing rendered; an `updateComponents` with a `TextField` (outside telegram-basic) → surfaced `UnsupportedComponent`, supported siblings intact; a malformed message → surfaced `MalformedMessage`, bot keeps working, in `tests/TgLLM.A2UI.Tests/` and `tests/TgLLM.Integration.Tests/A2uiUnsupportedTests.fs`
- [ ] T025 [US4] Wire catalog matching + unsupported/malformed surfacing through the observability seam (reuse `IHookObserver` or a small A2UI observer) in `src/TgLLM.A2UI/` + façades to green T024

**Checkpoint**: US4 surfacing green; slice-1/2/3 green.

---

## Phase 7: Polish & cross-cutting

- [ ] T026 [P] Build `examples/A2UIFSharp` and `examples/A2UICSharp` — an A2UI-speaking demo (ingest a surface, wire a sink that logs/echoes the action, handle a tap → update) over the `TRANSPORT` env var; must compile and run to the network boundary
- [ ] T027 [P] Update user docs: `docs/quickstart.md` (an "A2UI renderer" section), `README.md` (headline: "A2UI renderer for Telegram"), `CHANGELOG.md` (Unreleased → slice 004) — English, cite code/A2UI/Bot API only (Principle VII)
- [ ] T028 Final comprehensive Principle VII sweep across `src/`+`tests/`+`examples/` (strip any `FR-###`/`SC-###`/`US#`/`T0xx`/`research D#`/spec-file/slug citations from comments/test names) — keep the full suite green
- [ ] T029 Full-suite acceptance: run the entire suite over both transports and both façades; confirm slice-1/2/3 tests unchanged, Core carries no A2UI dependency (`rg` the Core project references), the C# canary shows no idiom leak, and SC-001..SC-008 are covered
- [ ] T030 Final `dotnet build -c Release` (0 warnings/0 errors) + `dotnet test` green

---

## Dependencies & order

- **Setup (T001–T002)** → **Foundational (T003–T012)** → user stories.
- **US1 (T013–T017)** is the MVP; depends only on Foundational.
- **US2 (T018–T021)** depends on US1 (needs a rendered surface with buttons); **US3 (T022–T023)** and
  **US4 (T024–T025)** depend on US1 and are largely independent of US2.
- **Polish (T026–T030)** last.
- Every phase re-runs the full suite; the 286 slice-1/2/3 tests MUST stay green throughout (FR-012), and
  Core must remain A2UI-free.

## Suggested MVP

Foundational + **US1** (render an A2UI surface as a Telegram message) — the smallest shippable increment:
an A2UI-speaking agent can draw a static interactive keyboard in Telegram.
