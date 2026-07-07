# Research & Decisions: A2UI Renderer for Telegram

Phase 0 decisions for slice 004. Each is **Decision / Rationale / Alternatives rejected**. A2UI v1.0
facts are grounded against a2ui.org; Telegram facts against core.telegram.org (Principle V).

## D1 — The `telegram-basic` catalog and its Telegram mapping

**Decision**: The renderer advertises one catalog, `telegram-basic`, and maps it to a Telegram message +
inline keyboard:

| A2UI component | Telegram mapping |
|----------------|------------------|
| `Text` (DynamicString, Markdown) | contributes to the message **text** (MarkdownV2, see D7) |
| `Button` with `action.event` | inline **callback** button → routes to the `a2ui-action` tool (D2) |
| `Button` with local `functionCall` `openUrl` | inline **URL** button (client-side, no callback) |
| `Row` | its Button children go in **one keyboard row** |
| `Column` | its children **stack** (each Button child on its own row; Text children concatenate) |
| `Divider` | a text separator line in the body |
| `Image` (optional) | sent as a photo, or a link line in the body |

**Rationale**: This is exactly what a Telegram message can show. Row/Column drive keyboard layout;
Text/Divider/Image drive the body. Everything else is surfaced as unsupported (D6).

**Alternatives rejected**: Trying to emulate rich components (Slider/Tabs/Modal) with buttons — lossy and
confusing; the honest contract is "these aren't in `telegram-basic`" (D6). A WebApp fallback that renders
a whole rich surface is deferred (backlog).

## D2 — A2UI Button → Tool Router tool button (reuse the hardened engine)

**Decision**: Each `Button` with a server-bound `action.event` becomes a Tool Router **tool button**
whose tool is a single internal `a2ui-action` handler and whose **structured argument** (the slice-2
payload) is a serialized **action descriptor** (`surfaceId`, `sourceComponentId`, action `name`, the
`context` path bindings, `wantResponse`, `actionId`). On tap, the `a2ui-action` handler resolves the
context against the surface's data model, builds the A2UI `action` message, and delivers it to the host
sink. The keyboard is built via the existing `ToolKeyboard`/`ToolPlan.plan`.

**Rationale**: Reuses the *already-hardened* slice-2/3 routing, durable binding store, deferred-ack, and
edit-in-place — the A2UI renderer is a mapping layer, not a second dispatcher. The structured-argument
capability (slice 2) is exactly the vehicle for carrying the action descriptor; the durable binding means
a tap on a pre-restart surface still emits its action (FR-010).

**Alternatives rejected**: A separate A2UI dispatcher/binding path — duplicates routing/persistence and
re-introduces the exact bugs slices 1–3 already fixed.

## D3 — Surface identity: host-supplied chat, one message, create-once

**Decision**: A2UI carries no chat identity, so the renderer's **ingest takes `(chat, a2uiMessage)`** —
the host binds a surface to a chat. One **surface = one Telegram message** `(chat, message_id)`, recorded
on the first render. A second `createSurface` for a live surface id is **rejected** (create-once);
changes go through `updateComponents`/`updateDataModel`.

**Rationale**: A2UI is transport/chat-agnostic (the spec's action/message envelopes carry only
`surfaceId`), so the host — which owns the Telegram connection — must say which chat. Create-once matches
A2UI's lifecycle (`createSurface` then `update*`).

**Alternatives rejected**: Inferring the chat from the A2UI stream — impossible, the field doesn't exist.
Multi-message surfaces — out of scope; one message keeps edit-in-place unambiguous.

## D4 — Streaming: coalesce per surface, render on flush

**Decision**: The renderer keeps a per-`surfaceId` buffer of the incoming messages (createSurface +
updateComponents + updateDataModel) and renders a **coherent snapshot on flush** — one send (first
render) or one edit (subsequent). In-order processing is preserved. Flush is triggered per ingested
message batch (MVP: render after each ingested message that leaves the surface in a renderable state —
`root` present); a concrete debounce/throttle policy is backlog.

**Rationale**: Telegram sends whole messages, not partial renders; coalescing collapses a burst for one
surface into one send/edit and avoids message spam. A2UI guarantees in-order delivery, so the buffer
applies updates in order.

**Alternatives rejected**: One Telegram message per A2UI message — spams the chat and breaks the
"one surface = one message" model.

## D5 — Data binding: absolute JSON-Pointer for DynamicString, empty on miss

**Decision**: `DynamicString` values (`{ "path": "/..." }`) resolve by **absolute JSON-Pointer**
(RFC 6901) against the surface's data model to produce Text bodies and Button labels. An unresolved path
renders as the **empty string** (documented), never a crash. Nested Collection-Scope, relative paths, and
`List` template iteration are out of scope this slice.

**Rationale**: Absolute-path text/label binding is enough to render static and updatable surfaces
(US1–US3). Template iteration needs the List component (not in telegram-basic) and Collection Scope, both
deferred.

**Alternatives rejected**: Full binding engine now — scope creep against components we don't render.

## D6 — Unsupported components & unknown catalog: surface, never silently drop

**Decision**: The renderer advertises only `telegram-basic`. A `createSurface` with an unknown
`catalogId`, or an `updateComponents` containing a component outside `telegram-basic` (or a malformed
message), is **surfaced** via the observability seam (reuse `IHookObserver`, or a small A2UI-specific
observer) as an explicit error / "unsupported component" — the supported siblings still render where
sensible, but nothing is silently dropped or rendered wrong.

**Rationale**: Matches A2UI's own rule that a renderer only instantiates approved catalog components, and
the library's "surface, don't swallow" convention (slices 1–3). A silent drop would make an agent think
its UI rendered when it didn't.

**Alternatives rejected**: Rendering a placeholder for every unsupported component — noisy; a single
surfaced report is clearer. Crashing — violates the never-crash convention.

## D7 — Markdown mapping (Bot API facts)

**Decision**: `Text` (A2UI Markdown) maps to Telegram **MarkdownV2**, with the renderer performing the
required MarkdownV2 escaping so arbitrary agent text can't break parsing or inject formatting. A
documented subset (bold, italic, inline code, links) maps through; other Markdown constructs degrade to
escaped literal text.

**Bot API facts** (core.telegram.org): MarkdownV2 requires escaping the reserved characters
`_ * [ ] ( ) ~ \` > # + - = | { } . !` with a preceding `\`; unescaped reserved characters cause a
`400 can't parse entities` error. Legacy `Markdown` is deprecated; `HTML` is the alternative (escape
`< > &`). The renderer owns this escaping so an agent's raw Text is always safe on the wire.

**Rationale**: Agent-produced text is untrusted for parse-safety; escaping is mandatory to avoid
`can't parse entities` failures and formatting injection. MarkdownV2 is Telegram's current standard.

**Alternatives rejected**: Passing agent Markdown through unescaped — breaks on any reserved char. Plain
text with no formatting — loses A2UI Text's Markdown intent.

## D8 — The A2UI `action` message builder (injected clock)

**Decision**: On a tap, the `a2ui-action` handler builds the outbound A2UI `action` message: `name`,
`surfaceId`, `sourceComponentId`, `timestamp` (ISO-8601), the **resolved** `context` (each context path
resolved against the surface's data model to a value), `wantResponse`, `actionId`. `timestamp` uses the
**injected `Clock`** (slice-3), not ambient time, so the emission is deterministic in tests. A
`wantResponse` action with no `actionId` is surfaced as malformed.

**Rationale**: The action message is the outbound contract; resolving context at tap time (not
render time) captures current data-model values. Reusing the injected clock keeps it testable and
consistent with the rest of the library.

**Alternatives rejected**: Ambient `DateTimeOffset.Now` — non-deterministic, breaks property/integration
tests, and repeats a mistake slice 3 fixed.

## D9 — A2UI v1.0 facts (grounded)

- Agent→renderer envelopes: `createSurface` (`surfaceId`, `catalogId`, optional `components`/`dataModel`/
  `surfaceProperties`), `updateComponents` (`surfaceId`, `components`), `updateDataModel` (`surfaceId`,
  `path`, `value`), `deleteSurface` (`surfaceId`); also `actionResponse` / `callFunction`. Every message
  carries `version: "v1.0"`.
- Components: flat **adjacency list** — `{ id, component: "Text"|"Button"|"Row"|..., ...props,
  children: [ids] }`. A `root` component must exist for the tree to be visible.
- Action: a Button's `action.event` = `{ name, context, wantResponse, actionId }`; a tap sends the agent
  an `action` message `{ name, surfaceId, sourceComponentId, timestamp, context (resolved), wantResponse,
  actionId }`; the agent may reply `actionResponse` (actionId-matched) and/or further `update*` messages.
- A local action is `action.functionCall` (`call`, `args`) — client-side (e.g. `openUrl`), no server
  round-trip.
- Data binding: JSON-Pointer (`{ "path": "/..." }`) `DynamicString`/`DynamicNumber`/`DynamicBoolean`.
- Streaming: incremental, **in-order delivery guaranteed**; renderer buffers until `root` exists.
- Catalog negotiation: client advertises `supportedCatalogIds`; the renderer matches
  `createSurface.catalogId` and rejects/falls back if unsupported.
- **`actionResponse` → ack** (mapping decision): a `wantResponse` action's later `actionResponse.value`/
  `error` can surface as the tap's toast via the slice-2/3 **deferred ack** (run → one ack with the
  agent's response, watchdog preserves the spinner budget). MVP may ack immediately and treat
  `actionResponse` as a follow-up `update*`; the deferred-ack refinement is noted for the plan.
