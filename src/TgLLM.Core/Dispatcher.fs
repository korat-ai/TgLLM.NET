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
type PerChatChannelDispatcher(?shutdownBudget: TimeSpan) =
    let channels = ConcurrentDictionary<ChatId, Channel<CancellationToken -> Task> * Task>()
    let cts = new CancellationTokenSource()

    /// Default 30s — the host shutdown timeout. Tests override it to keep the
    /// fallback-cancellation path fast.
    let shutdownBudget = defaultArg shutdownBudget (TimeSpan.FromSeconds 30.0)

    // Hand-rolled `WaitToReadAsync`/`TryRead` consumer loop rather than `IAsyncEnumerable`/
    // `TaskSeq` (Core stays at FSharp.Core + FSharp.UMX only).
    let consume (reader: ChannelReader<CancellationToken -> Task>) : Task =
        task {
            try
                let mutable keepReading = true

                while keepReading do
                    let! canRead = reader.WaitToReadAsync(cts.Token)

                    if not canRead then
                        keepReading <- false
                    else
                        let mutable hasWork = true

                        while hasWork do
                            match reader.TryRead() with
                            | true, work ->
                                try
                                    do! work cts.Token
                                with _ ->
                                    () // keep the chat's loop alive; see the type-level comment above.
                            | false, _ -> hasWork <- false
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
                    channel, consume channel.Reader)
            )

        channel

    interface IPressDispatcher with
        member _.Enqueue(chat: ChatId, work: CancellationToken -> Task) : ValueTask =
            let channel = getOrAddChannel chat
            channel.Writer.WriteAsync(work, cts.Token)

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
