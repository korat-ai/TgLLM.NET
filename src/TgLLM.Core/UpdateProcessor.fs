namespace TgLLM.Core

open System.Threading
open System.Threading.Tasks

/// T022 (contracts/core-ports.md "UpdateProcessor", data-model.md "Inbound: a press —
/// ack-first"). `PressContext` itself lives in Domain.fs — see the compile-order comment in
/// TgLLM.Core.fsproj for why.
[<Sealed>]
type UpdateProcessor
    (
        source: IUpdateSource,
        store: IHookStore,
        api: IBotApiClient,
        dispatcher: IPressDispatcher,
        observer: IHookObserver
    ) =

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

    /// Ack-first (SC-003): resolve, then `AnswerCallback` immediately regardless of outcome,
    /// THEN — only if a hook was found — enqueue it. Unknown/stale/malformed tokens are acked
    /// with no hook and no error (FR-010); the observer just hears about it (FR-009).
    let processPress (ct: CancellationToken) (press: ButtonPress) : Task =
        task {
            let! hookOption = store.TryResolve(press.Token, ct)
            let decision = Routing.decide (fun _ -> hookOption) press
            do! api.AnswerCallback(press.QueryId, ct)

            match decision with
            | RunHook hook -> do! dispatcher.Enqueue(press.Chat, buildWork press hook)
            | AcknowledgeOnly -> observer.OnUnknownToken(press)
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
