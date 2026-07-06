/// `TelegramBotApiClient : IBotApiClient` over Telegram.Bot 22.10.1, plus the pure Telegram.Bot
/// `CallbackQuery` -> `ButtonPress`/`AgentEvent` mapping `LongPollingUpdateSource` (compiled after
/// this file) reuses.
///
/// Bot API facts this file relies on, verified against core.telegram.org and Telegram.Bot's own
/// source (Principle V):
///   - `callback_data` is 1-64 BYTES. `CallbackToken.value` always produces a 22-character
///     unpadded base64url string (16 bytes encoded), well under that limit.
///   - `answerCallbackQuery` must be called for every callback query (known or not) to clear the
///     client's loading spinner; it accepts no keyboard-specific state, only the query id plus
///     optional notification text/alert/url/cache-time (all left at their defaults here — this
///     slice has no requirement to show the user a notification banner).
///   - `sendMessage`'s `text` is capped at 4096 characters — matches `MessageText.MaxLength`
///     (`TgLLM.Core.Values`), enforced upstream by `MessageText.create`/`unsafe` before any text
///     reaches this client.
///   - The HTTP shape (`POST {baseUrl}/bot{token}/{method}`, JSON body, `{"ok":true,"result":...}`
///     envelope) was confirmed by reading Telegram.Bot's own
///     `TelegramBotClient.cs`/`TelegramBotClientOptions.cs`/`Requests/RequestBase.cs` sources
///     (github.com/TelegramBots/Telegram.Bot) rather than assumed from an older version's memory.
namespace TgLLM.BotApi

open System.Threading
open System.Threading.Tasks
open FSharp.UMX
open Telegram.Bot
open Telegram.Bot.Types
open Telegram.Bot.Types.Enums
open Telegram.Bot.Types.ReplyMarkups
open TgLLM.Core

/// Pure mappings between Telegram.Bot's wire-level types and this library's transport-agnostic
/// domain model. No I/O; total and side-effect-free, so they're reused as-is by
/// `LongPollingUpdateSource` and are unit-testable without a fake HTTP server.
module Mapping =

    /// `RegisteredKeyboard` -> Telegram.Bot's `InlineKeyboardMarkup`: one `InlineKeyboardButton`
    /// per registered button. `Callback` buttons get `callback_data` set to the button's opaque
    /// `CallbackToken` (never the label or any agent state); `Url` buttons get a plain client-side
    /// link — `url`/`callback_data` are mutually exclusive on one button, so these two cases map
    /// to Telegram.Bot's two distinct factory methods, never both.
    let toInlineKeyboardMarkup (RegisteredKeyboard rows) : InlineKeyboardMarkup =
        rows
        |> List.map (fun row ->
            row
            |> List.map (fun (button: RegisteredButton) ->
                match button with
                | Callback(label, token) -> InlineKeyboardButton.WithCallbackData(ButtonLabel.value label, CallbackToken.value token)
                | Url(label, url) -> InlineKeyboardButton.WithUrl(ButtonLabel.value label, url))
            |> List.toSeq)
        |> List.toSeq
        |> InlineKeyboardMarkup

    /// Recovers the tapped button's visible label from the message's OWN current keyboard (the
    /// only place Telegram tells us what the button said — `CallbackQuery` itself carries only
    /// `Data`, never the label). Falls back to a placeholder for the protocol-impossible case
    /// where the callback's `Data` isn't found on the message's keyboard (e.g. the message's
    /// keyboard was edited away between send and tap); never throws, matching this module's total
    /// (pure, never-fails) mapping contract.
    let private placeholderLabel: ButtonLabel =
        match TgLLM.Core.ButtonLabel.create "(unknown button)" with
        | Ok l -> l
        | Error e -> failwith $"unreachable: hardcoded placeholder label failed validation ({e})"

    let private findButtonLabel (message: Message) (callbackData: string) : ButtonLabel =
        let matchingButton =
            message.ReplyMarkup
            |> Option.ofObj
            |> Option.bind (fun markup ->
                markup.InlineKeyboard
                |> Seq.collect id
                |> Seq.tryFind (fun button -> button.CallbackData = callbackData))

        match matchingButton with
        | Some button ->
            match ButtonLabel.create button.Text with
            | Ok label -> label
            | Error _ -> placeholderLabel
        | None -> placeholderLabel

    /// One Telegram.Bot `Update` -> `AgentEvent voption` (total; never throws). `ValueNone` when
    /// the update isn't a mappable button press: no `CallbackQuery`, no `Data` (e.g. a `Game`
    /// callback), or no `Message` (the callback query originated from an inline-mode message, or
    /// the original message is too old for Telegram to attach; `ButtonPress` requires
    /// `Chat`/`MessageId`, which only the message carries, so such updates carry no `ButtonPress`
    /// and are skipped rather than guessed at).
    let toAgentEvent (update: Update) : AgentEvent voption =
        match update.CallbackQuery |> Option.ofObj with
        | None -> ValueNone
        | Some query ->
            match query.Message |> Option.ofObj, CallbackToken.tryParse query.Data with
            | Some message, ValueSome token ->
                let press: ButtonPress =
                    { Token = token
                      QueryId = UMX.tag<callbackQueryId> query.Id
                      Chat = UMX.tag<chatId> message.Chat.Id
                      User =
                        { Id = UMX.tag<userId> query.From.Id
                          FirstName = query.From.FirstName
                          Username = query.From.Username }
                      MessageId = UMX.tag<messageId> (int64 message.MessageId)
                      // `query.Data` is nullable per the Bot API schema; `token` was just parsed
                      // from it successfully, and `CallbackToken.value` roundtrips to the exact
                      // same (non-null) string, so this is both null-safe and equivalent.
                      ButtonLabel = findButtonLabel message (CallbackToken.value token) }

                ValueSome(ButtonPressed press)
            | _ -> ValueNone

/// `IBotApiClient` over a real (or fake-hosted) `ITelegramBotClient`. Injected rather than
/// constructed from a bot token directly, so the façade owns client lifetime/HttpClient choice
/// and tests can point it at `FakeBotApiServer` via `TelegramBotClientOptions`'s `baseUrl`
/// override.
[<Sealed>]
type TelegramBotApiClient(client: ITelegramBotClient) =

    interface IBotApiClient with
        member _.SendText(chat: ChatId, text: MessageText, ct: CancellationToken) : Task<MessageId> =
            task {
                let! message =
                    client.SendMessage(
                        chatId = Telegram.Bot.Types.ChatId(UMX.untag chat),
                        text = MessageText.value text,
                        cancellationToken = ct
                    )

                return UMX.tag<messageId> (int64 message.MessageId)
            }

        member _.SendKeyboard
            (
                chat: ChatId,
                text: MessageText,
                keyboard: RegisteredKeyboard,
                ct: CancellationToken
            ) : Task<MessageId> =
            task {
                let markup = Mapping.toInlineKeyboardMarkup keyboard

                let! message =
                    client.SendMessage(
                        chatId = Telegram.Bot.Types.ChatId(UMX.untag chat),
                        text = MessageText.value text,
                        replyMarkup = (markup :> ReplyMarkup),
                        cancellationToken = ct
                    )

                return UMX.tag<messageId> (int64 message.MessageId)
            }

        member _.AnswerCallback(query: CallbackQueryId, ct: CancellationToken) : Task =
            client.AnswerCallbackQuery(callbackQueryId = UMX.untag query, cancellationToken = ct)

        /// Tool Router deferred-ack overload: `text`/`showAlert` map directly onto Telegram.Bot's
        /// `AnswerCallbackQuery` extension (verified against its own source, Principle V);
        /// `url`/`cacheTime` are left at their defaults, same as the no-arg overload above.
        member _.AnswerCallback(query: CallbackQueryId, text: string option, showAlert: bool, ct: CancellationToken) : Task =
            client.AnswerCallbackQuery(
                callbackQueryId = UMX.untag query,
                text = Option.toObj text,
                showAlert = showAlert,
                cancellationToken = ct
            )

        /// `editMessageText`'s `reply_markup` is OPTIONAL — omitting it (`None` here) leaves the
        /// message's CURRENT keyboard untouched, verified against Telegram.Bot's own
        /// `EditMessageText` extension (Principle V); `Some` replaces it, same unconditional
        /// mapping `SendKeyboard` already uses. Deliberately does NOT catch `ApiRequestException`
        /// here (`"message to edit not found"`/`"message is not modified"`) — see `Ports.fs`'s
        /// `EditMessageText` doc comment for why letting it propagate is the right layer:
        /// `UpdateProcessor`'s existing per-tool try/with already catches and reports any
        /// tool-body exception via `IHookObserver`, and this port has no `ButtonPress` to
        /// attribute a failure to in the first place.
        member _.EditMessageText
            (
                chat: ChatId,
                message: MessageId,
                text: MessageText,
                keyboard: RegisteredKeyboard option,
                ct: CancellationToken
            ) : Task =
            let replyMarkup = keyboard |> Option.map Mapping.toInlineKeyboardMarkup |> Option.toObj

            client.EditMessageText(
                chatId = Telegram.Bot.Types.ChatId(UMX.untag chat),
                messageId = int (UMX.untag message),
                text = MessageText.value text,
                replyMarkup = replyMarkup,
                cancellationToken = ct
            )
            :> Task

        /// Replaces the message's keyboard only, leaving its text untouched. Same
        /// propagate-don't-swallow error handling as `EditMessageText` above.
        member _.EditMessageReplyMarkup
            (
                chat: ChatId,
                message: MessageId,
                keyboard: RegisteredKeyboard option,
                ct: CancellationToken
            ) : Task =
            let replyMarkup = keyboard |> Option.map Mapping.toInlineKeyboardMarkup |> Option.toObj

            client.EditMessageReplyMarkup(
                chatId = Telegram.Bot.Types.ChatId(UMX.untag chat),
                messageId = int (UMX.untag message),
                replyMarkup = replyMarkup,
                cancellationToken = ct
            )
            :> Task
