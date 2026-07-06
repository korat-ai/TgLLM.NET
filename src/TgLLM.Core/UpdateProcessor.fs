namespace TgLLM.Core

open System
open System.Threading
open System.Threading.Tasks

/// The ack timing a resolved press requires. `AckFirst` acks immediately, before any hook/tool
/// runs — the slice-1 `IHookStore` resolver's policy (including its "unknown/stale token"
/// outcome). `Deferred` acks after the tool runs, or a watchdog fires, whichever is first — the
/// Tool Router resolver's policy, carried out by `UpdateProcessor`'s own `buildToolWork`/
/// `sendAckOnce`.
type AckPolicy =
    | AckFirst
    | Deferred

/// Which resolver a press's token matched, carrying whatever that resolver needs to run the
/// press's work. The Tool Router's resolver (`ToolDispatch`) takes priority; a miss there — or no
/// `ToolDispatch` at all — falls back to the slice-1 `IHookStore` resolver, represented here as
/// `HookStoreResolution` regardless of whether IT then finds a hook (an unknown/stale token is
/// still acked, just with no hook to run — `processHookStorePress` decides that on its own).
[<NoComparison; NoEquality>]
type PressResolution =
    | ToolResolution of tool: Tool * binding: ToolBinding * dispatch: ToolDispatch
    | HookStoreResolution

module PressResolution =
    /// The single, pure statement of "which resolver ⇒ which ack policy" — kept separate from the
    /// async resolve step (`UpdateProcessor`'s own `resolvePress`) so the mapping is testable in
    /// isolation and can't silently drift out of sync with `processPress`'s branches.
    let ackPolicy (resolution: PressResolution) : AckPolicy =
        match resolution with
        | ToolResolution _ -> Deferred
        | HookStoreResolution -> AckFirst

/// The engine that turns an inbound button press into ack + hook/tool execution.
/// `PressContext` itself lives in Domain.fs — see the compile-order comment in
/// TgLLM.Core.fsproj for why.
///
/// Takes an OPTIONAL `?toolDispatch` collaborator. When present and it resolves a press's token,
/// the press takes the deferred-ack TOOL path instead of the slice-1 ack-first `IHookStore` path.
/// Slice-1 callers construct this type WITHOUT `toolDispatch` (F# optional parameter), so their
/// behavior is byte-identical to before — `?toolDispatch` defaults to `None`, and every branch
/// below that matters for slice-1 callers is exactly the original `processPress` body, just
/// renamed `processHookStorePress`.
[<Sealed>]
type UpdateProcessor
    (
        source: IUpdateSource,
        store: IHookStore,
        api: IBotApiClient,
        dispatcher: IPressDispatcher,
        observer: IHookObserver,
        ?toolDispatch: ToolDispatch,
        ?watchdogBudget: TimeSpan
    ) =

    /// Default ~2s: safely under Telegram's unpublished answer-callback deadline, long enough for
    /// ordinary tool work. Tests override it to keep the watchdog path fast.
    let watchdogBudget = defaultArg watchdogBudget (TimeSpan.FromSeconds 2.0)

    /// Builds `PressContext.ReplyTextAsync`'s backing closure. An invalid `text` is a programmer
    /// error by the hook author (Always-Rule 6), not a business error — it fails fast rather
    /// than threading a `Result` through the façades' `Hook` delegate signature.
    let makeReplyTextAsync (press: ButtonPress) (workCt: CancellationToken) (text: string) : Task<MessageId> =
        match MessageText.create text with
        | Ok messageText -> api.SendText(press.Chat, messageText, workCt)
        | Error error -> invalidArg (nameof text) $"PressContext.ReplyTextAsync: invalid reply text ({error})"

    /// The work handed to `IPressDispatcher.Enqueue`: constructs this press's `PressContext` and
    /// runs the hook, catching and reporting any exception so the dispatcher's own per-chat loop
    /// — which has no `ButtonPress` to attribute a failure to — never has to.
    let buildWork (press: ButtonPress) (hook: Hook) : CancellationToken -> Task =
        fun workCt ->
            task {
                let context =
                    PressContext(press.ButtonLabel, press.Chat, press.User, press.MessageId, workCt, makeReplyTextAsync press workCt)

                try
                    do! hook context
                with ex ->
                    observer.OnHookFailed(press, ex)
            }

    /// The deferred-ack TOOL path: the tool runs first; the processor answers the callback query
    /// EXACTLY ONCE afterwards — the tool's own directive (via `ctx.Answer`), or an empty default.
    /// A watchdog races the tool so a slow tool can't blow the client's spinner budget; it does
    /// NOT cancel the tool — only the ack is defaulted, the tool keeps running to completion
    /// regardless. An `Interlocked` guard makes "exactly one ack" true by construction; the
    /// try/with around the actual API call is a defensive backstop for the real Bot API rejecting
    /// a stale/duplicate answer server-side (`"query ID is invalid"`), which this in-process guard
    /// already makes practically unreachable.
    /// `PressContext.EditTextAsync`'s backing closure on the deferred-ack tool path: edits the
    /// PRESSED message's text, leaving its current keyboard untouched (`None`). An invalid `text`
    /// is a programmer error by the tool author (Always-Rule 6), same fail-fast convention as
    /// `makeReplyTextAsync`.
    let makeEditTextAction (press: ButtonPress) (workCt: CancellationToken) (text: string) : Task =
        match MessageText.create text with
        | Ok messageText -> api.EditMessageText(press.Chat, press.MessageId, messageText, None, workCt)
        | Error error -> invalidArg (nameof text) $"PressContext.EditTextAsync: invalid text ({error})"

    /// `PressContext.EditKeyboardAsync`'s backing closure: re-plans the replacement layout and
    /// saves/tracks its bindings via the shared `ToolKeyboardOps.deliver` (Tools.fs) — same
    /// before-the-wire ordering guarantee, and the same stale-binding cleanup, as
    /// `TgBot.SendKeyboardPlan` uses for a fresh send.
    let makeEditKeyboardAction (press: ButtonPress) (dispatch: ToolDispatch) (workCt: CancellationToken) (plan: ToolKeyboard) : Task =
        ToolKeyboardOps.deliver
            "PressContext.EditKeyboardAsync"
            CallbackToken.generate
            dispatch.Store
            dispatch.Tracker
            (Some press.MessageId)
            (fun registeredKeyboard ->
                task {
                    do! api.EditMessageReplyMarkup(press.Chat, press.MessageId, Some registeredKeyboard, workCt)
                    return press.MessageId
                })
            workCt
            plan
        :> Task

    let buildToolWork (press: ButtonPress) (tool: Tool) (binding: ToolBinding) (dispatch: ToolDispatch) : CancellationToken -> Task =
        fun workCt ->
            task {
                let mutable ackText: string option = None
                let mutable ackAlert = false
                let mutable acked = 0

                let sendAckOnce () : Task =
                    task {
                        if Interlocked.CompareExchange(&acked, 1, 0) = 0 then
                            try
                                do! api.AnswerCallback(press.QueryId, ackText, ackAlert, workCt)
                            with _ ->
                                () // a losing/late ack call can legitimately fail server-side; swallow.
                    }

                let answerAction (text: string) (alert: bool) =
                    ackText <- Some text
                    ackAlert <- alert

                let context =
                    PressContext(
                        press.ButtonLabel,
                        press.Chat,
                        press.User,
                        press.MessageId,
                        workCt,
                        makeReplyTextAsync press workCt,
                        ?arg = binding.Arg,
                        answerAction = answerAction,
                        editTextAction = makeEditTextAction press workCt,
                        editKeyboardAction = makeEditKeyboardAction press dispatch workCt
                    )

                let runTool () : Task =
                    task {
                        try
                            do! tool context
                        with ex ->
                            observer.OnHookFailed(press, ex)

                        do! sendAckOnce ()
                    }

                use watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(workCt)

                let watchdog () : Task =
                    task {
                        try
                            do! Task.Delay(watchdogBudget, watchdogCts.Token)
                            do! sendAckOnce ()
                        with :? OperationCanceledException ->
                            ()
                    }

                let toolTask = runTool ()
                let watchdogTask = watchdog ()
                do! toolTask
                // The tool is done (whether it won or lost the ack race) — stop the timer.
                watchdogCts.Cancel()

                try
                    do! watchdogTask
                with :? OperationCanceledException ->
                    ()
            }

    /// Ack-first: resolve, then `AnswerCallback` immediately regardless of outcome, THEN — only if
    /// a hook was found — enqueue it. Unknown/stale/malformed tokens are acked with no hook and no
    /// error; the observer just hears about it. This is slice-1's original `processPress` body,
    /// unchanged — used both when `toolDispatch` is absent, and as the fallback when it misses
    /// (unknown token, or a binding whose tool is no longer registered).
    let processHookStorePress (ct: CancellationToken) (press: ButtonPress) : Task =
        task {
            let! hookOption = store.TryResolve(press.Token, ct)
            let decision = Routing.decide (fun _ -> hookOption) press
            do! api.AnswerCallback(press.QueryId, ct)

            match decision with
            | RunHook hook -> do! dispatcher.Enqueue(press.Chat, buildWork press hook)
            | AcknowledgeOnly -> observer.OnUnknownToken(press)
        }

    /// The single place that decides which resolver a press's token routes through: the Tool
    /// Router's resolver (if `toolDispatch` is wired in) takes priority over the slice-1
    /// `IHookStore` resolver. See `PressResolution.ackPolicy` for the ack policy each outcome
    /// implies — `processPress` below carries it out via whichever branch it dispatches to.
    let resolvePress (ct: CancellationToken) (press: ButtonPress) : Task<PressResolution> =
        task {
            match toolDispatch with
            | None -> return HookStoreResolution
            | Some dispatch ->
                let! resolved = dispatch.Resolve(press.Token, ct)

                match resolved with
                | ValueSome(tool, binding) -> return ToolResolution(tool, binding, dispatch)
                | ValueNone -> return HookStoreResolution
        }

    let processPress (ct: CancellationToken) (press: ButtonPress) : Task =
        task {
            match! resolvePress ct press with
            | ToolResolution(tool, binding, dispatch) -> do! dispatcher.Enqueue(press.Chat, buildToolWork press tool binding dispatch)
            | HookStoreResolution -> do! processHookStorePress ct press
        }

    /// Runs until `ct` is cancelled, processing each `AgentEvent` from `source` in arrival order.
    /// Hand-rolled `IAsyncEnumerator` loop rather than `TaskSeq`/`IAsyncEnumerable` `for`-sugar
    /// (Core stays at FSharp.Core + FSharp.UMX only), matching Dispatcher.fs.
    member _.RunAsync(ct: CancellationToken) : Task =
        task {
            use enumerator = source.Updates(ct).GetAsyncEnumerator(ct)
            let mutable moving = true

            while moving do
                let! hasNext = enumerator.MoveNextAsync()

                if hasNext then
                    match enumerator.Current with
                    | ButtonPressed press -> do! processPress ct press
                else
                    moving <- false
        }

/// Agent-facing send operation, shared by both façades.
module AgentOps =

    /// `KeyboardPlan.assign (tokenGen)` before `store.Register` before the actual send: hooks
    /// become resolvable the instant registration completes, strictly before the keyboard can
    /// reach the chat.
    let sendKeyboard
        (store: IHookStore)
        (api: IBotApiClient)
        (tokenGen: unit -> CallbackToken)
        (chat: ChatId)
        (text: MessageText)
        (spec: KeyboardSpec)
        (ct: CancellationToken)
        : Task<MessageId> =
        task {
            let registeredKeyboard, bindings = KeyboardPlan.assign (Seq.initInfinite (fun _ -> tokenGen ())) spec
            do! store.Register(bindings, ct)
            return! api.SendKeyboard(chat, text, registeredKeyboard, ct)
        }
