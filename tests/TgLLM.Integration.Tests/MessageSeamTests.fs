/// Tests for the additive Core message seam (`AgentEvent.MessageReceived`, `MessageHandler`,
/// `IMessageObserver`): a user text `Update` maps to `MessageReceived` identically on both
/// transports (the SAME shared `Mapping.toAgentEvent` both already call — neither transport has
/// code of its own); with no `?onMessage` wired, `MessageReceived` is a complete no-op; a handler
/// throw is caught and reported via `IMessageObserver.OnMessageFailed`, never derailing the run
/// loop.
module TgLLM.Integration.Tests.MessageSeamTests

open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Expecto
open FSharp.UMX
open TgLLM.Core
open TgLLM.BotApi
open TgLLM.Webhooks
open TgLLM.Integration.Tests.FakeBotApiServer

// ---------------------------------------------------------------------------------------------
// Pure-mapping + both-transports wiring
// ---------------------------------------------------------------------------------------------

let private drain (source: IUpdateSource) : Task<AgentEvent list> =
    task {
        let events = ResizeArray<AgentEvent>()
        let enumerator = source.Updates(CancellationToken.None).GetAsyncEnumerator(CancellationToken.None)
        let mutable go = true

        while go do
            let! hasNext = enumerator.MoveNextAsync()
            if hasNext then events.Add enumerator.Current else go <- false

        do! enumerator.DisposeAsync()
        return List.ofSeq events
    }

[<Tests>]
let messageMappingTests =
    testList "Mapping.toAgentEvent (user text messages)" [

        testCase "a plain user text Update maps to MessageReceived, carrying chat/sender/text" <| fun _ ->
            let json = TelegramJson.textMessageUpdate 1 555L 9 777L "Bob" "hello there"
            let update = Webhook.parseUpdate json

            match Mapping.toAgentEvent update with
            | ValueSome(MessageReceived msg) ->
                Expect.equal (UMX.untag msg.Chat) 555L "carries the chat id"
                Expect.equal (UMX.untag msg.Sender.Id) 777L "carries the sender's user id"
                Expect.equal msg.Sender.FirstName "Bob" "carries the sender's first name"
                Expect.equal (UMX.untag msg.MessageId) 9L "carries the message id"
                Expect.equal msg.Text "hello there" "carries the message text"
            | other -> failtestf "expected MessageReceived, got %A" other

        testCase "an update with neither a CallbackQuery nor a text Message is skipped (ValueNone)" <| fun _ ->
            // No `message`/`callback_query` field at all — an update kind `Mapping.toAgentEvent` does not map.
            let update = Webhook.parseUpdate """{ "update_id": 1 }"""
            Expect.equal (Mapping.toAgentEvent update) ValueNone "an unmappable update kind is skipped, not guessed at"

        testCase "a pushed webhook update surfaces as MessageReceived — the same shared mapping long polling uses"
        <| fun _ ->
            let json = TelegramJson.textMessageUpdate 2 321L 5 654L "Wanda" "webhook hello"
            let update = Webhook.parseUpdate json
            let source = WebhookUpdateSource()

            (source.Ingest(update, CancellationToken.None)).GetAwaiter().GetResult()
            source.Complete()

            match (drain (source :> IUpdateSource)).GetAwaiter().GetResult() with
            | [ MessageReceived msg ] -> Expect.equal (UMX.untag msg.Chat) 321L "carries the chat id"
            | other -> failtestf "expected exactly one MessageReceived, got %A" other

        testCaseAsync "a long-polled text message surfaces as MessageReceived — the same shared mapping webhooks use"
        <| async {
            do!
                task {
                    use! server = FakeBotApiServer.start ()

                    server.EnqueueResult(
                        "getUpdates",
                        TelegramJson.batch [ TelegramJson.textMessageUpdate 3 999L 4 111L "Alice" "polling hello" ]
                    )

                    let client = Telegram.Bot.TelegramBotClient(Telegram.Bot.TelegramBotClientOptions("123456789:TEST-fake-token", server.BaseUrl))
                    let source = LongPollingUpdateSource(client, timeoutSeconds = 0)
                    use cts = new CancellationTokenSource()

                    // `LongPollingUpdateSource`'s own pump loop runs until `ct` is cancelled — it
                    // never completes its channel on its own (unlike `WebhookUpdateSource`, which
                    // `.Complete()`s explicitly). Read exactly ONE event, then cancel — `drain`
                    // (which waits for the channel to COMPLETE) would hang forever here.
                    let enumerator = (source :> IUpdateSource).Updates(cts.Token).GetAsyncEnumerator(cts.Token)
                    let! hasNext = enumerator.MoveNextAsync()
                    Expect.isTrue hasNext "the enqueued getUpdates batch produced at least one event"
                    let first = enumerator.Current
                    cts.Cancel()

                    try
                        do! enumerator.DisposeAsync()
                    with :? System.OperationCanceledException ->
                        ()

                    match first with
                    | MessageReceived msg -> Expect.equal (UMX.untag msg.Chat) 999L "carries the chat id"
                    | other -> failtestf "expected the first event to be MessageReceived, got %A" other
                }
                |> Async.AwaitTask
        }
    ]

// ---------------------------------------------------------------------------------------------
// UpdateProcessor wiring: no-op without ?onMessage; handler throw -> OnMessageFailed; the SAME
// per-chat lane as button presses. Hand-rolled port fakes, mirroring
// TgLLM.Core.Tests/UpdateProcessorTests.fs's own template — this suite needs the SAME
// deterministic, no-HTTP precision for a seam this additive-critical.
// ---------------------------------------------------------------------------------------------

let private sampleMessage (chat: int64) (text: string) : IncomingMessage =
    { Chat = UMX.tag<chatId> chat
      Sender = { Id = UMX.tag<userId> 1L; FirstName = "Alice"; Username = null }
      MessageId = UMX.tag<messageId> 1L
      Text = text }

let private validLabel =
    match ButtonLabel.create "Yes" with
    | Ok label -> label
    | Error e -> failwithf "test setup: unreachable %A" e

let private samplePress (token: CallbackToken) (chat: int64) : ButtonPress =
    { Token = token
      QueryId = UMX.tag<callbackQueryId> $"query-{CallbackToken.value token}"
      Chat = UMX.tag<chatId> chat
      User = { Id = UMX.tag<userId> 1L; FirstName = "Alice"; Username = null }
      MessageId = UMX.tag<messageId> 1L
      ButtonLabel = validLabel }

type private FakeUpdateSource(events: AgentEvent list) =
    interface IUpdateSource with
        member _.Updates(_ct: CancellationToken) : IAsyncEnumerable<AgentEvent> =
            { new IAsyncEnumerable<AgentEvent> with
                member _.GetAsyncEnumerator(_ct2: CancellationToken) : IAsyncEnumerator<AgentEvent> =
                    let queue = Queue<AgentEvent>(events)
                    let mutable current: AgentEvent voption = ValueNone

                    { new IAsyncEnumerator<AgentEvent> with
                        member _.Current =
                            match current with
                            | ValueSome e -> e
                            | ValueNone -> failwith "Current accessed before a successful MoveNextAsync"

                        member _.MoveNextAsync() =
                            if queue.Count > 0 then
                                current <- ValueSome(queue.Dequeue())
                                ValueTask<bool>(true)
                            else
                                ValueTask<bool>(false)

                        member _.DisposeAsync() = ValueTask.CompletedTask } }

type private FakeBotApiClient() =
    let answered = ResizeArray<CallbackQueryId>()
    member _.Answered: CallbackQueryId list = List.ofSeq answered

    interface IBotApiClient with
        member _.SendText(_chat, _text, _ct) = Task.FromResult(UMX.tag<messageId> 0L)
        member _.SendText(_chat, _text, _parseMode, _ct) = Task.FromResult(UMX.tag<messageId> 0L)
        member _.SendKeyboard(_chat, _text, _keyboard, _ct) = Task.FromResult(UMX.tag<messageId> 0L)
        member _.SendKeyboard(_chat, _text, _keyboard, _parseMode, _ct) = Task.FromResult(UMX.tag<messageId> 0L)

        member _.AnswerCallback(query, _ct) =
            answered.Add query
            Task.CompletedTask

        member _.AnswerCallback(query, _text, _showAlert, _ct) =
            answered.Add query
            Task.CompletedTask

        member _.EditMessageText(_chat, _message, _text, _keyboard, _ct) = Task.FromResult EditApplied
        member _.EditMessageText(_chat, _message, _text, _keyboard, _parseMode, _ct) = Task.FromResult EditApplied
        member _.EditMessageReplyMarkup(_chat, _message, _keyboard, _ct) = Task.FromResult EditApplied
        member _.DeleteMessage(_chat, _message, _ct) = Task.FromResult true

/// Records enqueued work instead of running it — same rationale as
/// `UpdateProcessorTests.FakeDispatcher`: the test decides when (and whether) to invoke a
/// recorded work item, isolating `UpdateProcessor`'s own wiring from the real dispatcher.
type private FakeDispatcher() =
    let enqueued = ResizeArray<ChatId * (CancellationToken -> Task)>()
    member _.Enqueued: (ChatId * (CancellationToken -> Task)) list = List.ofSeq enqueued

    interface IPressDispatcher with
        member _.Enqueue(chat, work) =
            enqueued.Add(chat, work)
            ValueTask.CompletedTask

        member _.DisposeAsync() = ValueTask.CompletedTask

type private FakeHookObserver() =
    interface IHookObserver with
        member _.OnHookFailed(_press, _error) = ()
        member _.OnUnknownToken(_press) = ()
        member _.OnEditFailed(_press, _reason) = ()
        member _.OnRunLoopFailed(_error) = ()

type private FakeMessageObserver() =
    let failed = ResizeArray<IncomingMessage * exn>()
    member _.Failed: (IncomingMessage * exn) list = List.ofSeq failed

    interface IMessageObserver with
        member _.OnMessageFailed(message, error) = failed.Add(message, error)

[<Tests>]
let updateProcessorMessageSeamTests =
    testList "UpdateProcessor (message seam)" [

        testCase "a MessageReceived event with no ?onMessage wired is a complete no-op" <| fun _ ->
            let message = sampleMessage 1L "hi"
            let store: IHookStore = InMemoryHookStore()
            let api = FakeBotApiClient()
            let dispatcher = FakeDispatcher()
            let observer = FakeHookObserver()
            let source = FakeUpdateSource [ MessageReceived message ]
            // No `?onMessage` — the exact call shape every pre-existing caller already uses.
            let processor = UpdateProcessor(source, store, api, dispatcher, observer)

            (processor.RunAsync CancellationToken.None).GetAwaiter().GetResult()

            Expect.isEmpty dispatcher.Enqueued "nothing is enqueued for a message when no handler is wired"
            Expect.isEmpty api.Answered "a message event never triggers an AnswerCallback"

        testCase "a MessageReceived event with ?onMessage wired enqueues the handler on the message's own chat" <| fun _ ->
            let message = sampleMessage 42L "hi"
            let mutable handlerRan = false

            let handler: MessageHandler =
                fun msg _ct ->
                    handlerRan <- true
                    Expect.equal msg message "the handler receives the SAME message the event carried"
                    Task.CompletedTask

            let store: IHookStore = InMemoryHookStore()
            let api = FakeBotApiClient()
            let dispatcher = FakeDispatcher()
            let observer = FakeHookObserver()
            let source = FakeUpdateSource [ MessageReceived message ]
            let processor = UpdateProcessor(source, store, api, dispatcher, observer, onMessage = handler)

            (processor.RunAsync CancellationToken.None).GetAwaiter().GetResult()

            match dispatcher.Enqueued with
            | [ (chat, work) ] ->
                Expect.equal chat message.Chat "the handler is enqueued on the message's own chat"
                (work CancellationToken.None).GetAwaiter().GetResult()
                Expect.isTrue handlerRan "invoking the enqueued work actually runs the handler"
            | other -> failwithf "expected exactly one enqueued item, got %d" (List.length other)

        testCase "a throwing handler is reported via IMessageObserver.OnMessageFailed, not thrown out of the run loop" <| fun _ ->
            let message = sampleMessage 7L "boom please"
            let handler: MessageHandler = fun _ _ -> raise (System.Exception "boom")
            let store: IHookStore = InMemoryHookStore()
            let api = FakeBotApiClient()
            let dispatcher = FakeDispatcher()
            let observer = FakeHookObserver()
            let messageObserver = FakeMessageObserver()
            let source = FakeUpdateSource [ MessageReceived message ]

            let processor =
                UpdateProcessor(source, store, api, dispatcher, observer, onMessage = handler, messageObserver = messageObserver)

            (processor.RunAsync CancellationToken.None).GetAwaiter().GetResult()

            match dispatcher.Enqueued with
            | [ (_, work) ] -> (work CancellationToken.None).GetAwaiter().GetResult()
            | other -> failwithf "expected exactly one enqueued item, got %d" (List.length other)

            Expect.equal (List.length messageObserver.Failed) 1 "the handler's throw is reported, not swallowed silently"

            match messageObserver.Failed with
            | [ (failedMessage, error) ] ->
                Expect.equal failedMessage message "the observer attributes the failure to the right message"
                Expect.equal error.Message "boom" "the original exception is passed through"
            | other -> failwithf "expected exactly one reported failure, got %A" other

        testCase "a MessageReceived event and a ButtonPressed event for the same chat are BOTH enqueued on that chat's lane" <| fun _ ->
            let token = CallbackToken.generate ()
            let press = samplePress token 100L
            let message = sampleMessage 100L "hi"
            let handler: MessageHandler = fun _ _ -> Task.CompletedTask
            let hook: Hook = fun _ -> Task.CompletedTask
            let store: IHookStore = InMemoryHookStore()
            store.Register([ { Token = token; Hook = hook } ], CancellationToken.None).GetAwaiter().GetResult()
            let api = FakeBotApiClient()
            let dispatcher = FakeDispatcher()
            let observer = FakeHookObserver()
            let source = FakeUpdateSource [ MessageReceived message; ButtonPressed press ]
            let processor = UpdateProcessor(source, store, api, dispatcher, observer, onMessage = handler)

            (processor.RunAsync CancellationToken.None).GetAwaiter().GetResult()

            Expect.equal (List.length dispatcher.Enqueued) 2 "both the message and the press are enqueued"
            Expect.isTrue (dispatcher.Enqueued |> List.forall (fun (chat, _) -> chat = message.Chat)) "both land on the SAME chat's lane"
    ]
