# Phase 0 Research: LLM Tool Router

**Feature**: `002-llm-tool-router` | **Date**: 2026-07-06

This slice extends the already-built slice-1 library, so the technology stack is fixed. Research
here is (a) Telegram Bot API facts for the NEW operations (verified against core.telegram.org and
Telegram's open-sourced reference server), and (b) the design decisions for layering the Tool Router
on top of slice 1 without breaking it (FR-012). The architecture was designed in the main loop after
the architecture subagent hit a model credit limit; all Bot API facts are from a dedicated,
doc-grounded research pass.

## D1 — Edit message in place

- **Decision**: Add `EditMessageText(chat, messageId, text, replyMarkup?, ct)` and
  `EditMessageReplyMarkup(chat, messageId, replyMarkup?, ct)` to the `IBotApiClient` port; implement
  over Telegram.Bot's `EditMessageText` / `EditMessageReplyMarkup`. `editMessageText` can also replace
  the keyboard in the same call.
- **Rationale**: Telegram recommends editing in place over send+delete for interactive keyboards. A
  bot may edit messages it sent.
- **Error handling (FR edge case)**: editing a vanished message → `ApiRequestException` with
  `"message to edit not found"`; editing to identical content → `"message is not modified: ..."`.
  Both MUST be caught and surfaced via `IHookObserver` (no crash). There is **no published numeric
  edit-time-limit** for ordinary bot messages — treat "uneditable" purely as a caught error, not a
  client-side age check.
- **Alternatives considered**: send+delete (worse UX, two round-trips, flicker).

## D2 — Acknowledgement + toast/alert (the central decision)

- **Fact (verified)**: `answerCallbackQuery` can be called **exactly once** per callback query. A
  second call — or a call after the (unpublished) client window — fails 400
  `"query is too old and response timeout expired or query ID is invalid"`. Therefore an empty
  "ack-first" followed by a later toast is **impossible**. `text` is 0–200 chars; `show_alert=true`
  is a blocking modal vs. a transient toast. The numeric answer deadline is NOT published by Telegram
  (community "~10s" is unverified folklore).
- **Decision — differentiated ack policy**:
  - **Slice-1 closure presses** (the `IHookStore` path): keep **ack-first** exactly as slice 1 (ack
    is sent before the hook runs). This preserves slice-1 behavior and its ordering test (T028) and
    honors FR-012. Closures do not set toasts.
  - **Tool-Router presses**: **deferred ack**. The processor does NOT pre-ack. It runs the tool; the
    tool MAY set an ack directive via `ctx.Answer(text, alert)`. After the tool returns, the processor
    sends exactly ONE `answerCallbackQuery` with that directive (or empty). A **watchdog** sends a
    default empty ack if the tool exceeds a short budget (~2s, safely under the unverified window),
    keeping SC-003; the losing ack call fails with `QUERY_ID_INVALID` and is swallowed.
- **Rationale**: One-shot ack + tool-chosen toast forces the ack to follow the tool for tool presses;
  the watchdog protects the spinner budget; keeping closures ack-first protects slice-1 tests.
- **Alternatives considered**: uniform ack-after-hook (breaks slice-1 T028 ordering assertion —
  rejected); toast via a separate message (not a real toast — rejected).

## D3 — URL buttons

- **Decision**: The neutral plan supports URL buttons (`label + url`); `RegisteredButton` becomes a
  DU (`Callback of label*token` | `Url of label*url`); the Telegram.Bot mapping adds
  `InlineKeyboardButton.WithUrl`. URL buttons carry no token, no binding, no tool.
- **Rationale (verified)**: On one button `url` and `callback_data` are mutually exclusive; tapping a
  URL button is handled entirely client-side and sends **no** callback query to the bot (so there is
  nothing to route or acknowledge — by protocol, not a library limit). Telegram permits `http://`,
  `https://`, and `tg://` URLs (not https-only).
- **Alternatives considered**: none meaningful; this is the standard Bot API mechanism.

## D4 — Per-button argument = string, stored library-side

- **Decision**: A tool button carries an optional **string** argument. It lives in the binding store
  keyed by the button's token; only the short token goes into `callback_data`.
- **Rationale (verified)**: `callback_data` is **1–64 bytes** — it cannot hold arbitrary arguments.
  Storing the arg library-side removes that limit and makes bindings serializable for D5.

## D5 — Binding store: port + in-memory default + file store

- **Decision**: New port `IBindingStore` (token → `{ toolName; arg }`) with an in-memory default in
  Core and **one file-based (JSON-on-disk) durable implementation in a NEW leaf project
  `TgLLM.Persistence`** (Core stays IO-agnostic — Principle III). The file store loads existing
  bindings on construction, so a restart pointing at the same file restores them.
- **Rationale**: Bindings are now serializable (D4), so persistence is feasible. A file store gives a
  real end-to-end restart test (SC-004) with zero external dependencies (System.Text.Json only). A
  full external-DB adapter is deferred (Clarifications).
- **Alternatives considered**: in-memory-only + user-provided durable (SC-004 only testable on a stub
  — weaker); external DB now (scope/deps — deferred).

## D6 — Dual resolution without breaking slice 1 (FR-012)

- **Decision**: `UpdateProcessor` gains an **optional** tool-dispatch collaborator (`?toolDispatch`).
  When present, each press is first resolved against it (binding store → tool registry, deferred-ack
  path); on a miss it falls back to the existing `IHookStore` ack-first path unchanged. Slice-1 code
  and tests construct the processor WITHOUT the collaborator → behavior byte-identical → tests green.
- **Rationale**: Additive, F#-optional-parameter change; no signature break for slice-1 callers; one
  clean place that carries the per-press ack policy (D2).
- **Alternatives considered**: replace `IHookStore` with a resolver seam (breaks slice-1 test
  constructors — rejected); register generic dispatch hooks into `IHookStore` (loses per-press ack
  policy, so tool toasts impossible — rejected).

## D7 — Neutral, LLM-agnostic keyboard plan

- **Decision**: A new neutral type `ToolKeyboard` (rows of `PlanButton = Tool(label, toolName, arg?)
  | Url(label, url)`) that the host fills from its own LLM output. The library ships **no** vendor LLM
  parsers. (Named `ToolKeyboard` to avoid clashing with slice-1's existing `KeyboardPlan` module.)
- **Rationale**: Keeps the library format-agnostic and thin (Clarifications, FR-013); the host owns
  LLM-format adaptation.

## D8 — Project & type delta (feeds Phase 1)

- **New project**: `TgLLM.Persistence` (F#) — the file-based `IBindingStore`. Deps: `TgLLM.Core`,
  System.Text.Json. IO-doing leaf; nothing depends on it except façades/host.
- **Extended in `TgLLM.Core`**: ports `IToolRegistry`, `IBindingStore`; type `ToolKeyboard`; extended
  `PressContext` (Arg, EditTextAsync, EditKeyboardAsync, Answer); extended `IBotApiClient`
  (EditMessageText, EditMessageReplyMarkup, AnswerCallback with text/alert); `RegisteredButton` → DU
  (Callback | Url); a `ToolDispatch` (binding store + registry + deferred-ack) consumed by
  `UpdateProcessor` via the optional collaborator.
- **Extended in `TgLLM.BotApi`**: mapping for URL buttons + the new edit/answer client methods.
- **Extended in the façades**: tool registration, `ToolKeyboard` building, `SendKeyboardPlan`, and
  configuring the durable store — idiomatic in F# and C#, no leakage.
- All changes are **additive**; slice-1 public API and tests remain intact (FR-012).

## Verify-at-implementation (Principle V) — Telegram.Bot 22.x mapping (from research)

| Need | Telegram.Bot 22.10.1 |
|------|----------------------|
| Edit text (+ optional keyboard) | `EditMessageText(chatId, messageId, text, ..., replyMarkup, ...)` → `Message` |
| Replace keyboard only | `EditMessageReplyMarkup(chatId, messageId, replyMarkup, ...)` → `Message` |
| Ack + optional toast/alert | `AnswerCallbackQuery(callbackQueryId, text?, showAlert, url?, cacheTime?, ct)` |
| URL button | `InlineKeyboardButton.WithUrl(text, url)` |
| Errors | `Telegram.Bot.Exceptions.ApiRequestException` (`.ErrorCode`, `.Message` = exact server string) |
