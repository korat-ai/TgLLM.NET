<!--
SYNC IMPACT REPORT
==================
Version change: (unversioned template) → 1.0.0
Bump rationale: Initial ratification. The file was a bare template with placeholder
tokens and no prior version number; this is the first concrete adoption, so it starts
at 1.0.0 rather than being a MINOR/PATCH increment of an existing version.

Principles defined (all new):
  I.    Test-First Development (NON-NEGOTIABLE)
  II.   F# Core with Dual-Language Public APIs
  III.  Layered Architecture
  IV.   Transport Agnosticism — Long Polling AND Webhooks
  V.    Vendor-Grounded Correctness
  VI.   Task-Based Concurrency
  VII.  English, Always-Current Documentation
  VIII. Open-Source Excellence

Sections defined (all new):
  - Technology & Tooling Standards
  - Development Workflow & Quality Gates
  - Governance

Removed sections: none (template placeholder sections were replaced, not dropped).

Templates & artifacts reviewed for consistency:
  ✅ .specify/templates/plan-template.md   — "Constitution Check" gate is generic and
       resolves principles from this file at plan time; no change required.
  ✅ .specify/templates/tasks-template.md  — UPDATED: "Tests are OPTIONAL" language
       replaced with a mandatory TDD + property-based testing gate (Principle I).
  ✅ .specify/templates/spec-template.md   — reviewed; constitution adds no new mandatory
       spec sections; no change required.
  ✅ CLAUDE.md                             — points to "the current plan" for runtime
       guidance; consistent with the Governance section below.

Deferred / TODO placeholders: none.
-->

# TgLLM.NET Constitution

TgLLM.NET is an open-source Telegram Bot library for .NET. Its core is written in F#, and
it ships idiomatic public APIs for both C# and F# consumers. This constitution defines the
non-negotiable engineering principles that govern how the library is designed, built,
tested, documented, and released.

## Core Principles

### I. Test-First Development (NON-NEGOTIABLE)

Development follows Test-Driven Development without exception.

- Every behavior MUST be expressed as a failing test before its implementation exists:
  Red (write a failing test) → Green (make it pass with the simplest code) → Refactor.
- No implementation code is merged unless a test that specifies it was written first.
- Property-based tests (FsCheck) are MANDATORY for core logic — Bot API type
  (de)serialization, encoding/round-trip invariants, update parsing, and any function
  with a definable algebraic property. Example-based tests complement property tests;
  they never replace them.
- A task is not "done" until its tests — including the required property tests — are
  green in CI.

Rationale: A protocol library has an enormous correctness surface (hundreds of Bot API
types, optional fields, edge cases). Property tests catch entire classes of defects that
hand-picked examples miss, and test-first guarantees the public contract is designed for
use, not retrofitted.

### II. F# Core with Dual-Language Public APIs

The library is architected as a single F# core with two separate, idiomatic public
surfaces.

- The core (domain, protocol, update handling) MUST be implemented in F#.
- The public API MUST ship as distinct packages: one idiomatic **F#** surface and one
  idiomatic **C#** surface. Each is a separate NuGet package.
- Neither language's idioms may leak into the other's surface. The C# API MUST NOT expose
  raw `FSharpFunc`, `FSharpOption`, or F#-only types; the F# API MUST NOT force C#-style
  ergonomics onto F# consumers.
- The core MUST NOT depend on either façade. Dependencies flow façade → core only.

Rationale: F# gives the core a correct, expressive domain model; dedicated façades give
each language's users a first-class experience instead of a leaky lowest-common-denominator
API that feels foreign in one language or the other.

### III. Layered Architecture

The codebase MUST be organized into clearly separated layers with a single, enforced
dependency direction.

- Layers are explicit (e.g. Domain/core → Application/update handling → Transport → API
  façades), and dependencies flow in one direction only. No layer references a layer
  "above" it.
- The domain/core layer MUST be transport- and IO-agnostic: no dependency on HTTP clients,
  hosting, or webhook plumbing.
- Cross-layer leakage (an inner layer depending on an outer one, or transport concerns in
  the domain) is a blocking review failure.

Rationale: Strict layering keeps the protocol model pure and testable, makes transports
swappable (see Principle IV), and prevents the architecture from eroding into a tangle as
the Bot API surface grows.

### IV. Transport Agnosticism — Long Polling AND Webhooks

The library MUST support both Telegram update-delivery mechanisms as first-class,
interchangeable transports.

- Long polling and webhooks MUST both be fully supported.
- Update-handling and business logic MUST be identical regardless of transport; the
  transport is a pluggable boundary that produces the same update stream to the core.
- Choosing or switching transport MUST NOT require changes to a consumer's handler code.

Rationale: Telegram supports both mechanisms for legitimate deployment reasons (local
development and simple bots favor long polling; production webhooks favor scale and
latency). A library that hard-wires one is unfit for a large share of real deployments.

### V. Vendor-Grounded Correctness

Behavior is validated against authoritative documentation, never against memory or
assumption.

- The official **Telegram Bot API** documentation is the source of truth for types, field
  optionality, method semantics, rate limits, and error behavior. Implementations MUST
  match it.
- When using platform or dependency APIs, the official **vendor documentation**
  (Microsoft/.NET and others) MUST be consulted and followed.
- Any intentional deviation from documented behavior MUST be explicitly justified in code
  and in the user documentation.

Rationale: A client library's value is fidelity to the protocol it wraps. Guessing field
shapes or method semantics produces subtle, hard-to-debug failures for every downstream
consumer.

### VI. Task-Based Concurrency

Asynchronous code uses .NET Tasks by default.

- Asynchronous operations MUST use `Task` / `ValueTask` so that both F# and C# consumers
  get natural, interoperable async ergonomics.
- F#'s `Async<'T>` MUST be used ONLY where a required dependency exposes an `Async`-only
  API that cannot reasonably be adapted to `Task`. Such usage MUST be isolated at the
  boundary and documented.

Rationale: `Task` is the lingua franca of async on .NET and the interop-friendly choice
for a dual-language library; defaulting to it avoids forcing awkward conversions on C#
consumers while still allowing `Async` where a dependency leaves no alternative.

### VII. English, Always-Current Documentation

Documentation is a first-class, continuously maintained deliverable.

- All documentation and all code comments MUST be written in English.
- User-facing documentation MUST be updated in the same change that alters observable
  behavior. Documentation never lags the code it describes.
- Documentation MUST reference only source code and official vendor / Bot API
  documentation. It MUST NOT reference conversation context or Spec Kit specifications —
  specs are working artifacts that are NOT shipped in the repository; only documentation is.
- Code comments are used with judgment: they explain intent and non-obvious "why", not
  restate the "what". They are neither omitted where clarity requires them nor overused.

Rationale: For an open-source library, the docs are the product's first impression and its
primary support channel. English maximizes reach; same-change updates keep docs
trustworthy; grounding docs only in code and vendor sources keeps them durable and
reproducible for anyone reading the repository in isolation.

### VIII. Open-Source Excellence

The repository is a public open-source project and MUST present professionally on GitHub.

- The repository MUST maintain, at minimum: a README with quickstart, an OSI-approved
  LICENSE, CI status, API documentation, a changelog, a contribution guide, and runnable
  examples covering both long polling and webhooks and both the C# and F# APIs.
- Every merged change MUST leave the public face of the project release-ready — no broken
  builds, no failing CI, no stale docs on the default branch.

Rationale: The project's stated goal is to be an excellent open-source library. Discovery,
adoption, and contribution all depend on the repository looking and behaving like a
maintained, trustworthy project at every commit.

## Technology & Tooling Standards

- **Platform**: F# on .NET for the core; packages distributed via NuGet. Public APIs
  packaged separately for C# and F# consumers (Principle II).
- **Testing stack**: A .NET test runner with FsCheck for property-based tests
  (Principle I). Property tests are required for core protocol logic.
- **F# tooling discipline**: Semantic operations on F# source (`.fs` / `.fsi` / `.fsx`) —
  navigation, symbol/reference queries, type-at-position, diagnostics, refactoring,
  code actions — MUST use the `fslangmcp` MCP server rather than plain text search, which
  misses partial application, shadowing, and aliased opens. Plain text search (`rg`) remains
  appropriate for non-F# files (`.fsproj`, `Directory.Packages.props`, markdown, YAML,
  JSON) and idiom counts.
- **Tooling feedback loop**: When `fslangmcp` fails, is missing a capability, or behaves
  incorrectly, feedback MUST be filed to its tracking issue (**#100**) in the FsLangMCP
  repository within the same working session it surfaced — not batched for later.
- **Vendor verification**: Before relying on a platform or dependency API, verify it
  against official vendor documentation (Principle V).

## Development Workflow & Quality Gates

- **Subagent- and workflow-driven development**: Work is executed using subagents and
  orchestrated workflows — fan-out for independent tasks, and adversarial verification for
  correctness-critical logic (protocol serialization, transport behavior, public contracts).
- **Working language vs. deliverable language**: The team communicates and reasons in
  Russian. All shipped artifacts — code, code comments, documentation, commit messages
  intended for the public repository — are in English (Principle VII).
- **TDD gate**: No task advances past "in progress" until its tests are written first,
  fail, then pass, including the mandatory property-based tests for core logic
  (Principle I).
- **Review gate**: Code review MUST verify compliance with every principle, specifically:
  test-first evidence and property-test coverage (I); dual-API idiom cleanliness (II);
  layering with correct dependency direction (III); both transports supported and handler
  code unchanged across them (IV); vendor-grounded behavior (V); `Task`-based async with
  justified `Async` exceptions (VI); English docs updated in the same change (VII); and a
  release-ready public face (VIII).
- **Definition of done**: green CI (build + all tests), updated user documentation, and no
  outstanding principle violations.

## Governance

This constitution supersedes all other development practices. When a practice or a
generated artifact conflicts with this document, this document wins.

- **Amendments**: Any change to principles or governance MUST be made via an update to this
  file that (a) documents the change in the Sync Impact Report, (b) bumps the version per
  the policy below, and (c) propagates the change to dependent templates
  (`plan-template.md`, `spec-template.md`, `tasks-template.md`) so gates stay consistent.
- **Versioning policy** (semantic versioning of this constitution):
  - **MAJOR**: backward-incompatible governance changes — a principle removed or redefined
    in a way that invalidates existing compliance.
  - **MINOR**: a new principle or section added, or materially expanded guidance.
  - **PATCH**: clarifications, wording, and non-semantic refinements.
- **Compliance review**: Every pull request and review MUST verify compliance with the
  principles above. Complexity or any deviation MUST be justified in writing; unjustified
  deviations are rejected.
- **Runtime development guidance**: `CLAUDE.md` and the current feature plan provide the
  operational, day-to-day guidance that implements these principles.

**Version**: 1.0.0 | **Ratified**: 2026-07-04 | **Last Amended**: 2026-07-04
