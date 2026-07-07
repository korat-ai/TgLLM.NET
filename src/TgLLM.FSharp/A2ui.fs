/// The F# façade for the A2UI renderer: attaches a `TgLLM.A2UI.SurfaceRegistry` to a running
/// `TgBot`, registers the internal `a2ui-action` tool into the bot's OWN Tool Router, and exposes
/// `Ingest` — parse an agent->renderer message, decide its `RenderEffect` (`SurfaceRegistry.Apply`),
/// and carry it out over the bot's send/edit-in-place/delete path with MarkdownV2: `SendNew` sends
/// the first message; `EditExisting` edits the SAME message in place (`TgBot.EditKeyboardPlan`/
/// `EditText`); `DeleteMessage` deletes it (`TgBot.DeleteMessage`). Every surfaced `A2uiError` —
/// including one collected mid-render alongside successfully-rendered siblings — reaches the
/// attached `IA2uiObserver`, not just the immediate caller's own `Result`.
namespace TgLLM.FSharp

open System.Threading.Tasks
open Microsoft.Extensions.Logging
open TgLLM.Core
open TgLLM.A2UI

/// Bridges the A2UI observability seam to an `ILogger`: every surfaced `A2uiError` (unknown
/// catalog, unsupported component, malformed message, duplicate/unknown surface) and every
/// malformed tap-time action are logged, never silently dropped — the A2UI counterpart to
/// `LoggingHookObserver` above.
type LoggingA2uiObserver(logger: ILogger) =
    interface IA2uiObserver with
        member _.OnA2uiError(error: A2uiError) =
            logger.LogWarning("A2UI: {Description}", [| A2uiError.describe error :> obj |])

        member _.OnMalformedAction(descriptor: ActionDescriptor) =
            logger.LogWarning(
                "A2UI action {Name} for surface {SurfaceId} requested a response (wantResponse) but \
                 carried no actionId to correlate it — dropped rather than delivered",
                [| descriptor.Name :> obj; descriptor.SurfaceId :> obj |]
            )

/// One ingested A2UI surface, live over a running bot. Built by `A2ui.renderer`/`A2ui.rendererWithObserver`.
[<Sealed>]
type A2uiRenderer internal (bot: TgBot, registry: SurfaceRegistry, observer: IA2uiObserver) =

    /// The catalog this renderer advertises — the only one it currently renders.
    member _.Catalog: Catalog = Catalog.telegramBasic

    /// Ingests one A2UI agent->renderer message for `chat`. Never throws: a malformed message, an
    /// unknown catalog, an unsupported component, or a duplicate/unknown surface all surface as
    /// `Error` AND are reported to this renderer's `IA2uiObserver` — the two channels always agree
    /// on the condition itself; only the AUDIENCE differs (this call's own caller vs. anything else
    /// watching this bot's health). An `Unsupported` component collected alongside supported
    /// siblings that still render is reported ONLY to the observer, since the call itself still
    /// returns `Ok` (the siblings really did render). Carries out the decided `RenderEffect`:
    /// `SendNew` sends the first message; `EditExisting` edits the SAME message in place;
    /// `DeleteMessage` deletes it — `chat` is the host's own `Ingest` parameter throughout, since
    /// A2UI carries no chat identity of its own and a live surface stays bound to the chat it was
    /// created in.
    member _.Ingest(chat: ChatId, a2uiMessageJson: string) : Task<Result<unit, A2uiError>> =
        task {
            match A2uiMessage.parse a2uiMessageJson with
            | Error e -> return A2uiObservability.reportError observer e
            | Ok msg ->
                match registry.Apply(chat, msg) with
                | Error e -> return A2uiObservability.reportError observer e
                | Ok NoEffect -> return Ok()
                | Ok(SendNew(sendChat, rendered)) ->
                    A2uiObservability.reportUnsupported observer rendered

                    match MessageText.create rendered.Text with
                    | Error _ -> return A2uiObservability.reportError observer A2uiRenderer.NoRenderableTextError
                    | Ok text ->
                        let! messageId =
                            if List.isEmpty rendered.Keyboard.Rows then
                                bot.SendText(sendChat, text, Some MarkdownV2)
                            else
                                bot.SendKeyboardPlan(sendChat, text, rendered.Keyboard, parseMode = MarkdownV2)

                        registry.RecordMessageId(A2uiMessage.surfaceId msg, messageId)
                        return Ok()
                | Ok(EditExisting(messageId, rendered)) ->
                    A2uiObservability.reportUnsupported observer rendered

                    match MessageText.create rendered.Text with
                    | Error _ -> return A2uiObservability.reportError observer A2uiRenderer.NoRenderableTextError
                    | Ok text ->
                        do!
                            if List.isEmpty rendered.Keyboard.Rows then
                                bot.EditText(chat, messageId, text, parseMode = MarkdownV2)
                            else
                                bot.EditKeyboardPlan(chat, messageId, text, rendered.Keyboard, parseMode = MarkdownV2)

                        return Ok()
                | Ok(DeleteMessage messageId) ->
                    do! bot.DeleteMessage(chat, messageId)
                    return Ok()
        }

    /// A rendered surface with no Text/Divider/Image anywhere in its tree has no body a Bot API
    /// message can carry (`sendMessage`/`editMessageText` both require non-empty text) — reused by
    /// every `Ingest` branch that renders a surface, so the wording stays identical regardless of
    /// which effect hit it. This is also what a surface whose ONLY content is unsupported resolves
    /// to: `Renderer.render` collects no `BodyLines` for it, so its text is empty and this same
    /// branch fires — surfaced (both here and, per-component, via `A2uiObservability.reportUnsupported`
    /// above), never an empty/garbage message on the wire.
    static member private NoRenderableTextError: A2uiError =
        MalformedMessage "the rendered surface has no renderable text (a Bot API message requires non-empty text)"

/// Builds an `A2uiRenderer` over a running bot.
module A2ui =

    [<Literal>]
    let private ActionToolName = "a2ui-action"

    /// `bot`'s own logger, bridged to `IA2uiObserver` (`LoggingA2uiObserver`), or a silent
    /// `NoopA2uiObserver` if `bot` has none — the default an observer-less caller of `renderer` gets,
    /// mirroring `wireBot`'s own `LoggingHookObserver`-or-`NoopHookObserver` resolution for the rest
    /// of the Tool Router.
    let private defaultObserver (bot: TgBot) : IA2uiObserver =
        match bot.Logger with
        | Some logger -> LoggingA2uiObserver logger :> IA2uiObserver
        | None -> NoopA2uiObserver() :> IA2uiObserver

    /// Shared build path for both `renderer` and `rendererWithObserver`: registers the internal
    /// `a2ui-action` tool into the bot's OWN Tool Router (so tapping a `ServerEvent` Button routes
    /// through the hardened engine, resolves its context, and hands the result to `sink`, reporting
    /// a malformed tap to `observer`), and holds a fresh `SurfaceRegistry`. Requires `bot` to already
    /// have a Tool Router wired in (`TgBotConfig.WithTools`/`TgWebhookConfig.WithTools`) — without
    /// one, a rendered surface's own tool buttons would reach the wire, get tapped, and silently
    /// no-op forever, exactly the condition `TgBot.SendKeyboardPlan` itself already fails fast on for
    /// any other tool plan.
    let private build (bot: TgBot) (sink: ActionSink) (observer: IA2uiObserver) : A2uiRenderer =
        match bot.Tools with
        | None ->
            invalidOp
                "A2ui.renderer requires a Tool Router wired into the bot (call .WithTools on the \
                 bot config first) so its internal a2ui-action tool can route ServerEvent button \
                 taps — without one, every tap would silently no-op forever, since no ToolDispatch \
                 could ever resolve its binding."
        | Some tools ->
            let registry = SurfaceRegistry(Catalog.telegramBasic)

            match ToolName.create ActionToolName with
            | Error e -> invalidOp $"unreachable: the literal tool name '{ActionToolName}' failed validation ({e})"
            | Ok toolName ->
                tools.Registry.Register(toolName, A2uiActionTool.create registry sink bot.Clock observer)
                A2uiRenderer(bot, registry, observer)

    /// Attaches an A2UI renderer to `bot`, reporting every surfaced condition through `bot`'s own
    /// logger if it has one (`defaultObserver`), or silently if it doesn't. Use
    /// `rendererWithObserver` to supply a specific `IA2uiObserver` instead (F# has no optional-
    /// argument sugar for a plain module function, so the observer-carrying variant is this sibling
    /// rather than a trailing `?observer` parameter).
    let renderer (bot: TgBot) (sink: ActionSink) : A2uiRenderer = build bot sink (defaultObserver bot)

    /// Attaches an A2UI renderer to `bot`, reporting every surfaced condition to `observer` instead
    /// of `bot`'s own logger — the C# façade's `A2uiRenderer.Create` and any F# caller that wants a
    /// custom `IA2uiObserver` (rather than the logging default `renderer` uses) go through this.
    let rendererWithObserver (bot: TgBot) (sink: ActionSink) (observer: IA2uiObserver) : A2uiRenderer =
        build bot sink observer
