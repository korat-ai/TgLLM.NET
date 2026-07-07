/// Tests for `UpdateProcessor`'s ack-first policy, using in-memory fakes.
module TgLLM.Core.Tests.UpdateProcessorTests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Expecto
open FSharp.UMX
open TgLLM.Core

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

/// A finite `IUpdateSource` fake that yields a fixed list of events, then completes.
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

/// Records `AnswerCallback`/`SendText` calls instead of talking to Telegram. `throwForQueries`
/// simulates a transient `AnswerCallback` failure for specific query ids — every existing call
/// site omits it (empty by default), so this is a pure addition, unchanged behavior for every
/// pre-existing test.
type private FakeBotApiClient(?throwForQueries: CallbackQueryId list) =
    let throwFor = defaultArg throwForQueries [] |> Set.ofList
    let answered = ResizeArray<CallbackQueryId>()
    let sentTexts = ResizeArray<ChatId * MessageText>()
    member _.Answered: CallbackQueryId list = List.ofSeq answered
    member _.SentTexts: (ChatId * MessageText) list = List.ofSeq sentTexts

    interface IBotApiClient with
        member _.SendText(chat, text, _ct) =
            sentTexts.Add(chat, text)
            Task.FromResult(UMX.tag<messageId> 0L)

        /// Parse-mode overload — this suite never exercises it, mirrors the plain overload's
        /// bookkeeping so it stays observable the same way if a future test ever does.
        member _.SendText(chat, text, _parseMode, _ct) =
            sentTexts.Add(chat, text)
            Task.FromResult(UMX.tag<messageId> 0L)

        member _.SendKeyboard(_chat, _text, _keyboard, _ct) = Task.FromResult(UMX.tag<messageId> 0L)
        member _.SendKeyboard(_chat, _text, _keyboard, _parseMode, _ct) = Task.FromResult(UMX.tag<messageId> 0L)

        member _.AnswerCallback(query, _ct) =
            if throwFor.Contains query then
                raise (InvalidOperationException "simulated transient AnswerCallback failure")
            else
                answered.Add query
                Task.CompletedTask

        /// This file only exercises the slice-1 ack-first path (no `?toolDispatch`), so the
        /// deferred-ack overload is never actually invoked here — implemented to satisfy
        /// `IBotApiClient`, mirroring the 2-arg overload's bookkeeping.
        member _.AnswerCallback(query, _text, _showAlert, _ct) =
            answered.Add query
            Task.CompletedTask

        /// Edit* are only reachable via the deferred-ack tool path, never exercised in this
        /// ack-first-only suite — implemented to satisfy `IBotApiClient`.
        member _.EditMessageText(_chat, _message, _text, _keyboard, _ct) = Task.FromResult EditApplied
        member _.EditMessageText(_chat, _message, _text, _keyboard, _parseMode, _ct) = Task.FromResult EditApplied
        member _.EditMessageReplyMarkup(_chat, _message, _keyboard, _ct) = Task.FromResult EditApplied
        member _.DeleteMessage(_chat, _message, _ct) = Task.FromResult true

/// Records enqueued work instead of running it on real per-chat channels — the test decides
/// when (and whether) to invoke a recorded work item, so it can assert on the *closure UpdateProcessor
/// built* (ack-before-hook ordering, exception isolation) independently of the real dispatcher
/// (already covered by DispatcherTests.fs).
type private FakeDispatcher() =
    let enqueued = ResizeArray<ChatId * (CancellationToken -> Task)>()
    member _.Enqueued: (ChatId * (CancellationToken -> Task)) list = List.ofSeq enqueued

    interface IPressDispatcher with
        member _.Enqueue(chat, work) =
            enqueued.Add(chat, work)
            ValueTask.CompletedTask

        member _.DisposeAsync() = ValueTask.CompletedTask

type private FakeHookObserver() =
    let failed = ResizeArray<ButtonPress * exn>()
    let unknown = ResizeArray<ButtonPress>()
    member _.Failed: (ButtonPress * exn) list = List.ofSeq failed
    member _.Unknown: ButtonPress list = List.ofSeq unknown

    interface IHookObserver with
        member _.OnHookFailed(press, error) = failed.Add(press, error)
        member _.OnUnknownToken(press) = unknown.Add press
        member _.OnEditFailed(_press, _reason) = ()
        member _.OnRunLoopFailed(_error) = ()

[<Tests>]
let updateProcessorTests =
    testList "UpdateProcessor" [

        testCase "AnswerCallback is called for an unknown token; no hook runs, no error" <| fun _ ->
            let token = CallbackToken.generate ()
            let press = samplePress token 1L
            let store = InMemoryHookStore() // empty: token is unregistered/unknown
            let api = FakeBotApiClient()
            let dispatcher = FakeDispatcher()
            let observer = FakeHookObserver()
            let source = FakeUpdateSource [ ButtonPressed press ]
            let processor = UpdateProcessor(source, store, api, dispatcher, observer)

            (processor.RunAsync CancellationToken.None).GetAwaiter().GetResult()

            Expect.equal api.Answered [ press.QueryId ] "the unknown press is still acknowledged"
            Expect.isEmpty dispatcher.Enqueued "no hook is enqueued for an unknown token"
            Expect.equal observer.Unknown [ press ] "the observer is told about the unknown token"
            Expect.isEmpty observer.Failed "no failure is reported for a merely-unknown token"

        testCase "AnswerCallback is called for every press, including ones with a known hook" <| fun _ ->
            let token = CallbackToken.generate ()
            let press = samplePress token 1L
            let hook: Hook = fun _ -> Task.CompletedTask
            let store: IHookStore = InMemoryHookStore()
            store.Register([ { Token = token; Hook = hook } ], CancellationToken.None).GetAwaiter().GetResult()
            let api = FakeBotApiClient()
            let dispatcher = FakeDispatcher()
            let observer = FakeHookObserver()
            let source = FakeUpdateSource [ ButtonPressed press ]
            let processor = UpdateProcessor(source, store, api, dispatcher, observer)

            (processor.RunAsync CancellationToken.None).GetAwaiter().GetResult()

            Expect.equal api.Answered [ press.QueryId ] "a known-token press is acknowledged too"

        testCase "a known token's hook is enqueued on the press's own chat" <| fun _ ->
            let token = CallbackToken.generate ()
            let press = samplePress token 42L
            let mutable hookRan = false
            let hook: Hook = fun _ -> hookRan <- true; Task.CompletedTask
            let store: IHookStore = InMemoryHookStore()
            store.Register([ { Token = token; Hook = hook } ], CancellationToken.None).GetAwaiter().GetResult()
            let api = FakeBotApiClient()
            let dispatcher = FakeDispatcher()
            let observer = FakeHookObserver()
            let source = FakeUpdateSource [ ButtonPressed press ]
            let processor = UpdateProcessor(source, store, api, dispatcher, observer)

            (processor.RunAsync CancellationToken.None).GetAwaiter().GetResult()

            match dispatcher.Enqueued with
            | [ (chat, work) ] ->
                Expect.equal chat press.Chat "the hook is enqueued on the press's own chat"
                (work CancellationToken.None).GetAwaiter().GetResult()
                Expect.isTrue hookRan "invoking the enqueued work actually runs the resolved hook"
            | other -> failwithf "expected exactly one enqueued item, got %d" (List.length other)

        testCase "a throwing hook is reported via IHookObserver.OnHookFailed, and other presses are unaffected" <| fun _ ->
            let throwingToken = CallbackToken.generate ()
            let okToken = CallbackToken.generate ()
            let throwingPress = samplePress throwingToken 1L
            let okPress = samplePress okToken 2L
            let mutable okHookRan = false
            let throwingHook: Hook = fun _ -> raise (System.Exception "boom")
            let okHook: Hook = fun _ -> okHookRan <- true; Task.CompletedTask
            let store: IHookStore = InMemoryHookStore()

            store
                .Register(
                    [ { Token = throwingToken; Hook = throwingHook }
                      { Token = okToken; Hook = okHook } ],
                    CancellationToken.None
                )
                .GetAwaiter()
                .GetResult()

            let api = FakeBotApiClient()
            let dispatcher = FakeDispatcher()
            let observer = FakeHookObserver()
            let source = FakeUpdateSource [ ButtonPressed throwingPress; ButtonPressed okPress ]
            let processor = UpdateProcessor(source, store, api, dispatcher, observer)

            (processor.RunAsync CancellationToken.None).GetAwaiter().GetResult()

            Expect.equal (List.length dispatcher.Enqueued) 2 "both presses are enqueued despite one hook throwing later"

            // Simulate the dispatcher actually running the enqueued work (already the dispatcher's
            // own job per DispatcherTests.fs) to exercise UpdateProcessor's own try/with wrapper.
            for _, work in dispatcher.Enqueued do
                try
                    (work CancellationToken.None).GetAwaiter().GetResult()
                with _ ->
                    () // UpdateProcessor's wrapper is expected to swallow this itself; belt-and-braces here.

            Expect.equal (List.length observer.Failed) 1 "exactly the throwing hook's failure is reported"
            Expect.isTrue okHookRan "the second press's hook still ran — the failure did not derail it"

        testCase "PressResolution.ackPolicy: the Tool Router's resolution is Deferred, the hook-store's is AckFirst" <| fun _ ->
            let token = CallbackToken.generate ()

            let binding: ToolBinding =
                ToolBinding.create
                    token
                    (match ToolName.create "x" with
                     | Ok n -> n
                     | Error e -> failwithf "test setup: unreachable %A" e)
                    None

            let tool: Tool = fun _ -> Task.CompletedTask
            let dispatch = ToolDispatch(InMemoryToolRegistry(), InMemoryBindingStore())

            Expect.equal
                (PressResolution.ackPolicy (ToolResolution(tool, binding, dispatch)))
                Deferred
                "the Tool Router's resolution defers the ack until the tool runs (or the watchdog fires)"

            Expect.equal
                (PressResolution.ackPolicy HookStoreResolution)
                AckFirst
                "the slice-1 hook-store resolution acks immediately, before any hook runs"

        testCase
            "a press whose own processing throws (AnswerCallback itself failing) is reported via OnHookFailed, and RunAsync keeps processing later presses"
        <| fun _ ->
            let throwingToken = CallbackToken.generate ()
            let okToken = CallbackToken.generate ()
            let throwingPress = samplePress throwingToken 1L
            let okPress = samplePress okToken 2L
            // No hooks registered for EITHER token — both are "unknown", so `processHookStorePress`
            // reaches `AnswerCallback` before anything else; only the FIRST press's ack throws.
            let store: IHookStore = InMemoryHookStore()
            let api = FakeBotApiClient(throwForQueries = [ throwingPress.QueryId ])
            let dispatcher = FakeDispatcher()
            let observer = FakeHookObserver()
            let source = FakeUpdateSource [ ButtonPressed throwingPress; ButtonPressed okPress ]
            let processor = UpdateProcessor(source, store, api, dispatcher, observer)

            (processor.RunAsync CancellationToken.None).GetAwaiter().GetResult()

            Expect.equal api.Answered [ okPress.QueryId ] "only the SECOND press's ack actually completed; the first's threw"
            Expect.equal (List.length observer.Failed) 1 "the first press's processing failure is reported, not silently lost"

            match observer.Failed with
            | [ (failedPress, _) ] -> Expect.equal failedPress throwingPress "the observer attributes the failure to the right press"
            | other -> failwithf "expected exactly one reported failure, got %A" other

            Expect.equal
                observer.Unknown
                [ okPress ]
                "the SECOND press still completes its own unknown-token flow — the first press's failure did not derail the loop"
    ]
