# Changelog

All notable changes to this project are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project intends to adhere to
[Semantic Versioning](https://semver.org/) once it ships its first NuGet release.

## [Unreleased]

### Added

- Solution scaffolding: all `src/`, `tests/`, and `examples/` project skeletons with the
  dependency-direction wiring described in `specs/001-inline-keyboard-hooks/plan.md`.
- Repo hygiene: `Directory.Build.props`, `Directory.Packages.props` (central package management),
  `.editorconfig`, `LICENSE` (MIT), CI workflow.
- Transport-agnostic F# core (`TgLLM.Core`): domain model, value objects (`ButtonLabel`,
  `MessageText`), the `CallbackToken` codec, `Keyboard`/`KeyboardPlan` pure kernel, `Routing`
  decision logic, ports (`IUpdateSource`, `IHookStore`, `IPressDispatcher`, `IBotApiClient`,
  `IHookObserver`), the default `InMemoryHookStore` and `PerChatChannelDispatcher`
  implementations, and the ack-first `UpdateProcessor` engine — covered by Expecto + FsCheck
  property tests.
- **Long polling** transport (`LongPollingUpdateSource`: confirm-by-offset, deletes any webhook
  first) and **webhook** transport (`WebhookUpdateSource` + a `MapTelegramWebhook` ASP.NET Core
  minimal-API endpoint with secret-token verification) — both feed the identical core, so hook code
  is unchanged across transports.
- `TelegramBotApiClient`: `IBotApiClient` over Telegram.Bot, with the shared Telegram.Bot ↔ domain
  mapping used by both transports.
- **Idiomatic public façades**: `TgLLM.FSharp` (`Button.on` / `Keyboard.create` / `TgBot`,
  `Result`-based) and `TgLLM.CSharp` (`KeyboardBuilder` / `TelegramAgent`, exception-based) — a
  reflection canary proves no FSharp.Core type leaks into the C# surface.
- `IHookObserver` → `ILogger` bridge (`LoggingHookObserver`) so hook failures and unknown/stale
  presses are surfaced (opt in via `WithLogger` / `TelegramAgentOptions.Logger`).
- Runnable examples for both transports × both languages (`examples/PollingFSharp`,
  `PollingCSharp`, `WebhookFSharp`, `WebhookCSharp`) and a user [`docs/quickstart.md`](docs/quickstart.md).

### Notes

- Ships single-target `net10.0` for now; the `net8.0;net10.0` shipping matrix is enabled in CI
  (only the .NET 10 SDK/runtime is available locally). See `Directory.Build.props`.
