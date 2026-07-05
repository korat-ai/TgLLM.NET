# Phase 0 Research: Interactive Keyboards with Button Hooks (Agent PoC)

**Feature**: `001-inline-keyboard-hooks` | **Date**: 2026-07-04

All version/license facts were verified against NuGet and vendor documentation on 2026-07-04
(Telegram Bot API at core.telegram.org, .NET APIs at Microsoft Learn). This document resolves
every technical unknown before design (Phase 1).

## D1 — Telegram Bot API transport client

- **Decision**: Depend on **Telegram.Bot 22.10.1 (MIT)** as the raw Bot API client, consumed
  **only inside the transport layer** behind the `IBotApiClient` port and the `IUpdateSource`
  adapters. Its types never cross the public F#/C# façades.
- **Rationale**: Telegram.Bot absorbs the correctness burden of Bot API wire fidelity (hundreds of
  types, optional fields) — directly serving Principle V (vendor-grounded correctness) without us
  re-modeling the whole wire protocol for a PoC. Because the transport is an isolated, swappable
  layer (Principle III), the public surface still exposes only our own `Keyboard`/`Button`/
  `PressContext` vocabulary, preserving "the library adds nothing but convenience" (spec Overview).
- **Alternatives considered**:
  - *Funogram 3.0.4 / Funogram.Telegram 10.1.0 (MIT)* — F#-native request/response records with
    `Option`-typed fields; near-zero friction, but ~108★ and effectively one maintainer (bus-factor
    risk on edge-case Bot API coverage). Kept as a credible swap-in if F# wire idiom matters more.
  - *Hand-rolled minimal `HttpClient` client* — best long-term fit for the "convenience only"
    positioning and zero supply-chain surface, but modeling the `Update`→`Message`/`CallbackQuery`
    shape (dozens of optional fields) plus FsCheck round-trip tests pulls a large burden into the
    PoC. **Deferred** to a later slice once the wire contract is well understood from consuming it.
- **Consequence**: A dedicated `TgLLM.Protocol` wire-DTO project is **not needed for the PoC**;
  Telegram.Bot provides the DTOs and their (de)serialization. The transport layer maps Telegram.Bot
  types → our domain `AgentEvent` via pure, testable functions.

## D2 — JSON serialization

- **Decision**: **Deferred for the PoC.** Telegram.Bot owns wire (de)serialization (inbound webhook
  bodies are parsed with Telegram.Bot's serializer; outbound calls go through its client).
- **Rationale**: No independent JSON layer is required while Telegram.Bot is the transport. Adding
  one now would be speculative.
- **If/when we hand-roll the client** (D1 alternative): use **System.Text.Json +
  FSharp.SystemTextJson 1.4.36 (MIT)** — purpose-built for F# records/`Option`/DUs with STJ's
  built-in `JsonNamingPolicy.SnakeCaseLower` (.NET 8+) for the wire's snake_case fields.
- **Alternatives considered**: *Thoth.Json.Net 12.0.0* — the .NET-only package is stale since
  2024-05; the maintained sibling `Thoth.Json` carries a hard `Fable.Core` dependency unnecessary
  for a server library. Raw System.Text.Json — viable but hand-rolls every F# converter.

## D3 — Test framework + property-based testing

- **Decision**: **Expecto 11.1.0 + Expecto.FsCheck 11.1.0 (Apache-2.0)** for all F# test projects
  (core, transport, integration). **xUnit v3 3.2.2 + FsCheck.Xunit.v3 3.3.2** for the **C# façade**
  test project.
- **Rationale**: Expecto is F#-native (tests-as-values), and its first-party FsCheck integration is
  the most ergonomic home for the constitution's **mandatory** property tests (Principle I). The C#
  façade is tested in its own idiom (Principle II) with xUnit v3, the ecosystem-standard runner that
  best attracts C#-background contributors — while still using FsCheck where round-trip properties
  apply.
- **Alternatives considered**: *xUnit v3 + FsCheck.Xunit.v3* for the F# side too (strong tooling,
  but less idiomatic than Expecto for F#); *TUnit 1.58.0 + TUnit.FsCheck* — fastest-moving, but its
  FsCheck support is C#-first and incompatible with NativeAOT (FsCheck needs reflection), so it is
  experimental for an F#, FsCheck-mandatory project.
- **As built (first slice)**: pinned **Expecto 10.2.3 + Expecto.FsCheck 10.2.3-fscheck3** (FsCheck
  3.x) rather than 11.1.0 — the 11.x line has no released `dotnet test` VSTest-bridge adapter
  (`YoloDev.Expecto.TestSdk`) yet, and a working `dotnet test` is a hard requirement. Revisit when
  the 11.x adapter ships.

## D4 — Web host for the webhook endpoint

- **Decision**: Plain **ASP.NET Core minimal API** via the **`Microsoft.AspNetCore.App` framework
  reference** (no extra NuGet), isolated in the `TgLLM.AspNetCore` glue project. A single
  `MapPost` endpoint is mapped via an `IEndpointRouteBuilder` extension returning a sealed
  `IEndpointConventionBuilder` (Microsoft's documented guidance for library authors).
- **Rationale**: One POST endpoint needs no routing DSL; routing, JSON, DI, and logging all ship in
  the shared framework. Keeping the framework reference in a single leaf project keeps ASP.NET out
  of the core and other adapters (Principle III).
- **Alternatives considered**: *Oxpecker 2.0.1 (MIT)* — the current F# successor to *Giraffe 8.2.0*;
  *Falco 5.2.0* — lighter. All are overkill for one endpoint; **Oxpecker** is the pick **only if**
  the webhook host later grows into a multi-route F# app.

## D5 — Target frameworks (TFM)

- **Decision**: Multi-target **`net8.0;net10.0`** for all shipping packages.
- **Rationale**: .NET 8 (LTS) still has broad adoption but reaches end-of-support **2026-11-10**;
  .NET 10 (LTS, supported through Nov 2028) has been GA since Nov 2025. Multi-targeting maximizes
  reach today while making net10.0 the forward LTS, so the v1 TFM matrix does not need a breaking
  change within months of release. Telegram.Bot (netstandard2.0) and the F# stack all support both.
- **Alternatives considered**: single-target `net8.0` (short remaining support window);
  single-target `net10.0` (drops users still on 8 during 2026).
- **As built (first slice)**: single-target **`net10.0`** — only that SDK/runtime is installed, and
  multi-targeting `net8.0` would make `dotnet test` fail to *run* net8 test assemblies with no net8
  runtime present. The `net8.0;net10.0` shipping matrix is restored in CI (where both runtimes
  exist); library projects compile against net8 ref-packs fine, only test execution needs the runtime.

## D6 — Concurrency: sequential-per-chat, concurrent-across-chats (FR-015)

- **Decision**: One **`System.Threading.Channels` unbounded channel per chat** (`SingleReader =
  true`), keyed in a `ConcurrentDictionary<ChatId, worker>`; each chat has a single consumer loop
  (guarantees in-arrival-order, sequential processing within the chat), while distinct chats run
  concurrently. The polling loop and the webhook ingress both write into the same per-chat channels.
- **Rationale**: `SingleReader = true` is precisely what yields the per-chat ordering guarantee
  (FR-015, SC-007); it is not automatic from using a channel. Channels are the in-box, async-first,
  `ValueTask`-based producer/consumer primitive documented by Microsoft for this exact shape.
- **Gotchas captured for design**: an untouched chat's channel + consumer task lives forever unless
  evicted — **idle-chat eviction** is a noted non-functional follow-up (not required for PoC
  correctness). Unbounded channels risk memory growth under a single-chat burst; a bounded channel
  with `FullMode = Wait` is the backpressure option if needed later.
- **Alternatives considered**: F# `MailboxProcessor` per chat (heavier, less idiomatic for
  Task-based interop); a global sequential queue (violates cross-chat concurrency).

## D7 — Update ingestion & shutdown

- **Decision**: A `BackgroundService`/`IHostedService` hosts the long-polling loop and the per-chat
  dispatch supervisor. `getUpdates` uses `offset = max(update_id)+1` confirm-by-offset bookkeeping;
  the HTTP client timeout is set **above** the long-poll `timeout` (e.g. poll 30s → client ~40s).
  Transports are mutually exclusive: the library calls `deleteWebhook` before starting long polling.
  On shutdown, the `CancellationToken` stops new `getUpdates` calls, completes per-chat channels, and
  awaits in-flight hooks within the host shutdown timeout (default 30s).
- **Rationale**: Grounded in the Bot API `getUpdates`/`setWebhook` docs and Microsoft Learn hosting
  docs. At-least-once delivery means a crash between processing and offset-persist can re-deliver an
  update — which is exactly why stale/unknown presses must be acked with no hook and no error
  (FR-010).
- **Verify at contract-authoring time (Principle V)**: `callback_data` 1–64 bytes;
  `answerCallbackQuery` semantics and `text` 0–200 chars; message text limit (4096); webhook secret
  header `X-Telegram-Bot-Api-Secret-Token`; `getUpdates` `offset`/`allowed_updates` semantics;
  inline keyboard row/button count limits; `message_id` integer width.

## D8 — Callback token strategy (FR-011)

- **Decision**: The library assigns each button an **opaque short token** (16 random bytes →
  22-char base64url, well under the 1–64 byte `callback_data` limit) written into `callback_data`,
  and keeps the real button→hook association in the **Hook Store** (`IHookStore`, FR-016), resolved
  by token when the callback query arrives. Token encode/parse is **pure** and property-tested.
- **Rationale**: `callback_data` cannot hold arbitrary state (64-byte cap), so a token+store
  indirection is mandatory; keeping it pure makes routing the FsCheck heart of the design (Principle
  I). Agents never see or manage raw payloads (FR-011).
- **Alternatives considered**: encoding label/chat directly in `callback_data` (breaks past 64
  bytes, leaks state); signing payloads (unnecessary for in-memory PoC).

## D9 — Solution layering (feeds Phase 1)

- **Decision**: Ports/adapters with a single dependency direction; F# core is IO-agnostic; two
  separate idiomatic façade packages (F# and C#). Full graph in `plan.md` → Project Structure and
  in `data-model.md`/`contracts/`.
- **Rationale**: Directly encodes Principles II, III, IV. The imperative `PressContext` (FR-014) is
  a single bilingual sealed class (Task methods, nullable-not-Option) reused by both façades, with a
  reflection-based "no FSharpFunc/FSharpOption on the public surface" canary test guarding Principle
  II.

## Open items intentionally deferred to design/tasks

- Durable `IHookStore`: a live `Hook` function cannot be serialized; a durable store needs stable
  hook keys re-attached at startup (add optional `HookKey` when that slice arrives). Only the
  in-memory default ships now; the seam is not oversold (documented in `data-model.md`).
- Idle per-chat channel eviction (D6) — non-functional follow-up.
- `FSharp.Control.TaskSeq` vs hand-rolled `IAsyncEnumerator` loop in Core — **decision: hand-rolled**
  to keep Core at `FSharp.Core + FSharp.UMX` only (dependency-light OSS core); revisit if ergonomics
  demand it.
