namespace TgLLM.Core

open System.Collections.Concurrent
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks

/// Default `IHookObserver` (contracts/core-ports.md): silently drops everything. Façades bridge
/// a real observer to `ILogger` (T044); Core ships this as the dependency-free default so a
/// consumer never has to supply one just to get started.
type NoopHookObserver() =
    interface IHookObserver with
        member _.OnHookFailed(_press: ButtonPress, _error: exn) = ()
        member _.OnUnknownToken(_press: ButtonPress) = ()

/// Default `IPressDispatcher` (contracts/core-ports.md, research.md D6): one unbounded,
/// `SingleReader = true` channel + one consumer loop per chat, in a `ConcurrentDictionary`. Work
/// for the SAME chat runs sequentially in enqueue order (FIFO); DIFFERENT chats run concurrently.
///
/// A work item that throws is caught right here so the chat's consumer loop survives (FR-009).
/// Note this dispatcher has no `IHookObserver` dependency: `work` is an opaque
/// `CancellationToken -> Task` thunk with no `ButtonPress` attached, so there is nothing
/// meaningful to attribute a failure to at this layer. The *attributed* reporting
/// (`IHookObserver.OnHookFailed`, which needs `ButtonPress` context) happens one layer up, in
/// `UpdateProcessor`, which wraps the hook invocation in its own try/with before ever handing the
/// resulting thunk to `Enqueue`. This dispatcher's catch is a structural safety net only.
type PerChatChannelDispatcher() =
    let channels = ConcurrentDictionary<ChatId, Channel<CancellationToken -> Task> * Task>()
    let cts = new CancellationTokenSource()

    // Hand-rolled `WaitToReadAsync`/`TryRead` consumer loop rather than `IAsyncEnumerable`/
    // `TaskSeq` (research.md: Core stays at FSharp.Core + FSharp.UMX only).
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
                                    () // keep the chat's loop alive (FR-009); see the type-level comment above.
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
                    // Signal "no more work is coming" so each chat's `ReadAllAsync` loop drains
                    // whatever is already queued and then completes naturally; `cts` is also
                    // cancelled so a work item that respects cancellation can stop early within
                    // the host shutdown budget, per contracts/core-ports.md.
                    for KeyValue(_, (channel, _)) in channels do
                        channel.Writer.TryComplete() |> ignore

                    cts.Cancel()

                    for KeyValue(_, (_, consumerTask)) in channels do
                        try
                            do! consumerTask
                        with _ ->
                            ()

                    cts.Dispose()
                }
            )
