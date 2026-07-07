# Feature Specification: A2UI Renderer for Telegram

**Feature Branch**: `004-a2ui-renderer`

**Created**: 2026-07-07

**Status**: Draft

**Input**: User description: "Четвёртый срез TgLLM.NET. A2UI-рендерер для Telegram — новый leaf-проект
TgLLM.A2UI, рендерит Telegram-представимое подмножество открытого протокола A2UI (a2ui.org, Google,
Apache-2.0, v1.0) поверх Tool Router + edit-in-place из 002/003, двунаправленно. US1 рендер surface в
сообщение; US2 двунаправленный action-цикл; US3 стриминг/обновления; US4 каталог и неподдерживаемые
компоненты. Backlog: WebApp-fallback, input-компоненты + two-way binding, throttling-политика, List-
шаблоны, инфра-долги."

## Overview

Slices 1–3 built a Tool Router: an LLM agent produces a neutral keyboard plan through *our* data type,
and the library routes taps to tools, edits messages in place, and persists bindings. This slice takes
the next step in interoperability: it makes the library speak a **standard** agent-UI protocol.

**A2UI** (a2ui.org, Google, Apache-2.0) is an open, declarative protocol where an agent describes a UI
as messages — `createSurface` / `updateComponents` / `updateDataModel` / `deleteSurface` — built from a
pre-approved **component catalog**, and user interactions flow back to the agent as `action` messages.
Any agent that already emits A2UI (for a web/mobile renderer) can, with this slice, drive a **Telegram**
bot instead — with no Telegram-specific code on the agent side.

This slice adds a new leaf project, `TgLLM.A2UI`: a **renderer** that maps the Telegram-representable
subset of A2UI onto the existing Tool Router and edit-in-place machinery, **bidirectionally** — surfaces
become messages, buttons become inline keyboard taps that emit A2UI `action` messages, and the agent's
follow-up messages edit the message in place. Telegram can only display a fraction of A2UI's catalog, so
the renderer advertises a narrow **`telegram-basic`** catalog and surfaces (never silently drops) any
component it cannot render — consistent with A2UI's own rule that a renderer only instantiates approved
components.

The library **Core stays A2UI-agnostic**: A2UI lives entirely in the new leaf project (A2UI v1.0 is a
candidate spec — a moving target we stay *aware* of, not *coupled* to). It builds on and extends slices
1–3; it does not replace them, and their APIs and tests are untouched.

## Clarifications

### Session 2026-07-07

- Q: How much of A2UI does this slice render? → A: A narrow **`telegram-basic`** catalog — the subset a
  Telegram message + inline keyboard can represent: **Text** (→ message text, Markdown), **Button**
  (→ inline keyboard button), **Row** / **Column** (→ keyboard layout), **Divider** (→ separator), and
  optionally **Image** (→ photo or link). Input components (TextField, CheckBox, Slider, DateTimeInput,
  ChoicePicker) and rich containers (Tabs, Modal, Card, List templates, Video, AudioPlayer) are NOT
  rendered — they are surfaced as an "unsupported component," not silently rendered.
- Q: How does a surface map to Telegram? → A: One **surface ↔ one Telegram message** in a chat,
  identified by `(chat, message_id)`. `createSurface` + the initial `updateComponents` send the message;
  later `updateComponents` / `updateDataModel` on that surface **edit it in place**; `deleteSurface`
  deletes it.
- Q: How does a user tap flow back to the agent? → A: A Button carries an `action.event`; tapping it
  makes the renderer build an A2UI **`action`** message (`name`, `surfaceId`, `sourceComponentId`,
  `timestamp`, resolved `context`, `wantResponse`, `actionId`) and hand it to a **host-provided sink**
  (the host relays it to its agent over whatever transport it uses; the library ships no agent
  transport). The agent's follow-up `updateComponents`/`updateDataModel` re-render in place. A Button
  whose action is a **local `functionCall`** (e.g. `openUrl`) renders as a client-side button (URL) with
  no server round-trip and emits no `action`.
- Q: How is A2UI's incremental streaming handled, given Telegram sends whole messages? → A: The renderer
  **coalesces** the incoming message stream per `surfaceId` and renders a coherent snapshot on flush
  (a message is sent/edited as a whole, not streamed partially); in-order processing is preserved.
- Q: Which data binding is in scope? → A: Absolute **JSON-Pointer** resolution of `DynamicString` text/
  labels against the surface's data model (enough to render text and button labels). Nested
  Collection-Scope / `List` template iteration and two-way input binding are out of scope this slice.
- Q: Is A2UI's license compatible? → A: Yes — A2UI is Apache-2.0; implementing the protocol is
  compatible with this library's MIT license.
- Q: A2UI carries no chat/transport identity — how does a surface get a target Telegram chat? → A: The
  **host supplies the target chat** when it hands a `createSurface` to the renderer; the renderer binds
  that surface to `(chat, message_id)` on the first render. The library ships no A2UI transport, so the
  host is what connects an A2UI stream to a specific chat — the renderer's ingest takes `(chat, message)`.
- Q: What happens on a second `createSurface` for an already-live surface id? → A: **Reject with a
  surfaced error** — `createSurface` is create-once; a live surface is changed via `updateComponents`/
  `updateDataModel`, not re-created.
- Q: What does a bound Text with an unresolved data-model path render as? → A: The **empty string**
  (documented), never a crash.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Render an A2UI surface as a Telegram message (Priority: P1)

An agent that already speaks A2UI sends the renderer a `createSurface` plus `updateComponents` describing
a small UI — some Text and a couple of Buttons laid out in Rows/Columns. The renderer turns that
component tree into a single Telegram message: the Text becomes the message body (Markdown), the Buttons
become an inline keyboard, and Row/Column determine the button layout. The agent writes **no
Telegram-specific code** — it emits the same A2UI it would for a web renderer.

**Why this priority**: This is the whole point of the slice — an A2UI-speaking agent drives a Telegram
bot. On its own it is a usable product: static interactive surfaces render end-to-end.

**Independent Test**: Feed a `createSurface`+`updateComponents` (Text + two Buttons in a Row) to the
renderer; confirm one Telegram message is sent whose text and inline-keyboard layout match the tree.

**Acceptance Scenarios**:

1. **Given** a `createSurface` (telegram-basic catalog) with a root Column of a Text and a Row of two
   Buttons, **When** the renderer processes it, **Then** one message is sent with the Text as its body
   and the two Buttons as one keyboard row.
2. **Given** a Text component bound to a data-model path, **When** the surface renders, **Then** the
   message body shows the resolved value.
3. **Given** a Column of three Buttons, **When** it renders, **Then** the keyboard has three rows of one
   button each (Column → stacked, Row → side by side).

---

### User Story 2 - A tap flows back to the agent, and its reply re-renders in place (Priority: P2)

When a user taps a Button, the renderer builds an A2UI `action` message (the button's action name,
the surface id, the source component id, a timestamp, and the resolved context) and hands it to the
host's sink to relay to the agent. The agent responds with `updateComponents`/`updateDataModel` for the
same surface, and the renderer edits the **same message** in place — a multi-step A2UI conversation
rendered as one evolving Telegram message. `deleteSurface` removes it.

**Why this priority**: The bidirectional loop is what makes A2UI interactive; rendering (US1) must exist
first. It reuses the slice-2/3 edit-in-place and routing.

**Independent Test**: Render a surface with a Button; simulate a tap; confirm an A2UI `action` message
with the correct fields reaches the host sink; feed the agent's `updateComponents` reply; confirm the
same message is edited (no new message).

**Acceptance Scenarios**:

1. **Given** a rendered Button with an `action.event`, **When** the user taps it, **Then** the host sink
   receives an A2UI `action` message carrying the action name, surface id, source component id, and
   resolved context.
2. **Given** that tap, **When** the agent replies with `updateComponents` for the surface, **Then** the
   original message is edited in place (no new message).
3. **Given** a Button whose action is a local `functionCall` `openUrl`, **When** the user taps it,
   **Then** their client opens the link and no `action` is emitted.
4. **Given** a `deleteSurface`, **When** the renderer processes it, **Then** the surface's message is
   deleted.

---

### User Story 3 - Streaming and incremental surface updates (Priority: P3)

An agent streams a surface incrementally (createSurface, then several updateComponents/updateDataModel).
Because Telegram sends whole messages, the renderer coalesces the stream per surface and renders a
coherent snapshot; further updates to a live surface edit the message in place rather than sending new
messages.

**Why this priority**: Real agents stream; coalescing keeps one surface as one evolving message. P3
because a non-streaming (single-batch) surface already works from US1/US2.

**Independent Test**: Feed a createSurface followed by two updateComponents for the same surface without
an intervening flush trigger; confirm exactly one message is sent then edited, not three messages.

**Acceptance Scenarios**:

1. **Given** a createSurface followed by two updateComponents for the same surface, **When** the stream
   is flushed, **Then** exactly one message exists, reflecting the latest state.
2. **Given** a live surface, **When** an updateDataModel changes a bound value, **Then** the message
   text updates in place.

---

### User Story 4 - Catalog negotiation and unsupported components (Priority: P4)

The renderer advertises only the `telegram-basic` catalog. A `createSurface` naming an unknown catalog,
or an `updateComponents` containing a component outside telegram-basic, is handled explicitly: a surfaced
A2UI error / "unsupported component" report, never a silent drop or a corrupted render. The bot keeps
working.

**Why this priority**: Correct, honest behavior at the edge of the supported subset; lowest priority
because well-formed telegram-basic surfaces (US1–US3) are the common path.

**Independent Test**: Feed a surface containing a `Slider`; confirm the renderer reports "unsupported
component" (observable), does not crash, and still renders the supported siblings or refuses cleanly.

**Acceptance Scenarios**:

1. **Given** a `createSurface` with an unknown `catalogId`, **When** processed, **Then** the renderer
   emits a surfaced A2UI error and renders nothing for it.
2. **Given** an `updateComponents` containing a `TextField` (outside telegram-basic), **When** processed,
   **Then** the unsupported component is surfaced (observable) and does not corrupt the message.
3. **Given** a malformed A2UI message (missing `version`/required field), **When** processed, **Then** it
   is surfaced as an error and the bot keeps working.

---

### Edge Cases

- **Unknown `catalogId`** on `createSurface`: surfaced A2UI error, nothing rendered, no crash.
- **Unsupported component** (input/rich container) inside a supported surface: surfaced, not silently
  dropped or rendered wrong.
- **Malformed / wrong-version A2UI message**: surfaced as an error, bot keeps working.
- **Unresolved data-model path** for a bound Text: renders as the **empty string** (documented), never
  a crash.
- **`updateComponents` for an unknown / already-deleted surface**: surfaced, no crash.
- **Edit-in-place on a vanished surface message** (user deleted it): soft failure (reusing slice-3's
  soft edit-error handling), no exception to the host.
- **A surface with no renderable content** (only unsupported components): surfaced; no empty message
  spam.
- **A Button with `wantResponse: true` but no `actionId`**: surfaced as a malformed action.
- **Duplicate `surfaceId`** (a second createSurface for a live surface): **rejected** with a surfaced
  error (create-once); the agent changes a live surface via `updateComponents`/`updateDataModel`.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The renderer MUST accept A2UI agent→renderer messages (`createSurface`, `updateComponents`,
  `updateDataModel`, `deleteSurface`) and render a **surface as a single Telegram message**.
- **FR-002**: The renderer MUST map the **`telegram-basic`** catalog to Telegram: `Text` → message text
  (Markdown); `Button` → inline keyboard button; `Row` → buttons in one keyboard row; `Column` → stacked
  rows; `Divider` → a separator; `Image` (optional) → a photo or link. It MUST render only this subset.
- **FR-003**: A surface MUST map to a Telegram message identified by `(chat, message_id)`. Because A2UI
  carries no chat identity, the **host supplies the target chat** when it hands a `createSurface` to the
  renderer (the ingest takes the chat alongside the message); the initial render sends the message and
  records that `(chat, message_id)`; later updates target it.
- **FR-004**: On a Button press, the renderer MUST build an A2UI **`action`** message (`name`,
  `surfaceId`, `sourceComponentId`, `timestamp`, resolved `context`, `wantResponse`, `actionId`) and
  deliver it to a **host-provided sink**. The library ships no agent transport — the host relays it.
- **FR-005**: `updateComponents` / `updateDataModel` on a live surface MUST **edit its message in place**
  (reusing slice-2/3 edit-in-place); `deleteSurface` MUST delete the message.
- **FR-006**: A Button whose action is a local `functionCall` (e.g. `openUrl`) MUST render as a
  client-side button with no server round-trip and MUST emit no `action` message.
- **FR-007**: The renderer MUST **coalesce** the incoming message stream per `surfaceId` and render a
  coherent snapshot on flush (Telegram messages are whole, not partial), preserving in-order processing.
- **FR-008**: The renderer MUST advertise only the `telegram-basic` catalog; a `createSurface` with an
  unknown `catalogId`, or a component outside telegram-basic, MUST be **surfaced** (observable error /
  "unsupported component"), never silently dropped or rendered incorrectly.
- **FR-009**: `DynamicString` text/labels bound via **absolute JSON-Pointer** MUST resolve against the
  surface's data model; an unresolved path MUST have a clear, documented outcome (empty/placeholder),
  not a crash.
- **FR-010**: The renderer MUST reuse the Tool Router's tap routing, edit-in-place, and binding store; a
  Button's callback rides the same tap→action path (a durable binding survives restart as in slice 3).
- **FR-011**: Every capability above MUST behave identically across the **F# and C# façades** and across
  **long polling and webhooks**.
- **FR-012**: A2UI MUST live entirely in the new `TgLLM.A2UI` leaf project; **Core and the slice-1/2/3
  public API stay A2UI-agnostic and unchanged**, and their tests remain green.
- **FR-013**: A2UI messages MUST be parsed and validated (`version` "v1.0", required fields); a malformed
  message MUST be surfaced as an error, never crash the bot.

### Key Entities

- **Agent**: emits A2UI messages; a consumer of the library, not part of it. The library ships no A2UI
  agent transport — the host connects the agent to the renderer and to the action sink.
- **A2UI message**: an agent→renderer envelope (`createSurface` / `updateComponents` / `updateDataModel`
  / `deleteSurface`) carrying `version: "v1.0"`.
- **Surface**: an A2UI canvas; here, one Telegram message identified by `(chat, message_id)`.
- **Component**: a node `{ id, component, …props, children }` from the catalog; this slice renders the
  `telegram-basic` subset (Text, Button, Row, Column, Divider, Image).
- **Button action**: `action.event` (server-bound → emits an A2UI `action`) or a local `functionCall`
  (client-side, e.g. `openUrl`).
- **A2UI `action` message**: the outbound user-interaction message (name, surfaceId, sourceComponentId,
  timestamp, resolved context, wantResponse, actionId) handed to the host sink.
- **Data model**: the surface's JSON state; `DynamicString` values bind to it by absolute JSON-Pointer.
- **Catalog (`telegram-basic`)**: the set of components this renderer advertises and can render.
- **Renderer / Adapter**: the `TgLLM.A2UI` component that maps A2UI to the Tool Router + edit-in-place.
- **Action sink**: a host-provided callback the renderer delivers outbound `action` messages to.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A `createSurface`+`updateComponents` (Text + Buttons in Rows/Columns) renders exactly one
  Telegram message whose text and inline-keyboard layout match the component tree — verified structurally.
- **SC-002**: 100% of Button taps produce an A2UI `action` message with the correct name, surface id,
  source component id, and resolved context delivered to the host sink.
- **SC-003**: An `updateComponents`/`updateDataModel` on a live surface edits the **same** message in
  place (no new message), and `deleteSurface` removes it.
- **SC-004**: A local `functionCall` `openUrl` button opens its link client-side with no `action`
  emitted and no server handler invoked.
- **SC-005**: A component outside `telegram-basic`, an unknown `catalogId`, or a malformed message each
  produces a surfaced error and no corrupted render; subsequent messages still process.
- **SC-006**: A Text bound by JSON-Pointer shows the resolved data-model value.
- **SC-007**: The full loop (render → tap → `action` → agent update → re-render in place) passes under
  both long polling and webhooks and in both the F# and C# façades.
- **SC-008**: The complete slice-1/2/3 test suite still passes unchanged, and Core carries no A2UI
  dependency.

## Assumptions

- **Supported subset**: only the `telegram-basic` catalog renders (see Clarifications); everything else
  is surfaced as unsupported. This keeps the renderer honest about what a Telegram message can show.
- **One surface = one message**: `(chat, message_id)`; multi-message surfaces are out of scope.
- **Action relay is the host's job**: the renderer emits A2UI `action` messages to a host sink; the
  library ships no agent-side A2UI transport (consistent with shipping no LLM-vendor transports).
- **Streaming = coalesce-then-render**: whole-message send/edit, not partial streaming (see
  Clarifications).
- **Data binding depth**: absolute JSON-Pointer for `DynamicString` only; Collection-Scope/List
  templates and two-way input binding are deferred.
- **Relationship to slices 1–3**: extends the existing library, dual façades, and dual transports;
  reuses the Tool Router routing, edit-in-place, and binding store; slice-1/2/3 APIs stay supported
  (FR-012). A2UI lives in a new leaf project; Core stays A2UI-agnostic.
- **A2UI is a moving target**: v1.0 is a candidate spec; the renderer targets it but stays a thin,
  isolated adapter so the core is never coupled to a spec that may still change.
- **Out of scope for this slice (backlog, tracked so it is not lost)**: a WebApp fallback that renders a
  whole rich surface inside a Mini App; input components and two-way binding; a concrete streaming
  throttle policy (edit frequency); full nested JSON-Pointer / Collection-Scope / `List` template
  resolution; and the standing infrastructure backlog — enabling `net8.0` in the CI matrix, publishing
  to NuGet, tightening public-API XML documentation (CS1591), and a smoke test against the live Telegram
  Bot API.
