/// T030 (contracts/core-ports.md "IUpdateSource", research.md D7). Webhook transport: a host-agnostic
/// `IUpdateSource` fed by an HTTP endpoint that pushes each incoming `Update`. It buffers mapped
/// events in a single-reader channel so the endpoint can return 200 immediately (Telegram retries a
/// slow endpoint) while the processor drains events on its own. The `Update` -> domain mapping is the
/// SAME pure `Mapping.toAgentEvent` the long-polling source uses, so hook behavior is identical
/// across transports (FR-013).
namespace TgLLM.Webhooks

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.Threading.Channels
open Telegram.Bot.Types
open TgLLM.Core
open TgLLM.BotApi

/// Webhook helpers that are pure (secret verification) or stateless (JSON parsing) and so are unit
/// testable without a host.
module Webhook =

    /// Verify the `X-Telegram-Bot-Api-Secret-Token` request header against the configured secret.
    /// No configured secret ⇒ accept. A configured secret with a missing/!= header ⇒ reject. The
    /// comparison is constant-time (`FixedTimeEquals` also returns false on length mismatch) so the
    /// header — the only proof a request really came from Telegram — can't be probed via timing.
    let verifySecretToken (expected: string option) (actual: string option) : bool =
        match expected, actual with
        | None, _ -> true
        | Some _, None -> false
        | Some exp, Some act ->
            CryptographicOperations.FixedTimeEquals(
                ReadOnlySpan<byte>(Encoding.UTF8.GetBytes exp),
                ReadOnlySpan<byte>(Encoding.UTF8.GetBytes act)
            )

    /// Deserialize a webhook request body into a Telegram.Bot `Update` using Telegram.Bot's own
    /// serializer options (its wire types need its custom converters — Principle V).
    let parseUpdate (json: string) : Update =
        match JsonSerializer.Deserialize<Update>(json, Telegram.Bot.JsonBotAPI.Options) |> Option.ofObj with
        | Some update -> update
        | None -> invalidArg (nameof json) "webhook body did not deserialize to a Telegram Update"

/// `IUpdateSource` fed by pushed webhook updates. Multiple producers (concurrent inbound POSTs) may
/// `Ingest`; a single consumer (the processor) drains `Updates`.
[<Sealed>]
type WebhookUpdateSource() =

    let channel =
        Channel.CreateUnbounded<AgentEvent>(UnboundedChannelOptions(SingleReader = true, SingleWriter = false))

    /// Map one pushed `Update` to a domain event and buffer it. Unmappable updates (non-button-press)
    /// are dropped, matching the long-polling source. Returns quickly so the HTTP handler can 200.
    member _.Ingest(update: Update, ct: CancellationToken) : ValueTask =
        match Mapping.toAgentEvent update with
        | ValueSome event -> channel.Writer.WriteAsync(event, ct)
        | ValueNone -> ValueTask.CompletedTask

    /// Stop the stream (e.g. on shutdown), so `Updates` enumeration completes.
    member _.Complete() : unit = channel.Writer.TryComplete() |> ignore

    interface IUpdateSource with
        member _.Updates(ct: CancellationToken) : IAsyncEnumerable<AgentEvent> = channel.Reader.ReadAllAsync ct
