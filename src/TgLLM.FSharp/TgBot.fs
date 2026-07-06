/// T027 (contracts/fsharp-facade.md). The idiomatic F# public façade: module functions, `Result`
/// for keyboard validation, and `Task`-returning members. It wires the transport-agnostic core
/// (`TgLLM.Core`) to the long-polling transport (`TgLLM.BotApi`) — swapping to webhooks (T031)
/// leaves hook bodies untouched (FR-013).
namespace TgLLM.FSharp

open System
open System.Threading
open System.Threading.Tasks
open Telegram.Bot
open Microsoft.Extensions.Logging
open FSharp.UMX
open TgLLM.Core
open TgLLM.BotApi
open TgLLM.Webhooks

/// Bridges the core observability seam to an `ILogger` (FR-009): hook failures and unknown/stale
/// presses are surfaced, never swallowed.
type LoggingHookObserver(logger: ILogger) =
    interface IHookObserver with
        member _.OnHookFailed(press: ButtonPress, error: exn) =
            logger.LogError(
                error,
                "Hook for button {Button} in chat {Chat} failed",
                [| ButtonLabel.value press.ButtonLabel :> obj; UMX.untag press.Chat :> obj |]
            )

        member _.OnUnknownToken(press: ButtonPress) =
            logger.LogWarning(
                "Received a callback for an unknown or stale button in chat {Chat}",
                [| UMX.untag press.Chat :> obj |]
            )

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
[<NoComparison>]
type TgBotConfig =
    { BotToken: string
      BaseUrl: string option
      Logger: ILogger option
      /// Feature 002-llm-tool-router (T017): tools available to `SendKeyboardPlan`-sent keyboards.
      /// `None` (the default) means the bot has no Tool Router wired in — plain slice-1 behavior.
      Tools: ToolRegistry option
      /// Feature 002-llm-tool-router (T027, US3, research.md D5): the store backing every
      /// tool-button binding (`SendKeyboardPlan`'s saves AND `ToolDispatch`'s resolves). `None` (the
      /// default) keeps slice-1/MVP behavior — an in-process `InMemoryBindingStore`; `Some` (e.g. a
      /// `TgLLM.Persistence.FileBindingStore`) makes bindings survive a restart (SC-004).
      BindingStore: IBindingStore option }

    static member create(botToken: string) =
        { BotToken = botToken
          BaseUrl = None
          Logger = None
          Tools = None
          BindingStore = None }

    member this.WithBaseUrl(url: string) = { this with BaseUrl = Some url }
    /// Surface hook failures / unknown presses through this logger (FR-009).
    member this.WithLogger(logger: ILogger) = { this with Logger = Some logger }
    /// Wire a Tool Router registry into this bot (contracts/tool-router.md).
    member this.WithTools(tools: ToolRegistry) = { this with Tools = Some tools }
    /// Back tool bindings with a durable store (contracts/tool-router.md "Durable store") instead of
    /// the in-memory default — e.g. `TgLLM.Persistence.FileBindingStore.openAt "bindings.json"`.
    member this.WithBindingStore(store: IBindingStore) = { this with BindingStore = Some store }

/// Webhook bot configuration. `PublicUrl` is the HTTPS URL Telegram POSTs updates to; `SecretToken`
/// is echoed in the `X-Telegram-Bot-Api-Secret-Token` header and verified on every request.
/// `WithBaseUrl` overrides the Bot API endpoint (tests / local Bot API server).
[<NoComparison>]
type TgWebhookConfig =
    { BotToken: string
      PublicUrl: string
      SecretToken: string
      BaseUrl: string option
      Logger: ILogger option
      /// Feature 002-llm-tool-router (T017): kept in lockstep with `TgBotConfig.Tools` so hook/tool
      /// code and wiring stay identical regardless of transport (Principle IV).
      Tools: ToolRegistry option
      /// Feature 002-llm-tool-router (T027, US3): kept in lockstep with `TgBotConfig.BindingStore` —
      /// see its doc comment.
      BindingStore: IBindingStore option }

    static member create(botToken: string, publicUrl: string, secretToken: string) =
        { BotToken = botToken
          PublicUrl = publicUrl
          SecretToken = secretToken
          BaseUrl = None
          Logger = None
          Tools = None
          BindingStore = None }

    member this.WithBaseUrl(url: string) = { this with BaseUrl = Some url }
    /// Surface hook failures / unknown presses through this logger (FR-009).
    member this.WithLogger(logger: ILogger) = { this with Logger = Some logger }
    /// Wire a Tool Router registry into this bot (contracts/tool-router.md).
    member this.WithTools(tools: ToolRegistry) = { this with Tools = Some tools }
    /// Back tool bindings with a durable store instead of the in-memory default (contracts/tool-router.md).
    member this.WithBindingStore(store: IBindingStore) = { this with BindingStore = Some store }

/// A running bot: ingests updates in the background and lets the agent send keyboards/messages.
/// Dispose (`use!`/`IAsyncDisposable`) to stop ingestion and release the per-chat dispatcher.
[<Sealed>]
type TgBot
    internal
    (
        api: IBotApiClient,
        store: IHookStore,
        bindingStore: IBindingStore,
        dispatcher: IPressDispatcher,
        cts: CancellationTokenSource,
        runTask: Task,
        webhookSource: WebhookUpdateSource option
    ) =

    member _.SendKeyboard(chat: ChatId, text: MessageText, keyboard: KeyboardSpec) : Task<MessageId> =
        AgentOps.sendKeyboard store api CallbackToken.generate chat text keyboard cts.Token

    member _.SendText(chat: ChatId, text: MessageText) : Task<MessageId> = api.SendText(chat, text, cts.Token)

    /// Send a keyboard built from a neutral Tool Router plan (T017, contracts/tool-router.md);
    /// presses route to the tools registered via `TgBotConfig.WithTools`. Bindings are saved BEFORE
    /// the send completes, same ordering guarantee as `SendKeyboard`/`AgentOps.sendKeyboard`
    /// (data-model.md "Outbound: send a keyboard"). An invalid plan (e.g. a bad label/url that
    /// `Plan.rows` didn't catch) is a programmer error by the caller (Always-Rule 6) — this member
    /// returns `Task<MessageId>` directly (no `Result`), so it fails fast rather than forcing every
    /// caller to unwrap a `Result` for what should have been validated when the plan was built.
    member _.SendKeyboardPlan(chat: ChatId, text: MessageText, plan: ToolKeyboard) : Task<MessageId> =
        task {
            match ToolPlan.plan (Seq.initInfinite (fun _ -> CallbackToken.generate ())) plan with
            | Error e -> return invalidArg (nameof plan) $"TgBot.SendKeyboardPlan: invalid plan ({e})"
            | Ok(registeredKeyboard, bindings) ->
                do! bindingStore.Save(bindings, cts.Token)
                return! api.SendKeyboard(chat, text, registeredKeyboard, cts.Token)
        }

    /// C#-friendly overload that accepts a raw string (validated with `MessageText.unsafe`).
    member this.SendKeyboardPlan(chat: ChatId, text: string, plan: ToolKeyboard) : Task<MessageId> =
        this.SendKeyboardPlan(chat, MessageText.unsafe text, plan)

    /// C#-friendly overloads that accept a raw string (validated with `MessageText.unsafe`), so the
    /// C# façade never touches the F# `MessageText` type.
    member this.SendKeyboard(chat: ChatId, text: string, keyboard: KeyboardSpec) : Task<MessageId> =
        this.SendKeyboard(chat, MessageText.unsafe text, keyboard)

    member this.SendText(chat: ChatId, text: string) : Task<MessageId> =
        this.SendText(chat, MessageText.unsafe text)

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
            let bindingStore = config.BindingStore |> Option.defaultWith (fun () -> InMemoryBindingStore() :> IBindingStore)
            let dispatcher = new PerChatChannelDispatcher() :> IPressDispatcher
            let observer =
                match config.Logger with
                | Some logger -> LoggingHookObserver logger :> IHookObserver
                | None -> NoopHookObserver() :> IHookObserver
            let source = LongPollingUpdateSource(client) :> IUpdateSource
            let toolDispatch = config.Tools |> Option.map (fun tools -> ToolDispatch(tools.Registry, bindingStore))
            let processor = UpdateProcessor(source, store, api, dispatcher, observer, ?toolDispatch = toolDispatch)
            let cts = new CancellationTokenSource()
            let runTask = processor.RunAsync cts.Token
            return new TgBot(api, store, bindingStore, dispatcher, cts, runTask, None)
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
            let bindingStore = config.BindingStore |> Option.defaultWith (fun () -> InMemoryBindingStore() :> IBindingStore)
            let dispatcher = new PerChatChannelDispatcher() :> IPressDispatcher
            let observer =
                match config.Logger with
                | Some logger -> LoggingHookObserver logger :> IHookObserver
                | None -> NoopHookObserver() :> IHookObserver
            let webhookSource = WebhookUpdateSource()
            let toolDispatch = config.Tools |> Option.map (fun tools -> ToolDispatch(tools.Registry, bindingStore))
            let processor = UpdateProcessor(webhookSource :> IUpdateSource, store, api, dispatcher, observer, ?toolDispatch = toolDispatch)
            let cts = new CancellationTokenSource()
            let runTask = processor.RunAsync cts.Token
            return new TgBot(api, store, bindingStore, dispatcher, cts, runTask, Some webhookSource)
        }
