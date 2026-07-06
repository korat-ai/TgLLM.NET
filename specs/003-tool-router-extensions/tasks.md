---
description: "Task list for feature 003-tool-router-extensions"
---

# Tasks: Tool Router Extensions

**Input**: Design documents from `specs/003-tool-router-extensions/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/, quickstart.md

**Tests**: MANDATORY (constitution Principle I — Test-First, NON-NEGOTIABLE). Each `[test]` task writes
failing tests that MUST fail before its paired implementation makes them pass; property tests (FsCheck)
are required for the pure kernel.

**Invariant (FR-019)**: this feature is ADDITIVE on the green slice-1/2 library. The **slice-1/2 suite
(123 tests) MUST stay green** throughout; every phase re-runs the full suite. `dotnet build`/`dotnet test`
are the final oracle.

**Prerequisite hardening pass (separate, on this branch)**: the confirmed slice-1/2 review blockers are
fixed before these tasks build on them — run-loop supervision + polling retry/backoff (#1); the
`MessageBindingTracker` keyed by `(ChatId, MessageId)` + per-chat message ids in the fake server (#2);
the `ackPolicy` claim (#5); the C# façade fail-fast docs + honored `CancellationToken` (#6); the
`SendKeyboardPlan`-without-tools guard (#10). Do NOT re-do them here.

**Organization**: grouped by user story (US1 authorization → US2 manifest/args → US3 buttons → US4
lifecycle). Folded architectural review findings are tagged `[review #n]`.

## Format: `[ID] [P?] [Story?] Description`

- **[P]**: different files, no dependency on an incomplete task
- **[Story]**: US1 / US2 / US3 / US4 (story phases only)
- **[test]**: test-first; its failing tests gate the paired implementation

---

## Phase 1: Setup

- [ ] T001 Create the new leaf project `src/TgLLM.Persistence.LiteDb` (F#; ProjectReference `TgLLM.Core`) and test project `tests/TgLLM.Persistence.LiteDb.Tests` (Expecto + FsCheck); add both to `TgLLM.NET.sln` with correct references
- [ ] T002 [P] Add `LiteDB` to `Directory.Packages.props` and reference it from `TgLLM.Persistence.LiteDb` only (file-store users must not inherit it)

**Checkpoint**: solution builds with the new empty project; slice-1/2 suite still green.

---

## Phase 2: Foundational (shared additive kernel — blocks all stories)

**⚠️ CRITICAL**: keep slice-1/2 behavior intact. `ToolBinding` evolves ONCE here (owner + expiry +
single-use), read-compatible with slice-2 records; `PlanButton`/`RegisteredButton` gain WebApp/CopyText
cases — update the slice-2 mapping/tests to the new cases with unchanged existing behavior.

- [ ] T003 [P] [test] Failing tests for `OwnerScope` (`Anyone` | `User uid`) and its "is this presser allowed?" decision (Anyone→always; User→match; missing/anonymous→deny) in `tests/TgLLM.Core.Tests/OwnerScopeTests.fs`
- [ ] T004 Implement `OwnerScope` + the pure owner-check function in `src/TgLLM.Core/Tools.fs` to green T003
- [ ] T005 [test] Failing tests that a slice-2 `ToolBinding` value/JSON (no owner/expiry/single-use) loads with defaults `Anyone`/`None`/`false` (backward compat, FR-017) in `tests/TgLLM.Core.Tests/ToolBindingTests.fs`
- [ ] T006 Evolve `ToolBinding` additively (`Owner`, `ExpiresAt`, `SingleUse`) with defaults in `src/TgLLM.Core/Tools.fs`; keep `Arg: string option` (opaque payload, D3); update all construction sites to the defaults so slice-2 tests stay green
- [ ] T007 [P] [test] Failing tests for the expiry decision with an injected `Clock` (expired→refuse, live→allow, boundary) in `tests/TgLLM.Core.Tests/ExpiryTests.fs`
- [ ] T008 Add `Clock = unit -> DateTimeOffset` and the pure expiry check in `src/TgLLM.Core/Tools.fs` (no ambient `DateTimeOffset.Now`) to green T007
- [ ] T009 [P] [test] Failing tests for `ProcessedQueryTracker.TryBegin` (first id → true; repeat → false; bounded/TTL eviction) in `tests/TgLLM.Core.Tests/ProcessedQueryTrackerTests.fs`
- [ ] T010 Implement `ProcessedQueryTracker` (bounded, TTL'd seen-set) in `src/TgLLM.Core/Tools.fs` to green T009
- [ ] T011 [P] [test] Failing FsCheck property tests for the extended `ToolPlan.plan`: WebApp/CopyText buttons carry no binding and pass through; existing tool/url invariants hold; **plus the slice-2 property gaps** — duplicate input tokens consumed at most once, `validate` Ok ⇒ `plan` Ok, URL/WebApp/CopyText passthrough verbatim — in `tests/TgLLM.Core.Tests/ToolPlanExtTests.fs`
- [ ] T012 Extend `PlanButton` (`WebAppButton`, `CopyTextButton`) and `RegisteredButton` (`WebApp`, `CopyText`) DUs and `ToolPlan.plan`/`validate` (WebApp https, CopyText 1..256) in `src/TgLLM.Core/Tools.fs`/`Keyboard.fs`; extend the Telegram.Bot mapping (`WebApp`→`WithWebApp`, `CopyText`→`WithCopyText`) in `src/TgLLM.BotApi/TelegramBotApiClient.fs`; update the slice-2 mapping tests to the widened DU
- [ ] T013 [review #4] Reorder `ToolKeyboardOps.deliver` to remove old tokens only AFTER a successful send, and compensate (remove just-saved bindings) when send throws, in `src/TgLLM.Core/Tools.fs`; failing test in `tests/TgLLM.Core.Tests/ToolDispatchProcessorTests.fs` first (a throwing send leaves neither a stranded live keyboard nor orphan bindings)
- [ ] T014 [review #8] [test] Failing tests that a dropped non-canonical callback query is still acked (an ack-only event carrying the query id is emitted) in `tests/TgLLM.Integration.Tests/DroppedCallbackTests.fs`
- [ ] T015 [review #8] Emit an ack-only event for dropped/non-canonical callback queries in both transports (`src/TgLLM.BotApi/TelegramBotApiClient.fs` mapping + `src/TgLLM.Webhooks/WebhookUpdateSource.fs`) and ack it in `UpdateProcessor`, to green T014
- [ ] T016 [review #3] [test] Failing test that a tool queued behind a slow tool is still acked within budget (ack at enqueue / watchdog starts at enqueue) and that acking does not head-of-line-block other chats, in `tests/TgLLM.Integration.Tests/AckTimingTests.fs`
- [ ] T017 [review #3] Move the tool-path ack to enqueue-time (or start the watchdog at enqueue) and take the ack HTTP round-trip off the single ingestion loop in `src/TgLLM.Core/UpdateProcessor.fs` to green T016; slice-2 deferred-ack/watchdog tests stay green
- [ ] T018 [test] Failing tests for `IBindingStore.EvictExpired now` on `InMemoryBindingStore` (removes expired, keeps live, returns count) in `tests/TgLLM.Core.Tests/BindingStoreEvictionTests.fs`
- [ ] T019 Add `EvictExpired` to the `IBindingStore` seam + `InMemoryBindingStore`, and prune `MessageBindingTracker` entries past a bound, in `src/TgLLM.Core/Tools.fs` to green T018

**Checkpoint**: evolved binding + owner/expiry/dedup kernel green; widened button DU maps to Telegram;
folded engine findings (#3/#4/#8) green; slice-1/2 suite still green.

---

## Phase 3: User Story 1 — Press authorization (Priority: P1) 🎯 MVP

**Goal**: A keyboard scoped to an owner refuses non-owner taps on tool buttons (notice + no tool),
enforced identically after restart and across both transports/façades.

**Independent test**: owner-scoped keyboard; user B tap → refused + notice; user A tap → tool runs.

- [ ] T020 [P] [US1] [test] Failing property/example tests: resolution refuses a non-owner (or anonymous) press of an owner-scoped binding (ack + `OnUnknownToken`/refusal signal, no tool) and allows the owner, in `tests/TgLLM.Core.Tests/OwnerRoutingTests.fs`
- [ ] T021 [US1] Wire the owner check into the resolve/dispatch step (after dedup, before invoke) and write the owner scope into each tool binding at plan time, in `src/TgLLM.Core/UpdateProcessor.fs`/`Tools.fs`, to green T020
- [ ] T022 [US1] Add owner-scoped send to the F# façade (`SendKeyboardPlan ?owner ?notModifiedNotice`, `Owner.anyone`/`Owner.user`) with a built-in default notice, in `src/TgLLM.FSharp/TgBot.fs`/`ToolRouter.fs`
- [ ] T023 [P] [US1] Add owner-scoped send to the C# façade (`OwnerScope?` param, `Owner.Anyone`/`Owner.User`) in `src/TgLLM.CSharp/TelegramAgent.cs`/`ToolKeyboardPlan.cs`
- [ ] T024 [US1] [test] Integration: owner-scoped acceptance over BOTH transports and BOTH façades, including restart with a durable store (non-owner still refused, SC-002), in `tests/TgLLM.Integration.Tests/OwnerAuthorizationTests.fs` and `tests/TgLLM.CSharp.Tests/`

**Checkpoint**: US1 independently demonstrable; slice-1/2 suite still green.

---

## Phase 4: User Story 2 — Tool manifest + structured arguments (Priority: P2)

**Goal**: The registry emits a neutral manifest; buttons carry structured payloads round-tripped to tools.

- [ ] T025 [P] [US2] [test] Failing tests for `ToolMetadata` + manifest emission (every registered tool present; neutral `{name,description,parameters}`; metadata-less tools name-only; `ManifestJson` shape) in `tests/TgLLM.Core.Tests/ToolManifestTests.fs`
- [ ] T026 [US2] Add optional `description`/`argSchema` to registration, `ToolManifest`/`ToolManifestEntry`, and `Manifest()`/`ManifestJson()` neutral emission, in `src/TgLLM.Core/Tools.fs` to green T025
- [ ] T027 [P] [US2] [test] Failing tests: `Plan.toolWith<'T>` serializes a payload and `PressContext.GetArg<'T>()` round-trips it byte-for-byte; a slice-2 string arg still reads via `.Arg` (D3), in `tests/TgLLM.CSharp.Tests/` and `tests/TgLLM.Integration.Tests/StructuredArgTests.fs`
- [ ] T028 [US2] Add `Plan.toolWith<'T>` + `GetArg<'T>()`/`TryGetArg` (STJ serialize/deserialize) to the F# façade (`src/TgLLM.FSharp/`) and `Tool<T>`/`GetArg<T>` to the C# façade (`src/TgLLM.CSharp/`), keeping Core STJ-free, to green T027
- [ ] T029 [US2] Expose manifest emission on both façades (F# `ToolRegistry.Manifest/ManifestJson`, C# `ToolRegistry.Manifest/ManifestJson`) in `src/TgLLM.FSharp/ToolRouter.fs` and `src/TgLLM.CSharp/ToolRegistry.cs`
- [ ] T030 [review #7] [US2] Add a C#-facing `IBindingStore` adapter (nullable `ToolBinding?` / DTOs — no `FSharpOption`/`FSharpValueOption`) bridged to the F# seam, in `src/TgLLM.CSharp/`; extend the idiom-leak canary to walk one member level into referenced non-BCL types, in `tests/TgLLM.CSharp.Tests/FacadeTests.cs` (fails first, then green)

**Checkpoint**: US2 manifest + structured args green on both façades; canary widened; slice-1/2 green.

---

## Phase 5: User Story 3 — WebApp and CopyText buttons (Priority: P3)

**Goal**: WebApp and CopyText buttons render and are client-side; validation enforced.

- [ ] T031 [P] [US3] [test] Failing tests: `Plan.webApp` rejects non-https, `Plan.copyText` rejects empty/over-256; both produce client-side buttons with no binding, in `tests/TgLLM.Core.Tests/ClientButtonTests.fs`
- [ ] T032 [US3] Add `Plan.webApp`/`Plan.copyText` (F#) and `PlanRowBuilder.WebApp`/`.CopyText` (C#) builders with validation, in `src/TgLLM.FSharp/ToolRouter.fs` and `src/TgLLM.CSharp/ToolKeyboardPlan.cs`, to green T031
- [ ] T033 [US3] [test] Integration: a keyboard mixing tool + WebApp + CopyText — the tool button routes, WebApp/CopyText invoke no server handler — over both transports/façades, in `tests/TgLLM.Integration.Tests/ClientButtonsTests.fs`

**Checkpoint**: US3 buttons green; slice-1/2 green.

---

## Phase 6: User Story 4 — Lifecycle & reliability (Priority: P4)

**Goal**: Expiry/eviction, at-most-once redelivery, single-use, soft edit errors, embedded LiteDB store.

- [ ] T034 [US4] Wire expiry + single-use consumption + dedup (`ProcessedQueryTracker`) into the resolve/dispatch step in `src/TgLLM.Core/UpdateProcessor.fs` (expired/consumed/redelivered → ack, no invoke); tests in `tests/TgLLM.Core.Tests/LifecycleRoutingTests.fs` first
- [ ] T035 [P] [US4] [test] Failing tests for soft edit-error handling (`message is not modified` → success no-op; `message to edit not found` → observed soft failure, no exception) in `tests/TgLLM.Integration.Tests/EditErrorTests.fs`
- [ ] T036 [US4] Classify Telegram edit errors in the edit-in-place port impl (`src/TgLLM.BotApi/TelegramBotApiClient.fs`) and surface via `IHookObserver` instead of throwing (FR-015), to green T035
- [ ] T037 [US4] Add idle per-chat eviction to the dispatcher (configurable deadline; never drops/reorders in-flight presses) in `src/TgLLM.Core/Dispatcher.fs`; tests (SC-007, ordering preserved) in `tests/TgLLM.Core.Tests/DispatcherEvictionTests.fs` first
- [ ] T038 [P] [US4] [test] Failing tests for `LiteDbBindingStore`: Save→TryGet round-trip, `EvictExpired`, reload-after-restart (SC-010), and reading a slice-2 record (missing owner/expiry fields → defaults), in `tests/TgLLM.Persistence.LiteDb.Tests/LiteDbBindingStoreTests.fs`
- [ ] T039 [US4] Implement `LiteDbBindingStore : IBindingStore` (LiteDB; one collection keyed by token, documents carrying tool/arg/owner/expiry/single-use fields; `EvictExpired` = collection delete-by-query) in `src/TgLLM.Persistence.LiteDb/LiteDbBindingStore.fs` to green T038
- [ ] T040 [US4] Add `WithIdleChatEviction` (F#) / options (C#) and `LiteDbBindingStore.OpenAt` exposure to both façades, in `src/TgLLM.FSharp/TgBot.fs` and `src/TgLLM.CSharp/`
- [ ] T041 [US4] [test] Integration: expiry refusal, single-use confirm-once, at-most-once redelivery, and LiteDB restart persistence over both transports, in `tests/TgLLM.Integration.Tests/LifecycleTests.fs`

**Checkpoint**: US4 green; the store seam demonstrably generalizes (SC-010); slice-1/2 green.

---

## Phase 7: Polish & cross-cutting

- [ ] T042 [P] Extend `examples/ToolRouterFSharp` and `examples/ToolRouterCSharp` with an owner-scoped keyboard, an emitted manifest, a structured-arg button, WebApp/CopyText, and the LiteDB store
- [ ] T043 [P] Update user docs: `docs/quickstart.md` (auth + manifest + structured args + new buttons + LiteDB), `README.md`, `CHANGELOG.md` (Unreleased → slice 003) — English, cite code/vendor only (Principle VII)
- [ ] T044 Full-suite acceptance: run the entire suite over both transports and both façades; confirm slice-1/2 tests unchanged and all SC-001..SC-011 covered
- [ ] T045 Final `dotnet build -c Release` (0 warnings/0 errors) + `dotnet test` green; verify no `FSharpOption`/`FSharpFunc` leak on the C# surface (canary) and no ambient clock in Core

---

## Dependencies & order

- **Setup (T001–T002)** → **Foundational (T003–T019)** → user stories.
- **US1 (T020–T024)** is the MVP; depends only on Foundational.
- **US2 (T025–T030)**, **US3 (T031–T033)**, **US4 (T034–T041)** each depend on Foundational; US2/US3/US4
  are largely independent of one another and of US1 (different files where marked `[P]`).
- **Polish (T042–T045)** last.
- Every phase re-runs the full suite; the 123 slice-1/2 tests MUST stay green throughout (FR-019).

## Suggested MVP

Foundational + **US1** (press authorization) — the smallest shippable increment that fixes the real
security gap on the existing router.
