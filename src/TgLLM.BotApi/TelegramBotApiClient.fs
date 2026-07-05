/// T024 (contracts/core-ports.md "IBotApiClient"). `TelegramBotApiClient : IBotApiClient` over
/// Telegram.Bot 22.10.1, plus the pure Telegram.Bot `CallbackQuery` -> `ButtonPress`/`AgentEvent`
/// mapping `LongPollingUpdateSource` (T026, compiled after this file) reuses.
///
/// Bot API facts this file relies on, verified against core.telegram.org and Telegram.Bot's own
/// source (research.md D7, Principle V):
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
/// domain model (data-model.md). No I/O; total and side-effect-free, so they're reused as-is by
/// `LongPollingUpdateSource` (T026) and are unit-testable without a fake HTTP server (T023's
/// `Mapping.toInlineKeyboardMarkup` test).
module Mapping =

    /// `RegisteredKeyboard` -> Telegram.Bot's `InlineKeyboardMarkup`: one `InlineKeyboardButton`
    /// per registered button, `callback_data` set to the button's opaque `CallbackToken` (never the
    /// label or any agent state — FR-011, data-model.md "RegisteredKeyboard").
    let toInlineKeyboardMarkup (RegisteredKeyboard rows) : InlineKeyboardMarkup =
        rows
        |> List.map (fun row ->
            row
            |> List.map (fun (button: RegisteredButton) ->
                InlineKeyboardButton.WithCallbackData(ButtonLabel.value button.Label, CallbackToken.value button.Token))
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
    /// callback), or — "handle absent message" (T024) — no `Message` (the callback query
    /// originated from an inline-mode message, or the original message is too old for Telegram to
    /// attach; data-model.md's `ButtonPress` requires `Chat`/`MessageId`, which only the message
    /// carries, so such updates carry no `ButtonPress` and are skipped rather than guessed at).
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
/// constructed from a bot token directly, so the façade (T027) owns client lifetime/HttpClient
/// choice and tests (T023) can point it at `FakeBotApiServer` via `TelegramBotClientOptions`'s
/// `baseUrl` override.
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
