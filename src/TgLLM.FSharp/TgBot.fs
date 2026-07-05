/// T027 (contracts/fsharp-facade.md). The idiomatic F# public façade: module functions, `Result`
/// for keyboard validation, and `Task`-returning members. It wires the transport-agnostic core
/// (`TgLLM.Core`) to the long-polling transport (`TgLLM.BotApi`) — swapping to webhooks (T031)
/// leaves hook bodies untouched (FR-013).
namespace TgLLM.FSharp

open System
open System.Threading
open System.Threading.Tasks
open Telegram.Bot
open TgLLM.Core
open TgLLM.BotApi
open TgLLM.Webhooks

/// A button being described by the agent: a raw (unvalidated) label plus the handler to run when it
/// is tapped. Labels are validated by `Keyboard.create`, so an invalid label surfaces as an
/// `Error`, never an exception (the F# idiom; the C# façade throws instead — Principle II).
[<NoComparison; NoEquality>]
type ButtonSpec = internal { RawLabel: string; RawHook: Hook }

/// Attach a handler to a labelled button. The handler may return any `Task<'a>`; its result is
/// ignored by the runtime (it reacts through the `PressContext` it is given).
module Button =

    let on (label: string) (handler: PressContext -> Task<'a>) : ButtonSpec =
        { RawLabel = label
          RawHook = fun ctx -> (handler ctx) :> Task }

module Keyboard =

    /// Validate a keyboard laid out as rows of buttons. Returns `Error` (never throws) for an empty
    /// keyboard, an empty row, or an invalid label — `TgLLM.Core.Keyboard.create` (whose
    /// `ButtonSpec.Label` is a raw string) performs the validation.
    let create (rows: ButtonSpec list list) : Result<KeyboardSpec, KeyboardError> =
        rows
        |> List.map (List.map (fun (b: ButtonSpec) -> { Label = b.RawLabel; Hook = b.RawHook }: TgLLM.Core.ButtonSpec))
        |> TgLLM.Core.Keyboard.create

/// Long-polling bot configuration. `WithBaseUrl` overrides the Bot API endpoint — for a local Bot
/// API server, Telegram's test environment, or pointing tests at a fake server.
type TgBotConfig =
    { BotToken: string
      BaseUrl: string option }

    static member create(botToken: string) = { BotToken = botToken; BaseUrl = None }
    member this.WithBaseUrl(url: string) = { this with BaseUrl = Some url }

/// Webhook bot configuration. `PublicUrl` is the HTTPS URL Telegram POSTs updates to; `SecretToken`
/// is echoed in the `X-Telegram-Bot-Api-Secret-Token` header and verified on every request.
/// `WithBaseUrl` overrides the Bot API endpoint (tests / local Bot API server).
type TgWebhookConfig =
    { BotToken: string
      PublicUrl: string
      SecretToken: string
      BaseUrl: string option }

    static member create(botToken: string, publicUrl: string, secretToken: string) =
        { BotToken = botToken
          PublicUrl = publicUrl
          SecretToken = secretToken
          BaseUrl = None }

    member this.WithBaseUrl(url: string) = { this with BaseUrl = Some url }

/// A running bot: ingests updates in the background and lets the agent send keyboards/messages.
/// Dispose (`use!`/`IAsyncDisposable`) to stop ingestion and release the per-chat dispatcher.
[<Sealed>]
type TgBot
    internal
    (
        api: IBotApiClient,
        store: IHookStore,
        dispatcher: IPressDispatcher,
        cts: CancellationTokenSource,
        runTask: Task,
        webhookSource: WebhookUpdateSource option
    ) =

    member _.SendKeyboard(chat: ChatId, text: MessageText, keyboard: KeyboardSpec) : Task<MessageId> =
        AgentOps.sendKeyboard store api CallbackToken.generate chat text keyboard cts.Token

    member _.SendText(chat: ChatId, text: MessageText) : Task<MessageId> = api.SendText(chat, text, cts.Token)

    /// The webhook ingress to hand to `MapTelegramWebhook`. Meaningful only for a bot started with
    /// `startWebhook`; a long-polling bot has no HTTP ingress and this raises.
    member _.WebhookSource: WebhookUpdateSource =
        match webhookSource with
        | Some source -> source
        | None -> invalidOp "This bot uses long polling and has no webhook ingress (use TgBot.startWebhook)."

    interface IAsyncDisposable with
        member _.DisposeAsync() : ValueTask =
            task {
                // Completing the webhook channel (if any) lets the processor's Updates loop finish.
                webhookSource |> Option.iter (fun source -> source.Complete())
                cts.Cancel()
                // The background loop ends via cancellation; swallow the resulting cancellation (and
                // any late error) so teardown always completes cleanly.
                try
                    do! runTask
                with _ ->
                    ()

                do! dispatcher.DisposeAsync()
                cts.Dispose()
            }
            |> ValueTask

    static member private buildClient(botToken: string, baseUrl: string option) : ITelegramBotClient =
        let options =
            match baseUrl with
            | Some url -> TelegramBotClientOptions(botToken, url)
            | None -> TelegramBotClientOptions(botToken)

        TelegramBotClient(options) :> ITelegramBotClient

    /// Start ingesting updates via long polling (deletes any configured webhook first). The returned
    /// bot is already polling in the background.
    static member startPolling(config: TgBotConfig) : Task<TgBot> =
        task {
            let client = TgBot.buildClient (config.BotToken, config.BaseUrl)
            let api = TelegramBotApiClient(client) :> IBotApiClient
            let store = InMemoryHookStore() :> IHookStore
            let dispatcher = new PerChatChannelDispatcher() :> IPressDispatcher
            let observer = NoopHookObserver() :> IHookObserver
            let source = LongPollingUpdateSource(client) :> IUpdateSource
            let processor = UpdateProcessor(source, store, api, dispatcher, observer)
            let cts = new CancellationTokenSource()
            let runTask = processor.RunAsync cts.Token
            return new TgBot(api, store, dispatcher, cts, runTask, None)
        }

    /// Start ingesting updates via webhooks. Registers the webhook with Telegram (`setWebhook` with
    /// the secret token) and consumes pushed updates. Map the HTTP endpoint separately with
    /// `app.MapTelegramWebhook(bot.WebhookSource, config.SecretToken)`. Hook code is identical to a
    /// polling bot (FR-013).
    static member startWebhook(config: TgWebhookConfig) : Task<TgBot> =
        task {
            let client = TgBot.buildClient (config.BotToken, config.BaseUrl)
            do! client.SetWebhook(url = config.PublicUrl, secretToken = config.SecretToken)
            let api = TelegramBotApiClient(client) :> IBotApiClient
            let store = InMemoryHookStore() :> IHookStore
            let dispatcher = new PerChatChannelDispatcher() :> IPressDispatcher
            let observer = NoopHookObserver() :> IHookObserver
            let webhookSource = WebhookUpdateSource()
            let processor = UpdateProcessor(webhookSource :> IUpdateSource, store, api, dispatcher, observer)
            let cts = new CancellationTokenSource()
            let runTask = processor.RunAsync cts.Token
            return new TgBot(api, store, dispatcher, cts, runTask, Some webhookSource)
        }
