namespace TgLLM.Core

/// Agent-facing button spec. `Label` is a raw string — the agent shouldn't have to call
/// `ButtonLabel.create` themselves; `Keyboard.create` validates it (empty/too-long → the
/// position-qualified `KeyboardError`).
[<NoComparison; NoEquality>]
type ButtonSpec = { Label: string; Hook: Hook }

/// A validated keyboard layout, ready for token assignment (`KeyboardPlan.assign`). The case is
/// private so only `Keyboard.create` (and `KeyboardPlan`, in the same assembly) can produce one:
/// an existing `KeyboardSpec` value always satisfies >=1 row, every row >=1 button, every
/// button's label non-empty and within `ButtonLabel`'s length bound. Internally, labels are
/// stored already validated (`ButtonLabel`) so `KeyboardPlan.assign` never re-validates them.
[<NoComparison; NoEquality>]
type KeyboardSpec = private KeyboardSpec of (ButtonLabel * Hook) list list

/// One button on the wire-facing keyboard, after token assignment. Was a plain `{ Label; Token }`
/// record in slice 1; becomes a DU so a keyboard can mix hook/tool (`Callback`) and (`Url`)
/// buttons in the same layout. `Callback`'s shape is exactly slice-1's old record fields in case
/// order, so `Mapping.toInlineKeyboardMarkup`'s callback-button behavior is unchanged — only the
/// syntax at each call site (construction/pattern-match) changes from field access to this case.
type RegisteredButton =
    | Callback of label: ButtonLabel * token: CallbackToken
    | Url of label: ButtonLabel * url: string
    /// Wire-facing counterpart of `WebAppButton` (US3) — client-side, carries no `CallbackToken`.
    | WebApp of label: ButtonLabel * url: string
    /// Wire-facing counterpart of `CopyTextButton` (US3) — client-side, carries no `CallbackToken`.
    | CopyText of label: ButtonLabel * text: string

/// The wire-facing keyboard shape; the transport layer maps this to Telegram.Bot's
/// `InlineKeyboardMarkup`.
type RegisteredKeyboard = RegisteredKeyboard of RegisteredButton list list

module Keyboard =

    /// Validates the agent-supplied layout: >=1 row (else `EmptyKeyboard`), every row >=1 button
    /// (else `EmptyRow rowIndex`), every label non-empty and within bounds (else `EmptyLabel`/
    /// `TextTooLong`, at the button's (row, col)). `ButtonLabel.create` doesn't know its own
    /// position (it's also called standalone), so its position-agnostic `EmptyLabel(0, 0)` is
    /// re-wrapped here with the real (rowIndex, colIndex).
    let create (rows: ButtonSpec list list) : Result<KeyboardSpec, KeyboardError> =
        if List.isEmpty rows then
            Error EmptyKeyboard
        else
            let validateButton rowIndex colIndex (spec: ButtonSpec) =
                match ButtonLabel.create spec.Label with
                | Error(EmptyLabel _) -> Error(EmptyLabel(rowIndex, colIndex))
                | Error(TextTooLong(length, max)) -> Error(TextTooLong(length, max))
                | Error other -> Error other
                | Ok label -> Ok(label, spec.Hook)

            let validateRow rowIndex (row: ButtonSpec list) =
                if List.isEmpty row then
                    Error(EmptyRow rowIndex)
                else
                    row |> List.mapi (validateButton rowIndex) |> List.fold (fun acc item ->
                        match acc, item with
                        | Error e, _ -> Error e
                        | Ok _, Error e -> Error e
                        | Ok items, Ok item -> Ok(item :: items)) (Ok [])
                    |> Result.map List.rev

            rows
            |> List.mapi validateRow
            |> List.fold
                (fun acc item ->
                    match acc, item with
                    | Error e, _ -> Error e
                    | Ok _, Error e -> Error e
                    | Ok rows, Ok row -> Ok(row :: rows))
                (Ok [])
            |> Result.map (List.rev >> KeyboardSpec)

module KeyboardPlan =

    /// Pure: assigns one token per button from `tokens`, in row-major order, preserving row/column
    /// shape and labels. Returns the wire-facing keyboard
    /// plus one `HookBinding` per button (`bindings.Length = buttonCount`; distinct input tokens
    /// yield distinct button tokens, since each input token is consumed at most once).
    let assign (tokens: CallbackToken seq) (KeyboardSpec rows) : RegisteredKeyboard * HookBinding list =
        use tokenEnumerator = tokens.GetEnumerator()

        let nextToken () =
            if tokenEnumerator.MoveNext() then
                tokenEnumerator.Current
            else
                invalidArg (nameof tokens) "KeyboardPlan.assign requires at least one token per button."

        let mutable bindings: HookBinding list = []

        let registeredRows =
            rows
            |> List.map (
                List.map (fun (label, hook) ->
                    let token = nextToken ()
                    bindings <- { Token = token; Hook = hook } :: bindings
                    Callback(label, token))
            )

        RegisteredKeyboard registeredRows, List.rev bindings
