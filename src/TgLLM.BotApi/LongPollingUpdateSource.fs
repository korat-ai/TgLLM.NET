/// Long-polling transport adapter: an `IUpdateSource` that drives Telegram's `getUpdates`. Since
/// `getUpdates` and webhooks are
/// mutually exclusive, it deletes any configured webhook first, then loops with confirm-by-offset
/// bookkeeping (`offset = max(update_id) + 1`) so no update is missed or re-processed. Each wire
/// `Update` is mapped to an `AgentEvent` by the pure `Mapping.toAgentEvent` (reused from
/// `TelegramBotApiClient.fs`); unmappable updates are skipped, not guessed at.
namespace TgLLM.BotApi

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open System.Threading.Channels
open Telegram.Bot
open TgLLM.Core

/// `IUpdateSource` over long polling. The client is injected (the façade owns its lifetime and
/// `HttpClient`/`baseUrl` choice), mirroring `TelegramBotApiClient`. The long-poll `timeout` defaults
/// to 30s; a real HTTP client timeout must exceed it (Telegram.Bot's 100s default does). Tests pass
/// `timeoutSeconds = 0` (short polling) so the fake server answers immediately.
///
/// `initialBackoff`/`maxBackoff` (default 1s/30s; tests override both to keep the retry path fast)
/// govern the bounded-exponential retry a transient `GetUpdates` failure gets — see the `pump` doc
/// comment below.
[<Sealed>]
type LongPollingUpdateSource(client: ITelegramBotClient, ?timeoutSeconds: int, ?initialBackoff: TimeSpan, ?maxBackoff: TimeSpan) =

    let timeout = defaultArg timeoutSeconds 30
    let initialBackoff = defaultArg initialBackoff (TimeSpan.FromSeconds 1.0)
    let maxBackoff = defaultArg maxBackoff (TimeSpan.FromSeconds 30.0)

    interface IUpdateSource with
        member _.Updates(ct: CancellationToken) : IAsyncEnumerable<AgentEvent> =
            // Single producer (the pump loop) and single consumer (the caller) — the SingleReader
            // path is the same primitive the per-chat dispatcher relies on for ordering.
            let channel =
                Channel.CreateUnbounded<AgentEvent>(UnboundedChannelOptions(SingleReader = true, SingleWriter = true))

            let writer = channel.Writer

            /// A transient `GetUpdates` failure (network blip, Bot API 5xx/429, ...) must not
            /// permanently kill update ingestion — completing the channel WITH
            /// the exception (the old behavior) faults every future read on the consumer side
            /// forever, i.e. one bad poll kills the bot for good. Instead: retry with a bounded
            /// exponential backoff, resetting to `initialBackoff` after any successful poll so only
            /// CONSECUTIVE failures escalate the delay. `OperationCanceledException` is deliberately
            /// NOT caught by this inner handler (the `when` guard excludes it) — a real
            /// cancellation/shutdown still propagates straight to the outer handler below,
            /// unchanged, so `ct` is always honored promptly.
            let pump () : Task =
                task {
                    try
                        // getUpdates will not work while a webhook is set.
                        do! client.DeleteWebhook(cancellationToken = ct)

                        let mutable offset = 0
                        let mutable backoff = initialBackoff

                        while not ct.IsCancellationRequested do
                            try
                                let! updates =
                                    client.GetUpdates(
                                        offset = Nullable<int> offset,
                                        timeout = Nullable<int> timeout,
                                        cancellationToken = ct
                                    )

                                backoff <- initialBackoff

                                for update in updates do
                                    match Mapping.toAgentEvent update with
                                    | ValueSome event -> do! writer.WriteAsync(event, ct)
                                    | ValueNone -> ()

                                    // Confirm-by-offset: the next poll asks only for updates after this one.
                                    offset <- max offset (update.Id + 1)
                            with ex when not (ex :? OperationCanceledException) ->
                                do! Task.Delay(backoff, ct)
                                backoff <- min maxBackoff (backoff * 2.0)

                        writer.TryComplete() |> ignore
                    with
                    | :? OperationCanceledException -> writer.TryComplete() |> ignore
                    | ex -> writer.TryComplete ex |> ignore
                }

            // Fire the pump; cancellation via `ct` stops it and completes the channel.
            pump () |> ignore
            channel.Reader.ReadAllAsync ct
