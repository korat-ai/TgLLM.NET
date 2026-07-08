/// Shared fake Telegram Bot API HTTP server (a real Kestrel host on a loopback port), reused by
/// every BotApi-facing integration test in this project: `BotApiClientTests`, `LongPollingTests`,
/// `FSharpPollingAcceptanceTests`, and the transport/tool-routing acceptance suites.
module TgLLM.Integration.Tests.FakeBotApiServer

open System
open System.Collections.Concurrent
open System.IO
open System.Text.Json.Nodes
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging

/// One HTTP request the fake Bot API server received, parsed off the wire. `JsonNode` has no
/// `IComparable`, so this record can't support structural comparison (mirrors `HookBinding`'s
/// `[<NoComparison; NoEquality>]` in `TgLLM.Core.Domain` for the same reason — a field type that
/// doesn't support the derived capability).
[<NoComparison>]
type RecordedRequest =
    { Method: string
      Token: string
      Body: JsonNode option }

/// A minimal, real Kestrel-hosted fake of the Telegram Bot API.
///
/// Telegram.Bot 22.10.1 always POSTs to `{BaseRequestUrl}/{MethodName}`, where
/// `BaseRequestUrl = "{baseUrl}/bot{token}"` and `MethodName` is the exact lowerCamelCase Bot API
/// method name (`"sendMessage"`, `"getUpdates"`, `"answerCallbackQuery"`, `"deleteWebhook"`, ...) —
/// verified against Telegram.Bot's own source (`TelegramBotClientOptions.cs`, `TelegramBotClient.cs`,
/// `RequestBase.cs`; Principle V), not assumed. This server exposes exactly that one
/// route, `POST /bot{token}/{method}`, and replies with the Bot API's `{"ok":true,"result":...}`
/// envelope every real Telegram.Bot request expects.
/// One canned response queued for a Bot API method: either a success `result` payload
/// (`EnqueueResult`) or a Bot-API-shaped ERROR (`EnqueueError`) — needed to simulate
/// `"message to edit not found"`/`"message is not modified"` without a real vanished message.
type internal CannedResponse =
    | Success of resultJson: string
    | Failure of errorCode: int * description: string

[<Sealed>]
type FakeBotApiServer
    internal
    (
        app: WebApplication,
        requests: ConcurrentQueue<RecordedRequest>,
        responses: ConcurrentDictionary<string, ConcurrentQueue<CannedResponse>>,
        delays: ConcurrentDictionary<string, ConcurrentQueue<TimeSpan>>
    ) =

    /// Every request recorded so far, oldest first.
    member _.Requests: RecordedRequest list = List.ofSeq requests

    /// Requests for one Bot API method (e.g. `"sendMessage"`), oldest first.
    member this.RequestsFor(methodName: string) : RecordedRequest list =
        this.Requests |> List.filter (fun r -> r.Method = methodName)

    /// Queues one canned Bot-API `result` JSON payload (e.g. an `Update[]` array for
    /// `"getUpdates"`) to be returned by the NEXT call to `methodName`. Calls beyond the queue
    /// fall back to a method-aware default: `"getUpdates"` -> `[]`; `"sendMessage"` -> a
    /// synthesized `Message` with an auto-incrementing `message_id`; anything else -> `true`.
    member _.EnqueueResult(methodName: string, resultJson: string) : unit =
        responses.GetOrAdd(methodName, (fun _ -> ConcurrentQueue())).Enqueue(Success resultJson)

    /// Queues a canned Bot-API ERROR (`{"ok":false,"error_code":...,"description":...}`, HTTP 400)
    /// to be returned by the NEXT call to `methodName` — e.g.
    /// `server.EnqueueError("editMessageText", 400, "Bad Request: message to edit not found")`
    /// simulates editing a vanished message. Telegram.Bot's own client throws `ApiRequestException`
    /// for any `ok:false` response, regardless of HTTP status (Principle V, verified against its
    /// source), so the exact status code here is cosmetic realism, not load-bearing.
    member _.EnqueueError(methodName: string, errorCode: int, description: string) : unit =
        responses.GetOrAdd(methodName, (fun _ -> ConcurrentQueue())).Enqueue(Failure(errorCode, description))

    /// Delays the NEXT call to `methodName` by `delay` before this server writes back its response
    /// (canned or default) — widens the in-flight window of a real HTTP round-trip so a test can
    /// deterministically land two concurrent calls inside it, rather than relying on incidental
    /// scheduling luck (e.g. proving a caller serializes two concurrent sends for the same logical
    /// target rather than racing them).
    member _.DelayNextResponse(methodName: string, delay: TimeSpan) : unit =
        delays.GetOrAdd(methodName, (fun _ -> ConcurrentQueue())).Enqueue delay

    /// e.g. `"http://127.0.0.1:53214"` — pass as `TelegramBotClientOptions`'s `baseUrl`.
    member _.BaseUrl: string = (Seq.head app.Urls).TrimEnd('/')

    interface IAsyncDisposable with
        member _.DisposeAsync() : ValueTask = app.DisposeAsync()

module FakeBotApiServer =

    let private defaultResult (methodName: string) (body: JsonNode option) (nextMessageId: string -> int) : string =
        let field (key: string) (fallback: string) =
            body
            |> Option.bind (fun b -> Option.ofObj (b.Item key))
            |> Option.map (fun n -> n.ToJsonString())
            |> Option.defaultValue fallback

        match methodName with
        | "getUpdates" -> "[]"
        | "sendMessage" ->
            // Telegram's `message_id` is unique only PER CHAT — a global counter here would hide
            // that real-world shape and let a cross-chat collision bug pass every test. Keying the
            // counter by `chat_id` means two different chats' first sent message both legitimately
            // land on message_id 1, exactly like the real Bot API, so tests can represent (and
            // catch) that collision.
            let chatIdStr = field "chat_id" "0"
            $"""{{"message_id":{nextMessageId chatIdStr},"date":0,"chat":{{"id":{chatIdStr},"type":"private"}}}}"""
        | "editMessageText"
        | "editMessageReplyMarkup" ->
            // Telegram.Bot deserializes the result as a `Message`:
            // a real `editMessageText`/`editMessageReplyMarkup` echoes back the SAME `message_id` (the
            // request always carries it — unlike `sendMessage`, which allocates a fresh one).
            $"""{{"message_id":{field "message_id" "0"},"date":0,"chat":{{"id":{field "chat_id" "0"},"type":"private"}}}}"""
        | _ -> "true"

    /// Starts a real Kestrel host on an OS-assigned loopback port and returns the fake server,
    /// ready to accept Bot API calls at `.BaseUrl`.
    let start () : Task<FakeBotApiServer> =
        task {
            let builder = WebApplication.CreateBuilder()
            builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
            builder.Logging.ClearProviders() |> ignore
            let app = builder.Build()

            let requests = ConcurrentQueue<RecordedRequest>()
            let responses = ConcurrentDictionary<string, ConcurrentQueue<CannedResponse>>()
            let delays = ConcurrentDictionary<string, ConcurrentQueue<TimeSpan>>()
            // Per-chat, not global (see `defaultResult`'s doc comment above): keyed by the request's
            // raw `chat_id` JSON text so distinct chats never share a counter.
            let messageIdsByChat = ConcurrentDictionary<string, int>()

            let nextMessageIdFn (chatIdKey: string) : int =
                messageIdsByChat.AddOrUpdate(chatIdKey, (fun _ -> 1), (fun _ previous -> previous + 1))

            app.MapPost(
                "/bot{token}/{method}",
                Func<HttpContext, Task>(fun ctx ->
                    task {
                        let token = string ctx.Request.RouteValues["token"]
                        let methodName = string ctx.Request.RouteValues["method"]

                        use reader = new StreamReader(ctx.Request.Body)
                        let! bodyText = reader.ReadToEndAsync()

                        let bodyNode =
                            if String.IsNullOrWhiteSpace bodyText then None
                            else JsonNode.Parse bodyText |> Option.ofObj

                        requests.Enqueue { Method = methodName; Token = token; Body = bodyNode }

                        match delays.TryGetValue methodName with
                        | true, queue ->
                            match queue.TryDequeue() with
                            | true, delay -> do! Task.Delay delay
                            | false, _ -> ()
                        | false, _ -> ()

                        let canned =
                            match responses.TryGetValue methodName with
                            | true, queue ->
                                match queue.TryDequeue() with
                                | true, response -> Some response
                                | false, _ -> None
                            | false, _ -> None

                        ctx.Response.ContentType <- "application/json"

                        match canned with
                        | Some(Failure(errorCode, description)) ->
                            ctx.Response.StatusCode <- 400

                            let escapedDescription = System.Text.Json.JsonSerializer.Serialize<string>(description)

                            do!
                                ctx.Response.WriteAsync
                                    $"""{{"ok":false,"error_code":{errorCode},"description":{escapedDescription}}}"""
                        | Some(Success resultJson) -> do! ctx.Response.WriteAsync $"""{{"ok":true,"result":{resultJson}}}"""
                        | None ->
                            let resultJson = defaultResult methodName bodyNode nextMessageIdFn
                            do! ctx.Response.WriteAsync $"""{{"ok":true,"result":{resultJson}}}"""
                    }
                    :> Task)
            )
            |> ignore

            do! app.StartAsync()
            return FakeBotApiServer(app, requests, responses, delays)
        }

/// Builds Bot-API-shaped JSON for canned `getUpdates` responses — reused by every long-polling
/// integration test (`LongPollingTests`, `FSharpPollingAcceptanceTests`) that needs to hand the
/// fake server a batch of `Update`s.
module TelegramJson =

    /// One `callback_query` update with the minimum fields Bot API requires on
    /// `Update`/`CallbackQuery`/`Message`/`Chat`/`User` for a button press to round-trip through
    /// `Mapping.toAgentEvent`.
    let callbackQueryUpdate
        (updateId: int)
        (queryId: string)
        (callbackData: string)
        (chatId: int64)
        (messageId: int)
        (userId: int64)
        (firstName: string)
        : string =
        $$"""
        {
          "update_id": {{updateId}},
          "callback_query": {
            "id": "{{queryId}}",
            "from": { "id": {{userId}}, "is_bot": false, "first_name": "{{firstName}}" },
            "message": { "message_id": {{messageId}}, "date": 0, "chat": { "id": {{chatId}}, "type": "private" } },
            "chat_instance": "test-chat-instance",
            "data": "{{callbackData}}"
          }
        }
        """

    /// One plain user TEXT `message` update with the minimum fields Bot API requires on
    /// `Update`/`Message`/`Chat`/`User` for a text message to round-trip through
    /// `Mapping.toAgentEvent` as `MessageReceived`. `text` must not itself need JSON escaping
    /// (callers keep it to plain ASCII with no quotes/backslashes) — this is a test builder, not a
    /// general-purpose JSON writer.
    let textMessageUpdate (updateId: int) (chatId: int64) (messageId: int) (userId: int64) (firstName: string) (text: string) : string =
        $$"""
        {
          "update_id": {{updateId}},
          "message": {
            "message_id": {{messageId}},
            "date": 0,
            "chat": { "id": {{chatId}}, "type": "private" },
            "from": { "id": {{userId}}, "is_bot": false, "first_name": "{{firstName}}" },
            "text": "{{text}}"
          }
        }
        """

    /// Wraps a list of already-built update JSON objects into a `getUpdates`-shaped `Update[]`
    /// `result` array.
    let batch (updates: string list) : string = "[" + String.concat "," updates + "]"
