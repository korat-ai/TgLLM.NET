/// Webhook transport: a host-agnostic `IUpdateSource` fed by an HTTP endpoint that pushes each
/// incoming `Update`. It buffers mapped events in a bounded single-reader channel so the endpoint
/// can return immediately — 200 when accepted, or retryable 503 when saturated — while the
/// processor drains events on its own. The `Update` -> domain mapping is the SAME pure `Mapping.toAgentEvent` the
/// long-polling source uses, so hook behavior is identical across transports.
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
    /// serializer options (its wire types need its custom converters).
    let parseUpdate (json: string) : Update =
        match JsonSerializer.Deserialize<Update>(json, Telegram.Bot.JsonBotAPI.Options) |> Option.ofObj with
        | Some update -> update
        | None -> invalidArg (nameof json) "webhook body did not deserialize to a Telegram Update"

/// `IUpdateSource` fed by pushed webhook updates. Multiple producers (concurrent inbound POSTs) may
/// `Ingest`; a single consumer (the processor) drains `Updates`. The bounded queue protects the
/// process from an inbound burst growing memory without limit.
[<Sealed>]
type WebhookUpdateSource(capacity: int) =

    do
        if capacity <= 0 then
            invalidArg (nameof capacity) "webhook queue capacity must be positive"

    let channel =
        Channel.CreateBounded<AgentEvent>(
            BoundedChannelOptions(
                capacity,
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            )
        )

    /// Create a source with a conservative bounded backlog.
    new() = WebhookUpdateSource(1024)

    /// Map one pushed `Update` to a domain event and buffer it. An update mappable to neither a
    /// `CallbackQuery` outcome nor a plain user text `Message` (`ValueNone`) is dropped, matching
    /// the long-polling source. A `CallbackQuery` this library can't route to a `ButtonPress`
    /// (non-canonical `Data`, or no originating `Message`) is NOT dropped —
    /// `Mapping.toAgentEvent` yields `AckOnly queryId` for it; a user text message is NOT dropped
    /// either — it yields `MessageReceived` — both flow through this same channel like any other
    /// `AgentEvent` and reach `UpdateProcessor`'s own handling — this transport needs no code
    /// change of its own for either, since both fall out of sharing `Mapping.toAgentEvent` with
    /// `LongPollingUpdateSource`. Direct callers may await capacity; the HTTP endpoint uses
    /// `TryIngest` below so it never holds requests open behind a saturated queue.
    member _.Ingest(update: Update, ct: CancellationToken) : ValueTask =
        match Mapping.toAgentEvent update with
        | ValueSome event -> channel.Writer.WriteAsync(event, ct)
        | ValueNone -> ValueTask.CompletedTask

    /// Try to buffer an HTTP-delivered update without making the request wait for queue capacity.
    /// Returns false when the bounded queue is full or completed, allowing the endpoint to return a
    /// retryable overload response. Updates irrelevant to this library remain successful no-ops.
    member _.TryIngest(update: Update) : bool =
        match Mapping.toAgentEvent update with
        | ValueSome event -> channel.Writer.TryWrite event
        | ValueNone -> true

    /// Stop the stream (e.g. on shutdown), so `Updates` enumeration completes.
    member _.Complete() : unit = channel.Writer.TryComplete() |> ignore

    interface IUpdateSource with
        member _.Updates(ct: CancellationToken) : IAsyncEnumerable<AgentEvent> = channel.Reader.ReadAllAsync ct
