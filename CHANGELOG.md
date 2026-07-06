# Changelog

All notable changes to this project are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project intends to adhere to
[Semantic Versioning](https://semver.org/) once it ships its first NuGet release.

## [Unreleased]

### Added

- **Tool Router** (feature `002-llm-tool-router`, additive on top of the inline-keyboard-hooks
  slice; the slice-1 hook API and its tests are unchanged, FR-012):
  - Register named **tools** (`IToolRegistry`/`InMemoryToolRegistry`) and build a neutral, LLM-agnostic
    keyboard **plan** (`ToolKeyboard`/`PlanButton`, `ToolPlan.plan`) instead of wiring a hook per
    button — a tap resolves by name to the exact registered tool, with its bound string argument on
    `PressContext.Arg`. The library ships no vendor LLM parsers (FR-013) — the host maps its own
    agent's decision into `Plan.tool`/`Plan.toolWithArg`/`Plan.url` (F#) or
    `PlanRowBuilder.Tool`/`.Url` (C#).
  - **Edit-in-place + toast/alert**: `PressContext.EditTextAsync`/`EditKeyboardAsync` edit the
    tapped message's text/keyboard without sending a new message; `PressContext.Answer(text, alert)`
    sets the ack's toast/alert on the deferred-ack tool path (a watchdog protects the client's
    loading-spinner budget even if the tool is slow). New `IBotApiClient.EditMessageText`/
    `EditMessageReplyMarkup`/`AnswerCallback(text, alert)` ports, implemented over Telegram.Bot.
  - **Durable bindings**: a new leaf project, `TgLLM.Persistence`, ships `FileBindingStore` — a
    JSON-on-disk `IBindingStore` that loads existing bindings on open, so taps sent before a restart
    still route (`TgBotConfig.WithBindingStore` / `TelegramAgentOptions.BindingStore`).
  - **URL buttons**: `RegisteredButton` becomes a DU (`Callback | Url`) so a keyboard can mix tool
    buttons with plain client-side link buttons in the same layout; slice-1 callback-button behavior
    is unchanged.
  - Idiomatic façade surfaces in both `TgLLM.FSharp` (`ToolRegistry`, `Plan`, `TgBot.SendKeyboardPlan`)
    and `TgLLM.CSharp` (`ToolRegistry`, `PlanBuilder`/`PlanRowBuilder`, `KeyboardPlan`,
    `TelegramAgent.SendKeyboardPlanAsync`) — the C# idiom-leak canary covers the extended surface too.
  - Runnable examples (`examples/ToolRouterFSharp`, `ToolRouterCSharp`) demonstrating tool
    registration and a data-driven plan over both long polling and webhooks (`TRANSPORT` env var), and
    a Tool Router walkthrough in `docs/quickstart.md`.

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
