# Feature Specification: Tool Router Extensions

**Feature Branch**: `003-tool-router-extensions`

**Created**: 2026-07-06

**Status**: Draft

**Input**: User description: "Третий срез TgLLM.NET. Четыре аддитивных расширения Tool Router:
US1 авторизация нажатия / владелец кнопки; US2 LLM tool-manifest + структурные аргументы; US3
кнопки WebApp + CopyText; US4 жизненный цикл и надёжность (TTL/эвикция, идемпотентность двойного
тапа, мягкая обработка edit-ошибок, ещё один durable-стор). Общий фундамент: US1/US2/US4 обогащают
серверную запись биндинга (owner / структурный payload / expiry) — эволюционируем её один раз.
Инфраструктурные долги (net8 в CI, NuGet, XML-доки, live-smoke) — вне объёма, зафиксировать."

## Overview

The second slice let an LLM agent drive an inline keyboard through data: the host registers named
tools once, the agent produces a neutral keyboard plan, and the library routes each tap to the bound
tool. This third slice hardens and rounds out that Tool Router with four additive capabilities that
share one theme — **the binding is a server-side record, so we can put more on it**:

1. **Press authorization** — today any user in a group can tap anyone's button and the tool runs.
   A keyboard can now be scoped to an **owner** (a specific user, or explicitly "anyone"); a
   non-owner tap is refused politely and runs no tool.
2. **Tool manifest + structured arguments** — the registry can emit a neutral manifest of its tools
   (name, description, argument schema) that a host feeds to its LLM's function-calling API, closing
   the *other* half of the boilerplate ("tell the model which tools exist"). And a button's argument
   generalizes from a single string to an arbitrary serializable payload — possible precisely because
   the binding lives library-side, free of Telegram's 64-byte `callback_data` limit.
3. **Richer button vocabulary** — WebApp (Mini App launch) and CopyText (copy-to-clipboard) buttons
   alongside tool and URL buttons, for interactive multi-step LLM UIs.
4. **Lifecycle & reliability** — binding expiry/eviction (and reclaiming idle per-chat dispatch
   resources), at-most-once processing of a redelivered tap, graceful handling of Telegram edit
   errors, and a second durable store so the persistence seam is proven to generalize.

This slice **extends** the existing library (slices `001` and `002`); it does not replace them. The
slice-1 per-button hook API and the slice-2 Tool Router API — and their tests — remain unchanged.
Every new capability is additive and opt-in.

## Clarifications

### Session 2026-07-06

- Q: What is the DEFAULT press-authorization scope for a keyboard? → A: **Open (anyone in the chat)**,
  matching current behavior. Ownership is **opt-in**: a host explicitly scopes a keyboard to a user.
  Defaulting to owner-restricted would be a breaking behavior change and would violate "do not break
  slice-1/2." An explicit "anyone" scope is also selectable for clarity.
- Q: Does the structured argument replace the slice-2 string argument? → A: It **generalizes** it. The
  binding's argument becomes an arbitrary serializable payload; a plain string remains a valid
  argument (backward compatible). Bindings written by slice 2 (string argument) MUST still resolve
  after this slice's binding-record evolution.
- Q: What format is the tool manifest? → A: A **neutral, vendor-agnostic** manifest — a list of tools,
  each with a name, a description, and an argument **schema expressed as JSON Schema** (the common
  denominator that mainstream function-calling / tool-use APIs accept). The library does NOT wrap it
  in any specific LLM vendor's envelope; the host adapts the neutral manifest to its vendor if needed
  (consistent with slice 2's neutral, LLM-agnostic keyboard plan).
- Q: Does the library validate a structured argument against its schema? → A: **No.** The library
  carries the argument opaquely (serialize → store → deserialize → hand to the tool). The schema is
  advisory metadata for the LLM; validating the produced argument is the host's/LLM's responsibility.
  The library adds convenience, not capability.
- Q: What does "double-tap idempotency" guarantee? → A: **At-most-once processing per callback-query
  identity** — a tap that Telegram redelivers (same query id) invokes the tool at most once. Rapid
  user re-taps are distinct taps and, by default, run the tool each time (a persistent menu button is
  legitimately tapped many times). For confirm-once buttons, an **optional single-use binding** mode
  makes the first successful tap consume the binding so later taps are treated as unknown.
- Q: What is the second durable store? → A: An **embedded SQLite** store. It keeps the test oracle
  free of any external server (like the file store), and its query model fits expiry/eviction
  naturally. A networked store (e.g. Redis) remains backlog.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Only the intended user can press a button (Priority: P1)

When a host sends a keyboard, it can scope the keyboard to an **owner** — a specific end user (e.g.
the user whose message triggered it), or explicitly "anyone in the chat." When an end user taps a
button on an owner-scoped keyboard, the library checks whether the presser is the owner. If not, the
library **acknowledges the tap with a short notice** (e.g. "This isn't for you") and **does not invoke
the tool**. The owner scope is stored with the binding, so it still holds after a restart.

**Why this priority**: This closes a real gap in the existing product — in a group chat today, any
member can tap another user's button and the bound tool runs. It is the smallest change, touches the
core routing decision, and is a security/correctness fix rather than a new toy. It is P1 because it
protects every interaction the router already supports.

**Independent Test**: Send an owner-scoped keyboard for user A; have user B tap it → the tool does not
run and B sees the notice; have user A tap it → the tool runs. Verifiable end-to-end without any of
US2–US4.

**Acceptance Scenarios**:

1. **Given** a keyboard scoped to user A, **When** user B taps a button, **Then** the tap is
   acknowledged with the configured notice and no tool runs.
2. **Given** the same keyboard, **When** user A taps the button, **Then** the bound tool runs
   normally.
3. **Given** a keyboard scoped to "anyone" (or with no scope set), **When** any user taps, **Then**
   the tool runs — behavior identical to slice 2.
4. **Given** an owner-scoped keyboard sent before a restart, **When** the bot restarts and a non-owner
   taps, **Then** the tap is still refused (the owner scope survived the restart).
5. **Given** an owner-scoped button, **When** a tap arrives with no identifiable user (anonymous),
   **Then** it is treated as a non-owner and refused.

---

### User Story 2 - Tell the LLM which tools exist, and pass rich arguments (Priority: P2)

A host registers tools with a **description** and an optional **argument schema**. The library can
then emit a neutral **tool manifest** the host feeds to its LLM's function-calling API — so the same
catalog that *routes* taps also *describes itself* to the model, removing the boilerplate of
hand-writing tool descriptions. Separately, a button's argument is no longer limited to a string: a
plan can bind an arbitrary **structured payload** to a button, which the library stores and hands to
the tool on tap.

**Why this priority**: This closes the mirror half of the router's value proposition (slice 2 removed
"LLM decision → function call"; this removes "describe functions → LLM"). Structured arguments unlock
non-trivial tool inputs. It is P2 because it builds on the registry and binding model from slice 2.

**Independent Test**: Register two tools with descriptions and argument schemas; emit the manifest and
confirm it lists both with their metadata; send a plan binding a structured payload to a button; tap
it and confirm the tool receives the exact payload. A slice-2 string argument still routes unchanged.

**Acceptance Scenarios**:

1. **Given** two registered tools with descriptions and argument schemas, **When** the host emits the
   manifest, **Then** it contains both tools with their names, descriptions, and schemas, in a neutral
   format (no vendor-specific wrapper).
2. **Given** a tool registered without any metadata, **When** the manifest is emitted, **Then** the
   tool still appears (name only / empty description) and nothing errors.
3. **Given** a plan binding a structured payload to a button, **When** the button is tapped, **Then**
   the tool receives that exact payload (round-tripped through the binding store).
4. **Given** a plan binding a plain string argument (as in slice 2), **When** tapped, **Then** the
   tool receives the string unchanged — backward compatible.

---

### User Story 3 - WebApp and CopyText buttons (Priority: P3)

A keyboard plan can include **WebApp** buttons (which launch a Mini App in the user's client) and
**CopyText** buttons (which copy a preset text to the user's clipboard), alongside the existing tool
and URL buttons. Both are handled entirely client-side by Telegram — they carry no tool and trigger no
server-side handler in this slice.

**Why this priority**: A common, additive way to build richer interactive LLM UIs (launch a form,
copy a generated snippet). Lowest of the capability stories because it does not touch routing or
persistence.

**Independent Test**: Send a keyboard mixing a tool button, a WebApp button, and a CopyText button;
confirm the WebApp button launches its app and the CopyText button copies its text with no server
round-trip, while the tool button still routes.

**Acceptance Scenarios**:

1. **Given** a keyboard with a WebApp button, **When** the user taps it, **Then** the client launches
   the Mini App and no tool is invoked.
2. **Given** a keyboard with a CopyText button, **When** the user taps it, **Then** the client copies
   the button's text and no tool is invoked.
3. **Given** the same keyboard's tool button, **When** tapped, **Then** its tool runs normally.
4. **Given** a WebApp button whose URL is not https, or a CopyText text over the allowed length,
   **When** the plan is built, **Then** it is rejected with a validation error.

---

### User Story 4 - Bindings expire, taps are de-duplicated, edits fail softly (Priority: P4)

Operational hardening for long-lived bots: a binding can carry an **expiry** after which a tap is
treated as unknown (and the record is evicted); **idle per-chat processing resources are reclaimed**
after an idle period; a tap that Telegram **redelivers** invokes the tool **at most once**; a tool's
attempt to **edit a message that vanished or did not change** is surfaced softly instead of throwing;
and the persistence seam is proven with a **second durable store** (embedded SQLite).

**Why this priority**: These prevent unbounded growth and rough edges in production but are not
user-facing capability, so they trail the three capability stories.

**Independent Test**: (a) Send a keyboard with a short expiry; after it lapses, a tap is refused like
an unknown tap. (b) Deliver the same tap twice (same query id); the tool runs once. (c) Have a tool
edit a since-deleted message; the tool author sees a soft failure, not an exception. (d) Run the
slice's restart-persistence test against the SQLite store and get the same result as the file store.

**Acceptance Scenarios**:

1. **Given** a binding with a lapsed expiry, **When** the user taps it, **Then** the tap is
   acknowledged, no tool runs, the situation is surfaced, and the bot keeps working.
2. **Given** a per-chat worker idle beyond the configured period, **When** eviction runs, **Then** the
   worker's resources are reclaimed without dropping or reordering any in-flight press.
3. **Given** a callback query delivered twice with the same identity, **When** both are processed,
   **Then** the bound tool is invoked at most once.
4. **Given** a single-use binding, **When** it is tapped a second time after a successful first tap,
   **Then** the second tap is treated as unknown (no second invocation).
5. **Given** a tool that edits a message that was deleted or is unchanged, **When** it runs, **Then**
   the edit failure is surfaced (observable) and no exception propagates to the tool author.
6. **Given** the SQLite durable store and a keyboard sent before a simulated restart, **When** the bot
   restarts and the user taps a pre-restart button, **Then** the bound, still-registered tool runs.

---

### Edge Cases

- **Non-owner tap** on an owner-scoped keyboard: acknowledged with the notice, no tool run, surfaced.
- **Anonymous / missing presser** on an owner-scoped keyboard: treated as non-owner, refused.
- **Owner scope on a durable binding**: survives restart (stored with the binding).
- **Tool with no description/schema** in the manifest: appears by name, no error.
- **Structured argument that fails to serialize / deserialize**: rejected at plan build (serialize) or
  surfaced at tap (deserialize) with a clear rule; never a silent wrong invocation.
- **Slice-2 string-argument binding loaded after evolution**: still resolves (backward-compatible
  record read).
- **WebApp non-https URL / CopyText over length limit**: rejected with a validation error.
- **Expired binding tap**: refused like an unknown-tool tap (US2/slice-2 semantics), no crash.
- **Redelivered callback query (same id)**: tool invoked at most once.
- **Single-use binding tapped twice**: second tap treated as unknown.
- **Edit-in-place on a vanished message ("message to edit not found") or unchanged content
  ("message is not modified")**: surfaced softly; "not modified" treated as a successful no-op.
- **Idle per-chat eviction racing an incoming press for that chat**: the press is processed in order;
  eviction never drops or reorders in-flight work.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: A keyboard MAY be scoped to an **owner** at send time — a specific end user, or
  explicitly "anyone." The default when unset is **anyone** (identical to slice 2; not a breaking
  change).
- **FR-002**: On a press of an owner-scoped button, the library MUST compare the presser to the owner
  and, if they differ (or the presser is not identifiable), MUST acknowledge the tap with a
  host-configurable notice and MUST NOT invoke the tool.
- **FR-003**: The owner scope MUST be stored with the binding so it is enforced identically after a
  restart (durable stores).
- **FR-004**: The tool registry MUST be able to emit a **neutral tool manifest** — for each registered
  tool, its name, an optional description, and an optional argument **schema (JSON Schema)** — with no
  LLM-vendor-specific wrapping.
- **FR-005**: Tool registration MUST accept optional description and argument-schema metadata; tools
  registered without metadata MUST still register, route, and appear in the manifest (name only).
- **FR-006**: A tool button's argument MUST generalize from a string to an arbitrary **serializable
  structured payload**, stored library-side and handed to the tool on press. A plain string MUST
  remain a valid argument (backward compatible with slice 2).
- **FR-007**: The library MUST carry the structured argument **opaquely** (serialize on bind,
  deserialize on press, pass to the tool) and MUST NOT validate the argument against its schema — that
  is the host's/LLM's responsibility.
- **FR-008**: The keyboard plan MUST support **WebApp** buttons (launch a Mini App via an https URL);
  they carry no tool and invoke no server-side handler in this slice (handling a Mini App's postback
  is out of scope).
- **FR-009**: The keyboard plan MUST support **CopyText** buttons (copy a preset text to the
  clipboard, within Telegram's length limit); they carry no tool and invoke no handler.
- **FR-010**: The plan builder MUST reject an invalid WebApp URL (non-https) or an over-length
  CopyText text with a validation error (consistent with slice-1/2 validation).
- **FR-011**: A binding MAY carry an **expiry**; a press on an expired binding MUST be treated like a
  press on an unknown tool — acknowledged, no tool run, surfaced, no crash — and the expired record
  MUST be eligible for eviction.
- **FR-012**: The dispatcher MUST reclaim **idle per-chat processing resources** after a configurable
  idle period, WITHOUT dropping or reordering any in-flight press for that chat (closes a slice-1
  backlog debt).
- **FR-013**: The library MUST process each redelivered callback query **at most once** (dedupe by the
  callback query's identity), so a tap Telegram re-delivers does not double-invoke the tool.
- **FR-014**: The library MUST support an optional **single-use binding** mode: after the first
  successful press, further presses on that binding are treated as unknown (for confirm-once buttons).
- **FR-015**: Edit-in-place MUST handle Telegram's "message is not modified" (treat as a successful
  no-op) and "message to edit not found" (surface as a soft, observable failure) WITHOUT propagating
  an exception to the tool author.
- **FR-016**: The library MUST ship a **second durable binding store** (embedded SQLite) implementing
  the same store seam as the file store; the abstraction, the in-memory default, the file store, and
  the SQLite store MUST be interchangeable.
- **FR-017**: The server-side **binding record MUST evolve once** to carry the owner scope, the
  structured payload, and the optional expiry; bindings written by slice 2 (string argument, no owner,
  no expiry) MUST still load and resolve after the evolution.
- **FR-018**: Every capability above MUST behave identically across the **F# and C# façades** and
  across **long polling and webhooks** (consistent with slices 1–2).
- **FR-019**: The slice-1 per-button hook API and the slice-2 Tool Router API — and all their existing
  tests — MUST remain unchanged; every capability in this slice is **additive and opt-in**.

### Key Entities

- **Owner scope**: the authorization attached to a keyboard/binding — a specific end user, or
  "anyone." Compared against the presser on each tap; stored with the binding.
- **Tool (extended)**: a named, host-registered handler, now optionally carrying a **description** and
  an **argument schema** used only to build the manifest — not to constrain routing.
- **Tool Manifest**: the neutral, vendor-agnostic self-description of the registry — a list of tools
  (name + description + JSON-Schema argument schema) a host feeds to its LLM.
- **Structured argument / payload**: a button's bound argument, generalized from a string to an
  arbitrary serializable value; carried opaquely by the library.
- **Button descriptor (extended)**: tool button (label + tool + structured argument), URL button,
  **WebApp button** (label + https app URL), **CopyText button** (label + text).
- **Button Binding (evolved once)**: the persistable record — identifier → (tool name, structured
  payload, **owner scope**, **optional expiry**, single-use flag). Backward-compatible with slice-2
  string-argument records.
- **Binding Store (extended)**: the store seam with the in-memory default, the file store (slice 2),
  and a new **embedded SQLite** store; supports expiry-based eviction.
- **Dispatcher (extended)**: per-chat ordered processing, now reclaiming idle per-chat resources.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: On an owner-scoped keyboard, 100% of non-owner taps are refused (tool not invoked, notice
  shown) and 100% of owner taps invoke the tool — demonstrated across taps by multiple users.
- **SC-002**: Owner-scope enforcement holds identically after a simulated restart with a durable store.
- **SC-003**: The emitted manifest lists every registered tool with its name, description, and
  argument schema, in a neutral format, and can be handed to a function-calling request without the
  library adding vendor-specific wrapping — verified structurally.
- **SC-004**: A structured (non-string) argument round-trips plan → store → tool byte-for-byte intact,
  and a slice-2 string argument still routes unchanged.
- **SC-005**: WebApp and CopyText buttons render and are handled client-side — a tap on either invokes
  no server-side tool — demonstrated in a single test per button type.
- **SC-006**: A tap on an expired binding is refused like an unknown-tool tap without crashing, and the
  expired record becomes eligible for eviction.
- **SC-007**: Idle per-chat processing resources are reclaimed after the idle period across ≥1000
  short-lived chats without dropping or reordering any in-flight press.
- **SC-008**: A callback query redelivered with the same identity invokes the bound tool at most once.
- **SC-009**: An edit-in-place against a deleted or unchanged message surfaces a soft failure and
  propagates no exception to the tool author.
- **SC-010**: The SQLite store passes the same restart-persistence acceptance as the file store — the
  persistence seam demonstrably generalizes beyond one implementation.
- **SC-011**: Every flow in this slice passes under both long polling and webhooks and in both the F#
  and C# façades, and the complete slice-1/2 test suite still passes unchanged.

## Assumptions

- **Default authorization is open**: a keyboard is tappable by anyone unless the host scopes it to an
  owner (see Clarifications). This preserves slice-1/2 behavior and tests.
- **Manifest is neutral**: name + description + JSON-Schema argument schema, no LLM-vendor envelope;
  the host adapts it to its vendor if required (see Clarifications, FR-004).
- **Structured argument is opaque and unvalidated** by the library (see Clarifications, FR-007); it is
  serializable, and the tool interprets it.
- **WebApp buttons are launch-only** this slice; handling a Mini App's returned data (`web_app_data` /
  answering a web-app query) is a later feature.
- **Double-tap guarantee is at-most-once per callback-query identity** (redelivery), with an optional
  single-use binding mode for confirm-once buttons (see Clarifications, FR-013/FR-014).
- **Second durable store is embedded SQLite** (no external server), chosen so the test oracle stays
  self-contained and expiry/eviction map to queries; a networked store (Redis) remains backlog.
- **Binding record evolves once** to carry owner + structured payload + expiry, read-compatible with
  slice-2 records (FR-017), so the store schema is not rewritten per story.
- **Relationship to slices 1–2**: this feature extends the existing library, dual façades, and dual
  transports; the slice-1 hook API and the slice-2 Tool Router API remain supported (FR-019).
- **Out of scope for this slice (infrastructure backlog, tracked so it is not lost)**: enabling the
  `net8.0` leg of the shipping matrix in CI, publishing packages to NuGet, tightening public-API XML
  documentation coverage (CS1591), and a smoke test against the live Telegram Bot API with a real
  token. These are engineering/infra tasks, not user-facing capability, and belong in a hardening
  track.
