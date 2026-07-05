# Feature Specification: Interactive Keyboards with Button Hooks (Agent PoC)

**Feature Branch**: `001-inline-keyboard-hooks`

**Created**: 2026-07-04

**Status**: Draft

**Input**: User description: "мы делаем либу которая поможет управляться агентам с Telegram; агенты сидят со стороны бота; агенты реагируют на сообщения в чат либо на внешний раздражитель и могут писать в чат; либа даёт возможность строить агенту сложные UI и реагировать на них; либа зиждется на Bot API телеграмма; либа не даёт агенту ничего, кроме удобства использования Telegram. Первый тонкий срез: агент произвольно шлёт в чат набор клавиатуры и вешает любой хук на любые кнопки этой клавиатуры. Это основная фича. Для этого надо построить обработчик входящих сообщений. Базовый proof of concept."

## Overview

The library exists to make Telegram convenient for **agents** — bot-side logic that reacts to
chat messages or to external stimuli and can write back to a chat. It gives the agent no new
powers beyond what Telegram already offers; its only value is ergonomics: letting the agent
build interactive UI in a chat and react to interactions with a minimum of plumbing.

This first, deliberately thin slice proves the core loop: an agent can **send a set of buttons
to a chat and attach an arbitrary handler ("hook") to each button**, and when an end user taps a
button the agent's hook runs and can react in the chat. Delivering this requires an incoming-update
handler that routes button presses back to the right hook.

## Clarifications

### Session 2026-07-04

- Q: Which update-delivery transport(s) are in scope for this PoC slice? → A: Both long polling and
  webhooks; the update-handling core stays transport-agnostic (fully satisfies constitution
  Principle IV within the PoC).
- Q: How does an agent's hook react to a button press? → A: Imperatively, via a press-context object
  — the most convenient model for the agent (the library's core value). The hook receives a context
  exposing chat-reaction operations; a declarative style may be layered on later without breaking the
  imperative API.
- Q: Where do button→hook associations live? → A: In memory by default, but behind a storage-abstraction
  seam so the store can be relocated to external/durable storage without changing agent code.
- Q: How are concurrent presses/updates processed? → A: Sequentially within a single chat (in arrival
  order) and concurrently across different chats.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Send an interactive keyboard and react to taps (Priority: P1)

An agent composes a keyboard — a set of labeled buttons — and attaches a distinct hook to each
button. The agent sends this keyboard to a chat. An end user in that chat sees the buttons and
taps one. The agent's hook for exactly that button runs, receives the context of the tap, and
reacts (for example, by replying in the chat). The end user's client confirms the tap was received.

**Why this priority**: This is the whole point of the slice — the send-a-keyboard-and-react loop.
On its own it is a usable, demonstrable product: an agent can put an interactive control in a chat
and respond to it. Everything else is refinement.

**Independent Test**: Send a two-button keyboard (each button wired to its own hook) to a test
chat, tap each button in turn, and confirm the correct hook runs for each and the agent's reaction
appears in the chat, with the tap acknowledged on the end user's side.

**Acceptance Scenarios**:

1. **Given** the agent has defined a keyboard with buttons "Yes" and "No", each with its own hook,
   **When** the agent sends it to a chat, **Then** the end user sees both buttons under the message.
2. **Given** that keyboard is visible to the end user, **When** the user taps "Yes", **Then** the
   hook attached to "Yes" runs (and not the "No" hook) and the agent's reaction appears in the chat.
3. **Given** the user taps a button, **When** the hook is invoked, **Then** the end user's client
   stops showing a pending/loading state on the tap within a few seconds.
4. **Given** the "Yes" hook has run, **When** the user then taps "No", **Then** the "No" hook runs
   independently of the earlier tap.

---

### User Story 2 - Proactively send a keyboard from an external stimulus (Priority: P2)

An agent sends an interactive keyboard to a chat not in reply to a user message, but triggered by
something outside the chat — a timer firing, an external event, another system's signal. The
buttons and their hooks behave exactly as in User Story 1.

**Why this priority**: The stated model is that agents react "to a message **or to an external
stimulus**". Proactive sending proves the library does not assume every interaction begins with an
incoming user message. It is secondary only because the core tap-and-react loop (US1) must exist first.

**Independent Test**: With no preceding message from the target chat, trigger a keyboard send from
an external event (e.g., a manual trigger standing in for an external signal); confirm the keyboard
appears in the chat and its buttons route to their hooks as in US1.

**Acceptance Scenarios**:

1. **Given** no recent message from the target chat, **When** an external trigger asks the agent to
   send a keyboard, **Then** the keyboard is delivered to that chat.
2. **Given** a proactively sent keyboard, **When** the end user taps a button, **Then** its hook
   runs exactly as for a keyboard sent in reply.

---

### User Story 3 - Correct routing across many buttons, users, and keyboards (Priority: P3)

Multiple buttons, multiple concurrent keyboards, and multiple end users all interact. Every tap
invokes only the one hook registered for the tapped button, regardless of how many buttons,
keyboards, or users are in play.

**Why this priority**: Building "complex UI" means keyboards with many buttons and multiple
keyboards live at once. Routing must stay exact under that load. It is P3 because US1 already
demonstrates per-button routing at small scale; this hardens it.

**Independent Test**: Send two different keyboards to the same chat (and to a second chat), each
with several distinctly-hooked buttons; have taps arrive interleaved; confirm each tap invokes only
its own hook with no cross-invocation.

**Acceptance Scenarios**:

1. **Given** two keyboards each with three distinctly-hooked buttons, **When** buttons from both are
   tapped in an interleaved order, **Then** each tap invokes only its own button's hook.
2. **Given** two different end users tap buttons at nearly the same time, **When** the presses are
   handled, **Then** each user's tap invokes the correct hook with that user's context.

---

### Edge Cases

- **Stale keyboard after restart**: an end user taps a button whose hook is no longer registered
  (e.g., the bot process restarted). The press is acknowledged and no hook runs; no error is raised
  and update processing continues.
- **Hook failure**: a hook throws or misbehaves. The failure is isolated — the bot keeps processing
  further updates — and is surfaced (observable), not silently swallowed.
- **Rapid repeated taps**: an end user taps the same button several times quickly. Each valid press
  is acknowledged; hook invocation follows the agent's hook each time.
- **Simultaneous taps**: presses in different chats are handled concurrently; presses within the same
  chat are handled one at a time in arrival order, each with its own context.
- **Unknown / malformed press**: a press that does not correspond to any known button is acknowledged
  and ignored without error.
- **Large keyboards**: a keyboard with many buttons still routes correctly; the agent is not forced to
  manage low-level payload encoding or size limits for typical keyboards.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The library MUST let an agent define a keyboard as an ordered arrangement of one or more
  rows of buttons, where each button has a visible label.
- **FR-002**: The library MUST let the agent attach an arbitrary handler ("hook") to each button; a
  hook is agent-provided logic to run when that button is pressed.
- **FR-003**: The library MUST let the agent send a keyboard to any chat the bot can reach, at any
  time, including with no preceding message from that chat (proactive send).
- **FR-004**: The library MUST receive incoming button-press events and invoke the exact hook
  registered for the pressed button.
- **FR-005**: When invoking a hook, the library MUST provide it with the press context: which button
  was pressed, in which chat, by which end user, and on which originating message.
- **FR-006**: The library MUST let a hook react in the chat (at minimum, send a message back) as part
  of handling a press.
- **FR-007**: The library MUST acknowledge every button press back to Telegram so the end user's
  client reflects that the tap was received (no indefinite pending/loading state).
- **FR-008**: The library MUST route each press to only its own button's hook; a press MUST NOT
  invoke a hook belonging to a different button.
- **FR-009**: The library MUST keep processing subsequent updates when a hook fails; a failing hook
  MUST NOT halt update processing, and the failure MUST be observable rather than silently discarded.
- **FR-010**: The library MUST safely handle presses with no registered hook (e.g., stale keyboards):
  acknowledge the press, run no hook, and raise no error.
- **FR-011**: The library MUST manage the association between buttons and their hooks so the agent
  does not have to hand-encode Telegram callback payloads or manage their raw size limits for typical
  keyboards.
- **FR-012**: The library MUST support keyboards composed of multiple buttons across multiple rows as
  a building block for richer UI.
- **FR-013**: The library MUST support receiving button-press events via both long polling and
  webhooks, and hook behavior MUST be identical regardless of which transport delivered the press.
- **FR-014**: When a button is pressed, the library MUST invoke its hook with a press-context object
  that both carries the press context (per FR-005) and exposes operations for the hook to react in the
  chat (e.g., reply).
- **FR-015**: The library MUST process presses within the same chat one at a time in their arrival
  order, while presses in different chats MAY be processed concurrently.
- **FR-016**: The library MUST keep button→hook associations behind a storage abstraction so the
  default in-memory store can be replaced with an external/durable store without changes to agent code.

### Key Entities

- **Agent**: the bot-side logic that uses the library; it defines keyboards, attaches hooks, sends
  keyboards, and reacts to presses.
- **End User**: the Telegram participant who sees a keyboard in a chat and taps its buttons.
- **Chat**: a Telegram conversation the bot can send to.
- **Interactive Keyboard**: a set of buttons arranged in rows, delivered to a chat with a message.
- **Button**: a labeled element of a keyboard, associated with exactly one hook.
- **Hook (Handler)**: agent-provided logic invoked when its button is pressed; receives a press-context
  object carrying the press context and exposing operations to react in the chat.
- **Button-Press Event**: an end user tapping a button; carries the button identity, chat, end user,
  and originating message.
- **Hook Store**: the collection of button→hook associations. In-memory by default and accessed behind
  a storage abstraction so it can be relocated to external/durable storage without changing agent code.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: An agent can wire up a working interactive keyboard end-to-end — define buttons, attach
  a hook per button, and send it — without hand-writing any Telegram protocol/plumbing code, in under
  15 minutes for someone new to the library. *(Developer-experience metric — validated by a manual
  walkthrough against the quickstart and examples, not by an automated test.)*
- **SC-002**: Across a run of at least 100 button presses spanning all buttons of a keyboard, 100% of
  presses invoke the exact hook registered for the pressed button, with zero cross-invocations.
- **SC-003**: For at least 99% of taps, the end user's client shows the tap was received (pending
  state clears) within 3 seconds.
- **SC-004**: For at least 95% of taps under normal conditions, the registered hook begins executing
  within 3 seconds of the tap.
- **SC-005**: An agent can send a keyboard to a chat with no preceding user message (external-stimulus
  send) and have its buttons route to hooks identically to a keyboard sent in reply — demonstrated in
  a single test.
- **SC-006**: A hook that fails does not stop the bot: after an intentionally failing hook runs,
  subsequent presses still invoke their hooks, and the failure is recorded/observable.
- **SC-007**: In a test interleaving presses from two chats, presses within each chat are handled in
  arrival order (no reordering) while the two chats make progress concurrently.
- **SC-008**: The full send-keyboard-and-react flow passes its acceptance tests unchanged under both
  long polling and webhook delivery.

## Assumptions

- **Transport scope (PoC)**: This slice supports **both** update-reception mechanisms — long polling
  and webhooks — from the outset, fully satisfying constitution Principle IV within the PoC. The
  update-handling core is transport-agnostic: agent hook code is identical regardless of how the
  update arrived. Webhook delivery requires a publicly reachable endpoint (or tunnel) to exercise
  end-to-end; long polling runs locally without one.
- **Hook interaction model**: A hook is invoked imperatively with a press-context object that exposes
  operations to react in the chat (e.g., reply). This is the most convenient model for agents. A
  declarative style (hook returns a description of reactions) may be layered on later without breaking
  the imperative API.
- **Hook lifetime & storage**: Button-to-hook associations are held in an in-memory store by default,
  scoped to the running bot process; they are not persisted across restarts. The store sits behind a
  storage abstraction (FR-016) so it can be relocated to external/durable storage later without
  changing agent code. Presses that arrive after an association is lost are handled per FR-010.
- **Button kind**: Buttons in this slice are the kind that invoke agent logic on press (callback
  buttons). Other button kinds (opening URLs, switching to inline mode, etc.) are out of scope here.
- **Credentials & access**: The bot already has valid Telegram credentials and the right to post in
  the target chat. Credential acquisition and permission management are out of scope for this slice.
- **"Any chat the bot can reach"** follows Telegram's own rules (the user has started the bot, or the
  bot is a member of the group/channel with the needed rights). The library adds no capability beyond
  the Bot API — its only value is convenience.
- **Single bot identity**: One bot is assumed. Orchestrating multiple bots is out of scope for this slice.
- **Out of scope for this slice**: persisting hooks across restarts (only the storage seam is in
  scope, not a durable implementation), non-callback button types, rich message editing/media beyond
  sending a keyboard and a text reaction, multi-bot orchestration, and rate-limit backoff strategies
  (beyond simply not crashing).
