<!-- SPECKIT START -->
## Active feature: 001-inline-keyboard-hooks

**Current plan** (read for full context — project structure, tech, constraints):
`specs/001-inline-keyboard-hooks/plan.md`
Supporting artifacts: `spec.md`, `research.md` (decisions D1–D9), `data-model.md`,
`contracts/` (core-ports, fsharp-facade, csharp-facade), `quickstart.md`.

**Tech snapshot**: F# core + separate idiomatic C#/F# façade packages; layered, IO-agnostic core;
BOTH long polling and webhooks behind an `IUpdateSource` port; Telegram.Bot 22.10.1 confined to the
transport layer; `Task`/`ValueTask` only (no `Async`); per-chat `System.Threading.Channels` ordering;
in-memory hook store behind a storage seam. Multi-target `net8.0;net10.0`.

**Testing**: TDD is mandatory; Expecto + FsCheck (F#), xUnit v3 + FsCheck.Xunit.v3 (C# façade).
Property tests are REQUIRED for the pure kernel (token codec, keyboard planning, routing, ordering).

**Governance**: `.specify/memory/constitution.md` (v1.0.0) is binding. Use `fslangmcp` for semantic
F# queries once source exists. Discussion in Russian; all code/docs/comments in English.
<!-- SPECKIT END -->
