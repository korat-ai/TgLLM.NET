/// Tests for `TelegramBotApiClient` against a fake Telegram HTTP handler (`FakeBotApiServer`).
module TgLLM.Integration.Tests.BotApiClientTests

open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open Expecto
open FSharp.UMX
open Telegram.Bot
open TgLLM.Core
open TgLLM.BotApi
open TgLLM.Integration.Tests.FakeBotApiServer

let private label (text: string) : ButtonLabel =
    match ButtonLabel.create text with
    | Ok l -> l
    | Error e -> failwithf "test setup: unreachable %A" e

let private messageText (text: string) : MessageText =
    match MessageText.create text with
    | Ok m -> m
    | Error e -> failwithf "test setup: unreachable %A" e

/// A real `TelegramBotClient` pointed at the fake server's loopback port — exactly what
/// production code constructs, just with `baseUrl` overridden (`TelegramBotClientOptions`'s
/// second constructor parameter; verified via Telegram.Bot's own source).
let private makeClient (server: FakeBotApiServer) : ITelegramBotClient =
    // Must look like a real bot token (`{digits}:{rest}`, per `TelegramBotClientOptions`'s own
    // validation) even though nothing ever checks it beyond shape — the fake server doesn't
    // authenticate.
    let options = TelegramBotClientOptions("123456789:TEST-fake-token", server.BaseUrl)
    TelegramBotClient(options) :> ITelegramBotClient

/// `JsonNode`'s indexers are nullable (`Nullable` is enabled project-wide); these small helpers
/// turn "key/index present" into an ordinary non-null lookup so assertions read as plain field
/// access instead of null-checking ceremony at every step.
let private field (key: string) (node: JsonNode) : JsonNode =
    match node.[key] |> Option.ofObj with
    | Some child -> child
    | None -> failwithf "expected JSON field '%s' in %s" key (node.ToJsonString())

let private at (i: int) (node: JsonNode) : JsonNode =
    match node.[i] |> Option.ofObj with
    | Some child -> child
    | None -> failwithf "expected JSON index %d in %s" i (node.ToJsonString())

let private asString (node: JsonNode) : string = node.AsValue().GetValue<string>()
let private asInt64 (node: JsonNode) : int64 = node.AsValue().GetValue<int64>()

[<Tests>]
let botApiClientTests =
    testList "TelegramBotApiClient" [

        testCase "Mapping.toInlineKeyboardMarkup maps every row/button 1:1, callback_data = token" <| fun _ ->
            let tokenYes = CallbackToken.generate ()
            let tokenNo = CallbackToken.generate ()

            let registered =
                RegisteredKeyboard [
                    [ Callback(label "Yes", tokenYes)
                      Callback(label "No", tokenNo) ]
                ]

            let markup = Mapping.toInlineKeyboardMarkup registered
            let rows = markup.InlineKeyboard |> Seq.map List.ofSeq |> List.ofSeq

            match rows with
            | [ [ btnYes; btnNo ] ] ->
                Expect.equal btnYes.Text "Yes" "first button's visible text is preserved"
                Expect.equal btnYes.CallbackData (CallbackToken.value tokenYes) "first button's callback_data is its token"
                Expect.equal btnNo.Text "No" "second button's visible text is preserved"
                Expect.equal btnNo.CallbackData (CallbackToken.value tokenNo) "second button's callback_data is its token"
            | other -> failwithf "expected exactly one row of two buttons, got %A" other

        testCase "Mapping.toInlineKeyboardMarkup maps WebApp/CopyText buttons to their own Telegram.Bot factories" <| fun _ ->
            let registered =
                RegisteredKeyboard [
                    [ WebApp(label "Open", "https://example.test/app")
                      CopyText(label "Copy", "snippet-1") ]
                ]

            let markup = Mapping.toInlineKeyboardMarkup registered
            let rows = markup.InlineKeyboard |> Seq.map List.ofSeq |> List.ofSeq

            match rows with
            | [ [ btnWebApp; btnCopy ] ] ->
                Expect.equal btnWebApp.Text "Open" "the WebApp button's visible text is preserved"

                match btnWebApp.WebApp |> Option.ofObj with
                | Some webApp -> Expect.equal webApp.Url "https://example.test/app" "the WebApp button carries its url, not callback_data"
                | None -> failtest "expected the mapped button to carry a WebApp payload"

                Expect.isNull btnWebApp.CallbackData "a WebApp button is client-side — no callback_data"

                Expect.equal btnCopy.Text "Copy" "the CopyText button's visible text is preserved"

                match btnCopy.CopyText |> Option.ofObj with
                | Some copyText -> Expect.equal copyText.Text "snippet-1" "the CopyText button carries its clipboard text"
                | None -> failtest "expected the mapped button to carry a CopyText payload"

                Expect.isNull btnCopy.CallbackData "a CopyText button is client-side — no callback_data"
            | other -> failwithf "expected exactly one row of two buttons, got %A" other

        testCase "SendText posts sendMessage with chat_id and text" <| fun _ ->
            task {
                use! server = FakeBotApiServer.start ()
                let api: IBotApiClient = TelegramBotApiClient(makeClient server)

                let! messageId = api.SendText(UMX.tag<chatId> 42L, messageText "hello", CancellationToken.None)

                match server.RequestsFor "sendMessage" with
                | [ request ] ->
                    let body = request.Body |> Option.get
                    Expect.equal (body |> field "chat_id" |> asInt64) 42L "chat_id is sent"
                    Expect.equal (body |> field "text" |> asString) "hello" "text is sent"
                | other -> failwithf "expected exactly one sendMessage call, got %d" (List.length other)

                Expect.isGreaterThan (UMX.untag messageId) 0L "a positive message id is returned"
            }
            |> fun t -> t.GetAwaiter().GetResult()

        testCase "SendKeyboard posts sendMessage with reply_markup built from the RegisteredKeyboard" <| fun _ ->
            task {
                use! server = FakeBotApiServer.start ()
                let api: IBotApiClient = TelegramBotApiClient(makeClient server)
                let token = CallbackToken.generate ()
                let keyboard = RegisteredKeyboard [ [ Callback(label "Yes", token) ] ]

                let! _ = api.SendKeyboard(UMX.tag<chatId> 7L, messageText "Deploy?", keyboard, CancellationToken.None)

                match server.RequestsFor "sendMessage" with
                | [ request ] ->
                    let body = request.Body |> Option.get

                    let firstButton =
                        body |> field "reply_markup" |> field "inline_keyboard" |> at 0 |> at 0

                    Expect.equal (firstButton |> field "text" |> asString) "Yes" "button text reaches reply_markup"

                    Expect.equal
                        (firstButton |> field "callback_data" |> asString)
                        (CallbackToken.value token)
                        "button callback_data is the assigned token"
                | other -> failwithf "expected exactly one sendMessage call, got %d" (List.length other)
            }
            |> fun t -> t.GetAwaiter().GetResult()

        testCase "AnswerCallback posts answerCallbackQuery with the query id" <| fun _ ->
            task {
                use! server = FakeBotApiServer.start ()
                let api: IBotApiClient = TelegramBotApiClient(makeClient server)

                do! api.AnswerCallback(UMX.tag<callbackQueryId> "query-123", CancellationToken.None)

                match server.RequestsFor "answerCallbackQuery" with
                | [ request ] ->
                    let body = request.Body |> Option.get
                    Expect.equal (body |> field "callback_query_id" |> asString) "query-123" "callback_query_id is sent"
                | other -> failwithf "expected exactly one answerCallbackQuery call, got %d" (List.length other)
            }
            |> fun t -> t.GetAwaiter().GetResult()
    ]
