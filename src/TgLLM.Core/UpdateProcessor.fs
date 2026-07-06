namespace TgLLM.Core

open System
open System.Threading
open System.Threading.Tasks

/// T022 (contracts/core-ports.md "UpdateProcessor", data-model.md "Inbound: a press —
/// ack-first"). `PressContext` itself lives in Domain.fs — see the compile-order comment in
/// TgLLM.Core.fsproj for why.
///
/// Feature 002-llm-tool-router (T016, research.md D6): gains an OPTIONAL `?toolDispatch`
/// collaborator. When present and it resolves a press's token, the press takes the deferred-ack
/// TOOL path (D2) instead of the slice-1 ack-first `IHookStore` path. Slice-1 callers construct this
/// type WITHOUT `toolDispatch` (F# optional parameter), so their behavior is byte-identical to
/// before (FR-012) — `?toolDispatch` defaults to `None`, and every branch below that matters for
/// slice-1 callers is exactly the original `processPress` body, just renamed `processHookStorePress`.
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

    /// Default ~2s (research.md D2): safely under Telegram's unpublished answer-callback deadline,
    /// long enough for ordinary tool work. Tests override it to keep the watchdog path fast.
    let watchdogBudget = defaultArg watchdogBudget (TimeSpan.FromSeconds 2.0)

    /// Builds `PressContext.ReplyTextAsync`'s backing closure. An invalid `text` is a programmer
    /// error by the hook author (Always-Rule 6), not a business error — it fails fast rather
    /// than threading a `Result` through the façades' `Hook` delegate signature.
    let makeReplyTextAsync (press: ButtonPress) (workCt: CancellationToken) (text: string) : Task<MessageId> =
        match MessageText.create text with
        | Ok messageText -> api.SendText(press.Chat, messageText, workCt)
        | Error error -> invalidArg (nameof text) $"PressContext.ReplyTextAsync: invalid reply text ({error})"

    /// The work handed to `IPressDispatcher.Enqueue`: constructs this press's `PressContext` and
    /// runs the hook, catching and reporting any exception (FR-009) so the dispatcher's own
    /// per-chat loop — which has no `ButtonPress` to attribute a failure to — never has to.
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

    /// The deferred-ack TOOL path (feature 002-llm-tool-router, research.md D2/D6): the tool runs
    /// first; the processor answers the callback query EXACTLY ONCE afterwards — the tool's own
    /// directive (via `ctx.Answer`), or an empty default. A watchdog races the tool so a slow tool
    /// can't blow the client's spinner budget (SC-003); it does NOT cancel the tool — only the ack
    /// is defaulted, the tool keeps running to completion regardless. An `Interlocked` guard makes
    /// "exactly one ack" true by construction; the try/with around the actual API call is a
    /// defensive backstop for the real Bot API rejecting a stale/duplicate answer server-side
    /// (`"query ID is invalid"`), which this in-process guard already makes practically unreachable.
    let buildToolWork (press: ButtonPress) (tool: Tool) (binding: ToolBinding) : CancellationToken -> Task =
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
                                () // a losing/late ack call can legitimately fail server-side; swallow (D2).
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
                        answerAction = answerAction
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

    /// Ack-first (SC-003): resolve, then `AnswerCallback` immediately regardless of outcome,
    /// THEN — only if a hook was found — enqueue it. Unknown/stale/malformed tokens are acked
    /// with no hook and no error (FR-010); the observer just hears about it (FR-009). This is
    /// slice-1's original `processPress` body, unchanged (FR-012) — used both when `toolDispatch`
    /// is absent, and as the fallback when it misses (unknown token, or a binding whose tool is no
    /// longer registered).
    let processHookStorePress (ct: CancellationToken) (press: ButtonPress) : Task =
        task {
            let! hookOption = store.TryResolve(press.Token, ct)
            let decision = Routing.decide (fun _ -> hookOption) press
            do! api.AnswerCallback(press.QueryId, ct)

            match decision with
            | RunHook hook -> do! dispatcher.Enqueue(press.Chat, buildWork press hook)
            | AcknowledgeOnly -> observer.OnUnknownToken(press)
        }

    let processPress (ct: CancellationToken) (press: ButtonPress) : Task =
        task {
            match toolDispatch with
            | None -> do! processHookStorePress ct press
            | Some dispatch ->
                let! resolved = dispatch.Resolve(press.Token, ct)

                match resolved with
                | ValueSome(tool, binding) -> do! dispatcher.Enqueue(press.Chat, buildToolWork press tool binding)
                | ValueNone -> do! processHookStorePress ct press
        }

    /// Runs until `ct` is cancelled, processing each `AgentEvent` from `source` in arrival order.
    /// Hand-rolled `IAsyncEnumerator` loop rather than `TaskSeq`/`IAsyncEnumerable` `for`-sugar
    /// (research.md: Core stays at FSharp.Core + FSharp.UMX only), matching Dispatcher.fs.
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

/// Agent-facing send operation, shared by both façades (contracts/core-ports.md "AgentOps").
module AgentOps =

    /// `KeyboardPlan.assign (tokenGen)` before `store.Register` before the actual send: hooks
    /// become resolvable the instant registration completes, strictly before the keyboard can
    /// reach the chat (data-model.md "Outbound: send a keyboard").
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
