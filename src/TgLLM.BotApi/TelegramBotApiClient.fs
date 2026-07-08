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
open Telegram.Bot.Exceptions
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
    /// link; `WebApp`/`CopyText` map to their own Telegram.Bot factories — `url`/
    /// `callback_data`/`web_app`/`copy_text` are all mutually exclusive on one button, so each of
    /// these four cases maps to its own distinct Telegram.Bot factory method, never more than one.
    ///
    /// `WithWebApp`/`WithCopyText` and the `WebAppInfo(url)` / `CopyTextButton` (settable `Text`,
    /// no string constructor) shapes below are verified against the installed Telegram.Bot 22.10.1
    /// assembly via reflection (Principle V) — `InlineKeyboardButton.WithWebApp(string, WebAppInfo)`
    /// and `InlineKeyboardButton.WithCopyText(string, CopyTextButton)` are its only two factories
    /// for these button kinds.
    /// `TgLLM.Core.ParseMode option` -> Telegram.Bot's own `ParseMode` enum (verified against the
    /// installed Telegram.Bot 22.10.1 assembly, Principle V): `None` (no formatting requested)
    /// maps to `Telegram.Bot.Types.Enums.ParseMode.None`, the exact value `sendMessage`'s
    /// `parseMode` parameter already defaults to when a call site omits it entirely — so routing
    /// every send through this mapping, even for the "no parse mode" case, changes nothing on the
    /// wire.
    let toTelegramParseMode (mode: TgLLM.Core.ParseMode option) : Telegram.Bot.Types.Enums.ParseMode =
        match mode with
        | None -> Telegram.Bot.Types.Enums.ParseMode.None
        | Some TgLLM.Core.ParseMode.MarkdownV2 -> Telegram.Bot.Types.Enums.ParseMode.MarkdownV2
        | Some TgLLM.Core.ParseMode.Html -> Telegram.Bot.Types.Enums.ParseMode.Html

    let toInlineKeyboardMarkup (RegisteredKeyboard rows) : InlineKeyboardMarkup =
        rows
        |> List.map (fun row ->
            row
            |> List.map (fun (button: RegisteredButton) ->
                match button with
                | Callback(label, token) -> InlineKeyboardButton.WithCallbackData(ButtonLabel.value label, CallbackToken.value token)
                | Url(label, url) -> InlineKeyboardButton.WithUrl(ButtonLabel.value label, url)
                | WebApp(label, url) -> InlineKeyboardButton.WithWebApp(ButtonLabel.value label, WebAppInfo(url))
                | CopyText(label, text) ->
                    // Fully qualified: `TgLLM.Core.CopyTextButton` (the `PlanButton` case, `open`ed
                    // last) would otherwise shadow Telegram.Bot's own `CopyTextButton` type here.
                    InlineKeyboardButton.WithCopyText(ButtonLabel.value label, Telegram.Bot.Types.CopyTextButton(Text = text)))
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

    /// One Telegram.Bot `Update` -> `AgentEvent voption` (total; never throws). `ValueNone` only
    /// when the update is neither a `CallbackQuery` nor a plain user text `Message` (e.g. an
    /// edited message, a channel post, a media message with no text) — a different update kind
    /// entirely, nothing to ack, nothing to route; media/captions/edits/channel posts keep being
    /// SKIPPED, not guessed at (slice 005's own additive scope: user TEXT messages only). A
    /// `CallbackQuery` this library CAN'T map to a `ButtonPress` — no `Data` (e.g. a `Game`
    /// callback), `Data` that doesn't parse to a canonical `CallbackToken`, or no `Message` (the
    /// callback query originated from an inline-mode message, or the original message is too old
    /// for Telegram to attach; `ButtonPress` requires `Chat`/`MessageId`, which only the message
    /// carries) — still yields `AckOnly query.Id` (review #8) rather than being silently dropped:
    /// every callback query Telegram sent gets exactly one ack somewhere downstream, even when
    /// this library has nothing sensible to route it to.
    let toAgentEvent (update: Update) : AgentEvent voption =
        match update.CallbackQuery |> Option.ofObj with
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
            | _ -> ValueSome(AckOnly(UMX.tag<callbackQueryId> query.Id))
        | None ->
            // A plain user text message — the message-side sibling of the callback-query branch
            // above. `From` is absent on a channel post (which arrives as `update.ChannelPost`,
            // never `update.Message`, so is already excluded here) but the Bot API schema still
            // leaves it nullable on an ordinary `Message`; without an identifiable sender there is
            // no `EndUser` to build, so that update is skipped like any other unmappable one —
            // never guessed at.
            match update.Message |> Option.ofObj with
            | None -> ValueNone
            | Some message ->
                match message.Text |> Option.ofObj, message.From |> Option.ofObj with
                | Some text, Some sender when text.Length > 0 ->
                    let incoming: IncomingMessage =
                        { Chat = UMX.tag<chatId> message.Chat.Id
                          Sender =
                            { Id = UMX.tag<userId> sender.Id
                              FirstName = sender.FirstName
                              Username = sender.Username }
                          MessageId = UMX.tag<messageId> (int64 message.MessageId)
                          Text = text }

                    ValueSome(MessageReceived incoming)
                | _ -> ValueNone

/// Classifies Telegram's two well-known, non-fatal `editMessageText`/`editMessageReplyMarkup`
/// errors into an `EditOutcome` instead of letting them propagate as an exception.
///
/// Verified against the installed Telegram.Bot 22.10.1 assembly by decompilation (Principle V),
/// not assumed: `TelegramBotClient.SendRequest` hands an unsuccessful Bot API response to
/// `ExceptionsParser.Parse` (`DefaultExceptionParser` by default), whose body is exactly
/// `new ApiRequestException(apiResponse.Description, apiResponse.ErrorCode, apiResponse.Parameters)`
/// — so `ApiRequestException.Message` IS the wire `description` string, byte-for-byte, with no
/// reformatting. Matching on it directly (rather than, say, `ErrorCode`, which the Bot API does not
/// distinguish these two cases by) is matching the actual vendor contract. A substring match (not
/// equality) tolerates the real API's `"Bad Request: "` prefix and any further per-request detail
/// Telegram may append after the documented phrase.
module private EditErrorClassification =
    let private isNotModified (message: string) = message.Contains "message is not modified"
    let private isNotFound (message: string) = message.Contains "message to edit not found"
    let private isDeleteNotFound (message: string) = message.Contains "message to delete not found"

    /// Runs `send`, downgrading exactly the two recognized `ApiRequestException`s to a value; any
    /// OTHER exception (a different `ApiRequestException`, a network failure, ...) still propagates
    /// unchanged — only these two specific, well-known outcomes are ever downgraded.
    let classify (send: unit -> Task) : Task<EditOutcome> =
        task {
            try
                do! send ()
                return EditApplied
            with
            | :? ApiRequestException as ex when isNotModified ex.Message -> return EditNotModified
            | :? ApiRequestException as ex when isNotFound ex.Message -> return EditNotFound
        }

    /// `deleteMessage`'s own classify-don't-throw pass: `false` for the one well-known "already
    /// gone" outcome, `true` on success; any OTHER exception still propagates unchanged.
    let classifyDelete (send: unit -> Task) : Task<bool> =
        task {
            try
                do! send ()
                return true
            with :? ApiRequestException as ex when isDeleteNotFound ex.Message ->
                return false
        }

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

        /// Parse-mode overload: identical to the plain `SendText` above except it also passes
        /// `parseMode` through to `sendMessage` (`Mapping.toTelegramParseMode`) — the A2UI renderer
        /// requests `MarkdownV2` here; every other call site keeps using the overload above.
        member _.SendText(chat: ChatId, text: MessageText, parseMode: TgLLM.Core.ParseMode option, ct: CancellationToken) : Task<MessageId> =
            task {
                let! message =
                    client.SendMessage(
                        chatId = Telegram.Bot.Types.ChatId(UMX.untag chat),
                        text = MessageText.value text,
                        parseMode = Mapping.toTelegramParseMode parseMode,
                        cancellationToken = ct
                    )

                return UMX.tag<messageId> (int64 message.MessageId)
            }

        /// Parse-mode overload: identical to the plain `SendKeyboard` above except it also passes
        /// `parseMode` through to `sendMessage`.
        member _.SendKeyboard
            (
                chat: ChatId,
                text: MessageText,
                keyboard: RegisteredKeyboard,
                parseMode: TgLLM.Core.ParseMode option,
                ct: CancellationToken
            ) : Task<MessageId> =
            task {
                let markup = Mapping.toInlineKeyboardMarkup keyboard

                let! message =
                    client.SendMessage(
                        chatId = Telegram.Bot.Types.ChatId(UMX.untag chat),
                        text = MessageText.value text,
                        parseMode = Mapping.toTelegramParseMode parseMode,
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
        /// mapping `SendKeyboard` already uses. Classifies `"message to edit not found"`/`"message
        /// is not modified"` into `EditOutcome` (`EditErrorClassification.classify`) instead of
        /// letting them propagate; any OTHER exception still propagates, caught by
        /// `UpdateProcessor`'s existing per-tool try/with same as before.
        member _.EditMessageText
            (
                chat: ChatId,
                message: MessageId,
                text: MessageText,
                keyboard: RegisteredKeyboard option,
                ct: CancellationToken
            ) : Task<EditOutcome> =
            let replyMarkup = keyboard |> Option.map Mapping.toInlineKeyboardMarkup |> Option.toObj

            EditErrorClassification.classify (fun () ->
                client.EditMessageText(
                    chatId = Telegram.Bot.Types.ChatId(UMX.untag chat),
                    messageId = int (UMX.untag message),
                    text = MessageText.value text,
                    replyMarkup = replyMarkup,
                    cancellationToken = ct
                )
                :> Task)

        /// Parse-mode overload: identical to the plain `EditMessageText` above except it also passes
        /// `parseMode` through to `editMessageText` — the A2UI renderer requests `MarkdownV2` here
        /// (its `updateComponents`/`updateDataModel` edit re-renders with the SAME formatting its
        /// initial send used).
        member _.EditMessageText
            (
                chat: ChatId,
                message: MessageId,
                text: MessageText,
                keyboard: RegisteredKeyboard option,
                parseMode: TgLLM.Core.ParseMode option,
                ct: CancellationToken
            ) : Task<EditOutcome> =
            let replyMarkup = keyboard |> Option.map Mapping.toInlineKeyboardMarkup |> Option.toObj

            EditErrorClassification.classify (fun () ->
                client.EditMessageText(
                    chatId = Telegram.Bot.Types.ChatId(UMX.untag chat),
                    messageId = int (UMX.untag message),
                    text = MessageText.value text,
                    parseMode = Mapping.toTelegramParseMode parseMode,
                    replyMarkup = replyMarkup,
                    cancellationToken = ct
                )
                :> Task)

        /// Replaces the message's keyboard only, leaving its text untouched. Same
        /// classify-don't-throw handling as `EditMessageText` above.
        member _.EditMessageReplyMarkup
            (
                chat: ChatId,
                message: MessageId,
                keyboard: RegisteredKeyboard option,
                ct: CancellationToken
            ) : Task<EditOutcome> =
            let replyMarkup = keyboard |> Option.map Mapping.toInlineKeyboardMarkup |> Option.toObj

            EditErrorClassification.classify (fun () ->
                client.EditMessageReplyMarkup(
                    chatId = Telegram.Bot.Types.ChatId(UMX.untag chat),
                    messageId = int (UMX.untag message),
                    replyMarkup = replyMarkup,
                    cancellationToken = ct
                )
                :> Task)

        /// Deletes a message. Classify-don't-throw handling for the one well-known "already gone"
        /// outcome — same discipline as `EditMessageText`/`EditMessageReplyMarkup` above.
        member _.DeleteMessage(chat: ChatId, message: MessageId, ct: CancellationToken) : Task<bool> =
            EditErrorClassification.classifyDelete (fun () ->
                client.DeleteMessage(
                    chatId = Telegram.Bot.Types.ChatId(UMX.untag chat),
                    messageId = int (UMX.untag message),
                    cancellationToken = ct
                ))
