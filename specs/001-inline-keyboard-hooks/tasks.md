---
description: "Task list for feature 001-inline-keyboard-hooks"
---

# Tasks: Interactive Keyboards with Button Hooks (Agent PoC)

**Input**: Design documents from `specs/001-inline-keyboard-hooks/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/, quickstart.md

**Tests**: MANDATORY. Per the project constitution (Principle I — Test-First Development,
NON-NEGOTIABLE), every behavior is implemented test-first (Red → Green → Refactor), and
property-based tests (FsCheck) are REQUIRED for the pure kernel. Each `[test]` task writes failing
tests that MUST fail before the paired implementation task makes them pass.

**Organization**: Tasks are grouped by user story. Foundational (Phase 2) is the shared,
transport-agnostic core every story depends on.

## Format: `[ID] [P?] [Story?] Description`

- **[P]**: can run in parallel (different files, no dependency on an incomplete task)
- **[Story]**: US1 / US2 / US3 (user-story phases only; Setup/Foundational/Polish have no label)
- **[test]**: a test-first task; its failing tests gate the paired implementation task

## Path Conventions

Multi-project .NET library (per plan.md): `src/`, `tests/`, `examples/` at repo root. F# core is
IO-agnostic; Telegram.Bot and ASP.NET live only in leaf adapters.

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Solution, projects, dependencies, and repo hygiene.

- [X] T001 Create solution `TgLLM.NET.sln` and all project skeletons with correct `ProjectReference` dependency direction (per plan.md Project Structure): `src/TgLLM.Core` (F#), `src/TgLLM.BotApi` (F#), `src/TgLLM.Webhooks` (F#), `src/TgLLM.AspNetCore` (F#), `src/TgLLM.FSharp` (F#), `src/TgLLM.CSharp` (C#), `tests/TgLLM.Core.Tests` (F#), `tests/TgLLM.CSharp.Tests` (C#), `tests/TgLLM.Integration.Tests` (F#), `examples/PollingFSharp`, `examples/PollingCSharp`, `examples/WebhookFSharp`, `examples/WebhookCSharp`
- [X] T002 Add `Directory.Build.props` (single-target `net10.0` this phase — see deviation note in the file; `TreatWarningsAsErrors=true`, nullable enabled) and `Directory.Packages.props` (central versions: Telegram.Bot 22.10.1, FSharp.UMX 1.1.0, FSharp.Core 10.1.201, Expecto 10.2.3, Expecto.FsCheck 10.2.3-fscheck3, FsCheck 3.3.3, xunit.v3 3.2.2, FsCheck.Xunit.v3 3.3.3) at repo root
- [X] T003 [P] Add repo hygiene files: `.gitignore` (dotnet), `.editorconfig` (F#/C# style), `LICENSE` (MIT), `README.md` skeleton, `CHANGELOG.md`, `CONTRIBUTING.md` (contribution guide) (Principle VIII)
- [X] T004 [P] Add CI workflow `.github/workflows/ci.yml`: restore/build/test on `net10.0` now, with `net8.0` deferred until that SDK is available (see comment in the workflow)

**Checkpoint**: Solution builds empty; `dotnet test` runs (0 tests).

---

## Phase 2: Foundational (Blocking Prerequisites — transport-agnostic core)

**Purpose**: The pure kernel, domain, ports, default implementations, and engine that ALL user
stories depend on. This is the FsCheck heart of the design.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

### Domain & value objects (test-first)

- [X] T005 [P] Define UMX id measures (`ChatId`, `UserId`, `MessageId`, `CallbackQueryId`), `EndUser`, `Hook`, `HookBinding`, `ButtonPress`, `AgentEvent` in `src/TgLLM.Core/Domain.fs` (`KeyboardError` moved to `Values.fs` and `PressContext` stays in `Domain.fs` — both disclosed compile-order deviations; see the comment atop `TgLLM.Core.fsproj`)
- [X] T006 [P] [test] Failing Expecto tests for `ButtonLabel` / `MessageText` smart constructors (non-empty after trim, length bounds, Ok/Error cases) in `tests/TgLLM.Core.Tests/ValueObjectsTests.fs`
- [X] T007 Implement `ButtonLabel` / `MessageText` in `src/TgLLM.Core/Values.fs` to green T006
- [X] T008 [P] [test] Failing FsCheck property tests for `CallbackToken` codec (`tryParse (value t) = ValueSome t`; encoded form ≤ 64 bytes; `tryParse` total over arbitrary strings) in `tests/TgLLM.Core.Tests/CallbackTokenTests.fs`
- [X] T009 Implement `CallbackToken` (`ofGuid`/`generate`/`tryParse`/`value`) in `src/TgLLM.Core/CallbackToken.fs` to green T008

### Keyboard & pure kernel (test-first)

- [X] T010 [P] [test] Failing tests for `Keyboard.create` validation (≥1 row, ≥1 button/row, non-empty labels → Ok; empty keyboard/row/label → correct `KeyboardError`) in `tests/TgLLM.Core.Tests/KeyboardTests.fs`
- [X] T011 Implement `ButtonSpec`, `KeyboardSpec`, `Keyboard.create`, `RegisteredButton`, `RegisteredKeyboard` in `src/TgLLM.Core/Keyboard.fs` to green T010
- [X] T012 [P] [test] Failing FsCheck property tests for `KeyboardPlan.assign` (row/col shape preserved; labels preserved; one binding per button; `bindings.length = buttonCount`; distinct input tokens ⇒ distinct button tokens) in `tests/TgLLM.Core.Tests/KeyboardPlanTests.fs`
- [X] T013 Implement `KeyboardPlan.assign` in `src/TgLLM.Core/Keyboard.fs` to green T012
- [X] T014 [P] [test] Failing FsCheck property tests for `Routing.decide` (token present in resolver ⇒ `RunHook` with exactly that hook; absent/malformed ⇒ `AcknowledgeOnly`; total) in `tests/TgLLM.Core.Tests/RoutingTests.fs`
- [X] T015 Implement `RouteDecision` and `Routing.decide` in `src/TgLLM.Core/Routing.fs` to green T014

### Ports & default implementations (test-first)

- [X] T016 [P] Define ports `IUpdateSource`, `IHookStore`, `IPressDispatcher`, `IBotApiClient`, `IHookObserver` (Task/ValueTask signatures per contracts/core-ports.md) in `src/TgLLM.Core/Ports.fs`
- [X] T017 [P] [test] Failing tests for `InMemoryHookStore` (Register makes every binding resolvable; TryResolve unknown → `ValueNone`, never throws; Remove) in `tests/TgLLM.Core.Tests/InMemoryHookStoreTests.fs`
- [X] T018 Implement `InMemoryHookStore` (`ConcurrentDictionary`) in `src/TgLLM.Core/InMemoryHookStore.fs` to green T017
- [X] T019 [P] [test] Failing FsCheck model-based ordering property for `PerChatChannelDispatcher` (random cross-chat interleavings ⇒ per-chat FIFO preserved AND all chats make progress) in `tests/TgLLM.Core.Tests/DispatcherTests.fs`
- [X] T020 Implement `PerChatChannelDispatcher` (`System.Threading.Channels`, `SingleReader=true`, `ConcurrentDictionary<ChatId,_>`) and `NoopHookObserver` in `src/TgLLM.Core/Dispatcher.fs` to green T019

### Engine (test-first)

- [X] T021 [test] Failing tests for `UpdateProcessor` ack-first policy with in-memory fakes: `AnswerCallback` called for EVERY event including unknown token → ack-only, no hook, no error (FR-007, FR-010); `RunHook` enqueued on the press's per-chat channel; a throwing hook → `IHookObserver.OnHookFailed` and the loop continues (FR-009) in `tests/TgLLM.Core.Tests/UpdateProcessorTests.fs`
- [X] T022 Implement `UpdateProcessor.RunAsync` (ack-first) and `AgentOps.sendKeyboard` in `src/TgLLM.Core/UpdateProcessor.fs` to green T021 (`PressContext` itself lives in `Domain.fs` — see T005 note)

**Checkpoint**: Pure kernel + engine green (Expecto + FsCheck). No transport or façade yet.

---

## Phase 3: User Story 1 — Send an interactive keyboard and react to taps (Priority: P1) 🎯 MVP

**Goal**: An agent sends a keyboard with per-button hooks; a tap invokes the exact hook, which
reacts in the chat; the tap is acknowledged. Works over BOTH long polling and webhooks, in BOTH
F# and C#.

**Independent Test**: Send a two-button keyboard to a test chat; tap each button → correct hook runs
and reply appears, tap acknowledged; stale press after restart → ack no-op, no error.

### Outbound Bot API + long polling (test-first)

- [X] T023 [P] [US1] [test] Failing tests for `TelegramBotApiClient`: `RegisteredKeyboard` → Telegram.Bot `InlineKeyboardMarkup`; `SendKeyboard`/`SendText`/`AnswerCallback` issue the correct Bot API calls (against a fake Telegram HTTP handler) in `tests/TgLLM.Integration.Tests/BotApiClientTests.fs`
- [X] T024 [US1] Implement `TelegramBotApiClient : IBotApiClient` and the pure Telegram.Bot `CallbackQuery` → `ButtonPress`/`AgentEvent` mapping (handle absent `message`) in `src/TgLLM.BotApi/TelegramBotApiClient.fs`; while implementing, verify against core.telegram.org the Bot API facts this code relies on (`callback_data` 1–64 bytes, `answerCallbackQuery` semantics, message-text 4096 limit) per research D7 (Principle V)
- [X] T025 [US1] [test] Failing tests for `LongPollingUpdateSource`: confirm-by-offset (`offset = max(update_id)+1`); yields events in batch order; calls `deleteWebhook` before polling; graceful cancellation (against fake Bot API) in `tests/TgLLM.Integration.Tests/LongPollingTests.fs`
- [X] T026 [US1] Implement `LongPollingUpdateSource : IUpdateSource` in `src/TgLLM.BotApi/LongPollingUpdateSource.fs` to green T025

### F# façade + MVP acceptance (test-first)

- [X] T027 [US1] Implement F# façade in `src/TgLLM.FSharp/TgBot.fs`: `Button.on`, `Keyboard`, `TgBotConfig`, `TgBot.startPolling` (wires core + `InMemoryHookStore` + `PerChatChannelDispatcher` + `TelegramBotApiClient` + `UpdateProcessor`), `SendKeyboard`/`SendText`
- [X] T028 [US1] [test] F# façade acceptance over long polling (US1 scenarios 1–4): two-button keyboard; tap Yes → Yes-hook + reply (not No); ack clears spinner; tap No → No-hook independently; stale press after restart → ack no-op, no error (FR-004/006/007/008/010); assert `AnswerCallback` is issued BEFORE the hook runs and is NOT awaited behind it — the ordering guarantee behind SC-003/SC-004 (a slow/blocking hook must not delay the ack on a deterministic fake transport) in `tests/TgLLM.Integration.Tests/FSharpPollingAcceptanceTests.fs`

**MVP Checkpoint**: F# + long polling works end-to-end and passes US1 acceptance. Deployable/demoable.

### Webhook transport (test-first)

- [X] T029 [US1] [test] Failing tests for webhook ingress: `X-Telegram-Bot-Api-Secret-Token` mismatch → rejected (401) before body read; valid Update parsed and enqueued (SingleReader channel); endpoint returns 200 immediately in `tests/TgLLM.Integration.Tests/WebhookTests.fs`
- [X] T030 [US1] Implement `WebhookUpdateSource : IUpdateSource` (Channel-backed) + pure secret-token verification + Telegram.Bot Update parsing in `src/TgLLM.Webhooks/WebhookUpdateSource.fs` to green T029
- [X] T031 [US1] Implement `MapTelegramWebhook` minimal-API endpoint glue (sealed `IEndpointConventionBuilder`) in `src/TgLLM.AspNetCore/EndpointExtensions.fs` and `TgBot.startWebhook` (calls `setWebhook`) in `src/TgLLM.FSharp/TgBot.fs`

### C# façade (test-first)

- [X] T032 [US1] Implement C# façade in `src/TgLLM.CSharp/`: `KeyboardBuilder`/`RowBuilder`, `TgKeyboardException`, `TelegramAgentOptions`, `TelegramAgent` (`StartPollingAsync`/`StartWebhookAsync`/`SendKeyboardAsync`/`SendTextAsync`), reusing the Core `PressContext`
- [X] T033 [US1] [test] C# façade behavior tests + idiom-leak canary: reflection over the public surface asserts NO `FSharp.Core` type (`FSharpFunc`/`FSharpOption`/`FSharpValueOption`) appears; builder throws `TgKeyboardException` on invalid layout (xUnit v3 + FsCheck.Xunit.v3) in `tests/TgLLM.CSharp.Tests/` (depends on T032 — the C# façade must exist first)

### Both-transports acceptance

- [X] T034 [US1] [test] SC-008 acceptance: run the US1 scenario over BOTH long polling and webhook and assert identical behavior; hook bodies unchanged across transports (FR-013) in `tests/TgLLM.Integration.Tests/BothTransportsTests.fs`

**Checkpoint**: US1 complete — both transports, both façades, all US1/SC-008 tests green.

---

## Phase 4: User Story 2 — Proactively send a keyboard from an external stimulus (Priority: P2)

**Goal**: An agent sends a keyboard triggered by something outside the chat (no preceding user
message); buttons route to hooks identically.

**Independent Test**: With no prior message from the target chat, an external trigger sends a
keyboard; its buttons route to hooks exactly as in US1.

- [ ] T035 [US2] [test] Failing integration test (US2 scenarios 1–2, SC-005): external trigger (stand-in for an external event) sends a keyboard to a chat with no preceding message; delivered; buttons route to hooks identically to a reply-sent keyboard in `tests/TgLLM.Integration.Tests/ProactiveSendTests.fs`
- [ ] T036 [US2] Confirm/adjust the send path to target any chat id without a preceding update (add config for arbitrary target chat if missing) in `src/TgLLM.FSharp/TgBot.fs` and `src/TgLLM.CSharp/TelegramAgent.cs`
- [ ] T037 [P] [US2] Example apps `examples/PollingFSharp` and `examples/PollingCSharp`: proactive keyboard fired from a timer (Principle VIII)

**Checkpoint**: US1 and US2 both independently pass.

---

## Phase 5: User Story 3 — Correct routing across many buttons, users, and keyboards (Priority: P3)

**Goal**: Many buttons, concurrent keyboards, and multiple users; every tap invokes only its own
hook; per-chat order preserved and cross-chat concurrent; hook failures isolated.

**Independent Test**: Two keyboards each with several distinctly-hooked buttons, interleaved taps →
each invokes only its own hook; two users tapping near-simultaneously → correct per-user context.

- [ ] T038 [US3] [test] Failing test (US3 scenario 1, SC-002): two keyboards × several distinctly-hooked buttons, interleaved taps ≥100 → each invokes only its own hook, zero cross-invocation in `tests/TgLLM.Integration.Tests/RoutingAtScaleTests.fs`
- [ ] T039 [US3] [test] Failing test (US3 scenario 2, SC-007): near-simultaneous taps across two chats → per-chat arrival order preserved while chats progress concurrently in `tests/TgLLM.Integration.Tests/ConcurrencyOrderingTests.fs`
- [ ] T040 [US3] [test] Failing test (SC-006): an intentionally throwing hook is isolated and reported via `IHookObserver`; subsequent presses still invoke their hooks in `tests/TgLLM.Integration.Tests/HookFailureIsolationTests.fs`
- [ ] T041 [US3] Harden `PerChatChannelDispatcher` / `UpdateProcessor` for any gaps surfaced by T038–T040 in `src/TgLLM.Core/Dispatcher.fs` and `src/TgLLM.Core/UpdateProcessor.fs`
- [ ] T042 [P] [US3] Example apps `examples/WebhookFSharp` and `examples/WebhookCSharp` (Principle VIII)

**Checkpoint**: All user stories independently functional; Success Criteria covered by tests (SC-002/005/006/007/008), by the ack-before-hook ordering guarantee (SC-003/004), or by manual walkthrough (SC-001).

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Documentation, observability wiring, packaging, and final verification.

- [ ] T043 [P] Finalize user documentation: complete `README.md` (features, install, quickstart, badges) and place the adapted `quickstart.md` under `docs/` (Principle VII/VIII)
- [ ] T044 [P] Bridge `IHookObserver` → `ILogger` in both façades so hook failures/unknown tokens are surfaced (FR-009) in `src/TgLLM.FSharp/` and `src/TgLLM.CSharp/`
- [ ] T045 [P] NuGet package metadata (PackageId, description, MIT license, repo URL, README-in-package, symbols) on `src/TgLLM.FSharp` and `src/TgLLM.CSharp`
- [ ] T046 [P] XML doc comments on the public surface of both façades (English; cite Bot API where relevant) in `src/TgLLM.FSharp/` and `src/TgLLM.CSharp/`
- [ ] T047 Full verification: run the whole suite on `net8.0` AND `net10.0`; confirm the idiom-leak canary is green and every Success Criterion is met — SC-002/005/006/007/008 by their tests, SC-003/SC-004 by the ack-before-hook ordering assertion (T028), SC-001 by a manual quickstart walkthrough; update `CHANGELOG.md`
- [ ] T048 [P] Principle V gate: verify every Bot API fact in research D7 (`callback_data` 1–64 bytes; `answerCallbackQuery` semantics + `text` 0–200 chars; message-text 4096 limit; `X-Telegram-Bot-Api-Secret-Token` header; `getUpdates` offset/allowed_updates; inline-keyboard row/button limits; `message_id` width) against core.telegram.org and reconcile any deviation in code/docs

---

## Dependencies & Execution Order

### Phase dependencies

- **Setup (Phase 1)**: no dependencies — start immediately.
- **Foundational (Phase 2)**: depends on Setup — **BLOCKS all user stories**.
- **User Story 1 (Phase 3)**: depends on Foundational. The MVP.
- **User Story 2 (Phase 4)**: depends on Foundational; reuses US1's send path (independently testable).
- **User Story 3 (Phase 5)**: depends on Foundational + the dispatcher/processor (US1); independently testable.
- **Polish (Phase 6)**: depends on the user stories being complete.

### Story independence

- US1 is the core loop and carries the transport + façade wiring.
- US2 is a thin increment proving proactive/external-stimulus sends (no new core).
- US3 hardens routing/ordering/failure isolation with tests (+ any dispatcher fixes).

### Within each unit (TDD)

- Every `[test]` task MUST be written and FAIL before its paired implementation task.
- Kernel (Foundational) before any transport/façade.
- Ports before their default implementations; default implementations before the engine.
- Engine before the façades; one transport (polling) before the second (webhook).

---

## Parallel Opportunities

- **Setup**: T003, T004 in parallel (different files).
- **Foundational test-first tasks** touch different test files and can be authored in parallel:
  T006, T008, T010, T012, T014, T017, T019 [P]; plus T005 and T016 (distinct core files).
  Each paired implementation task then follows its test.
- **US1**: T023 and T033 are [P] (different test projects); T033 (C# tests) can proceed alongside
  F#-side work once the C# façade (T032) exists.
- **US2/US3 examples**: T037, T042 [P].
- **Polish**: T043, T044, T045, T046 [P].

### Parallel example: Foundational test authoring

```bash
# Author these failing test suites together (different files, no shared state):
Task: "T006 ValueObjects tests in tests/TgLLM.Core.Tests/ValueObjectsTests.fs"
Task: "T008 CallbackToken property tests in tests/TgLLM.Core.Tests/CallbackTokenTests.fs"
Task: "T010 Keyboard.create tests in tests/TgLLM.Core.Tests/KeyboardTests.fs"
Task: "T012 KeyboardPlan.assign property tests in tests/TgLLM.Core.Tests/KeyboardPlanTests.fs"
Task: "T014 Routing.decide property tests in tests/TgLLM.Core.Tests/RoutingTests.fs"
```

---

## Implementation Strategy

### MVP first (US1, minimal transport)

1. Complete Phase 1 (Setup) + Phase 2 (Foundational — pure kernel green).
2. In Phase 3, stop at the **MVP Checkpoint** (T023–T028): F# façade + long polling end-to-end.
3. **Validate US1 acceptance** on a real bot, then proceed to webhooks (T029–T031) and the C#
   façade (T032–T033), closing with the both-transports test (T034).

### Incremental delivery

- Foundational → US1 (MVP: F#+polling → +webhook → +C#) → US2 (proactive) → US3 (scale/robustness)
  → Polish. Each user story is a testable increment that does not break the previous ones.

---

## Notes

- `[P]` = different files, no dependency on an incomplete task.
- `[Story]` label maps each user-story task for traceability; Setup/Foundational/Polish carry none.
- `[test]` tasks are the Red in Red→Green→Refactor: they MUST fail before the paired impl task.
- Property tests (FsCheck) are the correctness backbone for the pure kernel (Principle I).
- User-facing docs stay current in the SAME change (Principle VII): quickstart/examples are updated within the story that alters behavior; T043 is final README/docs polish, not the first time docs appear.
- Commit after each task or logical group. Keep the default branch release-ready (Principle VIII).
- Verify Telegram/vendor facts against the docs while implementing (research D7 list; Principle V).
