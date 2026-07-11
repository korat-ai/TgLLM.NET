/// The idiomatic F# public façade: module functions, `Result` for keyboard validation, and
/// `Task`-returning members. It wires the transport-agnostic core (`TgLLM.Core`) to the
/// long-polling transport (`TgLLM.BotApi`) — swapping to webhooks leaves hook bodies untouched.
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

/// Bridges the core observability seam to an `ILogger`: hook failures and unknown/stale presses
/// are surfaced, never swallowed.
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

        member _.OnEditFailed(press: ButtonPress, reason: string) =
            logger.LogWarning(
                "Edit-in-place for chat {Chat}, message {MessageId} failed softly: {Reason}",
                [| UMX.untag press.Chat :> obj; UMX.untag press.MessageId :> obj; reason :> obj |]
            )

        member _.OnRunLoopFailed(error: exn) =
            logger.LogError(error, "The update-ingestion run loop stopped unexpectedly; this bot is no longer processing updates")

/// Bridges the Core message-seam observability to an `ILogger` — the message-side counterpart to
/// `LoggingHookObserver`.
type LoggingMessageObserver(logger: ILogger) =
    interface IMessageObserver with
        member _.OnMessageFailed(message: IncomingMessage, error: exn) =
            logger.LogError(
                error,
                "OnMessage handler for chat {Chat}, message {MessageId} failed",
                [| UMX.untag message.Chat :> obj; UMX.untag message.MessageId :> obj |]
            )

/// A button being described by the agent: a raw (unvalidated) label plus the handler to run when it
/// is tapped. Labels are validated by `Keyboard.create`, so an invalid label surfaces as an
/// `Error`, never an exception (the F# idiom; the C# façade throws instead).
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

/// Configuration shared by both transports: the bot token, Bot API endpoint override, hook/tool
/// observability, and Tool Router wiring. Embedded (as `Common`) in both `TgBotConfig` and
/// `TgWebhookConfig`, and mutated only through the `CommonConfig` module's `with*` functions, so
/// the two configs' fields and fluent methods can't drift out of lockstep (hook/tool
/// code and wiring stay identical regardless of transport).
[<NoComparison; NoEquality>]
type CommonConfig =
    { BotToken: string
      BaseUrl: string option
      Logger: ILogger option
      /// Tools available to `SendKeyboardPlan`-sent keyboards. `None` (the default) means the bot
      /// has no Tool Router wired in — plain closure-hook behavior.
      Tools: ToolRegistry option
      /// The store backing every tool-button binding (`SendKeyboardPlan`'s saves AND
      /// `ToolDispatch`'s resolves). `None` (the default) keeps the baseline behavior — an
      /// in-process `InMemoryBindingStore`; `Some` (e.g. a `TgLLM.Persistence.FileBindingStore`, or
      /// `TgLLM.Persistence.LiteDb.LiteDbBindingStore`) makes bindings survive a restart.
      BindingStore: IBindingStore option
      /// Backs the durable MAF agent session for a chat (serialized/deserialized session bytes,
      /// keyed by `ChatId` — see `TgLLM.Core.ISessionStore`/`SessionRecord`). `None` (the default)
      /// means the durable-session feature is OFF: unlike `BindingStore`, there is no in-memory
      /// default here, because a bot with no session store keeps today's purely in-memory per-chat
      /// conversation/approval behavior, byte-identical.
      SessionStore: ISessionStore option
      /// The idle window this bot's background `SessionEvictionSweeper` (`TgLLM.Core.Tools`) uses
      /// to call `ISessionStore.EvictIdle` on the configured session store. `None` (the default)
      /// means the bot's own wiring falls back to 30 days when a store IS configured — unlike
      /// `BindingEvictionInterval`, this field's `None` is meaningful beyond "use the built-in
      /// default": with no session store at all, there is nothing for a sweeper to sweep, so none is
      /// ever started, exactly mirroring `SessionStore` itself having no in-memory default.
      SessionIdleEviction: TimeSpan option
      /// Reclaims a per-chat dispatcher channel/worker once idle this long with nothing buffered.
      /// `None` (the default) keeps the baseline behavior — a chat's resources live for
      /// the whole run, exactly `PerChatChannelDispatcher`'s own no-`idleTimeout` default.
      IdleChatEviction: TimeSpan option
      /// "Now", as seen by expiry/redelivery-dedup decisions. `None` (the
      /// default) uses real wall-clock time; overriding it is primarily useful for deterministic
      /// tests of a host's own expiry logic, not something a production bot normally needs.
      Clock: Clock option
      /// The interval this bot's background `BindingEvictionSweeper` (`TgLLM.Core.Tools`) uses to
      /// call `IBindingStore.EvictExpired` on the configured/default binding store. Unlike
      /// `IdleChatEviction`, `None` here does NOT mean "off" — `IBindingStore.EvictExpired` has no
      /// other production caller, so the sweep itself always runs; `None` (the default) just uses
      /// `BindingEvictionSweeper`'s own built-in interval. Only the INTERVAL is configurable.
      BindingEvictionInterval: TimeSpan option
      /// The host's reaction to an incoming user text message (the Core seam's `MessageHandler`).
      /// `None` (the default) keeps every pre-existing bot's behavior byte-identical — a
      /// `MessageReceived` event reaching `UpdateProcessor` with no handler wired is a no-op.
      /// Config-time only (see `WithOnMessage`'s own doc comment for why): a bot already ingesting
      /// updates cannot late-bind this.
      OnMessage: MessageHandler option }

module CommonConfig =
    let create (botToken: string) : CommonConfig =
        { BotToken = botToken
          BaseUrl = None
          Logger = None
          Tools = None
          BindingStore = None
          SessionStore = None
          SessionIdleEviction = None
          IdleChatEviction = None
          Clock = None
          BindingEvictionInterval = None
          OnMessage = None }

    let withBaseUrl (url: string) (c: CommonConfig) = { c with BaseUrl = Some url }
    /// Surface hook failures / unknown presses through this logger.
    let withLogger (logger: ILogger) (c: CommonConfig) = { c with Logger = Some logger }
    /// Wire a Tool Router registry into this bot.
    let withTools (tools: ToolRegistry) (c: CommonConfig) = { c with Tools = Some tools }
    /// Back tool bindings with a durable store instead of the in-memory default — e.g.
    /// `TgLLM.Persistence.FileBindingStore.openAt "bindings.json"` or
    /// `TgLLM.Persistence.LiteDb.LiteDbBindingStore.OpenAt "bindings.db"`.
    let withBindingStore (store: IBindingStore) (c: CommonConfig) = { c with BindingStore = Some store }
    /// Back the durable MAF agent session with a store instead of today's purely in-memory
    /// conversation/approval behavior — e.g. a `TgLLM.Persistence`/`TgLLM.Persistence.LiteDb`
    /// `ISessionStore` implementation.
    let withSessionStore (store: ISessionStore) (c: CommonConfig) = { c with SessionStore = Some store }
    /// Override the idle window this bot's background session-eviction sweep uses — defaults to 30
    /// days when a session store IS configured. Has no effect on a bot with no session store.
    let withSessionIdleEviction (span: TimeSpan) (c: CommonConfig) = { c with SessionIdleEviction = Some span }
    /// Reclaim an idle chat's dispatcher resources after `timeout` with nothing buffered.
    let withIdleChatEviction (timeout: TimeSpan) (c: CommonConfig) = { c with IdleChatEviction = Some timeout }
    /// Override the clock expiry/redelivery-dedup decisions read "now" from — defaults to real time.
    let withClock (clock: Clock) (c: CommonConfig) = { c with Clock = Some clock }
    /// Override the interval this bot's background binding-eviction sweep uses — defaults to
    /// `BindingEvictionSweeper`'s own built-in interval. The sweep itself runs regardless of
    /// whether this is ever set.
    let withBindingEvictionInterval (interval: TimeSpan) (c: CommonConfig) = { c with BindingEvictionInterval = Some interval }
    /// Answer an incoming user text message with `handler`, run on that message's chat's
    /// dispatcher lane (serialized with that chat's own button presses, in arrival order).
    let withOnMessage (handler: MessageHandler) (c: CommonConfig) = { c with OnMessage = Some handler }

/// Long-polling bot configuration. `WithBaseUrl` overrides the Bot API endpoint — for a local Bot
/// API server, Telegram's test environment, or pointing tests at a fake server.
[<NoComparison; NoEquality>]
type TgBotConfig =
    { Common: CommonConfig }

    static member create(botToken: string) : TgBotConfig = { Common = CommonConfig.create botToken }

    member this.WithBaseUrl(url: string) = { this with Common = this.Common |> CommonConfig.withBaseUrl url }
    member this.WithLogger(logger: ILogger) = { this with Common = this.Common |> CommonConfig.withLogger logger }
    member this.WithTools(tools: ToolRegistry) = { this with Common = this.Common |> CommonConfig.withTools tools }

    member this.WithBindingStore(store: IBindingStore) =
        { this with Common = this.Common |> CommonConfig.withBindingStore store }

    member this.WithSessionStore(store: ISessionStore) =
        { this with Common = this.Common |> CommonConfig.withSessionStore store }

    /// Also sets the idle window this bot's background session-eviction sweep uses (defaults to 30
    /// days when omitted, but only ever a durable-session default — not the 1-arg overload's
    /// "no store at all" state).
    member this.WithSessionStore(store: ISessionStore, idleAfter: TimeSpan) =
        { this with
            Common = this.Common |> CommonConfig.withSessionStore store |> CommonConfig.withSessionIdleEviction idleAfter }

    member this.WithIdleChatEviction(timeout: TimeSpan) =
        { this with Common = this.Common |> CommonConfig.withIdleChatEviction timeout }

    member this.WithClock(clock: Clock) = { this with Common = this.Common |> CommonConfig.withClock clock }

    member this.WithBindingEvictionInterval(interval: TimeSpan) =
        { this with Common = this.Common |> CommonConfig.withBindingEvictionInterval interval }

    member this.WithOnMessage(handler: MessageHandler) = { this with Common = this.Common |> CommonConfig.withOnMessage handler }

/// Webhook bot configuration. `PublicUrl` is the HTTPS URL Telegram POSTs updates to; `SecretToken`
/// is echoed in the `X-Telegram-Bot-Api-Secret-Token` header and verified on every request.
/// `WithBaseUrl` overrides the Bot API endpoint (tests / local Bot API server).
[<NoComparison; NoEquality>]
type TgWebhookConfig =
    { Common: CommonConfig
      PublicUrl: string
      SecretToken: string }

    static member create(botToken: string, publicUrl: string, secretToken: string) : TgWebhookConfig =
        { Common = CommonConfig.create botToken
          PublicUrl = publicUrl
          SecretToken = secretToken }

    member this.WithBaseUrl(url: string) = { this with Common = this.Common |> CommonConfig.withBaseUrl url }
    member this.WithLogger(logger: ILogger) = { this with Common = this.Common |> CommonConfig.withLogger logger }
    member this.WithTools(tools: ToolRegistry) = { this with Common = this.Common |> CommonConfig.withTools tools }

    member this.WithBindingStore(store: IBindingStore) =
        { this with Common = this.Common |> CommonConfig.withBindingStore store }

    member this.WithSessionStore(store: ISessionStore) =
        { this with Common = this.Common |> CommonConfig.withSessionStore store }

    /// Also sets the idle window this bot's background session-eviction sweep uses (defaults to 30
    /// days when omitted, but only ever a durable-session default — not the 1-arg overload's
    /// "no store at all" state).
    member this.WithSessionStore(store: ISessionStore, idleAfter: TimeSpan) =
        { this with
            Common = this.Common |> CommonConfig.withSessionStore store |> CommonConfig.withSessionIdleEviction idleAfter }

    member this.WithIdleChatEviction(timeout: TimeSpan) =
        { this with Common = this.Common |> CommonConfig.withIdleChatEviction timeout }

    member this.WithClock(clock: Clock) = { this with Common = this.Common |> CommonConfig.withClock clock }

    member this.WithBindingEvictionInterval(interval: TimeSpan) =
        { this with Common = this.Common |> CommonConfig.withBindingEvictionInterval interval }

    member this.WithOnMessage(handler: MessageHandler) = { this with Common = this.Common |> CommonConfig.withOnMessage handler }

/// Internal signal ONLY: `TgBot.EditKeyboardPlan`'s own `send` closure to `ToolKeyboardOps.deliver`
/// raises this when the edit classifies as `EditNotFound`, so it reaches `deliver`'s EXISTING
/// throw-triggered compensation (removes the just-saved replacement bindings) exactly as any other
/// `send` failure would — mirrors `TgLLM.Core.UpdateProcessor`'s own `EditKeyboardNotFoundSignal`
/// for the SAME reason (a bot-level edit has no `PressContext`/`ButtonPress` to attribute an
/// `IHookObserver` report to, so it is caught right back here and turned into silent completion,
/// never a throw to the caller). Must be declared at module (not type) level — F# disallows an
/// `exception` inside a class body.
exception private EditKeyboardPlanNotFoundSignal

/// A running bot: ingests updates in the background and lets the agent send keyboards/messages.
/// Dispose (`use!`/`IAsyncDisposable`) to stop ingestion and release the per-chat dispatcher.
[<Sealed>]
type TgBot
    internal
    (
        api: IBotApiClient,
        store: IHookStore,
        bindingStore: IBindingStore,
        tracker: MessageBindingTracker,
        dispatcher: IPressDispatcher,
        toolDispatch: ToolDispatch option,
        tools: ToolRegistry option,
        clock: Clock,
        logger: ILogger option,
        sessionStore: ISessionStore option,
        cts: CancellationTokenSource,
        runTask: Task,
        evictionSweeper: BindingEvictionSweeper,
        sessionEvictionSweeper: SessionEvictionSweeper option,
        webhookSource: WebhookUpdateSource option
    ) =

    member _.SendKeyboard(chat: ChatId, text: MessageText, keyboard: KeyboardSpec) : Task<MessageId> =
        AgentOps.sendKeyboard store api CallbackToken.generate chat text keyboard cts.Token

    member _.SendText(chat: ChatId, text: MessageText) : Task<MessageId> = api.SendText(chat, text, cts.Token)

    /// Parse-mode overload: sends exactly like the overload above, plus a Bot API `parse_mode`
    /// (e.g. `Some MarkdownV2` for an already-escaped MarkdownV2 body). `None` behaves identically
    /// to the overload above.
    member _.SendText(chat: ChatId, text: MessageText, parseMode: ParseMode option) : Task<MessageId> =
        api.SendText(chat, text, parseMode, cts.Token)

    /// This bot's own Tool Router registry, if one was wired in (`TgBotConfig.WithTools`/
    /// `TgWebhookConfig.WithTools`) — `None` means no Tool Router exists, exactly the condition
    /// `SendKeyboardPlan` itself already fails fast on for a plan with tool buttons. Exposed so a
    /// collaborator built AFTER this bot (e.g. an A2UI renderer) can register further tools into
    /// the SAME registry `ToolDispatch` already resolves presses against, rather than needing its
    /// own separate wiring seam.
    member _.Tools: ToolRegistry option = tools

    /// This bot's own "now" (`TgBotConfig.WithClock`, defaulting to real UTC time) — the SAME
    /// clock `SendKeyboardPlan`'s `expiresIn` stamps against and `UpdateProcessor` reads expiry/
    /// redelivery-dedup decisions from. Exposed for the same reason as `Tools`: a collaborator
    /// built after this bot that needs a deterministic "now" (e.g. an A2UI action's timestamp)
    /// should share this bot's own clock rather than reading ambient wall-clock time.
    member _.Clock: Clock = clock

    /// This bot's own logger (`TgBotConfig.WithLogger`/`TgWebhookConfig.WithLogger`), if one was
    /// wired in — `None` means this bot's own hook/tool failures are reported via
    /// `NoopHookObserver` (silently dropped), exactly the condition `wireBot` itself already
    /// resolves for its own `IHookObserver`. Exposed for the same reason as `Tools`/`Clock`: a
    /// collaborator built after this bot (e.g. an A2UI renderer) that wants a sensible logging
    /// default should bridge to the SAME logger this bot itself reports through, rather than needing
    /// its own separate logger-wiring seam.
    member _.Logger: ILogger option = logger

    /// This bot's own session store (`TgBotConfig.WithSessionStore`/`TgWebhookConfig.WithSessionStore`),
    /// if one was wired in — `None` means the durable-session feature is off, exactly the condition
    /// `wireBot` itself leaves unresolved (no in-memory default, unlike `BindingStore`). Exposed for
    /// the same reason as `Tools`/`Clock`/`Logger`: a collaborator built AFTER this bot (e.g. the MAF
    /// bridge) can read the configured session store rather than needing its own separate wiring seam.
    member _.SessionStore: ISessionStore option = sessionStore

    /// Send a keyboard built from a neutral Tool Router plan; presses route to the tools
    /// registered via `TgBotConfig.WithTools`. Delegates to the shared `ToolKeyboardOps.deliver`
    /// (`TgLLM.Core.Tools`) — bindings are saved BEFORE the send completes, and the sent message's
    /// tokens are recorded into `tracker` so a LATER `ctx.EditKeyboardAsync` on that same message
    /// (`UpdateProcessor.makeEditKeyboardAction`) can find and remove them. `staleMessageId = None`
    /// here: a fresh send has no previous binding to remove.
    ///
    /// `owner` scopes every tool button on this keyboard to that presser; omitted (or
    /// `Owner.anyone`) preserves the baseline behavior — any presser resolves the button, unchanged.
    /// `deniedNotice` overrides the notice a refused non-owner sees; omitted uses the built-in
    /// default (`OwnerScope.DefaultDeniedNotice`).
    ///
    /// `expiresIn` stamps every tool binding this send produces with an `ExpiresAt` of this bot's
    /// own clock (`TgBotConfig.WithClock`, defaulting to real UTC time) plus `expiresIn`; omitted
    /// leaves the binding with no expiry, unchanged from before. `singleUse = true` stamps every
    /// binding as consumed after its first successful press; omitted (the default, `false`) leaves
    /// bindings reusable, unchanged from before.
    ///
    /// Fails fast if `plan` has a tool button but NO Tool Router was ever wired in
    /// (`TgBotConfig.WithTools`/`TgWebhookConfig.WithTools`) — without this
    /// check, such a button would reach the wire, get tapped, and silently no-op forever (no
    /// `ToolDispatch` could ever resolve its binding). A URL-only plan is always fine, regardless of
    /// wiring.
    member _.SendKeyboardPlan
        (
            chat: ChatId,
            text: MessageText,
            plan: ToolKeyboard,
            ?owner: OwnerScope,
            ?deniedNotice: string,
            ?expiresIn: TimeSpan,
            ?singleUse: bool,
            ?parseMode: ParseMode
        ) : Task<MessageId> =
        if toolDispatch.IsNone && ToolPlan.hasToolButtons plan then
            invalidOp
                "TgBot.SendKeyboardPlan: this plan has a tool button, but no Tool Router is wired in \
                 (call .WithTools on the bot config first) — every tap would silently no-op forever, \
                 since no ToolDispatch could ever resolve its binding. Wire a Tool Router, or use \
                 only URL buttons if no tool routing is needed."

        let expiresAt = expiresIn |> Option.map (fun span -> clock () + span)

        ToolKeyboardOps.deliver
            "TgBot.SendKeyboardPlan"
            CallbackToken.generate
            bindingStore
            tracker
            chat
            None
            (defaultArg owner Anyone)
            // Normalize away a `Some null`/`Some ""`: a C# caller omitting `deniedNotice` reaches this
            // F# optional parameter as `Some null` (the "omitted ⇒ None" sugar is F#-caller-side only),
            // which would store an empty override and show a blank notice instead of the built-in
            // default. Collapse a null/empty override to `None` so the default wins.
            (match deniedNotice with
             | Some s when not (System.String.IsNullOrEmpty s) -> Some s
             | _ -> None)
            expiresAt
            (defaultArg singleUse false)
            (fun registeredKeyboard -> api.SendKeyboard(chat, text, registeredKeyboard, parseMode, cts.Token))
            cts.Token
            plan

    /// C#-friendly overload that accepts a raw string (validated with `MessageText.unsafe`).
    member this.SendKeyboardPlan
        (
            chat: ChatId,
            text: string,
            plan: ToolKeyboard,
            ?owner: OwnerScope,
            ?deniedNotice: string,
            ?expiresIn: TimeSpan,
            ?singleUse: bool,
            ?parseMode: ParseMode
        ) : Task<MessageId> =
        this.SendKeyboardPlan(
            chat,
            MessageText.unsafe text,
            plan,
            ?owner = owner,
            ?deniedNotice = deniedNotice,
            ?expiresIn = expiresIn,
            ?singleUse = singleUse,
            ?parseMode = parseMode
        )

    /// C#-friendly overloads that accept a raw string (validated with `MessageText.unsafe`), so the
    /// C# façade never touches the F# `MessageText` type.
    member this.SendKeyboard(chat: ChatId, text: string, keyboard: KeyboardSpec) : Task<MessageId> =
        this.SendKeyboard(chat, MessageText.unsafe text, keyboard)

    member this.SendText(chat: ChatId, text: string) : Task<MessageId> =
        this.SendText(chat, MessageText.unsafe text)

    /// Edit an existing message's text in place from OUTSIDE a button press, leaving its CURRENT
    /// keyboard untouched — the bot-level counterpart to `PressContext.EditTextAsync` for an
    /// agent-initiated push (e.g. an A2UI surface whose update leaves it with no buttons at all)
    /// rather than a tap. `parseMode` renders `text` with the requested formatting, same "`None` =
    /// plain text" contract as `SendText`'s parse-mode overload. Never throws for a vanished message
    /// (`EditNotFound` — the user deleted it between send and this edit): returns the classified
    /// `EditOutcome` instead, so a caller can react to it (e.g. the A2UI façade evicts a surface's
    /// tracked message id on `EditNotFound` so its NEXT update re-sends a fresh message rather than
    /// trying, and failing, to edit the same vanished one forever).
    ///
    /// This call carries no keyboard of its own — a caller reaches for it precisely when its own
    /// content no longer has ONE (e.g. an A2UI surface whose update removed its last button) — so any
    /// tool bindings `tracker` still has recorded for `(chat, messageId)` from a PREVIOUS keyboard on
    /// this same message are now unreachable by anything live and are cleaned up here, exactly like
    /// `EditKeyboardPlan`'s own stale-token removal does for a REPLACEMENT keyboard, just against an
    /// empty one. Skipped on `EditNotFound`/an edit that never reached the wire — nothing to clean up
    /// against a message this bot no longer has any confirmed state for.
    member _.EditText(chat: ChatId, messageId: MessageId, text: MessageText, ?parseMode: ParseMode) : Task<EditOutcome> =
        task {
            let! outcome = api.EditMessageText(chat, messageId, text, None, parseMode, cts.Token)

            match outcome with
            | EditApplied
            | EditNotModified ->
                match tracker.TryGetPrevious(chat, messageId) with
                | Some staleTokens when not (List.isEmpty staleTokens) ->
                    do! (bindingStore.Remove(staleTokens, cts.Token)).AsTask()
                    tracker.Record(chat, messageId, [])
                | _ -> ()
            | EditNotFound -> ()

            return outcome
        }

    /// Edit an existing message's text AND keyboard in place from OUTSIDE a button press — the
    /// bot-level counterpart to `PressContext.EditKeyboardAsync` for an agent-initiated push (e.g.
    /// an A2UI `updateComponents`) rather than a tap. Re-plans `plan` via the SAME
    /// `ToolKeyboardOps.deliver` edit path `PressContext.EditKeyboardAsync` uses:
    /// `staleMessageId = Some messageId` removes `(chat, messageId)`'s previously-tracked bindings
    /// once the edit reaches the wire, and compensates — removing the just-saved replacement
    /// bindings — if the edit itself fails. `parseMode` renders `text` with the requested
    /// formatting, same "`None` = plain text" contract as `SendText`'s parse-mode overload.
    ///
    /// Never throws for a vanished message (`EditNotFound`): returns the classified `EditOutcome`
    /// instead, so a caller can react to it (e.g. the A2UI façade evicts a surface's tracked message
    /// id on `EditNotFound` so its NEXT update re-sends a fresh message rather than trying, and
    /// failing, to edit the same vanished one forever — `PressContext.EditKeyboardAsync`'s own
    /// convention is the SAME classify-don't-throw contract, just with its own observer report
    /// instead of a return value, since a tap's `Tool = PressContext -> Task` has no return channel
    /// back to a caller). Fails fast (same check as `SendKeyboardPlan`) if `plan` has a tool button
    /// but no Tool Router is wired in.
    member _.EditKeyboardPlan
        (
            chat: ChatId,
            messageId: MessageId,
            text: MessageText,
            plan: ToolKeyboard,
            ?parseMode: ParseMode
        ) : Task<EditOutcome> =
        if toolDispatch.IsNone && ToolPlan.hasToolButtons plan then
            invalidOp
                "TgBot.EditKeyboardPlan: this plan has a tool button, but no Tool Router is wired in \
                 (call .WithTools on the bot config first) — every tap would silently no-op forever, \
                 since no ToolDispatch could ever resolve its binding. Wire a Tool Router, or use \
                 only URL buttons if no tool routing is needed."

        task {
            try
                let! _ =
                    ToolKeyboardOps.deliver
                        "TgBot.EditKeyboardPlan"
                        CallbackToken.generate
                        bindingStore
                        tracker
                        chat
                        (Some messageId)
                        Anyone
                        None
                        None
                        false
                        (fun registeredKeyboard ->
                            task {
                                let! outcome = api.EditMessageText(chat, messageId, text, Some registeredKeyboard, parseMode, cts.Token)

                                match outcome with
                                | EditApplied
                                | EditNotModified -> return messageId
                                | EditNotFound -> return raise EditKeyboardPlanNotFoundSignal
                            })
                        cts.Token
                        plan

                return EditApplied
            with EditKeyboardPlanNotFoundSignal ->
                return EditNotFound
        }

    /// Deletes a message from OUTSIDE a button press (e.g. an A2UI `deleteSurface`) and cleans up
    /// any tool bindings this bot ever recorded for it (`MessageBindingTracker`), so a deleted
    /// surface's buttons don't linger in the binding store forever. Soft on an already-vanished
    /// message — the underlying `IBotApiClient.DeleteMessage` classifies "message to delete not
    /// found" rather than throwing — so this always completes normally.
    member _.DeleteMessage(chat: ChatId, messageId: MessageId) : Task =
        task {
            let! _ = api.DeleteMessage(chat, messageId, cts.Token)

            match tracker.TryGetPrevious(chat, messageId) with
            | Some tokens when not (List.isEmpty tokens) -> do! (bindingStore.Remove(tokens, cts.Token)).AsTask()
            | _ -> ()
        }
        :> Task

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
                do! (evictionSweeper :> IAsyncDisposable).DisposeAsync()

                match sessionEvictionSweeper with
                | Some s -> do! (s :> IAsyncDisposable).DisposeAsync()
                | None -> ()

                cts.Dispose()
            }
            |> ValueTask

    static member private buildClient(botToken: string, baseUrl: string option) : ITelegramBotClient =
        let options =
            match baseUrl with
            | Some url -> TelegramBotClientOptions(botToken, url)
            | None -> TelegramBotClientOptions(botToken)

        TelegramBotClient(options) :> ITelegramBotClient

    /// Shared Core wiring for both transports: builds the API client, hook store, binding store,
    /// tracker, dispatcher, and observer, threads them (plus an optional `ToolDispatch`) into an
    /// `UpdateProcessor`, and starts its run loop. `startPolling`/`startWebhook` differ only in
    /// which `IUpdateSource` they hand in and whether a `WebhookUpdateSource` exists for
    /// `TgBot.WebhookSource` — every other collaborator (and its wiring) is identical regardless
    /// of transport.
    static member private wireBot
        (common: CommonConfig)
        (client: ITelegramBotClient)
        (source: IUpdateSource)
        (webhookSource: WebhookUpdateSource option)
        : TgBot =
        let api = TelegramBotApiClient(client) :> IBotApiClient
        let store = InMemoryHookStore() :> IHookStore
        let bindingStore = common.BindingStore |> Option.defaultWith (fun () -> InMemoryBindingStore() :> IBindingStore)
        // NO in-memory default here (unlike `bindingStore` above) — `None` means the durable-session
        // feature stays off, keeping every bot's in-memory conversation/approval behavior unchanged.
        let sessionStore = common.SessionStore
        // Shared with `ToolDispatch` below (via `UpdateProcessor`) so `SendKeyboardPlan`'s
        // send-time record and `EditKeyboardAsync`'s edit-time remove agree on one message's
        // history of bindings.
        let tracker = MessageBindingTracker()
        let dispatcher = new PerChatChannelDispatcher(?idleTimeout = common.IdleChatEviction) :> IPressDispatcher

        let observer =
            match common.Logger with
            | Some logger -> LoggingHookObserver logger :> IHookObserver
            | None -> NoopHookObserver() :> IHookObserver

        // Same "bridge to the configured logger, else noop" resolution as `observer` above, for
        // the message-side seam. Built regardless of whether `common.OnMessage` is set — `Some`'s
        // own cost is nothing until a `MessageReceived` event actually reaches `UpdateProcessor`.
        let messageObserver =
            match common.Logger with
            | Some logger -> LoggingMessageObserver logger :> IMessageObserver
            | None -> NoopMessageObserver() :> IMessageObserver

        let toolDispatch = common.Tools |> Option.map (fun tools -> ToolDispatch(tools.Registry, bindingStore, tracker))
        // Resolved once here (not read ambiently from `DateTimeOffset.UtcNow`) so `SendKeyboardPlan`
        // stamps `expiresIn` against the SAME "now" `UpdateProcessor` uses to decide whether a press
        // is still live — matching `common.Clock`'s own default when the host never overrides it.
        let clock = defaultArg common.Clock (fun () -> DateTimeOffset.UtcNow)

        // Started here — alongside the run loop, below — and disposed alongside it
        // (`DisposeAsync`): `IBindingStore.EvictExpired` has no other production caller, so
        // WITHOUT this, `bindingStore` (whichever store backs this bot, default or host-supplied)
        // would grow unbounded with expiring/expired bindings forever. Shares this
        // bot's OWN `clock`, so a host that overrides it for deterministic tests gets a
        // deterministic sweep too.
        let evictionSweeper = new BindingEvictionSweeper(bindingStore, clock, ?interval = common.BindingEvictionInterval)

        // Started ONLY when a session store is configured — unlike `evictionSweeper` above, a bot
        // with no durable session has nothing for this sweeper to sweep, so none is ever built
        // (mirrors `sessionStore` itself having no in-memory default). `SessionIdleEviction`'s own
        // `None` here means "use this bot's own 30-day default", NOT "off" — that distinction is
        // `SessionStore option` itself, not this field.
        let sessionEvictionSweeper =
            match sessionStore with
            | Some store -> Some(new SessionEvictionSweeper(store, clock, defaultArg common.SessionIdleEviction (TimeSpan.FromDays 30.0)))
            | None -> None

        let processor =
            UpdateProcessor(
                source,
                store,
                api,
                dispatcher,
                observer,
                ?toolDispatch = toolDispatch,
                clock = clock,
                ?onMessage = common.OnMessage,
                messageObserver = messageObserver
            )
        let cts = new CancellationTokenSource()

        // A faulted run loop must be surfaced via `observer`, not silently swallowed at `Dispose` —
        // `DisposeAsync`'s own `try ... with _ -> ()` around `runTask` stays
        // as a defensive backstop only, since this wrapper already reports (and then completes
        // normally) for every ordinary failure. `OperationCanceledException` is the expected
        // shutdown path (`DisposeAsync` cancels `cts`), not a failure worth reporting.
        let runTask =
            task {
                try
                    do! processor.RunAsync cts.Token
                with
                | :? OperationCanceledException -> ()
                | ex -> observer.OnRunLoopFailed ex
            }

        new TgBot(
            api,
            store,
            bindingStore,
            tracker,
            dispatcher,
            toolDispatch,
            common.Tools,
            clock,
            common.Logger,
            sessionStore,
            cts,
            runTask,
            evictionSweeper,
            sessionEvictionSweeper,
            webhookSource
        )

    /// Start ingesting updates via long polling (deletes any configured webhook first). The returned
    /// bot is already polling in the background.
    static member startPolling(config: TgBotConfig) : Task<TgBot> =
        task {
            let client = TgBot.buildClient (config.Common.BotToken, config.Common.BaseUrl)
            let source = LongPollingUpdateSource(client) :> IUpdateSource
            return TgBot.wireBot config.Common client source None
        }

    /// Start ingesting updates via webhooks. Registers the webhook with Telegram (`setWebhook` with
    /// the secret token) and consumes pushed updates. Map the HTTP endpoint separately with
    /// `app.MapTelegramWebhook(bot.WebhookSource, config.SecretToken)`. Hook code is identical to a
    /// polling bot.
    static member startWebhook(config: TgWebhookConfig) : Task<TgBot> =
        task {
            let client = TgBot.buildClient (config.Common.BotToken, config.Common.BaseUrl)
            do! client.SetWebhook(url = config.PublicUrl, secretToken = config.SecretToken)
            let webhookSource = WebhookUpdateSource()
            return TgBot.wireBot config.Common client (webhookSource :> IUpdateSource) (Some webhookSource)
        }
