namespace TgLLM.Core

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

/// The Tool Router surface: tool errors/names, the neutral keyboard plan, tool bindings, ports,
/// and tool dispatch. Additive: nothing here changes the earlier types except
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
    /// Also covers a `WebAppButton` whose url is missing/non-https — same class
    /// of problem (a bad url), so it reuses this case rather than growing a near-duplicate one.
    | InvalidUrl of value: string
    | InvalidKeyboard of KeyboardError
    /// A `CopyTextButton` whose text is outside the Bot API's 1..256-character `copy_text` limit
    /// (vendor-verified against core.telegram.org).
    | InvalidCopyText of text: string

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

/// Who may press a tool (callback) button. `Anyone` (the default, unset) keeps
/// slice-1/2 behavior: any presser resolves the button. `User uid` restricts tool presses to that
/// exact user — enforced ONLY on tool buttons (client-side `UrlButton`/`WebAppButton`/
/// `CopyTextButton` carry no scope at all). `[<Struct>]` like `ButtonLabel`/`ToolName`:
/// a small, frequently-compared value.
[<Struct>]
type OwnerScope =
    | Anyone
    | User of userId: UserId

module OwnerScope =

    /// The pure "is this presser allowed?" decision. `Anyone` always allows, including an
    /// unidentifiable presser (`None`). `User uid` allows ONLY that exact user — a different user
    /// OR a missing/anonymous presser (`None`, e.g. no `from` on the callback query) is refused;
    /// there is no default-allow for an unidentifiable presser under a `User` scope; refusing is
    /// the safe default.
    let isAllowed (scope: OwnerScope) (presser: UserId option) : bool =
        match scope, presser with
        | Anyone, _ -> true
        | User uid, Some pressingUser -> uid = pressingUser
        | User _, None -> false

    /// The built-in English notice a refused presser sees when the keyboard's own send-time
    /// notice override (`ToolBinding.DeniedNotice`) was left unset. Overridable per keyboard —
    /// see `ToolKeyboardOps.deliver`'s `deniedNotice` parameter — this is only the fallback.
    [<Literal>]
    let DefaultDeniedNotice = "This button isn't for you."

/// "Now", injected rather than read ambiently — the expiry decision (`Expiry.isLive`) and
/// `ProcessedQueryTracker`'s TTL bookkeeping both take one of these instead of ever calling
/// `DateTimeOffset.Now`/`UtcNow` themselves, so Core stays deterministic and property-testable.
/// The façade/host supplies the real clock (`fun () -> DateTimeOffset.UtcNow`) when
/// wiring the resolve/dispatch step.
type Clock = unit -> DateTimeOffset

/// The pure expiry decision for a `ToolBinding.ExpiresAt`.
module Expiry =

    /// `None` (no expiry set) always lives. `Some expiresAt` lives strictly BEFORE `expiresAt` and
    /// is refused AT and after it — the boundary instant itself already counts as expired, not
    /// live (deliberate: "expires at 5:00" reads as "no longer valid from 5:00 onward"). Resolution
    /// treats a refused (expired) binding like an unknown tool: ack, no invoke, no crash.
    let isLive (now: DateTimeOffset) (expiresAt: DateTimeOffset option) : bool =
        match expiresAt with
        | None -> true
        | Some exp -> now < exp

/// The agent-supplied reaction to a tool button press. Unlike slice-1's `Hook`, a `Tool` is looked
/// up by `ToolName` (via `IToolRegistry`), not bound at keyboard-build time — the same registered
/// tool can be targeted by many different keyboards/bindings.
type Tool = PressContext -> Task

/// One button→tool association, as stored by `IBindingStore`. Unlike slice-1's `HookBinding`
/// (`Token -> live Hook` closure, non-serializable), every field here is plain data, so a
/// `ToolBinding` can be serialized and persisted — hence it derives structural
/// equality/comparison (useful for tests and durable-store round-trip checks), unlike
/// `HookBinding`/`RouteDecision`, which hold function values and can't.
/// Evolved additively: `Owner`/`ExpiresAt`/`SingleUse`
/// are NEW fields, on top of slice-2's exact `{ Token; ToolName; Arg }` shape. `Arg` stays `string
/// option` — an opaque, possibly-JSON payload — Core never depends on
/// System.Text.Json. `DeniedNotice` is a further additive field (not part of that original
/// three-field evolution): the per-keyboard override of the notice a non-owner sees on refusal —
/// stored alongside `Owner` for the same reason `Owner` itself is stored on the binding rather
/// than kept only in the sending process's memory: a host-configured notice must still be shown
/// after a restart, when nothing but the durable store connects the press to the keyboard that
/// produced it. `None` means "no override" — the refusal path falls back to
/// `OwnerScope.DefaultDeniedNotice`, so this field costs nothing for a keyboard that never sets it.
type ToolBinding =
    { Token: CallbackToken
      ToolName: ToolName
      Arg: string option
      /// NEW — who may press this binding's button. Defaults to `Anyone`.
      Owner: OwnerScope
      /// NEW — when this binding stops resolving. Defaults to `None` (never expires).
      ExpiresAt: DateTimeOffset option
      /// NEW — whether the first press that RESOLVES this binding consumes it (confirm-once
      /// mode), regardless of whether that press's tool goes on to succeed or throw. Defaults to
      /// `false`.
      SingleUse: bool
      /// NEW — this keyboard's own override of the notice shown to a refused (non-owner)
      /// presser. `None` uses `OwnerScope.DefaultDeniedNotice` at refusal time.
      DeniedNotice: string option }

module ToolBinding =

    /// The slice-2-shaped constructor: `token`/`toolName`/`arg` are the only fields a caller not
    /// yet using owner-scoping/expiry/single-use/notice needs to supply — every new field is
    /// filled with its default (`Anyone`/`None`/`false`/`None`), so a binding built this way is
    /// indistinguishable from a slice-2 binding that never had those fields at all.
    let create (token: CallbackToken) (toolName: ToolName) (arg: string option) : ToolBinding =
        { Token = token
          ToolName = toolName
          Arg = arg
          Owner = Anyone
          ExpiresAt = None
          SingleUse = false
          DeniedNotice = None }

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
        /// A validated `WebAppButton` — `url` has already been confirmed https.
        | ValidWebApp of label: ButtonLabel * url: string
        /// A validated `CopyTextButton` — `text` has already been confirmed
        /// 1..256 characters.
        | ValidCopyText of label: ButtonLabel * text: string

    /// Bot API vendor fact (core.telegram.org, Principle V): `web_app` (`WebAppInfo`) launches a
    /// Mini App; its `url` MUST be https. A plain scheme-prefix check (rather than
    /// `Uri.TryCreate`) is deliberate: the Bot API's own requirement is exactly "scheme is https",
    /// not RFC-perfect URI validity, and it sidesteps `Uri.TryCreate`'s nullable `out` parameter
    /// friction under strict nullness checking. Null-safe: a missing/empty url is simply not https.
    let private isHttps (url: string) : bool =
        not (String.IsNullOrEmpty url) && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)

    /// Bot API vendor fact (core.telegram.org, Principle V): `copy_text` (`CopyTextButton`)'s
    /// `text` is 1..256 characters. Null-safe via `String.IsNullOrEmpty` (which itself accepts a
    /// possibly-null string): a missing/empty text is simply out of range, reported as
    /// `InvalidCopyText` like any other invalid length.
    [<Literal>]
    let private CopyTextMinLength = 1

    [<Literal>]
    let private CopyTextMaxLength = 256

    let private isValidCopyTextLength (text: string) : bool =
        not (String.IsNullOrEmpty text) && text.Length >= CopyTextMinLength && text.Length <= CopyTextMaxLength

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
        | WebAppButton(label, url) ->
            match ButtonLabel.create label with
            | Error e -> Error(InvalidKeyboard e)
            | Ok validLabel -> if isHttps url then Ok(ValidWebApp(validLabel, url)) else Error(InvalidUrl url)
        | CopyTextButton(label, text) ->
            match ButtonLabel.create label with
            | Error e -> Error(InvalidKeyboard e)
            | Ok validLabel -> if isValidCopyTextLength text then Ok(ValidCopyText(validLabel, text)) else Error(InvalidCopyText text)

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
    /// `UrlButton`s). Used by `TgBot.SendKeyboardPlan` to fail fast when a plan with tool buttons is
    /// sent without a Tool Router wired in: such
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
                | UrlButton _ | WebAppButton _ | CopyTextButton _ -> false)
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
                | ValidWebApp(label, url) -> WebApp(label, url)
                | ValidCopyText(label, text) -> CopyText(label, text)
                | ValidTool(label, toolName, arg) ->
                    let token = nextToken ()
                    bindings <- ToolBinding.create token toolName arg :: bindings
                    Callback(label, token)

            let registeredRows = validatedRows |> List.map (List.map assignButton)
            RegisteredKeyboard registeredRows, List.rev bindings)

/// Advisory metadata supplied at tool registration, used only to build the manifest below —
/// never to constrain routing (`TryResolve` ignores it entirely). Both fields are optional: a
/// tool registered with neither still registers, routes, and appears in the manifest by name
/// alone.
type ToolMetadata =
    { Description: string option
      /// An opaque argument-schema text (conventionally JSON Schema). Carried verbatim — the
      /// registry never parses or validates it.
      ArgSchema: string option }

/// One entry in a `ToolManifest`: a registered tool's name plus its optional description and
/// argument schema. `Parameters` carries `ToolMetadata.ArgSchema` under the name a function-calling
/// API expects.
type ToolManifestEntry =
    { Name: string
      Description: string option
      Parameters: string option }

/// The registry's self-description: every registered tool, in registration order, with no
/// vendor-specific wrapping — a host adapts this shape to whatever function-calling API its LLM
/// expects.
type ToolManifest = { Tools: ToolManifestEntry list }

/// Registers named tools and resolves them by name. Add-or-replace by design (mirrors slice-1's
/// `IHookStore.Register` semantics for re-sends). `metadata` is advisory: omitting it (or leaving
/// its fields `None`) still registers and routes the tool identically — it only affects what
/// `Manifest` reports for that name.
type IToolRegistry =
    abstract Register: name: ToolName * tool: Tool * ?metadata: ToolMetadata -> unit
    abstract TryResolve: name: ToolName -> Tool voption
    abstract Manifest: unit -> ToolManifest

/// Default `IToolRegistry`: a `ConcurrentDictionary` keyed by `ToolName`. Mirrors
/// `InMemoryHookStore`'s shape, but keyed by name (stable across sends) rather than by per-button
/// `CallbackToken`. Registration order is tracked separately (`order`, guarded by `orderGate`) so
/// `Manifest` reports tools in a stable, deterministic sequence rather than whatever order the
/// underlying `ConcurrentDictionary` happens to enumerate; re-registering an already-known name
/// replaces its tool/metadata in place without moving or duplicating its position.
type InMemoryToolRegistry() =
    let tools = ConcurrentDictionary<ToolName, Tool>()
    let metadataByName = ConcurrentDictionary<ToolName, ToolMetadata option>()
    let order = ResizeArray<ToolName>()
    let orderGate = obj ()

    interface IToolRegistry with
        member _.Register(name: ToolName, tool: Tool, ?metadata: ToolMetadata) : unit =
            let isNewName = not (tools.ContainsKey name)
            tools[name] <- tool
            metadataByName[name] <- metadata

            if isNewName then
                lock orderGate (fun () -> order.Add name)

        member _.TryResolve(name: ToolName) : Tool voption =
            match tools.TryGetValue name with
            | true, tool -> ValueSome tool
            | false, _ -> ValueNone

        member _.Manifest() : ToolManifest =
            let registeredNames = lock orderGate (fun () -> order |> List.ofSeq)

            let entries =
                registeredNames
                |> List.map (fun toolName ->
                    let metadata =
                        match metadataByName.TryGetValue toolName with
                        | true, m -> m
                        | false, _ -> None

                    { Name = ToolName.value toolName
                      Description = metadata |> Option.bind (fun m -> m.Description)
                      Parameters = metadata |> Option.bind (fun m -> m.ArgSchema) })

            { Tools = entries }

/// Serializable button->tool bindings. In-memory default here; a durable (file-based)
/// implementation lives in the separate `TgLLM.Persistence` leaf project (Core stays
/// IO-agnostic, Principle III).
type IBindingStore =
    abstract Save: bindings: IReadOnlyList<ToolBinding> * ct: CancellationToken -> ValueTask
    abstract TryGet: token: CallbackToken * ct: CancellationToken -> ValueTask<ToolBinding voption>
    abstract Remove: tokens: IReadOnlyList<CallbackToken> * ct: CancellationToken -> ValueTask

    /// Removes every binding whose `ExpiresAt` is `Some` and no longer live as of `now` (per
    /// `Expiry.isLive` — the boundary instant itself already counts as expired), returning the
    /// count removed. A binding with `ExpiresAt = None` is never touched. Without eviction a
    /// long-lived bot grows this store unbounded. Callers (a periodic sweep) decide WHEN to call
    /// this; the store only decides WHICH bindings are stale as of the given instant.
    abstract EvictExpired: now: DateTimeOffset -> ValueTask<int>

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

        member _.EvictExpired(now: DateTimeOffset) : ValueTask<int> =
            let expiredTokens =
                bindings
                |> Seq.choose (fun kv -> if Expiry.isLive now kv.Value.ExpiresAt then None else Some kv.Key)
                |> Seq.toList

            for token in expiredTokens do
                bindings.TryRemove(token) |> ignore

            ValueTask.FromResult(List.length expiredTokens)

/// Periodically calls `IBindingStore.EvictExpired` so a long-lived bot's binding store doesn't
/// grow unbounded: `EvictExpired` itself has no other production caller, so without a background
/// sweeper, an expiring/expired binding accumulates forever regardless of which store backs a bot.
/// Owns its own background loop and `CancellationTokenSource`, STARTED at
/// construction and stopped via `IAsyncDisposable.DisposeAsync` — mirrors
/// `PerChatChannelDispatcher`'s "start eagerly, dispose cleanly" shape, and is wired into every
/// bot's lifecycle the same way (`TgBot.wireBot`, TgLLM.FSharp/TgBot.fs): started alongside the
/// update-ingestion run loop, disposed alongside it. `clock` is the SAME injected `Clock` the rest
/// of a bot's expiry/dedup decisions use (never ambient `DateTimeOffset.UtcNow`), so a host that
/// overrides it for deterministic tests gets a deterministic sweep too. `interval` defaults to 5
/// minutes — frequent enough that a long-lived bot's store never accumulates an unbounded number
/// of stale rows, infrequent enough to be a non-issue for any store's `EvictExpired` cost.
[<Sealed>]
type BindingEvictionSweeper(store: IBindingStore, clock: Clock, ?interval: TimeSpan) =
    let interval = defaultArg interval (TimeSpan.FromMinutes 5.0)
    let cts = new CancellationTokenSource()

    /// The background loop itself: sweeps once every `interval`, forever, until `cts` is
    /// cancelled (by `DisposeAsync`) — `Task.Delay` throwing `OperationCanceledException` is how
    /// this loop learns to stop, same idiom `UpdateProcessor`'s own watchdog task uses for a
    /// single-shot delay.
    let loopTask: Task =
        task {
            try
                while true do
                    do! Task.Delay(interval, cts.Token)
                    let! _ = store.EvictExpired(clock ())
                    ()
            with :? OperationCanceledException ->
                ()
        }

    /// Runs ONE sweep pass immediately — the exact operation the background loop above repeats
    /// every `interval`. Exposed directly so a test can drive the sweep deterministically (a fixed
    /// clock advanced past an expiry), without waiting on the real interval.
    member _.SweepOnce() : Task<int> = (store.EvictExpired(clock ())).AsTask()

    interface IAsyncDisposable with
        member _.DisposeAsync() : ValueTask =
            task {
                cts.Cancel()

                try
                    do! loopTask
                with _ ->
                    ()

                cts.Dispose()
            }
            |> ValueTask

/// Tracks which `CallbackToken`s are currently live for a sent/edited message's keyboard, so a
/// re-render (`ctx.EditKeyboardAsync`, e.g. a paginator/counter tool) can remove its OWN previous
/// bindings from the durable store instead of leaking one row per edit forever. `Record` is called
/// AFTER a send/edit reaches the wire — the map only ever reflects PAST sends, so it can never
/// affect the request that is currently producing it.
///
/// Keyed by `(ChatId, MessageId)`, NOT `MessageId` alone: Telegram's `message_id` is unique only
/// PER CHAT, so two different
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
///
/// Eviction seam: a long-lived bot otherwise grows
/// this index unbounded — one entry per DISTINCT `(chat, messageId)` ever recorded, forever.
/// `capacity` bounds the number of distinct keys tracked; once exceeded, the OLDEST-recorded
/// distinct key is dropped first (FIFO by first sight). Re-recording an ALREADY-tracked key (a
/// paginator/counter tool re-rendering the SAME message many times) does not itself consume
/// capacity or move that key's position — the growth this bounds is the number of distinct
/// messages ever seen, not the number of edits to any one of them, since a repeatedly-edited
/// single message is already just one entry, overwritten in place. Dropping the oldest entry only
/// means a FUTURE `ctx.EditKeyboardAsync` on that long-since-touched message won't find (and
/// therefore won't clean up) its previous bindings — the same category of accepted limitation as
/// the restart gap above, not a correctness issue for in-flight presses.
[<Sealed>]
type MessageBindingTracker(?capacity: int) =
    let capacity = defaultArg capacity 10_000
    let tokensByMessage = ConcurrentDictionary<ChatId * MessageId, CallbackToken list>()
    let gate = obj ()
    /// Insertion order of DISTINCT keys only — a re-`Record` of an already-tracked key is an
    /// overwrite, not a fresh insertion, so it is never enqueued again (see the type-level comment
    /// above for why that is the correct policy here, unlike `ProcessedQueryTracker`'s TTL-refresh
    /// re-enqueueing).
    let order = Queue<ChatId * MessageId>()

    let evictOverCapacity () =
        while tokensByMessage.Count > capacity && order.Count > 0 do
            let oldestKey = order.Dequeue()
            tokensByMessage.TryRemove(oldestKey) |> ignore

    /// Record (or overwrite) the tokens currently live for `(chat, messageId)`.
    member _.Record(chat: ChatId, messageId: MessageId, tokens: CallbackToken list) : unit =
        let key = (chat, messageId)
        let isNewKey = not (tokensByMessage.ContainsKey key)
        tokensByMessage[key] <- tokens

        if isNewKey then
            lock gate (fun () ->
                order.Enqueue key
                evictOverCapacity ())

    /// `(chat, messageId)`'s previously recorded tokens, if this tracker has ever recorded a
    /// keyboard for it. A `messageId` recorded for a DIFFERENT chat is a miss, never a hit.
    member _.TryGetPrevious(chat: ChatId, messageId: MessageId) : CallbackToken list option =
        match tokensByMessage.TryGetValue((chat, messageId)) with
        | true, tokens -> Some tokens
        | false, _ -> None

/// At-most-once redelivery dedup: a bounded, TTL'd set of recently-processed
/// `callback_query.id`s. `TryBegin` is `true` the FIRST time an id is seen — safe to process — and
/// `false` on any repeat within the TTL (Telegram, and this library's own webhook transport under
/// retry, can redeliver an update; a repeat resolves as "already done", never a second tool
/// invocation). This dedupes by QUERY id, not by user re-tap: rapid but DISTINCT taps get distinct
/// ids and each still runs the tool (conflating the two would break legitimately-repeatable menu
/// buttons — see `SingleUse` on `ToolBinding` for the actual confirm-once mode).
///
/// `clock` is a required constructor parameter (never ambient `DateTimeOffset.Now`), matching
/// `Expiry.isLive`'s own contract. Guarded by a plain `lock` rather than
/// `ConcurrentDictionary.AddOrUpdate`: that method's update delegate can run MORE than once under
/// contention, which would make the "is this the first sighting?" decision itself racy — exactly
/// the thing this type exists to get right. A single lock keeps read-decide-write atomic, which
/// matters more here than lock-free throughput for a per-callback-query bookkeeping structure.
[<Sealed>]
type ProcessedQueryTracker(clock: Clock, ?capacity: int, ?ttl: TimeSpan) =
    let capacity = defaultArg capacity 10_000
    let ttl = defaultArg ttl (TimeSpan.FromMinutes 5.0)
    let gate = obj ()
    let seenAt = Dictionary<string, DateTimeOffset>()
    /// Insertion order, paired with the timestamp recorded AT that insertion — used to evict the
    /// oldest entries once `capacity` is exceeded. A repeat past its TTL re-enqueues (with a fresh
    /// timestamp), so an id can appear more than once here; eviction below only actually removes
    /// `seenAt`'s entry when the dequeued (id, timestamp) pair is STILL the current one for that
    /// id — an earlier, since-refreshed sighting is a harmless no-op, not a wrongful eviction of
    /// the fresh one.
    let order = Queue<string * DateTimeOffset>()

    /// Drops the oldest entries (FIFO by insertion) until back at `capacity`. Only ever called
    /// under `gate`.
    let evictOverCapacity () =
        while seenAt.Count > capacity && order.Count > 0 do
            let oldestId, oldestSeenAt = order.Dequeue()

            match seenAt.TryGetValue oldestId with
            | true, currentSeenAt when currentSeenAt = oldestSeenAt -> seenAt.Remove oldestId |> ignore
            | _ -> () // superseded by a later (TTL-refreshed) sighting, which owns its own queue entry

    /// First time `queryId` is seen — or its previous sighting has aged out past `ttl` — records it
    /// and returns `true` (safe to process). A repeat within `ttl` returns `false` (drop; this is a
    /// redelivery of an already-processed callback query).
    ///
    /// Deliberate at-most-once trade-off: `queryId` is committed as "seen" the instant this returns
    /// `true` — BEFORE the caller (`UpdateProcessor.processPress`) has actually resolved, run, or
    /// acked anything for it. If that first attempt then fails transiently (the exact reason
    /// Telegram, or this library's own webhook transport, redelivers a query in the first place),
    /// the redelivery is dropped here just the same as an ordinary duplicate — this tracker has no
    /// way to distinguish "already succeeded" from "already attempted, but failed" once `TryBegin`
    /// has committed. Accepted rather than fixed: the alternative (committing only after success)
    /// would let two redeliveries run the SAME callback query concurrently before either finishes,
    /// risking a genuine double-run of a tool — worse than occasionally swallowing a redelivery
    /// that could have retried a transient failure.
    member _.TryBegin(queryId: string) : bool =
        let now = clock ()

        lock gate (fun () ->
            match seenAt.TryGetValue queryId with
            | true, seenTime when now - seenTime < ttl -> false
            | _ ->
                seenAt[queryId] <- now
                order.Enqueue(queryId, now)
                evictOverCapacity ()
                true)

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
    /// Some messageId` additionally removes `(chat, messageId)`'s previously-tracked bindings (the
    /// edit path: a tool that re-renders its own keyboard, e.g. a paginator/counter, must not leak
    /// one row per edit); `None` skips this (a fresh send has nothing stale to remove).
    ///
    /// Ordering: the OLD tokens are removed only AFTER `send` has succeeded — never
    /// before. Removing them first (the original ordering) meant a failed edit left the OLD
    /// keyboard visibly on the wire (Telegram never applied the failed edit) with its bindings
    /// already erased from the store — a stranded live keyboard whose buttons ack but resolve to
    /// nothing. If `send` throws, this now COMPENSATES by removing the just-saved NEW bindings
    /// (nothing on the wire ever pointed to them, since the edit that would have shown them never
    /// reached Telegram) before re-raising, so a failed delivery leaves neither a stranded keyboard
    /// nor an orphaned binding — then the original exception propagates unchanged. `chat` is
    /// required (not bundled into `staleMessageId`) because `MessageId` alone is ambiguous across
    /// chats — every caller already has a `ChatId` in hand (the press's own chat, or the chat a
    /// fresh send targets). `tracker.Record` runs only AFTER `send` completes, matching
    /// `MessageBindingTracker`'s own "reflects past sends only" contract. An invalid `plan` is a
    /// programmer error by the caller (Always-Rule 6) — fails fast (`invalidArg`, tagged with
    /// `context` for a useful message) rather than threading a `Result` through this API.
    ///
    /// `owner`/`deniedNotice`/`expiresAt`/`singleUse` apply uniformly to EVERY tool binding this
    /// one call produces — a keyboard has ONE owner scope, ONE expiry, and ONE single-use flag,
    /// shared by all its tool buttons, not a per-button setting (`ToolPlan.plan` itself stays
    /// agnostic to all four, defaulting every binding to `Anyone`/`None`/`None`/`false`; this is a
    /// deliberate post-processing step here rather than a `ToolPlan.plan` parameter, so `plan`'s own
    /// signature — and every already-green call site, including the FsCheck property suites — stays
    /// untouched). `TgBot.SendKeyboardPlan` passes the host's chosen scope/expiry/single-use;
    /// `UpdateProcessor`'s edit-in-place wiring (`ctx.EditKeyboardAsync`) passes
    /// `Anyone`/`None`/`None`/`false`, since a tool's own replacement keyboard carries none of these
    /// send-time options.
    let deliver
        (context: string)
        (tokenGen: unit -> CallbackToken)
        (store: IBindingStore)
        (tracker: MessageBindingTracker)
        (chat: ChatId)
        (staleMessageId: MessageId option)
        (owner: OwnerScope)
        (deniedNotice: string option)
        (expiresAt: DateTimeOffset option)
        (singleUse: bool)
        (send: RegisteredKeyboard -> Task<MessageId>)
        (ct: CancellationToken)
        (plan: ToolKeyboard)
        : Task<MessageId> =
        task {
            match ToolPlan.plan (Seq.initInfinite (fun _ -> tokenGen ())) plan with
            | Error e -> return invalidArg (nameof plan) $"{context}: invalid plan ({e})"
            | Ok(registeredKeyboard, unscopedBindings) ->
                let bindings =
                    unscopedBindings
                    |> List.map (fun b ->
                        { b with
                            Owner = owner
                            DeniedNotice = deniedNotice
                            ExpiresAt = expiresAt
                            SingleUse = singleUse })

                do! store.Save(bindings, ct)

                let! messageId =
                    task {
                        try
                            return! send registeredKeyboard
                        with ex ->
                            // Compensate: the edit never reached Telegram, so these just-saved
                            // bindings are unreachable by any live keyboard — remove them rather
                            // than leaking them forever, then propagate the original failure.
                            do! store.Remove(bindings |> List.map (fun b -> b.Token), ct)
                            return raise ex
                    }

                match staleMessageId |> Option.bind (fun messageId -> tracker.TryGetPrevious(chat, messageId)) with
                | Some staleTokens -> do! store.Remove(staleTokens, ct)
                | None -> ()

                tracker.Record(chat, messageId, bindings |> List.map (fun b -> b.Token))
                return messageId
        }
