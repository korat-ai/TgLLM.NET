# Research & Decisions: Tool Router Extensions

Phase 0 decisions for slice 003. Each is **Decision / Rationale / Alternatives rejected**. Bot API
facts are verified against `core.telegram.org` (Principle V). Several decisions fold in confirmed
findings from an adversarial review of slices 1–2.

## D1 — Press authorization: `OwnerScope = Anyone | User of UserId`, opt-in, default open

**Decision**: A keyboard carries an owner scope. `Anyone` (the default when unset) preserves slice-1/2
behavior. `User uid` restricts tool presses to that user. The scope is stored on each tool binding and
compared to the callback query's `from` user at resolve time; a mismatch (or an unidentifiable presser)
is acked with a notice and runs no tool. Enforced **only on tool (callback) buttons**.

**Rationale**: Fixes the real gap (any group member can tap another user's button today) without a
breaking change — defaulting to restricted would break every existing keyboard and slice-1/2 tests.
Storing the scope on the binding (not just in memory) makes it survive restart (FR-003) for free, since
the binding is already durable.

**Alternatives rejected**: (a) Default owner = the message's originating user — breaks backward compat
and is often wrong (broadcast keyboards). (b) Enforce in the transport — wrong layer; authorization is
a routing decision that belongs in Core next to tool resolution.

## D2 — Tool manifest: neutral `{ name, description, parameters(JSON Schema) }`, no vendor envelope

**Decision**: `IToolRegistry` can emit a `ToolManifest` — a list of entries, each `name` + optional
`description` + optional `parameters` (an opaque JSON-Schema value). Emission produces neutral JSON
(`[{ "name", "description", "parameters" }]`); the library adds no OpenAI/Anthropic wrapper. Tool
registration gains optional `description` / `argSchema` metadata; tools without it still register,
route, and appear (name only).

**Rationale**: `{ name, description, parameters }` with a JSON-Schema `parameters` is the common
denominator both mainstream function-calling APIs accept, so the host adapts the neutral shape with a
trivial rename if needed. Mirrors slice 2's neutral, LLM-agnostic keyboard plan (the library never
speaks a specific vendor's dialect). The schema is **advisory** — see D3.

**Alternatives rejected**: (a) Emit vendor-specific tool JSON — couples the library to a moving vendor
format, violates the neutral-plan principle. (b) Derive the schema from the tool's .NET signature via
reflection — tools are `PressContext -> Task`, they have no typed arg signature to reflect; the host
knows the arg shape, so the host supplies the schema.

## D3 — Structured argument: opaque payload; Core stays `string option`; façades (de)serialize

**Decision**: The button argument generalizes to a structured payload, but **Core keeps
`ToolBinding.Arg : string option`**, reinterpreted as an opaque, possibly-JSON payload. The façades add
typed entry points — F# `Plan.toolWith<'T>` / C# `PlanRowBuilder.Tool<T>(...)` serialize `'T` to that
string (System.Text.Json), and `PressContext.GetArg<'T>()` / `GetArg<T>()` deserialize it. The library
does **not** validate the payload against the manifest schema.

**Rationale**: Keeps Core System.Text.Json-free and IO-agnostic (Principle III) — serialization is a
façade concern. Backward compatibility is automatic: a slice-2 string argument *is* a valid payload
string, so existing bindings and tests are unaffected (FR-017). Not validating keeps the library a
neutral conduit (FR-007, "convenience not capability").

**Alternatives rejected**: (a) Put `JsonElement`/a structured type in Core — pulls STJ into the pure
kernel and breaks the IO-agnostic boundary. (b) Validate the payload against the schema in the library —
duplicates the host's/LLM's responsibility and forces a schema on every tool.

## D4 — WebApp & CopyText buttons (Bot API facts verified)

**Decision**: Extend `PlanButton`/`RegisteredButton` with `WebAppButton(label, url)` and
`CopyTextButton(label, text)`. Both are client-side: no tool, no callback query, no server handler.
Validation at plan build: WebApp `url` MUST be https; CopyText `text` MUST be 1–256 characters.

**Bot API facts** (core.telegram.org): `InlineKeyboardButton` requires **exactly one** optional field.
`copy_text` (`CopyTextButton`) copies text to the clipboard; **`text` is 1–256 characters**. `web_app`
(`WebAppInfo`) launches a Mini App; the app can act on the user's behalf via `answerWebAppQuery` **only
in private chats between a user and the bot**, and Web App URLs require https. `switch_inline_query` /
`switch_inline_query_current_chat` open inline mode (optional/stretch, FR unmarked as MAY).

**Consequence recorded**: WebApp inline buttons are effectively a **private-chat** feature, whereas
owner-scoping (D1) matters in **groups** — the two capabilities are largely orthogonal; a group
keyboard that mixes an owner-scoped tool button with a WebApp button is unusual but not rejected (the
WebApp button simply may not launch outside a private chat — a Telegram-client constraint, not ours).

**Rationale**: WebApp and CopyText are the two highest-value additions for interactive LLM UIs (launch a
form; copy a generated snippet). Modeling them as new `RegisteredButton` DU cases reuses the slice-2
url-XOR-callback mapping seam (each case maps to a distinct Telegram.Bot button factory).

**Alternatives rejected**: Reusing the `Url` case for WebApp — different Telegram semantics
(launch-in-app vs open-link) and different validation (https-only, private-chat), so a distinct case is
correct.

## D5 — Binding expiry with an injected clock

**Decision**: `ToolBinding` gains `ExpiresAt : DateTimeOffset option`. Resolution treats an expired
binding like an unknown tool (ack, no tool, surfaced, no crash) and marks it evictable. "Now" is
supplied by an injected `clock : unit -> DateTimeOffset` in the resolve/dispatch path — never
`DateTimeOffset.Now` read ambiently in Core.

**Rationale**: Injecting the clock keeps the expiry decision a pure, property-testable function
(feed a binding + a `now`, assert refuse/allow) and keeps Core deterministic. Reusing the existing
unknown-tool path (ack + `OnUnknownToken`) means expiry needs no new user-visible failure mode.

**Alternatives rejected**: Ambient `DateTimeOffset.Now` — non-deterministic, unproperty-testable, and a
hidden IO dependency in the pure kernel.

## D6 — At-most-once per `callback_query.id` (not per user re-tap)

**Decision**: The processor dedupes by the callback query's identity via a bounded, TTL'd seen-set;
a query id already processed is dropped (no second tool invocation). Rapid *distinct* user taps are
distinct ids and still run the tool each time. An opt-in **single-use binding** (`SingleUse : bool` on
the binding) additionally makes the first successful press consume the binding so later presses on it
resolve as unknown — the confirm-once mode (FR-014).

**Rationale**: Telegram (and our webhook transport under retries) can redeliver an update; dedup by
query id is the correct guard against double-invocation. Conflating it with "one tap per token" would
break legitimate repeatedly-tapped menu buttons. Single-use covers the genuine confirm-once product
need explicitly.

**Alternatives rejected**: Single-use tokens as the default — breaks persistent menus. Debounce by
`(user, token)` time-window — heuristic, drops legitimate fast taps, and still misses redelivery.

## D7 — Eviction: binding TTL sweep, idle per-chat channel reclaim, store-level delete

**Decision**: Three eviction seams: (a) expired bindings are removable from any store — SQLite via
`DELETE WHERE expires_at < now`, file/in-memory via a periodic sweep; (b) the `MessageBindingTracker`
prunes entries for messages past a bound; (c) the dispatcher reclaims a per-chat channel/worker after a
configurable idle period **without dropping or reordering in-flight presses** (closes the slice-1 idle
per-chat backlog debt, FR-012). Folds review finding #9 (nothing was evicted anywhere; `FileBindingStore`
rewrites the whole file per mutation — documented limitation, TTL/cap is the real fix).

**Rationale**: A long-lived bot otherwise grows unbounded (one hook/binding/tracker-entry per keyboard
forever). Idle-channel reclaim bounds dispatcher memory across many short-lived chats (SC-007).

**Alternatives rejected**: No eviction (status quo) — unbounded growth, already flagged by review.
Evicting on every mutation — churn; a periodic/threshold sweep is cheaper.

## D8 — Second durable store: embedded SQLite in a new leaf project

**Decision**: `SqliteBindingStore : IBindingStore` in a new `TgLLM.Persistence.Sqlite` leaf project
(dep: `Microsoft.Data.Sqlite`). One table keyed by token, columns for tool name, payload, owner,
expiry, single-use; expiry eviction is a `DELETE`. Interchangeable with the in-memory and file stores;
it MUST read slice-2 records (missing owner/expiry columns default to Anyone/none).

**Rationale**: Proves the store seam generalizes beyond one implementation (SC-010) using an **embedded**
engine — no external server, so the test oracle stays self-contained like the file store. SQLite's query
model makes expiry eviction a one-liner. Isolated in its own project so file-store-only consumers do not
inherit the SQLite native dependency (see plan Complexity Tracking).

**Alternatives rejected**: Redis/Postgres — needs an external server, complicates the test oracle;
networked stores stay backlog. Folding SQLite into `TgLLM.Persistence` — forces the dep on file users.

## D9 — Folded review findings (architectural, designed into this slice)

Confirmed findings from the slice-1/2 review that this slice's redesign absorbs (the availability /
data-corruption blockers are fixed separately as a hardening pass on this branch):

- **#3 — deferred-ack timing**: the tool-path watchdog currently starts when the *work runs*, so a tool
  queued behind a slow tool spins past the client budget, and the ack-first path awaits its ack HTTP
  round-trip inline in the single ingestion loop (head-of-line blocking across chats). US4 sends the ack
  at **enqueue time** (or starts the watchdog at enqueue) and moves acking **off the ingestion loop**.
- **#4 — `ToolKeyboardOps.deliver` failure ordering**: on the edit path, old tokens are removed *before*
  the edit reaches Telegram, so a failed edit strands a dead visible keyboard and orphans the new
  bindings. US2/US4 reorder to **remove-old only after a successful send**, and **compensate** (remove
  the just-saved bindings) when send throws.
- **#7 — `IBindingStore` leaks F# idioms on the C# surface**: `TryGet` returns
  `ValueTask<FSharpValueOption<_>>` and `ToolBinding.Arg` is `FSharpOption<string>`, so a C# host writing
  a custom store lands on F# types — uncaught because the idiom-leak canary does not walk store members.
  US2 adds a **C#-facing `IBindingStore` adapter** (nullable `ToolBinding?` / DTOs) and extends the canary
  to walk one member level into referenced non-BCL types.
- **#8 — dropped callback queries are never acked**: a callback whose `Data` is not a canonical token is
  dropped with no `AnswerCallback`, so the client spins ~30 s, violating the port's "ack EVERY press."
  US4 has transports emit an **ack-only event** carrying the query id for any callback they drop.
- **Property-test gaps**: add properties for duplicate input-token consumption, `validate` Ok ⇒ `plan`
  Ok, non-canonical token rejection, and URL passthrough (distributed across US test tasks).

## D10 — A2UI alignment (cheap, forward-looking; full adapter is a later slice)

**Decision**: Shape US2's manifest and the keyboard-plan vocabulary to be a natural *subset* of Google's
A2UI (a2ui.org) so a future `TgLLM.A2UI` renderer is a thin mapping, not a rewrite — WITHOUT adopting
A2UI's schema or coupling the core to it. Concretely: keep the "action id" concept explicit (our token =
A2UI action id), keep the manifest a self-describing "catalog" analogous to A2UI's pre-approved component
catalog, and keep UI structure separable from bound data (our per-button payload).

**Rationale**: Our Tool Router is independently a narrow A2UI (registry = catalog; neutral plan =
declarative component tree; token routing = actionResponse; edit-in-place = updateComponents /
deleteSurface). Aligning vocabulary now is free and makes interop cheap later. A2UI v1.0 is still a
candidate spec, so we stay A2UI-*aware*, not A2UI-*native*.

**Alternatives rejected**: Adopt A2UI's component schema as our plan type now — over-engineered for a
keyboard-centric surface (Telegram can't render most A2UI components without WebApp) and couples us to a
moving target. Ignore A2UI entirely — misses a cheap, high-signal interop opportunity. The full renderer
is tracked as candidate slice 004.

## Bot API facts (verified, Principle V)

- `InlineKeyboardButton`: exactly one optional field must be set.
- `callback_data`: 1–64 **bytes** (slice-2 constraint; args live library-side, D3).
- `copy_text` (`CopyTextButton`): `text` is **1–256 characters**; copies to clipboard, client-side.
- `web_app` (`WebAppInfo`): launches a Mini App; `answerWebAppQuery` is **private-chat only**; Web App
  URLs require **https**.
- `answerCallbackQuery`: **one-shot** (slice-2 fact; a second call → `QUERY_ID_INVALID`).
- `editMessageText` / `editMessageReplyMarkup` errors: `message is not modified` (treat as successful
  no-op), `message to edit not found` (surface as a soft failure) — folded into FR-015.
- `message_id`: **unique per chat**, not globally — the tracker MUST key by `(chat_id, message_id)`
  (blocker fixed in the hardening pass).
