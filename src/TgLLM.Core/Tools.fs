namespace TgLLM.Core

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

/// The Tool Router surface: tool errors/names, the neutral keyboard plan, tool bindings, ports,
/// and tool dispatch. Additive on top of slice 1: nothing here changes slice-1 types except
/// `RegisteredButton` (Keyboard.fs), which stays behaviorally identical for callbacks.
///
/// Compiles right after Keyboard.fs: `ToolPlan.plan` produces `RegisteredKeyboard`/`RegisteredButton`
/// (Keyboard.fs) and needs `ButtonLabel`/`CallbackToken` (Values.fs/CallbackToken.fs); `Tool` needs
/// `PressContext` (Domain.fs). It compiles before Ports.fs/UpdateProcessor.fs so `ToolDispatch` can be
/// threaded into `UpdateProcessor` as an optional collaborator.
///
/// `PlanButton`/`ToolKeyboard` (the neutral keyboard plan) live in Values.fs instead of here:
/// `PressContext.EditKeyboardAsync` (Domain.fs) needs `ToolKeyboard`, and Domain.fs compiles
/// before this file — see Values.fs's own comment on those two types.

/// Validation outcomes for the Tool Router surface. `InvalidKeyboard` wraps slice-1's
/// `KeyboardError` (deviation, disclosed: an original three-case sketch grew a fourth case
/// because `ToolPlan.plan`/`Plan.rows` reuse `ButtonLabel.create` and slice-1's row/keyboard-shape
/// checks, both of which already report `KeyboardError`, not a fresh error type).
type ToolError =
    | EmptyToolName
    | UnknownTool of name: string
    | InvalidUrl of value: string
    | InvalidKeyboard of KeyboardError

/// The name a tool is registered and referenced under. Single-case, smart-constructed like
/// `ButtonLabel`/`MessageText` — never a raw, unvalidated `string`.
[<Struct>]
type ToolName = private ToolName of string

module ToolName =

    /// `raw` is annotated nullable because this is a public API boundary — C# callers (and LLM
    /// output in general) may pass `null` despite the parameter's intent (Always-Rule 5).
    let create (raw: string | null) : Result<ToolName, ToolError> =
        let trimmed = (raw |> Option.ofObj |> Option.defaultValue "").Trim()
        if trimmed.Length = 0 then Error EmptyToolName else Ok(ToolName trimmed)

    let value (ToolName s) : string = s

/// The agent-supplied reaction to a tool button press. Unlike slice-1's `Hook`, a `Tool` is looked
/// up by `ToolName` (via `IToolRegistry`), not bound at keyboard-build time — the same registered
/// tool can be targeted by many different keyboards/bindings.
type Tool = PressContext -> Task

/// One button→tool association, as stored by `IBindingStore`. Unlike slice-1's `HookBinding`
/// (`Token -> live Hook` closure, non-serializable), every field here is plain data, so a
/// `ToolBinding` can be serialized and persisted — hence it derives structural
/// equality/comparison (useful for tests and durable-store round-trip checks), unlike
/// `HookBinding`/`RouteDecision`, which hold function values and can't.
type ToolBinding =
    { Token: CallbackToken
      ToolName: ToolName
      Arg: string option }

/// The pure kernel of the Tool Router (an FsCheck property-test target). Mirrors slice-1's
/// `Keyboard.create` -> `KeyboardPlan.assign` split, but combined into one function: unlike
/// `KeyboardSpec` (already validated before token assignment), a `ToolKeyboard` is unvalidated raw
/// data, so `plan` validates every button (label, tool name, url) THEN assigns tokens to tool
/// buttons only, passing URL buttons through untouched.
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

    /// Whether `keyboard` contains at least one `ToolButton` (as opposed to being made entirely of
    /// `UrlButton`s). Used by `TgBot.SendKeyboardPlan` (review finding #10, 003-tool-router-extensions)
    /// to fail fast when a plan with tool buttons is sent without a Tool Router wired in: such
    /// buttons would otherwise reach the wire, get tapped, and silently no-op forever — no
    /// `ToolDispatch` exists to ever resolve their bindings, so every press falls through to the
    /// slice-1 `IHookStore` path and is reported only as an unknown/stale token (or nothing at all,
    /// with the default `NoopHookObserver`). A URL-only plan never needs a Tool Router, so it's
    /// never flagged regardless of wiring.
    let hasToolButtons (keyboard: ToolKeyboard) : bool =
        keyboard.Rows
        |> List.exists (
            List.exists (function
                | ToolButton _ -> true
                | UrlButton _ -> false)
        )

    /// Validates every button, THEN assigns one token per `ToolButton` (URL buttons pass through
    /// untouched). Properties covered by the FsCheck suite: row/label shape preserved; one
    /// token+binding per tool button; URL buttons carry no binding; token count = tool-button
    /// count; distinct input tokens yield distinct button tokens (each input token is consumed at
    /// most once, same guarantee as `KeyboardPlan.assign`).
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

/// Registers named tools and resolves them by name. Add-or-replace by design (mirrors slice-1's
/// `IHookStore.Register` semantics for re-sends).
type IToolRegistry =
    abstract Register: name: ToolName * tool: Tool -> unit
    abstract TryResolve: name: ToolName -> Tool voption

/// Default `IToolRegistry`: a `ConcurrentDictionary` keyed by `ToolName`. Mirrors
/// `InMemoryHookStore`'s shape, but keyed by name (stable across sends) rather than by per-button
/// `CallbackToken`.
type InMemoryToolRegistry() =
    let tools = ConcurrentDictionary<ToolName, Tool>()

    interface IToolRegistry with
        member _.Register(name: ToolName, tool: Tool) : unit = tools[name] <- tool

        member _.TryResolve(name: ToolName) : Tool voption =
            match tools.TryGetValue name with
            | true, tool -> ValueSome tool
            | false, _ -> ValueNone

/// Serializable button->tool bindings. In-memory default here; a durable (file-based)
/// implementation lives in the separate `TgLLM.Persistence` leaf project (Core stays
/// IO-agnostic, Principle III).
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

/// Tracks which `CallbackToken`s are currently live for a sent/edited message's keyboard, so a
/// re-render (`ctx.EditKeyboardAsync`, e.g. a paginator/counter tool) can remove its OWN previous
/// bindings from the durable store instead of leaking one row per edit forever. `Record` is called
/// AFTER a send/edit reaches the wire — the map only ever reflects PAST sends, so it can never
/// affect the request that is currently producing it.
///
/// Keyed by `(ChatId, MessageId)`, NOT `MessageId` alone (review finding #2,
/// 003-tool-router-extensions): Telegram's `message_id` is unique only PER CHAT, so two different
/// chats' keyboard messages can legitimately share the same `MessageId` (e.g. each chat's
/// first-ever sent message). A bare-`MessageId` key would let an edit in one chat find — and
/// therefore remove — another chat's still-live bindings the instant their message ids collided;
/// this is the exact data-corruption bug this key shape fixes.
///
/// Accepted PoC-scope limitation (in-memory, per-process): this map is empty after a process
/// restart, so an edit made to a message that was originally SENT (or last edited) before the
/// restart won't find — and therefore won't remove — its pre-restart bindings from a durable store
/// (e.g. `TgLLM.Persistence.FileBindingStore`). Those rows are only cleaned up if/when that same
/// message is edited again after the tracker has re-learned its current tokens.
[<Sealed>]
type MessageBindingTracker() =
    let tokensByMessage = ConcurrentDictionary<ChatId * MessageId, CallbackToken list>()

    /// Record (or overwrite) the tokens currently live for `(chat, messageId)`.
    member _.Record(chat: ChatId, messageId: MessageId, tokens: CallbackToken list) : unit =
        tokensByMessage[(chat, messageId)] <- tokens

    /// `(chat, messageId)`'s previously recorded tokens, if this tracker has ever recorded a
    /// keyboard for it. A `messageId` recorded for a DIFFERENT chat is a miss, never a hit.
    member _.TryGetPrevious(chat: ChatId, messageId: MessageId) : CallbackToken list option =
        match tokensByMessage.TryGetValue((chat, messageId)) with
        | true, tokens -> Some tokens
        | false, _ -> None

/// The deferred-ack tool path's resolver: token -> binding (via `IBindingStore`) -> tool (via
/// `IToolRegistry`). `ValueNone` on EITHER miss (unknown token, or a binding whose tool is no
/// longer registered) so `UpdateProcessor` can uniformly fall back to the slice-1 `IHookStore`
/// ack-first path, which will itself report the unknown/stale press — no separate "known binding,
/// unknown tool" branch is needed.
///
/// `tracker` defaults to a fresh, private `MessageBindingTracker` so existing 2-arg call sites keep
/// compiling — pass an explicit instance (as `TgBot` does) to SHARE bookkeeping with whatever also
/// records a message's bindings at send time (`TgBot.SendKeyboardPlan`), so an edit can find and
/// remove them.
[<Sealed>]
type ToolDispatch(registry: IToolRegistry, store: IBindingStore, ?tracker: MessageBindingTracker) =
    let tracker = defaultArg tracker (MessageBindingTracker())

    /// Exposed so `UpdateProcessor`'s edit-in-place wiring can save the bindings for a tool
    /// button's REPLACEMENT keyboard (`PressContext.EditKeyboardAsync`) into the SAME store this
    /// dispatch resolves presses against — a re-plan without this would register bindings nothing
    /// could ever resolve.
    member _.Store: IBindingStore = store

    /// The message->tokens bookkeeping used to remove a superseded keyboard's bindings on edit
    /// (see `MessageBindingTracker`'s own doc comment).
    member _.Tracker: MessageBindingTracker = tracker

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

/// The shared "plan → save bindings → put the keyboard on the wire → track" operation, used by
/// both `TgBot.SendKeyboardPlan` (a fresh send) and `UpdateProcessor`'s edit-in-place wiring
/// (`PressContext.EditKeyboardAsync`). Mirrors `AgentOps.sendKeyboard`'s (UpdateProcessor.fs)
/// save-before-wire ordering guarantee, but for the Tool Router's serializable `ToolBinding`s
/// instead of slice-1's live `HookBinding` closures.
module ToolKeyboardOps =

    /// Validates+plans `plan`, saves its bindings BEFORE `send` puts the keyboard on the wire, then
    /// records the delivered message's tokens into `tracker` — this only ever affects a FUTURE
    /// `ctx.EditKeyboardAsync` on that same message, never this call itself. `staleMessageId =
    /// Some messageId` additionally removes `(chat, messageId)`'s previously-tracked bindings FIRST
    /// (the edit path: a tool that re-renders its own keyboard, e.g. a paginator/counter, must not
    /// leak one row per edit); `None` skips this (a fresh send has nothing stale to remove). `chat`
    /// is required (not bundled into `staleMessageId`) because `MessageId` alone is ambiguous
    /// across chats (review finding #2) — every caller already has a `ChatId` in hand (the press's
    /// own chat, or the chat a fresh send targets). The old tokens are looked up and removed BEFORE
    /// the new ones are saved; `tracker.Record` runs only AFTER `send` completes, matching
    /// `MessageBindingTracker`'s own "reflects past sends only" contract. An invalid `plan` is a
    /// programmer error by the caller (Always-Rule 6) — fails fast (`invalidArg`, tagged with
    /// `context` for a useful message) rather than threading a `Result` through this API.
    let deliver
        (context: string)
        (tokenGen: unit -> CallbackToken)
        (store: IBindingStore)
        (tracker: MessageBindingTracker)
        (chat: ChatId)
        (staleMessageId: MessageId option)
        (send: RegisteredKeyboard -> Task<MessageId>)
        (ct: CancellationToken)
        (plan: ToolKeyboard)
        : Task<MessageId> =
        task {
            match ToolPlan.plan (Seq.initInfinite (fun _ -> tokenGen ())) plan with
            | Error e -> return invalidArg (nameof plan) $"{context}: invalid plan ({e})"
            | Ok(registeredKeyboard, bindings) ->
                match staleMessageId |> Option.bind (fun messageId -> tracker.TryGetPrevious(chat, messageId)) with
                | Some staleTokens -> do! store.Remove(staleTokens, ct)
                | None -> ()

                do! store.Save(bindings, ct)
                let! messageId = send registeredKeyboard
                tracker.Record(chat, messageId, bindings |> List.map (fun b -> b.Token))
                return messageId
        }
