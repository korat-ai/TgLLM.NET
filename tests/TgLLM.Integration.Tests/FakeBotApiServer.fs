/// Shared fake Telegram Bot API HTTP server (a real Kestrel host on a loopback port), reused by
/// every BotApi-facing integration test in this project: T023 (`BotApiClientTests`), T025
/// (`LongPollingTests`), T028 (`FSharpPollingAcceptanceTests`) and later agents' T029+/T034 tests.
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
/// `RequestBase.cs`; research.md D7, Principle V), not assumed. This server exposes exactly that one
/// route, `POST /bot{token}/{method}`, and replies with the Bot API's `{"ok":true,"result":...}`
/// envelope every real Telegram.Bot request expects.
[<Sealed>]
type FakeBotApiServer
    internal
    (
        app: WebApplication,
        requests: ConcurrentQueue<RecordedRequest>,
        responses: ConcurrentDictionary<string, ConcurrentQueue<string>>
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
        responses.GetOrAdd(methodName, (fun _ -> ConcurrentQueue())).Enqueue resultJson

    /// e.g. `"http://127.0.0.1:53214"` — pass as `TelegramBotClientOptions`'s `baseUrl`.
    member _.BaseUrl: string = (Seq.head app.Urls).TrimEnd('/')

    interface IAsyncDisposable with
        member _.DisposeAsync() : ValueTask = app.DisposeAsync()

module FakeBotApiServer =

    let private defaultResult (methodName: string) (body: JsonNode option) (nextMessageId: unit -> int) : string =
        match methodName with
        | "getUpdates" -> "[]"
        | "sendMessage" ->
            let chatIdJson =
                body
                |> Option.bind (fun b -> Option.ofObj (b.Item "chat_id"))
                |> Option.map (fun n -> n.ToJsonString())
                |> Option.defaultValue "0"

            $"""{{"message_id":{nextMessageId ()},"date":0,"chat":{{"id":{chatIdJson},"type":"private"}}}}"""
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
            let responses = ConcurrentDictionary<string, ConcurrentQueue<string>>()
            let mutable nextMessageId = 0

            let nextMessageIdFn () =
                nextMessageId <- nextMessageId + 1
                nextMessageId

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

                        let resultJson =
                            match responses.TryGetValue methodName with
                            | true, queue ->
                                match queue.TryDequeue() with
                                | true, json -> json
                                | false, _ -> defaultResult methodName bodyNode nextMessageIdFn
                            | false, _ -> defaultResult methodName bodyNode nextMessageIdFn

                        ctx.Response.ContentType <- "application/json"
                        do! ctx.Response.WriteAsync $"""{{"ok":true,"result":{resultJson}}}"""
                    }
                    :> Task)
            )
            |> ignore

            do! app.StartAsync()
            return FakeBotApiServer(app, requests, responses)
        }

/// Builds Bot-API-shaped JSON for canned `getUpdates` responses — reused by every long-polling
/// integration test (T025 `LongPollingTests`, T028 `FSharpPollingAcceptanceTests`) that needs to
/// hand the fake server a batch of `Update`s.
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

    /// Wraps a list of already-built update JSON objects into a `getUpdates`-shaped `Update[]`
    /// `result` array.
    let batch (updates: string list) : string = "[" + String.concat "," updates + "]"
