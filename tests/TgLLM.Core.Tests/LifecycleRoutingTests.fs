/// Tests for `UpdateProcessor`'s lifecycle wiring: dedup, expiry, and single-use consumption, in
/// resolution order — dedup (`ProcessedQueryTracker.TryBegin`) first, so a redelivered callback
/// query is dropped before anything else runs; then, for a Tool Router press, an expired binding is
/// treated like an unknown token (falls back to the slice-1 ack-first path, same as any other
/// unresolvable press); then, after a single-use binding's tool runs, the binding itself is removed
/// so a later tap on it resolves as unknown too. `UpdateProcessor` takes an injected `Clock` (never
/// ambient `DateTimeOffset.Now`/`UtcNow`), matching `Expiry.isLive`'s own contract.
module TgLLM.Core.Tests.LifecycleRoutingTests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Expecto
open FSharp.UMX
open TgLLM.Core

let private validLabel =
    match ButtonLabel.create "Approve" with
    | Ok label -> label
    | Error e -> failwithf "test setup: unreachable %A" e

let private toolName (s: string) : ToolName =
    match ToolName.create s with
    | Ok n -> n
    | Error e -> failwithf "test setup: unreachable %A" e

let private pressFor (token: CallbackToken) (queryId: string) (userId: int64) : ButtonPress =
    { Token = token
      QueryId = UMX.tag<callbackQueryId> queryId
      Chat = UMX.tag<chatId> 1L
      User = { Id = UMX.tag<userId> userId; FirstName = "Presser"; Username = null }
      MessageId = UMX.tag<messageId> 1L
      ButtonLabel = validLabel }

/// A finite `IUpdateSource` fake that yields a fixed list of events, then completes (same shape as
/// `OwnerRoutingTests.fs`'s private fake — duplicated here since that one is module-private).
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

type private AckCall =
    | AckFirst of CallbackQueryId
    | AckDeferred of CallbackQueryId * string option * bool

type private FakeBotApiClient() =
    let calls = ResizeArray<AckCall>()
    member _.Calls: AckCall list = List.ofSeq calls

    interface IBotApiClient with
        member _.SendText(_chat, _text, _ct) = Task.FromResult(UMX.tag<messageId> 0L)
        member _.SendText(_chat, _text, _parseMode, _ct) = Task.FromResult(UMX.tag<messageId> 0L)
        member _.SendKeyboard(_chat, _text, _keyboard, _ct) = Task.FromResult(UMX.tag<messageId> 0L)
        member _.SendKeyboard(_chat, _text, _keyboard, _parseMode, _ct) = Task.FromResult(UMX.tag<messageId> 0L)

        member _.AnswerCallback(query, _ct) =
            calls.Add(AckFirst query)
            Task.CompletedTask

        member _.AnswerCallback(query, text, showAlert, _ct) =
            calls.Add(AckDeferred(query, text, showAlert))
            Task.CompletedTask

        member _.EditMessageText(_chat, _message, _text, _keyboard, _ct) = Task.FromResult EditApplied
        member _.EditMessageText(_chat, _message, _text, _keyboard, _parseMode, _ct) = Task.FromResult EditApplied
        member _.EditMessageReplyMarkup(_chat, _message, _keyboard, _ct) = Task.FromResult EditApplied
        member _.DeleteMessage(_chat, _message, _ct) = Task.FromResult true

type private FakeDispatcher() =
    let enqueued = ResizeArray<ChatId * (CancellationToken -> Task)>()
    member _.Enqueued: (ChatId * (CancellationToken -> Task)) list = List.ofSeq enqueued

    interface IPressDispatcher with
        member _.Enqueue(chat, work) =
            enqueued.Add(chat, work)
            ValueTask.CompletedTask

        member _.DisposeAsync() = ValueTask.CompletedTask

type private FakeHookObserver() =
    let unknown = ResizeArray<ButtonPress>()
    member _.Unknown: ButtonPress list = List.ofSeq unknown

    interface IHookObserver with
        member _.OnHookFailed(_press, _error) = ()
        member _.OnUnknownToken(press) = unknown.Add press
        member _.OnEditFailed(_press, _reason) = ()
        member _.OnRunLoopFailed(_error) = ()

/// Runs every enqueued work item in order, so a tool's side effects are visible to the test.
let private runEnqueued (dispatcher: FakeDispatcher) =
    for _, work in dispatcher.Enqueued do
        (work CancellationToken.None).GetAwaiter().GetResult()

[<Tests>]
let lifecycleRoutingTests =
    testList "UpdateProcessor lifecycle routing" [

        testCase "a press on an expired binding is refused like an unknown token: acked ack-first, no tool runs, surfaced" <| fun _ ->
            let token = CallbackToken.generate ()
            let now = DateTimeOffset.UnixEpoch.AddDays 1.0
            let mutable toolRan = false
            let tool: Tool = fun _ -> toolRan <- true; Task.CompletedTask
            let registry = InMemoryToolRegistry() :> IToolRegistry
            registry.Register(toolName "approve", tool)
            let store = InMemoryBindingStore() :> IBindingStore

            let binding =
                { ToolBinding.create token (toolName "approve") None with
                    ExpiresAt = Some(now.AddMinutes -1.0) }

            store.Save([ binding ], CancellationToken.None).GetAwaiter().GetResult()
            let dispatch = ToolDispatch(registry, store)
            let press = pressFor token "q-expired" 1L
            let api = FakeBotApiClient()
            let dispatcher = FakeDispatcher()
            let observer = FakeHookObserver()
            let source = FakeUpdateSource [ ButtonPressed press ]

            let processor =
                UpdateProcessor(source, InMemoryHookStore(), api, dispatcher, observer, toolDispatch = dispatch, clock = (fun () -> now))

            (processor.RunAsync CancellationToken.None).GetAwaiter().GetResult()

            Expect.isFalse toolRan "an expired binding must never run its tool"
            Expect.isEmpty dispatcher.Enqueued "no work is enqueued for an expired binding"
            Expect.equal api.Calls [ AckFirst press.QueryId ] "acked via the plain ack-first path, like any other unresolvable press"
            Expect.equal observer.Unknown [ press ] "the expired press is surfaced as an unknown/stale press"

        testCase "a binding whose expiry is still in the future runs the tool normally" <| fun _ ->
            let token = CallbackToken.generate ()
            let now = DateTimeOffset.UnixEpoch.AddDays 1.0
            let mutable toolRan = false
            let tool: Tool = fun _ -> toolRan <- true; Task.CompletedTask
            let registry = InMemoryToolRegistry() :> IToolRegistry
            registry.Register(toolName "approve", tool)
            let store = InMemoryBindingStore() :> IBindingStore

            let binding =
                { ToolBinding.create token (toolName "approve") None with
                    ExpiresAt = Some(now.AddMinutes 5.0) }

            store.Save([ binding ], CancellationToken.None).GetAwaiter().GetResult()
            let dispatch = ToolDispatch(registry, store)
            let press = pressFor token "q-live" 1L
            let api = FakeBotApiClient()
            let dispatcher = FakeDispatcher()
            let observer = FakeHookObserver()
            let source = FakeUpdateSource [ ButtonPressed press ]

            let processor =
                UpdateProcessor(source, InMemoryHookStore(), api, dispatcher, observer, toolDispatch = dispatch, clock = (fun () -> now))

            (processor.RunAsync CancellationToken.None).GetAwaiter().GetResult()
            runEnqueued dispatcher

            Expect.isTrue toolRan "a still-live binding runs its tool normally"
            Expect.isEmpty observer.Unknown "a still-live binding is not a refusal"

        testCase "a redelivered callback query id (same QueryId) is dropped entirely: the tool runs once, and there is no second ack" <| fun _ ->
            let token = CallbackToken.generate ()
            let mutable runCount = 0
            let tool: Tool = fun _ -> runCount <- runCount + 1; Task.CompletedTask
            let registry = InMemoryToolRegistry() :> IToolRegistry
            registry.Register(toolName "approve", tool)
            let store = InMemoryBindingStore() :> IBindingStore
            let binding = ToolBinding.create token (toolName "approve") None
            store.Save([ binding ], CancellationToken.None).GetAwaiter().GetResult()
            let dispatch = ToolDispatch(registry, store)
            let press = pressFor token "q-redelivered" 1L
            let api = FakeBotApiClient()
            let dispatcher = FakeDispatcher()
            let observer = FakeHookObserver()
            // The SAME event, twice — simulating Telegram (or this library's own webhook retry)
            // redelivering the identical callback query.
            let source = FakeUpdateSource [ ButtonPressed press; ButtonPressed press ]
            let processor = UpdateProcessor(source, InMemoryHookStore(), api, dispatcher, observer, toolDispatch = dispatch)

            (processor.RunAsync CancellationToken.None).GetAwaiter().GetResult()
            runEnqueued dispatcher

            Expect.equal runCount 1 "the tool ran exactly once despite the redelivered query id"
            Expect.equal (List.length dispatcher.Enqueued) 1 "only the first sighting is ever enqueued"

            Expect.equal
                (List.length api.Calls)
                1
                "no second ack is ever attempted for a query id that was already processed (re-acking would fail server-side)"

        testCase "distinct query ids for the SAME token each run the tool — redelivery dedup is per query id, not per token" <| fun _ ->
            let token = CallbackToken.generate ()
            let mutable runCount = 0
            let tool: Tool = fun _ -> runCount <- runCount + 1; Task.CompletedTask
            let registry = InMemoryToolRegistry() :> IToolRegistry
            registry.Register(toolName "approve", tool)
            let store = InMemoryBindingStore() :> IBindingStore
            let binding = ToolBinding.create token (toolName "approve") None
            store.Save([ binding ], CancellationToken.None).GetAwaiter().GetResult()
            let dispatch = ToolDispatch(registry, store)
            let firstPress = pressFor token "q-1" 1L
            let secondPress = pressFor token "q-2" 1L
            let api = FakeBotApiClient()
            let dispatcher = FakeDispatcher()
            let observer = FakeHookObserver()
            let source = FakeUpdateSource [ ButtonPressed firstPress; ButtonPressed secondPress ]
            let processor = UpdateProcessor(source, InMemoryHookStore(), api, dispatcher, observer, toolDispatch = dispatch)

            (processor.RunAsync CancellationToken.None).GetAwaiter().GetResult()
            runEnqueued dispatcher

            Expect.equal runCount 2 "two distinct taps on the same persistent button both run the tool — not conflated with redelivery"

        testCase "a single-use binding is consumed after its first successful tap: a second tap on the SAME token resolves as unknown" <| fun _ ->
            let token = CallbackToken.generate ()
            let mutable runCount = 0
            let tool: Tool = fun _ -> runCount <- runCount + 1; Task.CompletedTask
            let registry = InMemoryToolRegistry() :> IToolRegistry
            registry.Register(toolName "confirm", tool)
            let store = InMemoryBindingStore() :> IBindingStore
            let binding = { ToolBinding.create token (toolName "confirm") None with SingleUse = true }
            store.Save([ binding ], CancellationToken.None).GetAwaiter().GetResult()
            let dispatch = ToolDispatch(registry, store)
            let firstPress = pressFor token "q-first-tap" 1L
            let secondPress = pressFor token "q-second-tap" 1L
            let api = FakeBotApiClient()
            let dispatcher = FakeDispatcher()
            let observer = FakeHookObserver()
            let source = FakeUpdateSource [ ButtonPressed firstPress ]
            let processor = UpdateProcessor(source, InMemoryHookStore(), api, dispatcher, observer, toolDispatch = dispatch)

            (processor.RunAsync CancellationToken.None).GetAwaiter().GetResult()
            runEnqueued dispatcher

            Expect.equal runCount 1 "the first tap ran the tool"

            let stillBound = (store.TryGet(token, CancellationToken.None)).GetAwaiter().GetResult()
            Expect.equal stillBound ValueNone "a single-use binding is removed from the store after its first successful tap"

            // A second, independent press (different query id, so dedup doesn't interfere) on the
            // SAME token now finds no binding at all — same as any other unknown/stale press.
            let secondDispatcher = FakeDispatcher()
            let secondObserver = FakeHookObserver()
            let secondSource = FakeUpdateSource [ ButtonPressed secondPress ]
            let secondApi = FakeBotApiClient()

            let secondProcessor =
                UpdateProcessor(secondSource, InMemoryHookStore(), secondApi, secondDispatcher, secondObserver, toolDispatch = dispatch)

            (secondProcessor.RunAsync CancellationToken.None).GetAwaiter().GetResult()

            Expect.equal runCount 1 "the second tap on the consumed binding must not run the tool again"
            Expect.isEmpty secondDispatcher.Enqueued "no work is enqueued for the consumed binding"
            Expect.equal secondObserver.Unknown [ secondPress ] "the second tap surfaces as an unknown/stale press"

        testCase "a binding with SingleUse = false (the default) is NOT consumed and keeps resolving after repeated taps" <| fun _ ->
            let token = CallbackToken.generate ()
            let mutable runCount = 0
            let tool: Tool = fun _ -> runCount <- runCount + 1; Task.CompletedTask
            let registry = InMemoryToolRegistry() :> IToolRegistry
            registry.Register(toolName "menu", tool)
            let store = InMemoryBindingStore() :> IBindingStore
            let binding = ToolBinding.create token (toolName "menu") None
            store.Save([ binding ], CancellationToken.None).GetAwaiter().GetResult()
            let dispatch = ToolDispatch(registry, store)
            let firstPress = pressFor token "q-menu-1" 1L
            let secondPress = pressFor token "q-menu-2" 1L
            let api = FakeBotApiClient()
            let dispatcher = FakeDispatcher()
            let observer = FakeHookObserver()
            let source = FakeUpdateSource [ ButtonPressed firstPress; ButtonPressed secondPress ]
            let processor = UpdateProcessor(source, InMemoryHookStore(), api, dispatcher, observer, toolDispatch = dispatch)

            (processor.RunAsync CancellationToken.None).GetAwaiter().GetResult()
            runEnqueued dispatcher

            Expect.equal runCount 2 "a non-single-use (persistent menu) binding keeps resolving across repeated distinct taps"

        testCase
            "a fast double-tap on a single-use binding invokes the tool exactly once, even with a slow tool on the REAL per-chat dispatcher"
        <| fun _ ->
            let token = CallbackToken.generate ()
            let mutable runCount = 0

            // Slow-ish tool: long enough that, under the bug, the SECOND tap's resolve would race
            // ahead of the first tap's tool-completion-time consumption and find the binding still
            // present.
            let tool: Tool =
                fun _ ->
                    task {
                        do! Task.Delay 200
                        Interlocked.Increment(&runCount) |> ignore
                    }

            let registry = InMemoryToolRegistry() :> IToolRegistry
            registry.Register(toolName "confirm-once", tool)
            let store = InMemoryBindingStore() :> IBindingStore
            let binding = { ToolBinding.create token (toolName "confirm-once") None with SingleUse = true }
            store.Save([ binding ], CancellationToken.None).GetAwaiter().GetResult()
            let dispatch = ToolDispatch(registry, store)
            // Two DISTINCT query ids (a real double-tap) on the SAME token/message — redelivery
            // dedup (keyed by query id) must not be what prevents the double run here.
            let firstPress = pressFor token "q-double-tap-1" 1L
            let secondPress = pressFor token "q-double-tap-2" 1L
            let api = FakeBotApiClient()
            let observer = FakeHookObserver()
            let source = FakeUpdateSource [ ButtonPressed firstPress; ButtonPressed secondPress ]

            // The REAL dispatcher: both presses land on the SAME chat, so its per-chat channel runs
            // their enqueued work sequentially — exactly the shape a fast double-tap produces in
            // production (unlike `FakeDispatcher`, which the test itself drives one item at a time).
            let dispatcher = new PerChatChannelDispatcher(shutdownBudget = TimeSpan.FromSeconds 5.0)
            let processor = UpdateProcessor(source, InMemoryHookStore(), api, dispatcher, observer, toolDispatch = dispatch)

            (processor.RunAsync CancellationToken.None).GetAwaiter().GetResult()
            // Drain: waits for both enqueued work items (including the slow tool) to finish.
            ((dispatcher :> IPressDispatcher).DisposeAsync()).AsTask().GetAwaiter().GetResult()

            Expect.equal runCount 1 "the single-use binding must be consumed at the FIRST tap's resolution, before either tap's tool ever runs — the second tap must never invoke the tool"
    ]
