<!-- SPECKIT START -->
## Active feature: 004-a2ui-renderer (extends 001+002+003)

**Current plan** (read for full context — structure, tech, constraints, the telegram-basic mapping):
`specs/004-a2ui-renderer/plan.md`
Supporting artifacts: `spec.md` (+ Clarifications), `research.md` (decisions D1–D9 + A2UI v1.0 / Bot API
facts + the mapping table), `data-model.md`, `contracts/a2ui-renderer.md`, `quickstart.md`.
Done slices: `specs/001-inline-keyboard-hooks/`, `specs/002-llm-tool-router/`, `specs/003-tool-router-extensions/`.

**What 004 adds (additive on 001/002/003, FR-012 — earlier APIs/tests untouched)**: positions the library
as an **A2UI renderer for Telegram**. A NEW leaf `TgLLM.A2UI` renders the Telegram-representable subset
(`telegram-basic` catalog: Text/Button/Row/Column/Divider/Image) of Google's open A2UI protocol
(a2ui.org, Apache-2.0, v1.0) onto the existing Tool Router + edit-in-place, **bidirectionally**:
`createSurface`/`updateComponents` → send a message; a Button tap → an A2UI `action` message to a
host-provided sink; agent `update*` → edit-in-place; `deleteSurface` → delete. **Core stays A2UI-agnostic**
(A2UI only in the leaf; v1.0 is a candidate spec — thin isolated adapter).

**Load-bearing design (research.md)**: each A2UI Button → a Tool Router **tool button** whose structured
arg (slice-2 payload) is the serialized **action descriptor** (surfaceId/componentId/name/context) — a tap
rides the hardened slice-2/3 routing + deferred-ack + edit-in-place + durable binding store, NO new engine.
Host supplies the target chat at ingest (A2UI is chat-agnostic); surface = one message `(chat, message_id)`,
create-once; stream coalesced per surface; Text → MarkdownV2 (renderer escapes); action `timestamp` from the
injected `Clock`; unsupported component / unknown catalog / malformed → **surfaced**, never silently dropped.

**Tech snapshot**: F# core (unchanged, A2UI-agnostic) + new F# leaf `TgLLM.A2UI` + dual C#/F# façades; both
transports; Telegram.Bot 22.10.1 confined to transport; System.Text.Json + FSharp.SystemTextJson in the leaf
(NOT Core); `Task`/`ValueTask` only. Single-target `net10.0` (net8 in CI/backlog).

**Testing**: TDD mandatory; Expecto + FsCheck (F#), xUnit v3 + FsCheck.Xunit.v3 (C#). Property tests for the
PURE mapping (component tree + data model → message text + keyboard plan; Row/Column layout; unsupported
surfaced) and A2UI parse round-trip. `dotnet build`/`dotnet test` are the final oracle (fslangmcp `check`
can diverge — FsLangMCP#100). Comprehensive Principle VII sweep at Polish (comments cite code/A2UI/Bot API
only, never FR/SC/US/research-D#/spec files — the recurring slip).

**Governance**: `.specify/memory/constitution.md` (v1.0.0) is binding. Use `fslangmcp` for semantic F#
queries. Discussion in Russian; all code/docs/comments in English.
<!-- SPECKIT END -->
