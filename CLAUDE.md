<!-- SPECKIT START -->
## Active feature: 003-tool-router-extensions (extends 001+002)

**Current plan** (read for full context — project structure, tech, constraints, folded review findings):
`specs/003-tool-router-extensions/plan.md`
Supporting artifacts: `spec.md` (+ Clarifications), `research.md` (decisions D1–D10 + Bot API facts),
`data-model.md`, `contracts/tool-router-extensions.md`, `quickstart.md`.
Slice 1 (done): `specs/001-inline-keyboard-hooks/`. Slice 2 (done): `specs/002-llm-tool-router/`.

**What 003 adds (additive on 001/002, FR-019 — slice-1/2 API/tests untouched)**: four opt-in extensions
riding the server-side binding record. US1 **press authorization** (`OwnerScope = Anyone | User`; owner
stored on the tool binding; non-owner tap on a tool button → notice + no tool; tool buttons only).
US2 **neutral tool manifest** (`{name,description,parameters(JSON Schema)}`, no vendor wrapper) +
**structured args** (opaque payload: Core keeps `Arg: string option`, façades serialize `'T` / `GetArg<'T>()`
— STJ stays out of Core). US3 **WebApp + CopyText** buttons (client-side; WebApp https/private-oriented,
CopyText ≤256). US4 **lifecycle**: binding expiry (injected `Clock`), idle per-chat eviction, at-most-once
per `callback_query.id`, single-use bindings, soft edit-error handling, embedded **SQLite** store (new
`TgLLM.Persistence.Sqlite` leaf). Foundation: evolve `ToolBinding` once (+owner +expiry +single-use),
read-compatible with slice-2 records.

**Folded review findings (from a Fable review of 001/002)**: blockers fixed in a hardening pass on this
branch (run-loop supervision + polling retry; tracker keyed by `(ChatId, MessageId)`; decorative
`ackPolicy`; C# façade doc/`ct`; dead-keyboard guard). Architectural findings designed INTO 003: ack-at-
enqueue / off-loop ack (US4, #3), `deliver` remove-after-send + compensate (US2/US4, #4), C#-facing
`IBindingStore` adapter (US2, #7), ack dropped non-canonical callbacks (US4, #8), property-test gaps.

**Tech snapshot**: F# core (IO-agnostic, STJ-free) + dual C#/F# façades; both transports behind
`IUpdateSource`; Telegram.Bot 22.10.1 confined to transport; `Task`/`ValueTask` only; per-chat Channels
ordering. Single-target `net10.0` (net8 in CI/backlog). Candidate slice **004**: an A2UI renderer for
Telegram (backlog — a2ui.org alignment folded into US2 vocabulary only).

**Testing**: TDD mandatory; Expecto + FsCheck (F#), xUnit v3 + FsCheck.Xunit.v3 (C#). Property tests for
the pure kernel (owner match, manifest, payload round-trip, expiry with injected clock, dedup, + slice-2
gaps). `dotnet build`/`dotnet test` are the final oracle (fslangmcp `check` can diverge — see FsLangMCP#100).

**Governance**: `.specify/memory/constitution.md` (v1.0.0) is binding. Use `fslangmcp` for semantic F#
queries. Discussion in Russian; all code/docs/comments in English.
<!-- SPECKIT END -->
