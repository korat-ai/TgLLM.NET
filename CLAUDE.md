<!-- SPECKIT START -->
## Active feature: 002-llm-tool-router (extends 001)

**Current plan** (read for full context — project structure, tech, constraints):
`specs/002-llm-tool-router/plan.md`
Supporting artifacts: `spec.md` (+ Clarifications), `research.md` (decisions D1–D8 + Bot API facts),
`data-model.md`, `contracts/tool-router.md`, `quickstart.md`.
Slice 1 (done, green — 62 tests): `specs/001-inline-keyboard-hooks/`.

**What 002 adds (additive on slice 1, FR-012 — slice-1 API/tests untouched)**: a Tool Router — host
registers named tools; an LLM produces a neutral `ToolKeyboard` plan (label + tool name + optional
**string** arg); the library routes taps to tools. Serializable bindings (`token → toolName+arg`) →
durable **file (JSON) binding store** in a NEW `TgLLM.Persistence` project (Core stays IO-agnostic).
Richer `PressContext`: edit the pressed message in place + toast/alert. URL buttons.

**Two load-bearing design decisions (research.md)**: (1) `answerCallbackQuery` is ONE-SHOT → tool
toasts need **deferred ack** (run tool → one ack with its directive, watchdog ~2s preserves SC-003);
slice-1 closures stay **ack-first** (keeps T028 green). (2) `UpdateProcessor` gains an OPTIONAL
`?toolDispatch` collaborator — present → tool path (deferred ack); absent/miss → slice-1 IHookStore
path unchanged.

**Tech snapshot**: F# core (IO-agnostic) + dual C#/F# façades; both transports behind `IUpdateSource`;
Telegram.Bot 22.10.1 confined to transport; `Task`/`ValueTask` only; per-chat Channels ordering.
Single-target `net10.0` (net8 in CI/backlog).

**Testing**: TDD mandatory; Expecto + FsCheck (F#), xUnit v3 + FsCheck.Xunit.v3 (C#). Property tests
for the pure kernel (`ToolPlan.plan`, tool resolution). `dotnet build`/`dotnet test` are the final
oracle (fslangmcp `check` can diverge from build — see FsLangMCP#100).

**Governance**: `.specify/memory/constitution.md` (v1.0.0) is binding. Use `fslangmcp` for semantic F#
queries. Discussion in Russian; all code/docs/comments in English.
<!-- SPECKIT END -->
