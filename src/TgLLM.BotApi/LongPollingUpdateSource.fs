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
[<Sealed>]
type LongPollingUpdateSource(client: ITelegramBotClient, ?timeoutSeconds: int) =

    let timeout = defaultArg timeoutSeconds 30

    interface IUpdateSource with
        member _.Updates(ct: CancellationToken) : IAsyncEnumerable<AgentEvent> =
            // Single producer (the pump loop) and single consumer (the caller) — the SingleReader
            // path is the same primitive the per-chat dispatcher relies on for ordering.
            let channel =
                Channel.CreateUnbounded<AgentEvent>(UnboundedChannelOptions(SingleReader = true, SingleWriter = true))

            let writer = channel.Writer

            let pump () : Task =
                task {
                    try
                        // getUpdates will not work while a webhook is set.
                        do! client.DeleteWebhook(cancellationToken = ct)

                        let mutable offset = 0

                        while not ct.IsCancellationRequested do
                            let! updates =
                                client.GetUpdates(
                                    offset = Nullable<int> offset,
                                    timeout = Nullable<int> timeout,
                                    cancellationToken = ct
                                )

                            for update in updates do
                                match Mapping.toAgentEvent update with
                                | ValueSome event -> do! writer.WriteAsync(event, ct)
                                | ValueNone -> ()

                                // Confirm-by-offset: the next poll asks only for updates after this one.
                                offset <- max offset (update.Id + 1)

                        writer.TryComplete() |> ignore
                    with
                    | :? OperationCanceledException -> writer.TryComplete() |> ignore
                    | ex -> writer.TryComplete ex |> ignore
                }

            // Fire the pump; cancellation via `ct` stops it and completes the channel.
            pump () |> ignore
            channel.Reader.ReadAllAsync ct
