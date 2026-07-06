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
    /// isolation. `processPress` (below) actually CONSULTS this — matching jointly on
    /// `(ackPolicy resolution, resolution)` — rather than branching on `resolution` alone, so a
    /// future change to this mapping that isn't mirrored in `processPress`'s branches (or vice
    /// versa) fails loudly at runtime (an `unreachable` case) instead of silently drifting.
    let ackPolicy (resolution: PressResolution) : AckPolicy =
        match resolution with
        | ToolResolution _ -> Deferred
        | HookStoreResolution -> AckFirst

/// Shared "ack exactly once" state for the Tool Router's deferred-ack path (review #3). Built and
/// STARTED at ENQUEUE time (press arrival) by `UpdateProcessor.processPress`'s `Deferred` branch,
/// rather than inside the enqueued work thunk itself (`buildToolWork`) — a tool queued behind a
/// slow tool on the SAME chat previously inherited that queue wait before its own watchdog clock
/// even started (the thunk — and therefore the watchdog it used to create — only ran once the
/// per-chat dispatcher got to it), silently blowing the client's spinner budget regardless of the
/// budget's own configured value. Starting the watchdog here instead means its countdown begins
/// the instant the press is resolved, independent of how deep its chat's queue is; the tool's own
/// directive can still win the ack race via the same `Interlocked`-CAS guard as before — nothing
/// about "the tool's own `ctx.Answer` wins if it finishes first" changes, only WHEN the race
/// starts. `[<NoComparison; NoEquality>]`: like `HookBinding`/`RouteDecision`, every field here is
/// function-valued, so structural equality/comparison can't be derived (and nothing ever needs
/// it).
[<NoComparison; NoEquality>]
type private DeferredAckState =
    { /// Backs `PressContext.Answer` — records the tool's ack directive; does not itself send it.
      AnswerAction: string -> bool -> unit
      /// Sends the ack exactly once (the tool's directive if `AnswerAction` was called, an empty
      /// default otherwise) — safe to call from both the watchdog and the tool's own completion,
      /// racing harmlessly via the internal `Interlocked` guard.
      SendAckOnce: unit -> Task
      /// Stops the watchdog and awaits its graceful unwind — call ONCE, after the tool itself has
      /// finished running (whether it won or lost the ack race).
      Finish: unit -> Task }

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

    /// Builds and STARTS a `DeferredAckState` for `press` (review #3) — called at ENQUEUE time,
    /// before the tool's work is ever handed to `dispatcher.Enqueue`, so the watchdog's countdown
    /// begins at press arrival regardless of how deep the chat's queue is. The watchdog races the
    /// tool so a slow tool can't blow the client's spinner budget; it does NOT cancel the tool —
    /// only the ack is defaulted if the watchdog wins, the tool keeps running to completion
    /// regardless. An `Interlocked` guard (inside `sendAckOnce`) makes "exactly one ack" true by
    /// construction; the try/with around the actual API call is a defensive backstop for the real
    /// Bot API rejecting a stale/duplicate answer server-side (`"query ID is invalid"`), which this
    /// in-process guard already makes practically unreachable. `ct` is the OVERALL processor's own
    /// token (`RunAsync`'s), not the per-invocation token the dispatcher later hands the work
    /// thunk — the only one available at enqueue time, and the correct scope for "stop counting
    /// down if the whole processor is shutting down" anyway.
    let startDeferredAck (press: ButtonPress) (ct: CancellationToken) : DeferredAckState =
        let mutable ackText: string option = None
        let mutable ackAlert = false
        let mutable acked = 0
        let watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(ct)

        let sendAckOnce () : Task =
            task {
                if Interlocked.CompareExchange(&acked, 1, 0) = 0 then
                    try
                        do! api.AnswerCallback(press.QueryId, ackText, ackAlert, ct)
                    with _ ->
                        () // a losing/late ack call can legitimately fail server-side; swallow.
            }

        let watchdogTask: Task =
            task {
                try
                    do! Task.Delay(watchdogBudget, watchdogCts.Token)
                    do! sendAckOnce ()
                with :? OperationCanceledException ->
                    ()
            }

        { AnswerAction =
            fun text alert ->
                ackText <- Some text
                ackAlert <- alert
          SendAckOnce = sendAckOnce
          Finish =
            fun () ->
                task {
                    // The tool is done (whether it won or lost the ack race) — stop the timer.
                    watchdogCts.Cancel()

                    try
                        do! watchdogTask
                    with :? OperationCanceledException ->
                        ()

                    watchdogCts.Dispose()
                } }

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
            press.Chat
            (Some press.MessageId)
            (fun registeredKeyboard ->
                task {
                    do! api.EditMessageReplyMarkup(press.Chat, press.MessageId, Some registeredKeyboard, workCt)
                    return press.MessageId
                })
            workCt
            plan
        :> Task

    /// The enqueued work itself: runs the tool, then sends its (or the watchdog's) ack and stops
    /// the watchdog. `ackState` was already built and STARTED at ENQUEUE time
    /// (`startDeferredAck`, called from `processPress`'s `Deferred` branch) — this thunk only runs
    /// once the per-chat dispatcher gets to it, which may be well after the watchdog's countdown
    /// already began (review #3).
    let buildToolWork
        (press: ButtonPress)
        (tool: Tool)
        (binding: ToolBinding)
        (dispatch: ToolDispatch)
        (ackState: DeferredAckState)
        : CancellationToken -> Task =
        fun workCt ->
            task {
                let context =
                    PressContext(
                        press.ButtonLabel,
                        press.Chat,
                        press.User,
                        press.MessageId,
                        workCt,
                        makeReplyTextAsync press workCt,
                        ?arg = binding.Arg,
                        answerAction = ackState.AnswerAction,
                        editTextAction = makeEditTextAction press workCt,
                        editKeyboardAction = makeEditKeyboardAction press dispatch workCt
                    )

                try
                    do! tool context
                with ex ->
                    observer.OnHookFailed(press, ex)

                do! ackState.SendAckOnce()
                do! ackState.Finish()
            }

    /// The ack-only path (review #8, folded into the Foundational phase): a callback query the
    /// transport could not map to a `ButtonPress` at all (non-canonical `Data`, or no originating
    /// `Message`) still gets exactly one `AnswerCallback` — no hook/tool ever runs, since there is
    /// nothing resolvable here. Unlike `processHookStorePress`'s unknown-token outcome, there is no
    /// `ButtonPress` to attribute a failure to (or report via `IHookObserver.OnUnknownToken`, which
    /// requires one) — a failing ack itself is swallowed, matching the same defensive convention
    /// `buildToolWork`'s `sendAckOnce` already uses for a losing/late ack call.
    let processAcknowledgeOnly (ct: CancellationToken) (queryId: CallbackQueryId) : Task =
        task {
            try
                do! api.AnswerCallback(queryId, ct)
            with _ ->
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

    /// Dispatches on BOTH the resolution AND its `PressResolution.ackPolicy` jointly — not just the
    /// resolution alone — so the policy mapping is actually consulted,
    /// not merely a parallel, never-read spec. The two "impossible" combinations
    /// (`Deferred`+`HookStoreResolution`, `AckFirst`+`ToolResolution`) can only fire if
    /// `PressResolution.ackPolicy`'s mapping is ever changed without updating the branches below (or
    /// vice versa) — exactly the drift `ackPolicy`'s own doc comment claims can't happen; this match
    /// makes that claim literally enforced: such a drift now fails LOUDLY at runtime instead of
    /// silently doing the wrong thing.
    let processPress (ct: CancellationToken) (press: ButtonPress) : Task =
        task {
            let! resolution = resolvePress ct press

            match PressResolution.ackPolicy resolution, resolution with
            | Deferred, ToolResolution(tool, binding, dispatch) ->
                // Start the watchdog NOW (press arrival) — review #3 — not once the per-chat
                // dispatcher eventually RUNS the enqueued work; see `startDeferredAck`'s own doc
                // comment for why.
                let ackState = startDeferredAck press ct
                do! dispatcher.Enqueue(press.Chat, buildToolWork press tool binding dispatch ackState)
            | AckFirst, HookStoreResolution -> do! processHookStorePress ct press
            | Deferred, HookStoreResolution
            | AckFirst, ToolResolution _ ->
                failwith $"unreachable: PressResolution.ackPolicy diverged from processPress's own branches for %A{resolution}"
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
                    | ButtonPressed press ->
                        try
                            do! processPress ct press
                        with ex ->
                            // One press's processing must never take down the WHOLE run loop — e.g.
                            // `AnswerCallback` itself throwing on the ack-first path, before any
                            // hook/tool ever runs. Reuses `OnHookFailed`:
                            // from the observer's point of view "this press's processing blew up"
                            // is the same signal regardless of whether the failure came from a hook
                            // body (already caught inside `buildWork`/`buildToolWork`) or from
                            // `processPress` itself.
                            observer.OnHookFailed(press, ex)
                    | AckOnly queryId -> do! processAcknowledgeOnly ct queryId
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
