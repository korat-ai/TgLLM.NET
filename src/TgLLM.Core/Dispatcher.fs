namespace TgLLM.Core

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks

/// Default `IHookObserver`: silently drops everything. Façades bridge a real observer to
/// `ILogger`; Core ships this as the dependency-free default so a consumer never has to supply
/// one just to get started.
type NoopHookObserver() =
    interface IHookObserver with
        member _.OnHookFailed(_press: ButtonPress, _error: exn) = ()
        member _.OnUnknownToken(_press: ButtonPress) = ()
        member _.OnEditFailed(_press: ButtonPress, _reason: string) = ()
        member _.OnRunLoopFailed(_error: exn) = ()

/// What a per-chat consumer's wait for its next item resolved to (`PerChatChannelDispatcher`'s own
/// `waitForWork`, below) — a private implementation detail, not part of any public contract.
type private WaitOutcome =
    /// An item is ready to read right now.
    | HasWork
    /// The channel was completed (by `DisposeAsync`, at shutdown) AND fully drained — the
    /// consumer's normal, permanent end.
    | ChannelDone
    /// No item arrived within the configured idle deadline, and the channel is NOT completed —
    /// this chat is idle; its resources are eligible for reclaim (US4, FR-012).
    | IdleTimedOut

/// Default `IPressDispatcher`: one unbounded, `SingleReader = true` channel + one consumer loop
/// per chat, in a `ConcurrentDictionary`. Work for the SAME chat runs sequentially in enqueue
/// order (FIFO); DIFFERENT chats run concurrently.
///
/// A work item that throws is caught right here so the chat's consumer loop survives.
/// Note this dispatcher has no `IHookObserver` dependency: `work` is an opaque
/// `CancellationToken -> Task` thunk with no `ButtonPress` attached, so there is nothing
/// meaningful to attribute a failure to at this layer. The *attributed* reporting
/// (`IHookObserver.OnHookFailed`, which needs `ButtonPress` context) happens one layer up, in
/// `UpdateProcessor`, which wraps the hook invocation in its own try/with before ever handing the
/// resulting thunk to `Enqueue`. This dispatcher's catch is a structural safety net only.
///
/// `idleTimeout` (US4, FR-012): `None` (the default) means a chat's channel/consumer lives for the
/// dispatcher's WHOLE lifetime — exactly slice-1 behavior, and what every caller that doesn't pass
/// it explicitly still gets. `Some` deadline reclaims a chat's resources once no new work has
/// arrived for that long (SC-007), so a long-lived bot serving many short-lived chats doesn't grow
/// this dictionary unbounded. Reclaim never drops or reorders in-flight/buffered work: the idle
/// wait can only fire while the consumer has NOTHING buffered (an item's arrival always wins the
/// race, per `waitForWork` below), and the channel's writer is completed (rejecting only FUTURE
/// writes, per `Channel<T>`'s own contract) before anything already buffered is drained.
type PerChatChannelDispatcher(?shutdownBudget: TimeSpan, ?idleTimeout: TimeSpan) =
    let channels = ConcurrentDictionary<ChatId, Channel<CancellationToken -> Task> * Task>()
    let cts = new CancellationTokenSource()

    /// Default 30s — the host shutdown timeout. Tests override it to keep the
    /// fallback-cancellation path fast.
    let shutdownBudget = defaultArg shutdownBudget (TimeSpan.FromSeconds 30.0)

    /// Waits for this chat's next item, distinguishing "an item arrived" from "idle beyond the
    /// deadline" from "shut down" — see `WaitOutcome`'s own doc comment. With no `idleTimeout`
    /// configured, this is exactly the original (pre-US4) `WaitToReadAsync` wait, unchanged.
    let waitForWork (reader: ChannelReader<CancellationToken -> Task>) : Task<WaitOutcome> =
        match idleTimeout with
        | None ->
            task {
                let! canRead = reader.WaitToReadAsync(cts.Token)
                return if canRead then HasWork else ChannelDone
            }
        | Some timeout ->
            task {
                use idleCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token)
                idleCts.CancelAfter(timeout)

                try
                    let! canRead = reader.WaitToReadAsync(idleCts.Token)
                    return if canRead then HasWork else ChannelDone
                with :? OperationCanceledException ->
                    // Distinguish WHICH token fired: the overall dispatcher shutting down (`cts`)
                    // ends this consumer for good, same as `ChannelDone`; the idle-only `idleCts`
                    // firing on its own means nothing but time has passed — idle, not shutdown.
                    return if cts.IsCancellationRequested then ChannelDone else IdleTimedOut
            }

    // Hand-rolled `WaitToReadAsync`/`TryRead` consumer loop rather than `IAsyncEnumerable`/
    // `TaskSeq` (Core stays at FSharp.Core + FSharp.UMX only).
    let consume (chat: ChatId) (channel: Channel<CancellationToken -> Task>) : Task =
        let reader = channel.Reader

        let runBuffered () : Task =
            task {
                let mutable hasWork = true

                while hasWork do
                    match reader.TryRead() with
                    | true, work ->
                        try
                            do! work cts.Token
                        with _ ->
                            () // keep the chat's loop alive; see the type-level comment above.
                    | false, _ -> hasWork <- false
            }

        task {
            try
                let mutable keepReading = true

                while keepReading do
                    let! outcome = waitForWork reader

                    match outcome with
                    | ChannelDone -> keepReading <- false
                    | HasWork -> do! runBuffered ()
                    | IdleTimedOut ->
                        // Reclaim (US4, FR-012): complete the writer FIRST — this only rejects
                        // FUTURE writes (`Channel<T>`'s own contract), so anything that raced in
                        // just before this point is still buffered and safely readable below.
                        // `Enqueue` retries against a freshly (re)created channel if it instead
                        // raced in AFTER completion (`ChannelClosedException`).
                        channel.Writer.TryComplete() |> ignore
                        do! runBuffered () // drain anything that raced in before completion won
                        channels.TryRemove(chat) |> ignore
                        keepReading <- false
            with :? System.OperationCanceledException ->
                ()
        }

    let getOrAddChannel (chat: ChatId) : Channel<CancellationToken -> Task> =
        let channel, _ =
            channels.GetOrAdd(
                chat,
                (fun _ ->
                    let options = UnboundedChannelOptions(SingleReader = true)
                    let channel = Channel.CreateUnbounded<CancellationToken -> Task>(options)
                    channel, consume chat channel)
            )

        channel

    /// The number of chats currently holding a live channel/consumer — a concrete-type-only
    /// testability hook (not part of `IPressDispatcher`), so idle reclaim (FR-012) is directly
    /// observable rather than inferred from side effects alone.
    member _.ActiveChatCount: int = channels.Count

    interface IPressDispatcher with
        member _.Enqueue(chat: ChatId, work: CancellationToken -> Task) : ValueTask =
            ValueTask(
                task {
                    let mutable posted = false

                    while not posted do
                        let channel = getOrAddChannel chat

                        try
                            do! channel.Writer.WriteAsync(work, cts.Token)
                            posted <- true
                        with :? ChannelClosedException ->
                            // Raced with an idle-reclaim pass completing this chat's channel
                            // between lookup and write — retry against a freshly (re)created one;
                            // never silently drops the work item.
                            ()
                }
            )

        member _.DisposeAsync() : ValueTask =
            ValueTask(
                task {
                    // Signal "no more work is coming" so each chat's consumer loop drains whatever is
                    // already queued and then completes naturally — a completed-but-non-empty channel
                    // still yields its buffered items via `WaitToReadAsync`/`TryRead`, no cancellation
                    // needed for that. Await the drain FIRST (bounded by the host shutdown budget),
                    // and only cancel `cts` afterwards, as the fallback that
                    // stops any work that's still running (or a consumer that's still draining) past
                    // the budget. Cancelling BEFORE awaiting would hand an already-cancelled token to
                    // queued-but-not-yet-started work, aborting it instead of draining it (the bug this
                    // ordering fixes: a consumer parked on `WaitToReadAsync(cts.Token)` with buffered
                    // items must not be cancelled out from under it).
                    for KeyValue(_, (channel, _)) in channels do
                        channel.Writer.TryComplete() |> ignore

                    let allDrained = Task.WhenAll [| for KeyValue(_, (_, consumerTask)) in channels -> consumerTask |]
                    do! Task.WhenAny(allDrained, Task.Delay shutdownBudget) :> Task

                    // Fallback: cancel anything still running/still draining past the budget, then
                    // give it one more (unbounded) chance to unwind before disposing `cts`.
                    cts.Cancel()

                    for KeyValue(_, (_, consumerTask)) in channels do
                        try
                            do! consumerTask
                        with _ ->
                            ()

                    cts.Dispose()
                }
            )
