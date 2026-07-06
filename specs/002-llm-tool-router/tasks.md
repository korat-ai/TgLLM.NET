---
description: "Task list for feature 002-llm-tool-router"
---

# Tasks: LLM Tool Router

**Input**: Design documents from `specs/002-llm-tool-router/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/, quickstart.md

**Tests**: MANDATORY (constitution Principle I — Test-First, NON-NEGOTIABLE). Each `[test]` task
writes failing tests that MUST fail before its paired implementation makes them pass; property tests
(FsCheck) are required for the pure kernel.

**Invariant (FR-012)**: this feature is ADDITIVE on the green slice-1 library. The **62 slice-1 tests
MUST stay green** throughout; every phase re-runs the full suite.

**Organization**: grouped by user story (US1 core → US2 reactions → US3 persistence → US4 URL).

## Format: `[ID] [P?] [Story?] Description`

- **[P]**: different files, no dependency on an incomplete task
- **[Story]**: US1 / US2 / US3 / US4 (story phases only)
- **[test]**: test-first; its failing tests gate the paired implementation

---

## Phase 1: Setup

- [X] T001 Create the new project `src/TgLLM.Persistence` (F#; ProjectReference `TgLLM.Core`) and test project `tests/TgLLM.Persistence.Tests` (Expecto + FsCheck), plus example skeletons `examples/ToolRouterFSharp` (F#) and `examples/ToolRouterCSharp` (C#); add all to `TgLLM.NET.sln` with correct references
- [X] T002 [P] Confirm no new central packages are required (System.Text.Json ships in the framework); if the file store needs any, add the version to `Directory.Packages.props`

**Checkpoint**: solution builds with the new empty projects; slice-1 suite still green.

---

## Phase 2: Foundational (shared additive kernel — blocks all stories)

**⚠️ CRITICAL**: keep slice-1 behavior intact. `RegisteredButton` becomes a DU (T007) — update the
slice-1 mapping and its test to the `Callback` case; callback-button behavior is unchanged.

- [X] T003 [P] [test] Failing tests for `ToolName.create` (non-empty after trim) and `ToolError` cases in `tests/TgLLM.Core.Tests/ToolNameTests.fs`
- [X] T004 Implement `ToolName` (smart constructor) and `ToolError` in `src/TgLLM.Core/Tools.fs` to green T003
- [X] T005 Define the neutral plan types `PlanButton` (`ToolButton` | `UrlButton`) and `ToolKeyboard` in `src/TgLLM.Core/Tools.fs`
- [X] T006 [P] [test] Failing FsCheck property tests for `ToolPlan.plan` (row/label shape preserved; one token+binding per tool button; URL buttons carry no binding; token count = tool-button count; distinct input tokens ⇒ distinct button tokens) in `tests/TgLLM.Core.Tests/ToolPlanTests.fs`
- [X] T007 Implement `RegisteredButton` as a DU (`Callback of label*token` | `Url of label*url`), `ToolBinding`, and `ToolPlan.plan` (assign tokens to tool buttons, pass URL buttons through) in `src/TgLLM.Core/Keyboard.fs`/`Tools.fs`; update slice-1 `Mapping.toInlineKeyboardMarkup` in `src/TgLLM.BotApi/TelegramBotApiClient.fs` (Callback → `WithCallbackData`, Url → `WithUrl`) and adjust the affected slice-1 mapping test to the `Callback` case — behavior of callback buttons unchanged
- [X] T008 [P] [test] Failing tests for `InMemoryToolRegistry` (register/replace/resolve by name; unknown → `ValueNone`) in `tests/TgLLM.Core.Tests/ToolRegistryTests.fs`
- [X] T009 Implement `IToolRegistry` + `InMemoryToolRegistry` in `src/TgLLM.Core/Tools.fs` to green T008
- [X] T010 [P] [test] Failing tests for `InMemoryBindingStore` (Save → TryGet round-trip; unknown → `ValueNone`; Remove) in `tests/TgLLM.Core.Tests/BindingStoreTests.fs`
- [X] T011 Implement `IBindingStore` port + `InMemoryBindingStore` in `src/TgLLM.Core/Tools.fs` to green T010

**Checkpoint**: pure kernel (`ToolPlan.plan`) + registry + in-memory store green; slice-1 suite still green.

---

## Phase 3: User Story 1 — Tool Router core (Priority: P1) 🎯 MVP

**Goal**: Register named tools; send a keyboard from a neutral plan (with args); taps invoke the exact
registered tool with its argument — no per-button glue.

**Independent Test**: register two tools; send a plan naming them (with args); tap each → correct tool
runs with correct arg; a plan naming an unregistered tool → tap acked, no run, surfaced.

- [X] T012 [US1] Extend `PressContext` with `Arg : string | null` and `Answer(text, ?alert)` (records the deferred-ack directive) in `src/TgLLM.Core/Domain.fs`
- [X] T013 [US1] Add `IBotApiClient.AnswerCallback(query, text, showAlert, ct)` overload in `src/TgLLM.Core/Ports.fs` and implement it in `src/TgLLM.BotApi/TelegramBotApiClient.fs`
- [X] T014 [US1] Implement `ToolDispatch` (resolve token → binding via `IBindingStore` → tool via `IToolRegistry`) in `src/TgLLM.Core/Tools.fs`
- [X] T015 [US1] [test] Failing tests for the `UpdateProcessor` deferred-ack tool path (in-memory fakes): tool press → tool runs with `Arg`; the ack is sent EXACTLY ONCE after the tool with its directive; a watchdog sends a default ack if the tool exceeds the budget; unknown tool → ack + observer, no crash; the slice-1 closure path stays ack-first when `toolDispatch` is absent in `tests/TgLLM.Core.Tests/ToolDispatchProcessorTests.fs`
- [X] T016 [US1] Extend `UpdateProcessor` with an optional `?toolDispatch` collaborator + the deferred-ack path + watchdog in `src/TgLLM.Core/UpdateProcessor.fs` (slice-1 path unchanged when omitted) to green T015
- [X] T017 [US1] Implement the F# façade tool surface — `ToolRegistry`, `Plan` module (`tool`/`toolWithArg`/`url`/`rows`), `TgBotConfig.WithTools`, `TgBot.SendKeyboardPlan` (wires a `ToolDispatch` into the processor) — in `src/TgLLM.FSharp/ToolRouter.fs`
- [X] T018 [US1] [test] F# acceptance over long polling: register tools, send a plan with args, tap → the bound tool runs with its arg (SC-002); a plan naming an unregistered tool → tap acked, no run, surfaced (SC-005) in `tests/TgLLM.Integration.Tests/ToolRouterAcceptanceTests.fs`
- [X] T019 [US1] Implement the C# façade tool surface — `ToolRegistry`, `PlanBuilder`/`PlanRowBuilder`, `KeyboardPlan`, `TelegramAgentOptions.Tools`, `SendKeyboardPlanAsync` — in `src/TgLLM.CSharp/`
- [X] T020 [P] [US1] [test] C# tool-router behavior tests + re-run the idiom-leak canary (no FSharp.Core on the extended surface) in `tests/TgLLM.CSharp.Tests/`

**Checkpoint**: MVP — an LLM-style plan routes taps to tools with args, in F# and C#, over polling; slice-1 green.

---

## Phase 4: User Story 2 — Edit the pressed message in place + toast/alert (Priority: P2)

**Goal**: A tool can edit the originating message (text and/or keyboard) in place, and show a toast/alert.

**Independent Test**: tap a tool that edits the message to new text + a new keyboard → same message
changes, no new message; a tool that requests a toast → the ack carries it.

- [ ] T021 [US2] Add `IBotApiClient.EditMessageText` / `EditMessageReplyMarkup` in `src/TgLLM.Core/Ports.fs` and implement over Telegram.Bot in `src/TgLLM.BotApi/TelegramBotApiClient.fs`, catching `ApiRequestException` (`message to edit not found` / `message is not modified`) and surfacing via `IHookObserver` (no crash)
- [ ] T022 [US2] Extend `PressContext` with `EditTextAsync(text)` and `EditKeyboardAsync(plan)` (the latter re-plans + registers bindings for the replacement keyboard) wired to the new client methods in `src/TgLLM.Core/Domain.fs`/`UpdateProcessor.fs`; expose them on both façades' `PressContext` view
- [ ] T023 [US2] [test] Integration: a tapped tool edits the pressed message in place (text + replaced keyboard) — the same message changes and NO new message is sent (SC-003); a tapped tool that calls `Answer(text, alert)` → `answerCallbackQuery` carries the text/`show_alert` in `tests/TgLLM.Integration.Tests/EditInPlaceTests.fs`
- [ ] T024 [US2] [test] Integration edge: editing a vanished message (fake returns `message to edit not found`) is surfaced via the observer and does not crash the bot in `tests/TgLLM.Integration.Tests/EditInPlaceTests.fs`

**Checkpoint**: multi-step in-place LLM flows work; slice-1 green.

---

## Phase 5: User Story 3 — Bindings survive a restart (Priority: P3)

**Goal**: With a file binding store, taps on keyboards sent before a restart still route.

**Independent Test**: send a plan with a file store; re-open the store in a fresh bot (same tools);
tap a pre-restart button → the bound tool runs.

- [ ] T025 [US3] [test] Failing tests for `FileBindingStore` (Save → TryGet; re-open the file in a new instance and TryGet returns the saved binding; Remove) in `tests/TgLLM.Persistence.Tests/FileBindingStoreTests.fs`
- [ ] T026 [US3] Implement `FileBindingStore : IBindingStore` (System.Text.Json on disk; loads existing bindings on open; single-writer serialization) in `src/TgLLM.Persistence/FileBindingStore.fs` to green T025
- [ ] T027 [US3] Wire `TgBotConfig.WithBindingStore` (F#) and `TelegramAgentOptions.BindingStore` (C#) so the configured store backs the `ToolDispatch` in `src/TgLLM.FSharp/` and `src/TgLLM.CSharp/`
- [ ] T028 [US3] [test] Integration: send a plan with a `FileBindingStore`; simulate a restart (new bot + processor, same store file + re-registered tools); tap a pre-restart button → the bound tool runs (SC-004); a tap whose tool is no longer registered → acked + surfaced in `tests/TgLLM.Integration.Tests/RestartPersistenceTests.fs`

**Checkpoint**: restart-safe bindings; slice-1 green.

---

## Phase 6: User Story 4 — URL buttons (Priority: P4)

**Goal**: A keyboard can mix URL buttons (open a link, no tool) with tool buttons.

**Independent Test**: send a plan with one URL button and one tool button; the URL button maps to a
URL button (no token/binding) and the tool button routes.

- [ ] T029 [US4] [test] Integration: a plan with a URL button + a tool button → the sent keyboard's URL button carries the url (no `callback_data`/token/binding) and the tool button still routes on tap in `tests/TgLLM.Integration.Tests/UrlButtonTests.fs`
- [ ] T030 [US4] Wire the URL button end-to-end at the façades — `Plan.url` (F#) and `PlanRowBuilder.Url` (C#) — and validate a non-empty url (`ToolError.InvalidUrl` on empty); the core `ToolPlan.plan` + mapping URL passthrough already exist from T007

**Checkpoint**: all four stories independently pass; slice-1 green.

---

## Phase 7: Polish & Cross-Cutting

- [ ] T031 [P] Example apps `examples/ToolRouterFSharp` and `examples/ToolRouterCSharp`: register tools + build a data-driven plan (stand-in for an LLM decision), demonstrating BOTH long polling and webhooks (Principle VIII)
- [ ] T032 [P] Update user docs: a Tool Router section in `README.md`, `docs/quickstart.md`, and a `CHANGELOG.md` entry (Principle VII/VIII)
- [ ] T033 Full verification: run the whole suite on `net10.0`; confirm the slice-1 62 tests are still green AND SC-001..SC-007 and the C# leak canary pass; confirm the library ships NO business tools (FR-011 — the tool catalog is user code only); verify the Bot API facts used (research D1–D4: edit errors, one-shot ack, URL semantics, callback_data size) against the code

## Phase 8: Added coverage (from /speckit-analyze — close SC-007 / SC-002 gaps)

> These are US1 acceptance extensions; run them once US1 (T012–T020) is complete. Appended here to
> avoid renumbering the additive task list.

- [ ] T034 [US1] [test] SC-007 both-transports tool-router acceptance: run the US1 tool-routing scenario over long polling AND webhooks and assert identical tool behavior (bound tool + arg) across transports in `tests/TgLLM.Integration.Tests/ToolRouterBothTransportsTests.fs`
- [ ] T035 [US1] [test] SC-002 tool-routing at scale: ≥100 interleaved taps spanning multiple tools and arguments each invoke exactly their bound tool with the correct arg, zero cross-invocation (engine-level, real `ToolDispatch` + `UpdateProcessor` + in-memory fakes, like slice-1's routing-at-scale test) in `tests/TgLLM.Integration.Tests/ToolRoutingAtScaleTests.fs`

---

## Dependencies & Execution Order

- **Setup (P1)** → **Foundational (P2, blocks all stories)** → **US1 (P3)** → US2 / US3 / US4 → **Polish**.
- US2, US3, US4 all depend on Foundational + US1's `ToolDispatch`/façade; they are independently testable
  and can proceed in parallel once US1 is done (US3 also depends on Setup's `TgLLM.Persistence`).
- **Within each unit (TDD)**: the `[test]` task fails first, then its implementation greens it.
- **Every phase** re-runs the full suite to hold the slice-1-green invariant (FR-012).

## Parallel Opportunities

- Foundational test-first tasks touch different files: T003, T006, T008, T010 [P].
- US1: T020 (C# tests) [P] alongside F# work once the C# façade (T019) exists.
- After US1: US2 / US3 / US4 phases can be worked in parallel (different files); T031, T032 [P] in Polish.

## Implementation Strategy

- **MVP** = Setup + Foundational + US1 (T001–T020): an LLM-style plan routes taps to registered tools
  with args, in both façades over polling. Stop and validate.
- **Incremental**: US2 (in-place reactions) → US3 (persistence) → US4 (URL) → Polish. Each is a testable
  increment that does not break the previous stories or slice 1.

## Notes

- `[P]` = different files, no dependency on an incomplete task; `[test]` = the Red in Red→Green→Refactor.
- Property tests (FsCheck) are the correctness backbone for `ToolPlan.plan` and tool resolution.
- The ONLY non-purely-additive change is `RegisteredButton` → DU (T007); it updates slice-1's internal
  mapping + its test to the `Callback` case with unchanged callback-button behavior.
- Verify Telegram facts against the docs while implementing (research D1–D4; Principle V).
- Naming (intentional, mirrors slice 1's `KeyboardSpec`/`Keyboard`): the neutral plan type is
  `ToolKeyboard` in the F# core; the C# façade exposes it as `KeyboardPlan`. Same type, idiomatic name
  per language — not a drift.
- Keep the default branch release-ready; commit after each task or logical group.
