# Feature Specification: MAF Bridge — Human-in-the-Loop Approval as Telegram Buttons

**Feature Branch**: `005-maf-bridge`

**Created**: 2026-07-08

**Status**: Draft

**Input**: User description: a flagship leaf package bridging a Microsoft Agent Framework (MAF) agent to a
Telegram bot — a direct MAF bridge, human-in-the-loop (HITL) approval-first MVP, reusing the Tool Router,
edit-in-place, owner-scoped single-use buttons, and durable bindings from the earlier slices. The library
core stays MAF-agnostic (and remains A2UI-agnostic); MAF lives only in the new leaf. Both facades (F#/C#)
and both transports (long polling / webhooks) are supported. Additive on the earlier slices — their public
behavior and tests are not changed.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Approve or reject an agent's tool call from a Telegram button (Priority: P1)

An integrator runs an AI agent that must ask a human before it performs a consequential action (send an
email, transfer funds, delete a record). When the agent reaches such an action mid-run, it pauses and asks
for approval. The bridge turns that pause into a Telegram message with **Approve** and **Reject** buttons in
the conversation's chat. The person tapping decides; the bridge relays the decision back to the agent, which
resumes and either finishes or asks for the next approval. The same message updates in place to show what
happened — no new message per step.

**Why this priority**: This is the flagship's reason to exist. The agent framework defines the *protocol*
for human approval but ships no chat surface for it; the earlier slices already built exactly the surface
needed — owner-scoped, single-use, expiring buttons with deferred acknowledgement, edit-in-place, and a
durable binding store. "An agent's tool approval as a Telegram button" does not exist elsewhere on this
platform, and it is deliverable on hardened primitives without new engine work. Shipped alone, it is a
complete, valuable product.

**Independent Test**: Configure an agent whose tool requires approval, start a run in a chat, and verify
that (a) Approve/Reject buttons appear, (b) tapping Approve lets the agent complete the action and the
message updates to the result, (c) tapping Reject makes the agent skip the action and the message updates
accordingly — all without the integrator writing any button or callback handling.

**Acceptance Scenarios**:

1. **Given** an agent whose next step needs approval, **When** the run reaches that step in a chat, **Then**
   a single message with Approve and Reject buttons appears in that chat.
2. **Given** those buttons, **When** the conversation's initiator taps Approve, **Then** the agent resumes
   with an approval, performs the step, and the original message updates in place to reflect the outcome.
3. **Given** those buttons, **When** the initiator taps Reject, **Then** the agent resumes with a rejection,
   does not perform the step, and the message updates in place to reflect that.
4. **Given** an approval message, **When** someone other than the conversation's initiator taps a button,
   **Then** the tap is refused and the agent is not resumed.
5. **Given** an approval already decided, **When** the same button is tapped again, **Then** the second tap
   is refused and the agent is not resumed a second time (the decision takes effect exactly once).
6. **Given** the agent asks for a further approval after the first is resolved, **When** the next request
   arrives, **Then** it is presented as a fresh decision (its own buttons) and the flow repeats.

---

### User Story 2 - Reuse the agent's declared tools as the bot's tool surface (Priority: P2)

An integrator has already described the agent's tools to the agent framework (each with a name, a
description, and a parameter schema). Rather than re-describe those tools to the bot library by hand, the
integrator projects the agent's tool declarations into the library's tool registry in one call, so the same
tools are available as the library's tool buttons and appear in its tool manifest.

**Why this priority**: The agent framework's tool declaration and the library's tool registry are nearly the
same shape, so the projection is low-effort and high-leverage — it removes duplicate, drift-prone tool
descriptions and makes the two systems agree by construction. It is valuable on its own but is not required
for the P1 approval loop, hence P2.

**Independent Test**: Declare a set of agent tools, project them into the library's registry in one call,
and verify each projected tool appears in the library's tool manifest with the same name, description, and
parameter schema — with no per-tool manual re-description.

**Acceptance Scenarios**:

1. **Given** a set of agent tool declarations, **When** the integrator projects them into the registry,
   **Then** every tool appears in the library's tool manifest with matching name, description, and schema.
2. **Given** a projected tool with an invalid or unrepresentable declaration, **When** projection runs,
   **Then** the problem is surfaced to the integrator rather than silently producing a broken tool.

---

### User Story 3 - A minimal agent-as-a-bot conversation (Priority: P3)

An integrator wires an agent to a chat so that a person's plain text message is answered by the agent. The
person types; the bridge asks the agent for a reply and sends the agent's answer back as a message. This is
the minimal conversational base onto which the P1 approval loop attaches (an approval interrupts an ordinary
turn).

**Why this priority**: The approval loop presupposes an ordinary turn to interrupt; this story provides that
base and exercises the one new core capability the library needs (surfacing an incoming user *text* message,
not only a button tap). It is intentionally minimal — a single request/response turn, no live streaming.

**Independent Test**: Send a text message to a wired agent in a chat and verify the agent's reply is sent
back as a message in the same chat, with conversation continuity across turns within the same chat.

**Acceptance Scenarios**:

1. **Given** an agent wired to a chat, **When** a person sends a text message, **Then** the agent's reply is
   sent back as a message in that chat.
2. **Given** two messages in one chat, **When** the second is sent after the first is answered, **Then** the
   agent processes them one at a time in order for that chat (no interleaving within a chat).
3. **Given** an agent turn that raises an approval request, **When** the turn runs, **Then** the approval
   flow of User Story 1 takes over for that turn.

---

### User Story 4 - The approval loop is reliable and observable (Priority: P4)

An operator needs the approval loop to behave predictably under real timing and failure: the person gets
immediate feedback that their tap registered even though the agent may take seconds to continue; a decision
survives long enough to find the request it belongs to; and any tap that can no longer be honored is
reported rather than lost.

**Why this priority**: Reliability and observability harden the P1 loop for real use; they are refinements on
top of a working loop rather than the loop itself.

**Independent Test**: Drive the loop while injecting realistic conditions — a slow agent continuation, a tap
on a decision whose run has already ended, a malformed decision payload — and verify each is surfaced or
handled per its rule with no silent loss.

**Acceptance Scenarios**:

1. **Given** an agent continuation that takes several seconds, **When** the initiator taps a decision,
   **Then** the tap is acknowledged immediately and the outcome is shown when the continuation completes.
2. **Given** a decision whose run has already finished (or whose pending request is no longer known), **When**
   it is tapped, **Then** the situation is surfaced to the host and nothing is silently dropped.
3. **Given** a malformed or unparseable decision payload, **When** it arrives, **Then** it is surfaced rather
   than acted upon.

---

### Edge Cases

- **Non-owner tap** on an approval button → refused; the agent is not resumed (owner-scoped, per the earlier
  slice's ownership rule).
- **Double tap / repeat decision** on an already-resolved approval → refused; the decision takes effect
  exactly once (single-use).
- **Stale / expired / unknown decision** — the run already ended, the in-memory session is gone, or the token
  is unrecognized → surfaced to the host through observation, never silently swallowed.
- **Malformed decision payload** (cannot be parsed back into a valid approve/reject decision) → surfaced, not
  acted upon.
- **Multiple approval requests in one agent turn** → each pending request is presented as its own decision;
  the turn continues once its pending requests are decided.
- **Agent produces no text and no approval request** (empty turn) → the person is not left without feedback;
  the empty result is surfaced rather than sending an empty message.
- **Agent reply longer than a single Telegram message allows** → surfaced/handled rather than crashing the
  turn (consistent with how over-long text is handled elsewhere in the library).
- **Two chats served by one bridge** → decisions and replies are addressed to the chat that owns the
  conversation, never another chat (the cross-chat-safety rule established in the previous slice).
- **Agent continuation fails** (the framework raises during resume) → the failure is surfaced and the message
  reflects that the step did not complete, rather than leaving stale live buttons.

## Requirements *(mandatory)*

### Functional Requirements

**Approval loop (User Story 1)**

- **FR-001**: The bridge MUST detect when a running agent pauses to request human approval for a tool call and
  present that request in the conversation's chat as a single message carrying an Approve control and a Reject
  control.
- **FR-002**: The bridge MUST let the host specify which chat a conversation belongs to when a run is started
  (the agent framework is chat-agnostic; the host supplies the target).
- **FR-003**: On Approve, the bridge MUST resume the agent with an approval for the corresponding request; on
  Reject, it MUST resume with a rejection; in both cases the agent continues from where it paused.
- **FR-004**: After a decision, the bridge MUST update the original approval message in place to reflect the
  outcome or the next step, rather than sending a new message for each step.
- **FR-005**: The bridge MUST restrict who can act on an approval to the conversation's initiator; a tap by
  anyone else MUST be refused and MUST NOT resume the agent (owner-scoped).
- **FR-006**: The bridge MUST ensure each approval decision takes effect at most once; a repeated tap on an
  already-resolved decision MUST be refused and MUST NOT resume the agent again (single-use).
- **FR-007**: When the agent raises a further approval request after one is resolved, the bridge MUST present
  it as a new decision and repeat the loop.

**Tool-surface unification (User Story 2)**

- **FR-008**: The bridge MUST let the host project an agent's declared tools into the library's tool registry
  in a single operation, preserving each tool's name, description, and parameter schema.
- **FR-009**: Projected tools MUST appear in the library's tool manifest identically to tools registered
  directly, so no tool needs to be described twice.
- **FR-010**: When a tool declaration cannot be represented in the registry, the bridge MUST surface the
  problem to the host rather than register a broken tool.

**Minimal agent-as-a-bot (User Story 3)**

- **FR-011**: The bridge MUST answer an incoming user text message by asking the agent for a reply and sending
  that reply back as a message in the same chat.
- **FR-012**: The library MUST surface an incoming user *text* message to the host (today only button taps are
  surfaced), via an additive event and hook, without changing the existing tap-handling behavior.
- **FR-013**: Messages and taps within a single chat MUST be processed one at a time in arrival order for that
  chat (serialized per chat), so a conversation's state is never accessed concurrently.

**Reliability & observability (User Story 4)**

- **FR-014**: The bridge MUST acknowledge a decision tap immediately, before the agent's (possibly
  multi-second) continuation completes, and reveal the outcome when the continuation finishes.
- **FR-015**: A decision that can no longer be honored — the run ended, the conversation state is gone, or the
  token is unknown/expired — MUST be surfaced to the host through observation and MUST NOT be silently dropped.
- **FR-016**: A malformed or unparseable decision MUST be surfaced and MUST NOT be acted upon.
- **FR-017**: A failure raised by the agent while resuming MUST be surfaced, and the approval message MUST NOT
  be left showing live buttons for a step that will not complete.

**Cross-cutting constraints**

- **FR-018**: The library core MUST remain agnostic of the agent framework — no core type may depend on the
  agent framework; the bridge and all framework types MUST live only in the new leaf.
- **FR-019**: The feature MUST be additive: the public behavior and existing tests of the earlier slices MUST
  remain unchanged.
- **FR-020**: The feature MUST work under both supported transports (long polling and webhooks) and be usable
  from both the F# and the C# facade.
- **FR-021**: Every host-observable failure mode named above MUST be reportable through an observation seam,
  consistent with the "never silently drop" rule established in the previous slice.

### Key Entities *(include if feature involves data)*

- **Conversation**: an ongoing exchange between one person and the agent, bound to exactly one chat; carries
  the agent's in-memory working state for that chat (not persisted across process restart in this release);
  processed serially.
- **Approval Request**: a pending human decision raised by the agent — which tool, with what arguments, for
  which conversation/owner — identified by a token so a later tap can be matched back to it.
- **Approval Decision**: the person's approve-or-reject answer to a specific Approval Request; owner-scoped and
  single-use.
- **Tool Declaration → Tool Projection**: an agent-declared tool (name, description, parameter schema) and its
  image in the library's tool registry/manifest.
- **Bridge**: the top-level object that wires one agent to Telegram — accepts incoming text and taps, drives
  the agent, renders approvals, and reports failures.
- **Observation**: the reporting seam through which the host learns of surfaced conditions (stale/unknown
  decision, malformed decision, resume failure, projection problem).

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: An integrator can turn an approval-requiring agent into a working Telegram approval bot while
  writing zero button-plumbing or callback-routing code of their own — they supply only the agent and the
  target chat.
- **SC-002**: When the agent requests approval, the decision buttons appear in the chat within the time of a
  single message send under normal conditions.
- **SC-003**: 100% of taps by anyone other than the conversation's initiator are refused (no non-owner tap
  ever resumes the agent).
- **SC-004**: 100% of repeated taps on an already-resolved decision are refused (no decision ever resumes the
  agent more than once).
- **SC-005**: Across a multi-step approval conversation, the number of messages the bot sends does not grow
  with the number of decision steps for a given request — the same message is updated in place.
- **SC-006**: 0 silent drops — every stale, unknown, malformed, or failed decision is surfaced to the host in
  a form the host can observe.
- **SC-007**: 100% of the earlier slices' existing tests continue to pass unchanged (the feature is additive).
- **SC-008**: An agent-declared tool set is available through the library's tool manifest without the
  integrator re-describing any tool — every projected tool matches its source declaration's name, description,
  and schema.
- **SC-009**: A person's text message to a wired agent is answered by the agent's reply in the same chat, and
  successive messages in one chat are answered in order.

## Assumptions

- **Ownership** of an approval follows the previous slice's rule: the conversation's initiator is the owner;
  only they may act on the approval, and each approval is single-use.
- **Multiple approval requests in one agent turn** are each presented as their own decision; the turn proceeds
  once its pending requests are decided. (Refinable in clarification; the common case is a single request.)
- **Conversation state is in-memory for this release** — surviving a process restart mid-approval (durable
  agent sessions) is explicitly out of scope and deferred to a later slice.
- **The reply path is non-streaming for this release** — the agent's reply is produced as a whole and sent as
  a message; live token-streaming with coalesced edits is deferred to a later slice.
- **Approval decisions reuse the existing durable binding store and expiry mechanism** from the previous
  slices; a decision that outlives its request's usefulness is treated as stale and surfaced.
- **The host relays between the bridge and its agent** — the bridge does not choose or host the agent; the
  integrator supplies a configured agent and the target chat.
- **Agent framework dependency is pinned to an exact version** and confined to the leaf, so framework churn
  cannot reach the core or the facades (the leaf is the blast shield); the pinned version is re-verified during
  planning.
- **Serialization of conversation state per chat** is achieved by routing incoming text and taps for a chat
  through the same per-chat processing lane, which also gives safe (non-concurrent) access to the agent's
  working state.

### Out of scope (deferred backlog)

- Live streaming of the agent's reply (token-by-token) with throttled, coalesced edit-in-place.
- Durable, restart-surviving conversation state (persisted agent sessions).
- Multi-step guided flows / wizards driven by the agent framework's workflow constructs (step keyboards and
  checkpoints).
- The agent planning a whole keyboard layout for the bot to render.
- A broader agent-UI foundation that consumes any event-stream agent-to-user protocol (a separate future
  leaf); the positioning stays the vision but is not this slice's MVP.
- Standing infrastructure debts carried from earlier slices: a second target framework in the CI matrix,
  package publication, full public-API doc-comment coverage, and smoke tests against a live bot and a live
  agent.

### Dependencies

- Builds on the earlier slices' Tool Router, owner-scoped single-use buttons, deferred acknowledgement,
  edit-in-place delivery, and durable binding store.
- Requires the host to supply a configured agent (from the agent framework) and to designate the chat a
  conversation belongs to.
- Requires one additive core capability: surfacing an incoming user text message (today only button taps are
  surfaced), routed through the existing per-chat processing lane.
