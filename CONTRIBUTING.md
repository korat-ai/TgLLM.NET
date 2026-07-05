# Contributing to TgLLM.NET

Thanks for your interest in contributing! This project follows a strict test-first workflow —
please read this guide before opening a pull request.

## Prerequisites

- .NET SDK 10.0 (see `global.json` if present, or the version pinned in CI).
- Familiarity with F# (the core and one façade) and/or C# (the other façade).

## Development workflow

1. **Test-first, always.** Every behavior change starts with a failing test (Red), then the
   minimal implementation to pass it (Green), then refactoring. Property-based tests (FsCheck)
   are mandatory for the pure kernel (token codec, keyboard planning, routing, dispatcher
   ordering) — see the project constitution at `.specify/memory/constitution.md`, Principle I.
2. **Respect the layering.** `TgLLM.Core` is transport- and IO-agnostic: no HTTP client, no
   hosting, no JSON library dependency. Telegram.Bot and ASP.NET Core stay in the leaf adapter
   projects (`TgLLM.BotApi`, `TgLLM.Webhooks`, `TgLLM.AspNetCore`).
3. **Keep both façades idiomatic.** `TgLLM.FSharp` and `TgLLM.CSharp` are separate NuGet
   packages. Neither should leak the other language's idioms (no `FSharpOption`/`FSharpFunc` on
   the C# surface; no forced C#-style ergonomics on the F# surface).
4. **Use `Task`/`ValueTask`**, not `Async<'T>`, except where a dependency leaves no alternative
   (and isolate/document that boundary).
5. **Ground behavior in vendor docs.** Telegram Bot API behavior (limits, semantics, optional
   fields) must be verified against <https://core.telegram.org/bots/api>, not assumed.

## Building and testing

```bash
dotnet build TgLLM.NET.sln
dotnet test TgLLM.NET.sln
```

## Documentation

User-facing documentation (`README.md`, `docs/quickstart.md`, code comments) must be updated in
the same change that alters observable behavior. All documentation and code comments are in
English.

## Code style

- `.editorconfig` at the repo root defines formatting; run your editor's F#/C# formatter before
  committing.
- Follow the patterns already established in `TgLLM.Core` for new domain code (smart
  constructors returning `Result`, exhaustive pattern matching, immutable records/DUs).

## Submitting changes

- Keep pull requests focused; one user story or one bugfix per PR where possible.
- Include or update tests for any behavior change.
- Ensure `dotnet build` and `dotnet test` are green before requesting review.
