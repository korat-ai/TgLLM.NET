/// The F# façade for the A2UI renderer: attaches a `TgLLM.A2UI.SurfaceRegistry` to a running
/// `TgBot`, registers the internal `a2ui-action` tool into the bot's OWN Tool Router, and exposes
/// `Ingest` — parse an agent->renderer message, decide its `RenderEffect` (`SurfaceRegistry.Apply`),
/// and carry it out over the bot's send/edit-in-place/delete path with MarkdownV2: `SendNew` sends
/// the first message; `EditExisting` edits the SAME message in place (`TgBot.EditKeyboardPlan`/
/// `EditText`); `DeleteMessage` deletes it (`TgBot.DeleteMessage`).
namespace TgLLM.FSharp

open System.Threading.Tasks
open TgLLM.Core
open TgLLM.A2UI

/// One ingested A2UI surface, live over a running bot. Built by `A2ui.renderer`.
[<Sealed>]
type A2uiRenderer internal (bot: TgBot, registry: SurfaceRegistry) =

    /// The catalog this renderer advertises — the only one it currently renders.
    member _.Catalog: Catalog = Catalog.telegramBasic

    /// Ingests one A2UI agent->renderer message for `chat`. Never throws: a malformed message, an
    /// unknown catalog, an unsupported component, or a duplicate/unknown surface all surface as
    /// `Error`, not an exception. Carries out the decided `RenderEffect`: `SendNew` sends the first
    /// message; `EditExisting` edits the SAME message in place; `DeleteMessage` deletes it —
    /// `chat` is the host's own `Ingest` parameter throughout, since A2UI carries no chat identity
    /// of its own and a live surface stays bound to the chat it was created in.
    member _.Ingest(chat: ChatId, a2uiMessageJson: string) : Task<Result<unit, A2uiError>> =
        task {
            match A2uiMessage.parse a2uiMessageJson with
            | Error e -> return Error e
            | Ok msg ->
                match registry.Apply(chat, msg) with
                | Error e -> return Error e
                | Ok NoEffect -> return Ok()
                | Ok(SendNew(sendChat, rendered)) ->
                    match MessageText.create rendered.Text with
                    | Error _ -> return Error(A2uiRenderer.NoRenderableTextError)
                    | Ok text ->
                        let! messageId =
                            if List.isEmpty rendered.Keyboard.Rows then
                                bot.SendText(sendChat, text, Some MarkdownV2)
                            else
                                bot.SendKeyboardPlan(sendChat, text, rendered.Keyboard, parseMode = MarkdownV2)

                        registry.RecordMessageId(A2uiMessage.surfaceId msg, messageId)
                        return Ok()
                | Ok(EditExisting(messageId, rendered)) ->
                    match MessageText.create rendered.Text with
                    | Error _ -> return Error(A2uiRenderer.NoRenderableTextError)
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
    /// which effect hit it.
    static member private NoRenderableTextError: A2uiError =
        MalformedMessage "the rendered surface has no renderable text (a Bot API message requires non-empty text)"

/// Builds an `A2uiRenderer` over a running bot.
module A2ui =

    [<Literal>]
    let private ActionToolName = "a2ui-action"

    /// Attaches an A2UI renderer to `bot`: registers the internal `a2ui-action` tool into the
    /// bot's OWN Tool Router (so tapping a `ServerEvent` Button routes through the hardened
    /// engine, resolves its context, and hands the result to `sink`), and holds a fresh
    /// `SurfaceRegistry`. Requires `bot` to already have a Tool Router wired in
    /// (`TgBotConfig.WithTools`/`TgWebhookConfig.WithTools`) — without one, a rendered surface's
    /// own tool buttons would reach the wire, get tapped, and silently no-op forever, exactly the
    /// condition `TgBot.SendKeyboardPlan` itself already fails fast on for any other tool plan.
    let renderer (bot: TgBot) (sink: ActionSink) : A2uiRenderer =
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
                tools.Registry.Register(toolName, A2uiActionTool.create registry sink bot.Clock (NoopA2uiActionObserver()))
                A2uiRenderer(bot, registry)
