# Tasks: MAF Bridge — HITL Approval as Telegram Buttons

**Input**: Design documents from `/specs/005-maf-bridge/`

**Prerequisites**: plan.md, spec.md, research.md (D1–D13), data-model.md, contracts/maf-bridge.md

**Tests**: MANDATORY (Constitution Principle I — Test-First, NON-NEGOTIABLE). Every behavior is written
test-first (Red → Green → Refactor); property-based tests (FsCheck) are REQUIRED for the pure domain logic
(descriptor round-trip, owner-scope resolution, default rendering, projection field mapping).

**Organization**: by user story. US1 (P1) is the shippable MVP — the approval loop, drivable via
host-initiated `StartRun` with no dependency on the message seam. US3 (the Core message seam) is additive.

**Leaf boundary (every task)**: MAF types (`AIAgent`, `AgentSession`, `ToolApprovalRequestContent`,
`AIFunction`, `JsonElement`) live ONLY under `src/TgLLM.Maf/`. `TgLLM.Core` and both façades gain NO MAF
dependency. Slice-1/2/3/4 public APIs + tests stay byte-identical (FR-019).

**Principle VII (every code/test file)**: no comment, test name, or assertion string may cite `FR-###`,
`SC-###`, `US#`, `T0xx`, `research D#`, spec file names, or the branch slug — comments reference code
symbols + MAF / Telegram (Bot API) / .NET vendor docs only.

**Grounding caveat (D1/D2)**: MAF is preview and renames types across releases — the approval content is
`ToolApprovalRequestContent`/`ToolApprovalResponseContent` (NOT `FunctionApproval*`). **Re-run the
reflection probe before the first `dotnet add package` and at any version bump.**

---

## Phase 1: Setup (Shared Infrastructure)

- [ ] T001 Re-run the reflection probe (scratchpad throwaway project, `Microsoft.Agents.AI` 1.13.0) to re-confirm the approval/session/tool surface still matches research.md D-facts before any package is added; note any drift.
- [ ] T002 Create the leaf project `src/TgLLM.Maf/TgLLM.Maf.fsproj` (net10.0) referencing `src/TgLLM.FSharp/TgLLM.FSharp.fsproj`; pin `Microsoft.Agents.AI` to EXACT `1.13.0` (via `Directory.Packages.props` + a `<PackageReference>` with no floating range); add `System.Text.Json`/`FSharp.SystemTextJson` for the descriptor only.
- [ ] T003 [P] Create the test project `tests/TgLLM.Maf.Tests/TgLLM.Maf.Tests.fsproj` (Expecto + Expecto.FsCheck + FsCheck) referencing `TgLLM.Maf` and `TgLLM.Core`, with `Program.fs` entry point (mirror `tests/TgLLM.A2UI.Tests`).
- [ ] T004 [P] Scaffold runnable example projects `examples/MafFSharp/` (fsproj + placeholder `Program.fs`) and `examples/MafCSharp/` (csproj + placeholder `Program.cs`), referencing the leaf.
- [ ] T005 Add `TgLLM.Maf`, `TgLLM.Maf.Tests`, `MafFSharp`, `MafCSharp` to the solution; confirm `dotnet build -c Release` is green (empty leaf) and the full existing suite (462) still passes untouched.

**Checkpoint**: leaf + test project build; nothing in slices 1–4 changed.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: the pure, MAF-agnostic (or MAF-boundary) leaf pieces every story builds on. All are pure or
data-structure logic — each is written test-first, property tests where a law exists.

- [ ] T006 [P] Property + example tests for the approval descriptor round-trip in `tests/TgLLM.Maf.Tests/ApprovalDescriptorTests.fs` — `tryParse (serialize d) = Some d` for arbitrary descriptors; `tryParse` is total (bad JSON / missing fields / empty `RequestId`/`Tool` / null → `None`, never throws). MUST FAIL first.
- [ ] T007 [P] Property + example tests for owner-scope resolution in `tests/TgLLM.Maf.Tests/OwnerResolutionTests.fs` — explicit scope wins; message-initiated → `User sender`; host-initiated private chat (positive `chat.id`) → `User peer`; host-initiated non-private → `Anyone`. MUST FAIL first.
- [ ] T008 [P] Property + example tests for default approval rendering in `tests/TgLLM.Maf.Tests/ApprovalRenderingTests.fs` — `defaultRender` total on arbitrary prompts, body non-empty, plain-text (no parse mode), labels Approve/Reject; `validate` surfaces an over-limit body / invalid label as `Result.Error`, never throws. MUST FAIL first.
- [ ] T009 [P] Unit tests for `PendingApprovals` in `tests/TgLLM.Maf.Tests/PendingApprovalsTests.fs` — `Add`/`TryConsume` returns the entry to exactly one caller and removes it atomically (a second consume → `ValueNone`); `AbandonAllFor` drains a chat. MUST FAIL first.
- [ ] T010 [P] Define the leaf's shared vocabulary in `src/TgLLM.Maf/Types.fs` — `MafError` (`BodyInvalid`, `ReplyTooLong`), `ApprovalPrompt`/`ApprovalRender`/`ApprovalFormatter`, `ProjectionProblem`/`ProjectionReport`, `IMafObserver` (six members), `MafBridgeOptions` (`[<NoComparison; NoEquality>]`). No MAF types here.
- [ ] T011 [US-foundation] Implement `ApprovalDescriptor` + `serialize`/`tryParse` in `src/TgLLM.Maf/ApprovalDescriptor.fs` (plain flat DTO, `int64` chat retagged with UMX on parse) to green T006.
- [ ] T012 [P] Implement `RunOwner.resolve` in `src/TgLLM.Maf/OwnerResolution.fs` (pure; private-chat-peer from `ChatId` per the Bot API sign rule) to green T007.
- [ ] T013 [P] Implement `ApprovalRendering.defaultRender` + `validate` in `src/TgLLM.Maf/ApprovalRendering.fs` (plain text, `MessageText.create`/`ButtonLabel.create` validation) to green T008.
- [ ] T014 [US-foundation] Implement `PendingApproval` + `PendingApprovals` table (`[<NoComparison; NoEquality>]`, atomic `TryConsume`) in `src/TgLLM.Maf/PendingApprovals.fs` to green T009 (holds the live `ToolApprovalRequestContent` — leaf-only).

**Checkpoint**: the pure primitives are green and property-tested; no bridge/agent wired yet.

---

## Phase 3: User Story 1 — Approve/reject an agent's tool call from a button (Priority: P1) 🎯 MVP

**Goal**: a run that pauses on an approval-required tool renders one owner-scoped `[Approve][Reject]`
message; a tap resumes the agent exactly once and edits the message in place to the outcome / next request.

**Independent Test**: with a scripted agent that pauses for approval then returns a result, `StartRun`
in a chat → buttons appear; Approve → result edited in place; Reject → rejection edited in place; wrong
user / double tap → refused, no second resume.

### Tests for User Story 1 (write first, MUST FAIL) ⚠️

- [ ] T015 [P] [US1] Build the offline `ScriptedAgent : AIAgent` (override `RunCoreAsync` + `CreateSessionCoreAsync`; `Serialize/DeserializeSessionCoreAsync`/`RunCoreStreamingAsync` → `NotSupportedException`) in `tests/TgLLM.Integration.Tests/MafScriptedAgent.fs` — a step script yielding text or a `ToolApprovalRequestContent`, asserting the resumed `RequestId`/`Approved`.
- [ ] T016 [P] [US1] Integration tests for the approve/reject happy paths in `tests/TgLLM.Integration.Tests/MafBridgeApprovalTests.fs` via `FakeBotApiServer` — one message with `[Approve][Reject]`; Approve → resume(true) → outcome edited in place; Reject → resume(false) → rejection edited in place; a follow-up approval in the same turn → fresh buttons on the SAME message (message count does not grow). MUST FAIL.
- [ ] T017 [P] [US1] Integration tests for refusals in `tests/TgLLM.Integration.Tests/MafBridgeRefusalTests.fs` — out-of-scope tap refused (denied notice, no resume); repeat tap on a decided approval refused (single-use + table consume, no second resume); a sibling-button race resolves at most once. MUST FAIL.
- [ ] T018 [P] [US1] Integration test for the resume-failure path in `tests/TgLLM.Integration.Tests/MafBridgeFailureTests.fs` — a scripted throw on resume → `IMafObserver.OnResumeFailed` + the message edited to a failure note with NO live buttons remaining. MUST FAIL.

### Implementation for User Story 1

- [ ] T019 [US1] Implement `Conversation` + `Conversations` (lazy per-chat `AgentSession`, `GetOrCreate`/`Drop`) in `src/TgLLM.Maf/Conversations.fs` (MAF-typed, leaf-only, `ValueTask` from `CreateSessionAsync`).
- [ ] T020 [US1] Implement approval detection in `src/TgLLM.Maf/ApprovalDetection.fs` — scan `AgentResponse.Messages[*].Contents` for `ToolApprovalRequestContent`, extract `RequestId` + `FunctionCallContent` (`Name`/`Arguments`) into an `ApprovalPrompt`; total (no approvals → empty).
- [ ] T021 [US1] Implement the bridge core in `src/TgLLM.Maf/Bridge.fs` — register `maf-approve`/`maf-reject` into the bot's `ToolRegistry` (double-attach guard, as `A2ui.renderer`); render+send one approval message (`SendKeyboardPlan`, owner scope from `RunOwner.resolve`, single-use, optional expiry); record the `PendingApproval`.
- [ ] T022 [US1] Implement the decision tool handlers in `src/TgLLM.Maf/Bridge.fs` — parse the descriptor, `TryConsume` the pending entry, resume via `request.CreateResponse(approved) → ChatMessage(User,[..]) → RunAsync(session)` (multi-second work inside the tool → slice-3 deferred-ack rides for free), then edit-in-place: outcome text, or the next request's fresh `[Approve][Reject]` (bot-level `EditKeyboardPlan`); clean up the sibling token; on resume throw → `OnResumeFailed` + failure-note edit, `AbandonAllFor` the rest.
- [ ] T023 [US1] Implement the start functions in `src/TgLLM.Maf/Bridge.fs` — `Maf.startPolling` / `startPollingWith` / `startWebhook` (+ `…With`): build the bot from config (requires `.WithTools`), register the tools, return `MafBridge` (`Bot`, `StartRun : chat*prompt*?owner → Task` routed through the chat's dispatcher lane, `IAsyncDisposable`).
- [ ] T024 [US1] Wire `MafBridgeOptions.Observer` (default → bot logger, else Noop) so US1's stale/resume-fail paths report; confirm T015–T018 green and the existing 462 suite still green.

**Checkpoint**: MVP — an approval-requiring agent is a working Telegram approval bot via `StartRun`.

---

## Phase 4: User Story 2 — Reuse the agent's declared tools (Priority: P2)

**Goal**: project MAF `AIFunction`s into the library's registry/manifest in one call.

**Independent Test**: declare tools, `MafTools.project`, assert each appears in `ToolRegistry.ManifestJson()`
with matching name/description/schema; an invalid/duplicate declaration is surfaced, valid siblings register.

### Tests for User Story 2 (write first, MUST FAIL) ⚠️

- [ ] T025 [P] [US2] Property + example tests for the projection field mapping in `tests/TgLLM.Maf.Tests/ProjectionTests.fs` — `Name`→`ToolName`, `Description`→`ToolMetadata.Description` (empty→`None`), `JsonSchema.GetRawText()`→`ArgSchema` verbatim; manifest parity; invalid name / duplicate-within-set → `ProjectionProblem`, siblings still register; mirrored to `IMafObserver.OnProjectionProblem`. MUST FAIL.
- [ ] T026 [P] [US2] Integration test that a projected tool is actually invokable via the registry handler (`AIFunctionArguments` from the structured arg → `InvokeAsync` → JSON reply) in `tests/TgLLM.Integration.Tests/MafProjectionInvokeTests.fs`. MUST FAIL.

### Implementation for User Story 2

- [ ] T027 [US2] Implement `MafTools.project : ToolRegistry → AIFunction seq → ProjectionReport` in `src/TgLLM.Maf/Projection.fs` — per-tool `Result`, siblings register, problems collected + mirrored to the observer; the registered handler parses the arg into `AIFunctionArguments` and calls `InvokeAsync`.
- [ ] T028 [US2] Confirm T025–T026 green; manifest parity verified; existing suite green.

**Checkpoint**: US1 + US2 both work independently.

---

## Phase 5: User Story 3 — Minimal agent-as-a-bot + the Core message seam (Priority: P3)

**Goal**: an incoming user text message is answered by the agent's reply; the additive, MAF-agnostic Core
seam surfaces incoming text and routes it through the per-chat lane.

**Independent Test**: send text to a wired agent → reply in the same chat; two messages in one chat →
answered in arrival order; a `MessageReceived` with no `OnMessage` wired is a no-op (slices 1–4 unchanged).

### Tests for User Story 3 (write first, MUST FAIL) ⚠️

- [ ] T029 [P] [US3] Core-seam tests in `tests/TgLLM.Integration.Tests/MessageSeamTests.fs` — a user text `Update` maps to `AgentEvent.MessageReceived` (both transports, shared mapping); with no `?onMessage`, it is a no-op and every slice-1/2/3/4 behavior is byte-identical; a handler throw → `IMessageObserver.OnMessageFailed`, contained. MUST FAIL.
- [ ] T030 [P] [US3] Integration test for the text turn in `tests/TgLLM.Integration.Tests/MafTextTurnTests.fs` — incoming text → `RunAsync` → reply sent in the same chat; two messages in one chat processed in arrival order on the same lane; a turn that instead raises an approval hands off to the US1 flow. MUST FAIL.
- [ ] T031 [P] [US3] C# idiom-canary + text-turn smoke in `tests/TgLLM.CSharp.Tests/MafBridgeTests.cs` — the leaf's C# surface and the `TelegramAgentOptions.OnMessage` delta expose no `FSharpFunc`/`FSharpOption`/`FSharpValueOption`; a basic C# text turn works. MUST FAIL.

### Implementation for User Story 3

- [ ] T032 [US3] Add the Core seam (MAF-agnostic, additive) in `src/TgLLM.Core/Domain.fs` — `IncomingMessage` record + `AgentEvent.MessageReceived` case (existing cases untouched).
- [ ] T033 [US3] Add `MessageHandler` + `IMessageObserver` (Noop default) in `src/TgLLM.Core/Ports.fs` (new small interface — do NOT grow `IHookObserver`).
- [ ] T034 [US3] Extend `Mapping.toAgentEvent` in `src/TgLLM.Core/UpdateProcessor.fs` to map a user-text `Message` `Update` → `MessageReceived` (all other kinds still skipped); add `?onMessage`/`?messageObserver` optional params to `UpdateProcessor`, enqueuing `MessageReceived` on the SAME per-chat lane (`src/TgLLM.Core/Dispatcher.fs`) as taps, exceptions → `OnMessageFailed`.
- [ ] T035 [US3] Add the façade delta in `src/TgLLM.FSharp/TgBot.fs` — `CommonConfig.OnMessage: MessageHandler option` + `WithOnMessage` on BOTH `TgBotConfig` and `TgWebhookConfig` (lockstep).
- [ ] T036 [US3] Add the C# façade delta in `src/TgLLM.CSharp/TelegramAgent.cs` — `IncomingMessageInfo` DTO (long ids, no UMX leak) + `TelegramAgentOptions.OnMessage : Func<IncomingMessageInfo, CancellationToken, Task>?` (BCL delegate).
- [ ] T037 [US3] Wire the bridge's text-turn handler into the start functions in `src/TgLLM.Maf/Bridge.fs` — `config.WithOnMessage(bridge handler)` at startup (config-time wiring, since a running bot cannot late-bind); incoming text → `Conversations.GetOrCreate` → `RunAsync` → reply / hand off to the approval flow.
- [ ] T038 [US3] Confirm T029–T031 green; the full suite green with the Core seam additive (slices 1–4 byte-identical).

**Checkpoint**: US1 + US2 + US3 independently functional.

---

## Phase 6: User Story 4 — Reliability & observability sweep (Priority: P4)

**Goal**: every surfaced condition reaches the observer; the loop never throws or spams.

**Independent Test**: inject each condition and assert the observer sees it and the message state is correct.

### Tests for User Story 4 (write first, MUST FAIL) ⚠️

- [ ] T039 [P] [US4] Observability tests in `tests/TgLLM.Integration.Tests/MafObservabilityTests.fs` — post-restart / unknown decision → `OnStaleDecision` (descriptor still readable), acked, no resume; malformed decision arg → `OnMalformedDecision`; empty turn (no text, no approval) → `OnEmptyTurn`, no empty message sent; reply over the Bot API length limit → `OnInvalidOutput`, turn not crashed. MUST FAIL.
- [ ] T040 [P] [US4] Test the immediate-ack timing + approval expiry in `tests/TgLLM.Integration.Tests/MafAckTimingTests.fs` — a slow (multi-second) resume is acked within the spinner budget (deferred-ack); an expired decision keyboard tap lands in the stale path. MUST FAIL.

### Implementation for User Story 4

- [ ] T041 [US4] Complete the surfacing in `src/TgLLM.Maf/Bridge.fs` — `OnStaleDecision` on a `TryConsume` miss, `OnMalformedDecision` on descriptor parse failure, `OnEmptyTurn` + suppress an empty send, `OnInvalidOutput` on send-side validation (`ApprovalRendering.validate` / over-long reply); honor `MafBridgeOptions.ApprovalExpiry`.
- [ ] T042 [US4] Completeness sweep: assert (in code review + a coverage test) that EVERY `IMafObserver` member has a triggering path and no failure path throws out of the run loop; confirm T039–T040 + full suite green.

**Checkpoint**: the loop is hardened and fully observable.

---

## Phase 7: Polish & Cross-Cutting Concerns

- [ ] T043 [P] Complete the runnable F# example `examples/MafFSharp/Program.fs` — a MAF agent with an approval-required tool wired to a bot in ~20 lines (approval buttons + a text turn), matching `quickstart.md`.
- [ ] T044 [P] Complete the runnable C# example `examples/MafCSharp/Program.cs` — the same via the C# surface (`MafTelegramBridge.StartPolling`), idiom-clean.
- [ ] T045 [P] Update user documentation (`README.md` + `docs/`) for the MAF bridge — the approval-as-button quickstart, the tool-projection one-liner, both façades, the in-memory-session + preview-churn caveats; English; cite code + MAF/Bot API docs only.
- [ ] T046 Principle VII sweep across all new `src/TgLLM.Maf`, Core-seam, façade, test, and example files — remove any `FR/SC/US/T0xx/research-D#/spec-file/branch-slug` reference from comments/test names/assertions (the recurring slip); comments cite code + MAF/Telegram/.NET docs only.
- [ ] T047 Run the `quickstart.md` walkthrough end to end (both façades) against `FakeBotApiServer`; fix any drift between the doc and the shipped API.
- [ ] T048 Final gate: `dotnet build -c Release` (0 warn/0 err) + `dotnet test -c Release` all green (462 prior + new); `git diff --stat` confirms `src/TgLLM.Core` carries no MAF reference and slice-1/2/3/4 sources are untouched; a leaf-boundary check confirms no MAF type appears in Core/façade signatures.

---

## Dependencies & Execution Order

### Phase dependencies

- **Setup (P1)** → **Foundational (P2)** blocks all stories → **US1 (P3)** MVP → US2 / US3 / US4 → **Polish (P7)**.
- US1 depends only on Foundational (drivable via `StartRun`, no message seam). US2 depends only on Foundational. US3 adds the Core seam (extends `Bridge.fs`'s start functions from US1). US4 hardens US1's loop (extends `Bridge.fs`).
- Because US3 and US4 both extend `src/TgLLM.Maf/Bridge.fs`, they are sequential with US1's `Bridge.fs` tasks, not `[P]` against them.

### Within each story

- Tests first, MUST FAIL before implementation (TDD gate). Pure modules ([P], different files) before the bridge that composes them. Bridge core (T021) before its handlers (T022) before its start functions (T023).

### Parallel opportunities

- Setup: T003, T004 `[P]`. Foundational: T006–T009 (tests) `[P]`; T010, T012, T013 `[P]` (different files; T011/T014 follow their tests).
- US1 tests T015–T018 `[P]`. US2 tests T025–T026 `[P]`. US3 tests T029–T031 `[P]`. US4 tests T039–T040 `[P]`.
- Polish T043–T045 `[P]`.

---

## Implementation Strategy

**MVP = Phase 1 + Phase 2 + Phase 3 (US1)** — an approval-requiring MAF agent becomes a working Telegram
approval bot, drivable via `StartRun`. Stop and validate here; it is independently shippable.

**Incremental**: US2 (tool projection) → US3 (text turn + Core seam) → US4 (observability hardening) →
Polish (examples, docs, canary, Principle VII, final gate). Each adds value without breaking the prior
stories; commit after each task or logical group; the existing 462-test suite stays green throughout.
