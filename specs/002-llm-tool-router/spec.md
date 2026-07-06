# Feature Specification: LLM Tool Router

**Feature Branch**: `002-llm-tool-router`

**Created**: 2026-07-06

**Status**: Draft

**Input**: User description: "Второй срез TgLLM.NET. Главная фича — Tool Router для LLM-агентов:
хост регистрирует каталог именованных инструментов; в рантайме LLM решает, какие кнопки показать и
какой зарегистрированный инструмент повесить на каждую (data-driven, по имени инструмента); либа
строит клавиатуру и маршрутизирует нажатия к инструментам. Плюс capability-долги первого среза:
редактирование нажатого сообщения на месте + toast/alert; durable-персистентность связок; URL-кнопки.
Инфраструктурные долги (net8 в CI, NuGet, XML-доки, eviction каналов, live-smoke) — вне объёма,
зафиксировать как backlog."

## Overview

The first slice let an agent attach a compiled hook (a live function) to each button. That is ideal
when the bot-side logic is written ahead of time — but an **LLM agent decides at runtime** what a
button should do, and it produces *decisions/data*, not compiled code. This slice adds the missing
convenience layer: a **Tool Router**.

The host registers a catalog of **named tools** (the tools themselves are user code — the library
ships none). At runtime an LLM produces a **keyboard plan** — which buttons to show and which
registered tool (by name), with optional arguments, each button maps to. The library turns that plan
into an inline keyboard and routes each press to the right registered tool, removing the boilerplate
glue between "LLM decision → user-function invocation." Because a binding is now `token → tool name +
argument` (all serializable) rather than a live closure, bindings can also be **persisted** and
survive a restart.

This slice also closes capability debts from the first slice that this theme depends on: richer
in-place reactions (edit the pressed message, show a toast/alert) and URL buttons. It builds on and
extends the existing library (slice `001`); it does not replace it.

## Clarifications

### Session 2026-07-06

- Q: What argument does a button carry for its tool? → A: An optional **string** argument the tool
  interprets itself (e.g. an id, or JSON embedded in the string). It is stored library-side, so it is
  serializable (works with the durable store) and is NOT bound by Telegram's 64-byte `callback_data`
  limit.
- Q: How deep is durable binding persistence in this slice? → A: The library ships a binding-store
  abstraction, the in-memory default, and **one working file-based durable store** (JSON on disk, no
  external database). This lets the restart guarantee (SC-004) be verified end-to-end without heavy
  dependencies; a full external-database adapter is a later slice.
- Q: Who turns the LLM's decision into a keyboard plan? → A: The library is **format-agnostic** — it
  defines a NEUTRAL keyboard-plan type (rows of button descriptors); the host maps its own LLM's
  output (JSON / tool-call) into that type. The library ships no LLM-vendor-specific parsers.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Data-driven keyboard from a runtime tool decision (Priority: P1)

A host registers a catalog of named tools once (e.g. `approve_deploy`, `reject_deploy`,
`show_logs`). At runtime an LLM, reacting to a chat message or an external event, decides which
buttons to present and which registered tool (plus an optional argument) each button triggers. The
host hands that decision to the library, which sends the keyboard. When an end user taps a button,
the library invokes exactly the bound tool with the button's argument — the host writes **no
per-button routing code**.

**Why this priority**: This is the whole point of the slice — letting an LLM bind behavior to
buttons at runtime through data, not code. On its own it is a usable product: an LLM can drive an
interactive keyboard end-to-end.

**Independent Test**: Register two tools; feed a keyboard plan naming those tools (with arguments);
send it; tap each button; confirm the correct tool runs with the correct argument, and no per-button
glue was written.

**Acceptance Scenarios**:

1. **Given** tools `approve` and `reject` are registered, **When** the host sends a plan with buttons
   "Approve" → `approve` and "Reject" → `reject`, **Then** the end user sees both buttons.
2. **Given** that keyboard, **When** the user taps "Approve", **Then** the `approve` tool runs (and
   not `reject`), receiving the button's bound argument.
3. **Given** a plan that binds the same tool to two buttons with different arguments, **When** each is
   tapped, **Then** the tool runs once per tap with that button's own argument.
4. **Given** a plan naming a tool that is not registered, **When** the user taps it, **Then** the tap
   is acknowledged, no tool runs, the situation is surfaced, and the bot keeps working.

---

### User Story 2 - React by editing the pressed message in place (Priority: P2)

When a tool runs, it can update the **originating** message — change its text and/or replace its
inline keyboard — instead of only sending a new message. It can also show the end user a short
notification (a toast) or a blocking alert as part of acknowledging the tap. This makes multi-step
LLM flows (menus, wizards, confirmations) feel native instead of spamming new messages.

**Why this priority**: Interactive LLM UIs are the point of the router; editing in place is what
makes them usable. It is P2 because the router (US1) must exist first.

**Independent Test**: Send a keyboard; tap a button whose tool edits the message to new text and a
new keyboard; confirm the same message changed (no new message) and, if configured, the toast/alert
appeared.

**Acceptance Scenarios**:

1. **Given** a sent keyboard, **When** a tapped tool edits the message in place, **Then** the original
   message's text and/or keyboard change and no new message is created.
2. **Given** a tapped tool that requests a toast, **When** it acknowledges, **Then** the end user sees
   the notification text (or a blocking alert if requested).
3. **Given** a tool that replaces the keyboard with a next-step keyboard, **When** the user taps a new
   button, **Then** the new button's tool runs (a multi-step flow).

---

### User Story 3 - Bindings survive a bot restart (Priority: P3)

With a durable binding store configured, the button→tool bindings persist. After the bot restarts,
an end user tapping a keyboard that was sent *before* the restart still has their tap routed to the
(still-registered) tool — the interaction is not lost.

**Why this priority**: Long-lived LLM agents restart (deploys, crashes); losing every outstanding
keyboard on restart is a poor experience. It is P3 because it is only feasible once bindings are
data (US1) and is opt-in.

**Independent Test**: Send a keyboard with a durable store configured; simulate a restart (new
process, same store, same registered tools); tap a pre-restart button; confirm the bound tool runs.

**Acceptance Scenarios**:

1. **Given** a durable store and a keyboard sent before restart, **When** the bot restarts and the
   user taps a pre-restart button, **Then** the bound tool runs with its original argument.
2. **Given** a restart after which the bound tool is no longer registered, **When** the user taps,
   **Then** the tap is acknowledged and surfaced, with no crash (as in US1 scenario 4).

---

### User Story 4 - URL buttons alongside tool buttons (Priority: P4)

A keyboard can mix **URL buttons** (which open a link in the user's client, handled entirely by
Telegram) with tool buttons. URL buttons carry no tool and trigger no server-side handler.

**Why this priority**: A common, cheap addition that rounds out "build complex UI"; lowest priority
because it does not involve routing.

**Independent Test**: Send a keyboard with one URL button and one tool button; confirm the URL button
opens its link with no server round-trip, and the tool button still routes.

**Acceptance Scenarios**:

1. **Given** a keyboard with a URL button, **When** the user taps it, **Then** their client opens the
   link and no tool is invoked.
2. **Given** the same keyboard's tool button, **When** tapped, **Then** its tool runs normally.

---

### Edge Cases

- **Unregistered tool** (LLM named a tool the host never registered, or a durable binding outlived its
  tool): acknowledged, no tool run, surfaced, no crash.
- **Same tool, different arguments** across buttons: each tap invokes the tool with its own argument.
- **Empty plan / empty row / empty label**: rejected with a validation error (as in slice 1).
- **Invalid URL** on a URL button: rejected with a validation error.
- **Edit-in-place on a vanished message** (deleted, or too old to edit): surfaced, no crash.
- **Argument too large** to carry: rejected or truncated with a clear, documented rule (bindings are
  stored library-side, so the argument is not bound by Telegram's 64-byte `callback_data` limit).

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The library MUST let a host register named tools into a catalog (add, replace, and
  resolve by name). Tool implementations are user code; the library ships none.
- **FR-002**: The library MUST build and send an inline keyboard from a runtime **keyboard plan** (an
  ordered set of rows of button descriptors) without the host writing per-button routing code.
- **FR-003**: A tool (callback) button descriptor MUST reference a tool by name and MAY carry an
  optional **string** argument for that button. The argument is stored library-side, so it is not
  limited by Telegram's 64-byte `callback_data` size.
- **FR-004**: On a tool button press, the library MUST resolve the referenced tool by name and invoke
  it with the press context and that button's bound argument.
- **FR-005**: A press whose bound tool name is not currently registered MUST be acknowledged, run no
  tool, be surfaced (observable), and not crash the bot.
- **FR-006**: The press context MUST let a tool edit the originating (pressed) message in place —
  change its text and/or replace its inline keyboard — in addition to sending new messages.
- **FR-007**: The press context MUST let a tool optionally show the end user a short notification
  (toast) or a blocking alert when acknowledging the press.
- **FR-008**: Button→tool bindings MUST be persistable in a durable store so they survive a bot
  restart; after restart, a press on a pre-restart keyboard MUST resolve to the still-registered tool.
  Bindings are serializable (identifier → tool name + optional string argument). This slice ships a
  binding-store abstraction, the in-memory default, and **one file-based (JSON-on-disk) durable
  store**; a full external-database adapter is out of scope for this slice.
- **FR-013**: The library MUST define a neutral, **LLM-agnostic keyboard-plan type** that the host
  populates from its own LLM's output; the library MUST NOT ship parsers for specific LLM vendor
  tool-call formats.
- **FR-009**: The keyboard builder MUST support URL buttons (open a link) alongside tool buttons; URL
  buttons carry no tool and invoke no handler.
- **FR-010**: Every capability above MUST work identically across the F# and C# façades and across
  long polling and webhooks (consistent with slice 1).
- **FR-011**: The library MUST NOT ship business tools; it provides only tool registration, routing,
  the richer reaction surface, and the persistence seam. (The library adds convenience, not
  capability — consistent with the constitution.)
- **FR-012**: The lower-level per-button hook API from slice 1 MUST continue to work; the Tool Router
  is an additive convenience layered on top, not a replacement.

### Key Entities

- **Host**: the bot-side program that registers the tool catalog and forwards LLM keyboard plans.
- **LLM Agent**: at runtime, decides the keyboard plan (labels + tool names + arguments); a consumer
  of the library, not part of it.
- **End User**: taps buttons in a chat.
- **Tool**: a named, host-registered handler (name + user logic); invoked with the press context and
  the button's argument.
- **Tool Catalog**: the registry of tools by name.
- **Keyboard Plan**: the runtime decision — a neutral, LLM-agnostic data type of rows of button
  descriptors (tool button: label + tool name + optional string argument; URL button: label + link),
  populated by the host from its own LLM's output.
- **Button Binding**: the persistable association identifier → (tool name, optional string argument).
- **Press Context (extended)**: carries the press context and the bound string argument, and exposes
  reaction operations: reply, **edit the pressed message in place**, replace its keyboard, and show a
  toast/alert.
- **Binding Store**: holds button bindings; in-memory by default, with a file-based (JSON-on-disk)
  durable store for restart persistence.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A host can turn an LLM's keyboard decision (labels + tool names + arguments) into a
  working interactive keyboard whose taps invoke the right tools, writing **no per-button routing
  code** — the only per-button code is one-time tool registration. *(Verified structurally: the
  send-and-route path is driven entirely by the plan data.)*
- **SC-002**: Across ≥100 taps spanning multiple tools and arguments, 100% invoke exactly the tool
  bound to the tapped button with that button's argument, with zero cross-invocation.
- **SC-003**: A tapped tool can change the originating message's text and/or keyboard in place, with
  no new message created — demonstrated in a single test.
- **SC-004**: With the file-based durable store configured, a tap on a keyboard sent before a
  simulated restart still invokes the bound, still-registered tool.
- **SC-005**: A press referencing an unregistered tool is acknowledged and surfaced without crashing;
  subsequent presses still work.
- **SC-006**: A URL button opens its link with no server-side handler invoked.
- **SC-007**: The tool-router flow passes its acceptance tests unchanged under both long polling and
  webhooks, and in both the F# and C# façades.

## Assumptions

- **Per-button argument**: a button may carry an optional **string** argument the tool interprets
  (see Clarifications). Because bindings are stored library-side, the argument is not limited by
  Telegram's 64-byte `callback_data` cap.
- **Durable persistence depth**: this slice delivers the serializable binding model, a binding-store
  abstraction, the in-memory default, and **one file-based (JSON-on-disk) durable store** (see
  Clarifications). A full external-database integration is deferred to a later slice.
- **Keyboard-plan ownership**: the library defines a neutral, LLM-agnostic keyboard-plan type; the
  host maps its own LLM's output into it (see Clarifications, FR-013). The library ships no
  vendor-specific LLM parsers.
- **Tool signature**: a tool is user code invoked with the extended press context plus its bound
  argument; the library does not constrain what a tool does.
- **Relationship to slice 1**: this feature extends the existing library and its dual façades /
  dual transports; the slice-1 per-button hook API remains supported (FR-012).
- **Out of scope for this slice (infrastructure backlog, tracked so it is not lost)**: enabling the
  `net8.0` leg of the shipping matrix in CI, publishing packages to NuGet, tightening public-API XML
  documentation coverage, idle per-chat channel eviction in the dispatcher, and a smoke test against
  the live Telegram Bot API (slice 1 is verified only against a fake Bot API server). These are
  engineering/infra tasks, not user-facing capability, and belong in a hardening track.
