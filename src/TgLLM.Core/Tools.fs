namespace TgLLM.Core

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

/// Feature 002-llm-tool-router (data-model.md "ToolError", "ToolName", neutral keyboard plan, tool
/// binding, ports, tool dispatch). Additive on slice 1 (FR-012): nothing here changes slice-1 types
/// except `RegisteredButton` (Keyboard.fs, T007), which stays behaviorally identical for callbacks.
///
/// Compiles right after Keyboard.fs: `ToolPlan.plan` produces `RegisteredKeyboard`/`RegisteredButton`
/// (Keyboard.fs) and needs `ButtonLabel`/`CallbackToken` (Values.fs/CallbackToken.fs); `Tool` needs
/// `PressContext` (Domain.fs). It compiles before Ports.fs/UpdateProcessor.fs so `ToolDispatch` can be
/// threaded into `UpdateProcessor` as an optional collaborator (T016).

/// Validation outcomes for the Tool Router surface (data-model.md "ToolError"). `InvalidKeyboard`
/// wraps slice-1's `KeyboardError` (deviation, disclosed: the data-model sketch lists three cases;
/// a fourth is needed because `ToolPlan.plan`/`Plan.rows` reuse `ButtonLabel.create` and slice-1's
/// row/keyboard-shape checks, both of which already report `KeyboardError`, not a fresh error type).
type ToolError =
    | EmptyToolName
    | UnknownTool of name: string
    | InvalidUrl of value: string
    | InvalidKeyboard of KeyboardError

/// The name a tool is registered and referenced under (data-model.md "ToolName"). Single-case,
/// smart-constructed like `ButtonLabel`/`MessageText` — never a raw, unvalidated `string`.
[<Struct>]
type ToolName = private ToolName of string

module ToolName =

    /// `raw` is annotated nullable because this is a public API boundary — C# callers (and LLM
    /// output in general) may pass `null` despite the parameter's intent (Always-Rule 5).
    let create (raw: string | null) : Result<ToolName, ToolError> =
        let trimmed = (raw |> Option.ofObj |> Option.defaultValue "").Trim()
        if trimmed.Length = 0 then Error EmptyToolName else Ok(ToolName trimmed)

    let value (ToolName s) : string = s

/// The agent-supplied reaction to a tool button press (data-model.md "Ports"). Unlike slice-1's
/// `Hook`, a `Tool` is looked up by `ToolName` (via `IToolRegistry`), not bound at keyboard-build
/// time — the same registered tool can be targeted by many different keyboards/bindings.
type Tool = PressContext -> Task

/// The host-filled, LLM-agnostic keyboard plan (data-model.md "Neutral keyboard plan", research.md
/// D7). `label`/`toolName`/`url` are raw strings — validated by `ToolPlan.plan` at send time (mirrors
/// slice-1's `ButtonSpec`, whose raw `Label` is validated by `Keyboard.create`).
type PlanButton =
    | ToolButton of label: string * toolName: string * arg: string option
    | UrlButton of label: string * url: string

/// The neutral, unvalidated plan (data-model.md "ToolKeyboard"): >=1 row, each >=1 button by
/// convention; `ToolPlan.plan` and the façade's `Plan.rows` are where that shape is actually
/// enforced (see their doc comments) — this record itself is a plain data holder, like slice-1's
/// `ButtonSpec list list` before `Keyboard.create` validates it.
type ToolKeyboard = { Rows: PlanButton list list }

/// One button→tool association, as stored by `IBindingStore` (data-model.md "Tool binding").
/// Unlike slice-1's `HookBinding` (`Token -> live Hook` closure, non-serializable), every field here
/// is plain data, so a `ToolBinding` can be serialized and persisted (D5) — hence it derives
/// structural equality/comparison (useful for tests and durable-store round-trip checks), unlike
/// `HookBinding`/`RouteDecision`, which hold function values and can't.
type ToolBinding =
    { Token: CallbackToken
      ToolName: ToolName
      Arg: string option }

/// The pure kernel of the Tool Router (data-model.md "Pure kernel", FsCheck target T006). Mirrors
/// slice-1's `Keyboard.create` -> `KeyboardPlan.assign` split, but combined into one function: unlike
/// `KeyboardSpec` (already validated before token assignment), a `ToolKeyboard` is unvalidated raw
/// data, so `plan` validates every button (label, tool name, url) THEN assigns tokens to tool buttons
/// only, passing URL buttons through untouched (research.md D3).
module ToolPlan =

    /// A button whose label/tool-name/url have already been validated — the token-assignment pass
    /// below can't fail once it has one of these (mirrors `KeyboardSpec`'s internal
    /// `(ButtonLabel * Hook) list list` shape).
    type private ValidatedButton =
        | ValidTool of label: ButtonLabel * toolName: ToolName * arg: string option
        | ValidUrl of label: ButtonLabel * url: string

    let private validateButton (button: PlanButton) : Result<ValidatedButton, ToolError> =
        match button with
        | UrlButton(label, url) ->
            match ButtonLabel.create label with
            | Error e -> Error(InvalidKeyboard e)
            | Ok validLabel -> if String.IsNullOrWhiteSpace url then Error(InvalidUrl url) else Ok(ValidUrl(validLabel, url))
        | ToolButton(label, toolName, arg) ->
            match ButtonLabel.create label with
            | Error e -> Error(InvalidKeyboard e)
            | Ok validLabel ->
                match ToolName.create toolName with
                | Error e -> Error e
                | Ok validToolName -> Ok(ValidTool(validLabel, validToolName, arg))

    /// Same short-circuiting fold shape as slice-1's `Keyboard.create` (Keyboard.fs).
    let private sequenceResults (items: Result<'a, ToolError> list) : Result<'a list, ToolError> =
        items
        |> List.fold
            (fun acc item ->
                match acc, item with
                | Error e, _ -> Error e
                | Ok _, Error e -> Error e
                | Ok xs, Ok x -> Ok(x :: xs))
            (Ok [])
        |> Result.map List.rev

    /// A row with zero buttons is rejected (mirrors slice-1 `Keyboard.create`'s `EmptyRow`); a
    /// non-empty row delegates to `validateButton` per button.
    let private validateRow (rowIndex: int) (row: PlanButton list) : Result<ValidatedButton list, ToolError> =
        if List.isEmpty row then
            Error(InvalidKeyboard(EmptyRow rowIndex))
        else
            row |> List.map validateButton |> sequenceResults

    /// A keyboard with zero rows is rejected (mirrors slice-1 `Keyboard.create`'s `EmptyKeyboard`).
    let private validateRows (rows: PlanButton list list) : Result<ValidatedButton list list, ToolError> =
        if List.isEmpty rows then
            Error(InvalidKeyboard EmptyKeyboard)
        else
            rows |> List.mapi validateRow |> sequenceResults

    /// Validates a plan's shape (>=1 row, each >=1 button) and every button (label, tool name, url)
    /// WITHOUT assigning tokens — the façade's `Plan.rows` smart constructor uses this so build-time
    /// errors surface immediately (mirrors slice-1's `Keyboard.create`). `plan` (below) reuses the
    /// same validation as a defense-in-depth re-check at send time: `ToolKeyboard` is a plain public
    /// record, not opaque, so nothing stops a caller from constructing one directly and skipping
    /// `Plan.rows`.
    let validate (keyboard: ToolKeyboard) : Result<ToolKeyboard, ToolError> =
        validateRows keyboard.Rows |> Result.map (fun _ -> keyboard)

    /// Validates every button, THEN assigns one token per `ToolButton` (URL buttons pass through
    /// untouched, D3). Properties (T006): row/label shape preserved; one token+binding per tool
    /// button; URL buttons carry no binding; token count = tool-button count; distinct input tokens
    /// yield distinct button tokens (each input token is consumed at most once, same guarantee as
    /// `KeyboardPlan.assign`).
    let plan (tokens: CallbackToken seq) (keyboard: ToolKeyboard) : Result<RegisteredKeyboard * ToolBinding list, ToolError> =
        validateRows keyboard.Rows
        |> Result.map (fun validatedRows ->
            use tokenEnumerator = tokens.GetEnumerator()

            let nextToken () =
                if tokenEnumerator.MoveNext() then
                    tokenEnumerator.Current
                else
                    invalidArg (nameof tokens) "ToolPlan.plan requires at least one token per tool button."

            let mutable bindings: ToolBinding list = []

            let assignButton (button: ValidatedButton) : RegisteredButton =
                match button with
                | ValidUrl(label, url) -> Url(label, url)
                | ValidTool(label, toolName, arg) ->
                    let token = nextToken ()
                    bindings <- { Token = token; ToolName = toolName; Arg = arg } :: bindings
                    Callback(label, token)

            let registeredRows = validatedRows |> List.map (List.map assignButton)
            RegisteredKeyboard registeredRows, List.rev bindings)

/// Registers named tools and resolves them by name (data-model.md "Ports"). Add-or-replace by
/// design (mirrors slice-1's `IHookStore.Register` semantics for re-sends).
type IToolRegistry =
    abstract Register: name: ToolName * tool: Tool -> unit
    abstract TryResolve: name: ToolName -> Tool voption

/// Default `IToolRegistry`: a `ConcurrentDictionary` keyed by `ToolName`. Mirrors
/// `InMemoryHookStore`'s shape, but keyed by name (stable across sends) rather than by
/// per-button `CallbackToken` (data-model.md "IToolRegistry").
type InMemoryToolRegistry() =
    let tools = ConcurrentDictionary<ToolName, Tool>()

    interface IToolRegistry with
        member _.Register(name: ToolName, tool: Tool) : unit = tools[name] <- tool

        member _.TryResolve(name: ToolName) : Tool voption =
            match tools.TryGetValue name with
            | true, tool -> ValueSome tool
            | false, _ -> ValueNone

/// Serializable button->tool bindings (data-model.md "IBindingStore", research.md D5). In-memory
/// default here; a durable (file-based) implementation lives in the separate `TgLLM.Persistence`
/// leaf project (Core stays IO-agnostic, Principle III).
type IBindingStore =
    abstract Save: bindings: IReadOnlyList<ToolBinding> * ct: CancellationToken -> ValueTask
    abstract TryGet: token: CallbackToken * ct: CancellationToken -> ValueTask<ToolBinding voption>
    abstract Remove: tokens: IReadOnlyList<CallbackToken> * ct: CancellationToken -> ValueTask

/// Default `IBindingStore`: a `ConcurrentDictionary` keyed by `CallbackToken`, same shape as
/// `InMemoryHookStore` — all operations complete synchronously, hence `ValueTask` throughout.
type InMemoryBindingStore() =
    let bindings = ConcurrentDictionary<CallbackToken, ToolBinding>()

    interface IBindingStore with
        member _.Save(newBindings: IReadOnlyList<ToolBinding>, _ct: CancellationToken) : ValueTask =
            for binding in newBindings do
                bindings[binding.Token] <- binding

            ValueTask.CompletedTask

        member _.TryGet(token: CallbackToken, _ct: CancellationToken) : ValueTask<ToolBinding voption> =
            match bindings.TryGetValue token with
            | true, binding -> ValueTask.FromResult(ValueSome binding)
            | false, _ -> ValueTask.FromResult ValueNone

        member _.Remove(tokens: IReadOnlyList<CallbackToken>, _ct: CancellationToken) : ValueTask =
            for token in tokens do
                bindings.TryRemove(token) |> ignore

            ValueTask.CompletedTask

/// The deferred-ack tool path's resolver (T014, data-model.md "Tool dispatch", research.md D6):
/// token -> binding (via `IBindingStore`) -> tool (via `IToolRegistry`). `ValueNone` on EITHER miss
/// (unknown token, or a binding whose tool is no longer registered) so `UpdateProcessor` can uniformly
/// fall back to the slice-1 `IHookStore` ack-first path, which will itself report the unknown/stale
/// press (FR-005/FR-010) — no separate "known binding, unknown tool" branch is needed.
[<Sealed>]
type ToolDispatch(registry: IToolRegistry, store: IBindingStore) =

    member _.Resolve(token: CallbackToken, ct: CancellationToken) : ValueTask<(Tool * ToolBinding) voption> =
        ValueTask<(Tool * ToolBinding) voption>(
            task {
                let! bindingOption = store.TryGet(token, ct)

                match bindingOption with
                | ValueNone -> return ValueNone
                | ValueSome binding ->
                    match registry.TryResolve binding.ToolName with
                    | ValueNone -> return ValueNone
                    | ValueSome tool -> return ValueSome(tool, binding)
            }
        )
