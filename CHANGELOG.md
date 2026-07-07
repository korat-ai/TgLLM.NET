# Changelog

All notable changes to this project are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project intends to adhere to
[Semantic Versioning](https://semver.org/) once it ships its first NuGet release.

## [Unreleased]

### Added

- **Tool Router extensions** (additive on top of the Tool Router; every existing `SendKeyboardPlan`/
  `SendKeyboardPlanAsync` call keeps compiling and behaving exactly as before — every new parameter
  is optional):
  - **Press authorization**: `TgBot.SendKeyboardPlan`/`TelegramAgent.SendKeyboardPlanAsync` take an
    optional owner scope (`Owner.user`/`Owner.User(id)`, backed by `TgLLM.Core.OwnerScope`) that
    restricts every tool button on that send to one Telegram user; anyone else who taps it is acked
    with a toast (the built-in notice, or an `?deniedNotice`/`deniedNotice` override) and no tool
    ever runs. The scope (and notice override) live on the binding itself, so authorization survives
    a restart against any of the durable stores below, not just the in-memory default.
  - **A neutral tool manifest for LLM function-calling**: `ToolRegistry.Register` gains optional
    `description`/`argSchema` metadata (advisory only — it never affects routing); the registry's
    `ManifestJson()` (both façades; `ToolRegistry.Manifest()` on the F# side) renders every
    registered tool as a plain `[{ name, description, parameters }]` array with no vendor-specific
    wrapping, ready to hand to an LLM's tool-calling request.
  - **Structured tool-button arguments**: `Plan.toolWith`/`PlanRowBuilder.Tool<T>` bind a typed
    payload (serialized with `System.Text.Json`) into a button instead of a raw string, read back
    with `PressContext.GetArg<'T>()`/`TryGetArg<'T>()`; a plain string argument still routes through
    `.Arg` exactly as before.
  - **WebApp and CopyText buttons**: `Plan.webApp`/`PlanRowBuilder.WebApp` (launches a Telegram Mini
    App) and `Plan.copyText`/`PlanRowBuilder.CopyText` (copies text to the presser's clipboard) —
    both handled entirely client-side, invoking no tool and producing no callback event, and freely
    mixable with tool and URL buttons in the same keyboard.
  - **Binding lifecycle**: `SendKeyboardPlan`'s `?expiresIn`/`?singleUse` (`expiresIn`/`singleUse` in
    C#) stamp every binding a send produces with an expiry and/or a confirm-once (single-use) flag;
    an expired or already-consumed binding resolves as a silent no-invocation ack, never a crash or
    an error surfaced to the presser. A redelivered callback query id is deduplicated the same way
    (at most one tool invocation per query, even under transport-level retry).
    `TgBotConfig.WithIdleChatEviction`/`TelegramAgentOptions.IdleChatEviction` reclaims a chat's
    per-chat dispatcher resources once it has sat idle with nothing buffered, so a long-running
    bot's bookkeeping doesn't grow unbounded. A soft edit failure (the edited message was deleted, or
    already shows the requested content) is classified and reported via `IHookObserver`, never
    thrown to the tool author.
  - **A second durable store**: `TgLLM.Persistence.LiteDb.LiteDbBindingStore` — an embedded LiteDB
    `IBindingStore`, interchangeable with the existing in-memory and JSON-file stores via
    `.WithBindingStore`/`BindingStore`, proving the store seam generalizes beyond one backend.

- **Tool Router** (additive on top of the inline-keyboard-hooks slice; the slice-1 hook API and its
  tests are unchanged):
  - Register named **tools** (`IToolRegistry`/`InMemoryToolRegistry`) and build a neutral, LLM-agnostic
    keyboard **plan** (`ToolKeyboard`/`PlanButton`, `ToolPlan.plan`) instead of wiring a hook per
    button — a tap resolves by name to the exact registered tool, with its bound string argument on
    `PressContext.Arg`. The library ships no vendor LLM parsers — the host maps its own agent's
    decision into `Plan.tool`/`Plan.toolWithArg`/`Plan.url` (F#) or `PlanRowBuilder.Tool`/`.Url` (C#).
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

- Solution scaffolding: all `src/`, `tests/`, and `examples/` project skeletons with a layered
  dependency direction (domain core → transports → façades).
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

### Fixed

- The update-ingestion run loop is now supervised end to end: a fault is reported through
  `IHookObserver.OnRunLoopFailed` instead of silently leaving the bot stopped with no signal, and
  long polling retries a transient `getUpdates` failure with a bounded exponential backoff instead
  of tearing down update ingestion for good.
- The message-to-bindings tracker behind edit-in-place is now keyed by chat *and* message id
  (instead of message id alone), so two different chats can no longer collide on the same message id.

### Notes

- Ships single-target `net10.0` for now; the `net8.0;net10.0` shipping matrix is enabled in CI
  (only the .NET 10 SDK/runtime is available locally). See `Directory.Build.props`.
- Not yet published to NuGet; packaging, public-API XML-doc coverage, and a smoke test against the
  live Telegram Bot API remain backlog items.
