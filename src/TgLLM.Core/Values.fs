namespace TgLLM.Core

open System

/// Validation outcomes shared by the value objects below and by `Keyboard.create`
/// (data-model.md "KeyboardError"). Row/column limits driven by Bot API vendor verification
/// (Principle V) are deferred to Phase 3 (T024/T048); only the checks this Foundational phase
/// actually enforces are represented here.
type KeyboardError =
    /// A keyboard with zero rows.
    | EmptyKeyboard
    /// A row with zero buttons, at this 0-based row index.
    | EmptyRow of rowIndex: int
    /// A button whose label is empty after trimming, at this 0-based (row, column).
    /// `ButtonLabel.create`/`MessageText.create` are called both standalone (no keyboard
    /// position exists yet) and from `Keyboard.create` (which knows the position); standalone
    /// callers report `(0, 0)` as a position-agnostic sentinel, and `Keyboard.create` re-wraps
    /// the error with the real position before returning it to its caller.
    | EmptyLabel of rowIndex: int * colIndex: int
    /// Text longer than the max length allowed for its context.
    | TextTooLong of length: int * max: int

/// The visible text on a button (FR-001, data-model.md "ButtonLabel").
[<Struct>]
type ButtonLabel = private ButtonLabel of string

module ButtonLabel =

    /// Bot API vendor verification for inline button text length is deferred to Phase 3
    /// (T024/T048, Principle V) — core.telegram.org does not document a character limit for
    /// inline keyboard button text the way it documents `callback_data`'s 1–64 BYTE limit. This
    /// bound is a conservative placeholder that gives the smart constructor a concrete, testable
    /// invariant now; revisit once Phase 3 confirms (or replaces) it against the vendor docs.
    [<Literal>]
    let MaxLength = 64

    /// `raw` is annotated nullable because this is a public API boundary — C# callers (and
    /// external input generally) may pass `null` despite the parameter's intent (Always-Rule 5).
    let create (raw: string | null) : Result<ButtonLabel, KeyboardError> =
        let trimmed = (raw |> Option.ofObj |> Option.defaultValue "").Trim()
        if trimmed.Length = 0 then Error(EmptyLabel(0, 0))
        elif trimmed.Length > MaxLength then Error(TextTooLong(trimmed.Length, MaxLength))
        else Ok(ButtonLabel trimmed)

    let value (ButtonLabel s) = s

/// The text of a message that carries a keyboard or a reply (FR-006, data-model.md "MessageText").
[<Struct>]
type MessageText = private MessageText of string

module MessageText =

    /// Telegram Bot API `sendMessage` text limit (research.md D7; vendor-confirmed value).
    [<Literal>]
    let MaxLength = 4096

    /// `raw` is annotated nullable because this is a public API boundary — C# callers (and
    /// external input generally) may pass `null` despite the parameter's intent (Always-Rule 5).
    let create (raw: string | null) : Result<MessageText, KeyboardError> =
        let trimmed = (raw |> Option.ofObj |> Option.defaultValue "").Trim()
        if trimmed.Length = 0 then Error(EmptyLabel(0, 0))
        elif trimmed.Length > MaxLength then Error(TextTooLong(trimmed.Length, MaxLength))
        else Ok(MessageText trimmed)

    let value (MessageText s) = s

    /// Fail-fast constructor for trusted literals (contracts/fsharp-facade.md, quickstart.md:
    /// `MessageText.unsafe "Deploy?"`). An invalid `raw` is a programmer error by the caller
    /// (Always-Rule 6) — the caller controls the literal, so this is not a business error to
    /// route through `Result`. Mirrors the fail-fast pattern already used by
    /// `UpdateProcessor.fs`'s `makeReplyTextAsync`.
    let unsafe (raw: string) : MessageText =
        match create raw with
        | Ok m -> m
        | Error e -> invalidArg (nameof raw) $"MessageText.unsafe: invalid text ({e})"

/// The host-filled, LLM-agnostic keyboard plan (data-model.md "Neutral keyboard plan", research.md
/// D7). Relocated here from `Tools.fs` (feature 002-llm-tool-router, T022, disclosed deviation): US2
/// needs `PressContext.EditKeyboardAsync` (Domain.fs) to accept a `ToolKeyboard`, and `PressContext`
/// compiles before `Tools.fs` (see the compile-order comment in `TgLLM.Core.fsproj`); `PlanButton`/
/// `ToolKeyboard` have no dependency beyond primitive types, so moving just these two types this
/// early costs nothing and unblocks the forward reference. `ToolPlan`, `ToolBinding`, `ToolError`,
/// `IToolRegistry`, `IBindingStore`, and `ToolDispatch` all stay in `Tools.fs`, unaffected.
type PlanButton =
    | ToolButton of label: string * toolName: string * arg: string option
    | UrlButton of label: string * url: string

/// The neutral, unvalidated plan (data-model.md "ToolKeyboard"): >=1 row, each >=1 button by
/// convention; `ToolPlan.plan` and the façade's `Plan.rows` are where that shape is actually
/// enforced (see their doc comments) — this record itself is a plain data holder, like slice-1's
/// `ButtonSpec list list` before `Keyboard.create` validates it.
type ToolKeyboard = { Rows: PlanButton list list }
