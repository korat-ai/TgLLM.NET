/// Tests for `UpdateProcessor`'s deferred-ack tool path — the optional `?toolDispatch` collaborator
/// wired into `UpdateProcessor`. Uses REAL in-memory collaborators (`InMemoryToolRegistry`/
/// `InMemoryBindingStore`) plus in-memory fakes for the transport-facing ports, mirroring
/// `UpdateProcessorTests.fs`'s fakes.
module TgLLM.Core.Tests.ToolDispatchProcessorTests

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

let private samplePress (token: CallbackToken) (chat: int64) : ButtonPress =
    { Token = token
      QueryId = UMX.tag<callbackQueryId> $"query-{CallbackToken.value token}"
      Chat = UMX.tag<chatId> chat
      User = { Id = UMX.tag<userId> 1L; FirstName = "Alice"; Username = null }
      MessageId = UMX.tag<messageId> 1L
      ButtonLabel = validLabel }

/// A finite `IUpdateSource` fake that yields a fixed list of events, then completes (same shape as
/// `UpdateProcessorTests.fs`'s private fake — duplicated here since that one is module-private).
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

/// One recorded `AnswerCallback` call, distinguishing the slice-1 ack-first overload from the Tool
/// Router's deferred-ack (text/alert) overload.
type private AckCall =
    | AckFirst of CallbackQueryId
    | AckDeferred of CallbackQueryId * string option * bool

/// One recorded `EditMessageText`/`EditMessageReplyMarkup` call.
type private EditCall =
    | EditText of ChatId * MessageId * string * RegisteredKeyboard option
    | EditKeyboard of ChatId * MessageId * RegisteredKeyboard option

type private FakeBotApiClient() =
    let calls = ResizeArray<AckCall>()
    let edits = ResizeArray<EditCall>()
    member _.Calls: AckCall list = List.ofSeq calls
    member _.Edits: EditCall list = List.ofSeq edits

    interface IBotApiClient with
        member _.SendText(_chat, _text, _ct) = Task.FromResult(UMX.tag<messageId> 0L)
        member _.SendKeyboard(_chat, _text, _keyboard, _ct) = Task.FromResult(UMX.tag<messageId> 0L)

        member _.AnswerCallback(query, _ct) =
            calls.Add(AckFirst query)
            Task.CompletedTask

        member _.AnswerCallback(query, text, showAlert, _ct) =
            calls.Add(AckDeferred(query, text, showAlert))
            Task.CompletedTask

        member _.EditMessageText(chat, message, text, keyboard, _ct) =
            edits.Add(EditText(chat, message, MessageText.value text, keyboard))
            Task.FromResult EditApplied

        member _.EditMessageReplyMarkup(chat, message, keyboard, _ct) =
            edits.Add(EditKeyboard(chat, message, keyboard))
            Task.FromResult EditApplied

/// Records enqueued work instead of running it on real per-chat channels — the test decides when to
/// invoke a recorded work item (same pattern as `UpdateProcessorTests.fs`'s private fake).
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

/// Wires a real `InMemoryToolRegistry` + `InMemoryBindingStore` (already green, Foundational phase)
/// behind a `ToolDispatch`, with one binding saved for `token -> (registeredToolName, arg)`.
let private makeDispatch (token: CallbackToken) (registeredToolName: string) (arg: string option) (tool: Tool option) : ToolDispatch =
    let registry = InMemoryToolRegistry() :> IToolRegistry
    tool |> Option.iter (fun t -> registry.Register(toolName registeredToolName, t))
    let store = InMemoryBindingStore() :> IBindingStore

    store
        .Save([ ToolBinding.create token (toolName registeredToolName) arg ], CancellationToken.None)
        .GetAwaiter()
        .GetResult()

    ToolDispatch(registry, store)

[<Tests>]
let toolDispatchProcessorTests =
    testList "UpdateProcessor deferred-ack tool path" [

        testCase "a tool press runs the bound tool with its Arg, then acks exactly once with its directive" <| fun _ ->
            let token = CallbackToken.generate ()
            let press = samplePress token 1L
            let mutable capturedArg: string | null = "not set"

            let tool: Tool =
                fun ctx ->
                    task {
                        capturedArg <- ctx.Arg
                        ctx.Answer("done", alert = true)
                    }

            let dispatch = makeDispatch token "approve" (Some "42") (Some tool)
            let api = FakeBotApiClient()
            let dispatcher = FakeDispatcher()
            let observer = FakeHookObserver()
            let source = FakeUpdateSource [ ButtonPressed press ]
            let processor = UpdateProcessor(source, InMemoryHookStore(), api, dispatcher, observer, toolDispatch = dispatch)

            (processor.RunAsync CancellationToken.None).GetAwaiter().GetResult()

            match dispatcher.Enqueued with
            | [ (chat, work) ] ->
                Expect.equal chat press.Chat "the tool work is enqueued on the press's own chat"
                (work CancellationToken.None).GetAwaiter().GetResult()
            | other -> failwithf "expected exactly one enqueued item, got %d" (List.length other)

            Expect.equal capturedArg "42" "the tool ran with the bound arg on ctx.Arg"
            Expect.equal api.Calls [ AckDeferred(press.QueryId, Some "done", true) ] "acked exactly once, with the tool's directive"

        testCase "a tool with no Answer call gets a default empty ack" <| fun _ ->
            let token = CallbackToken.generate ()
            let press = samplePress token 1L
            let tool: Tool = fun _ -> Task.CompletedTask
            let dispatch = makeDispatch token "noop" None (Some tool)
            let api = FakeBotApiClient()
            let dispatcher = FakeDispatcher()
            let observer = FakeHookObserver()
            let source = FakeUpdateSource [ ButtonPressed press ]
            let processor = UpdateProcessor(source, InMemoryHookStore(), api, dispatcher, observer, toolDispatch = dispatch)

            (processor.RunAsync CancellationToken.None).GetAwaiter().GetResult()

            match dispatcher.Enqueued with
            | [ (_, work) ] -> (work CancellationToken.None).GetAwaiter().GetResult()
            | other -> failwithf "expected exactly one enqueued item, got %d" (List.length other)

            Expect.equal api.Calls [ AckDeferred(press.QueryId, None, false) ] "a silent tool still gets a default empty ack"

        testCase "a watchdog sends a default ack if the tool exceeds the budget; the tool keeps running and only one ack is ever sent" <| fun _ ->
            let token = CallbackToken.generate ()
            let press = samplePress token 1L
            let toolFinished = TaskCompletionSource()

            let tool: Tool =
                fun ctx ->
                    task {
                        do! Task.Delay 500
                        ctx.Answer("too late", alert = false)
                        toolFinished.TrySetResult() |> ignore
                    }

            let dispatch = makeDispatch token "slow" None (Some tool)
            let api = FakeBotApiClient()
            let dispatcher = FakeDispatcher()
            let observer = FakeHookObserver()
            let source = FakeUpdateSource [ ButtonPressed press ]

            let processor =
                UpdateProcessor(source, InMemoryHookStore(), api, dispatcher, observer, toolDispatch = dispatch, watchdogBudget = TimeSpan.FromMilliseconds 50.0)

            (processor.RunAsync CancellationToken.None).GetAwaiter().GetResult()

            let workTask =
                match dispatcher.Enqueued with
                | [ (_, work) ] -> work CancellationToken.None
                | other -> failwithf "expected exactly one enqueued item, got %d" (List.length other)

            // The watchdog (50ms) fires well before the tool (500ms) finishes.
            let mutable tries = 0
            while api.Calls.IsEmpty && tries < 300 do
                Thread.Sleep 10
                tries <- tries + 1

            Expect.equal api.Calls [ AckDeferred(press.QueryId, None, false) ] "the watchdog's default ack fired while the tool was still running"

            // The tool is NOT cancelled by the watchdog — it keeps running to completion.
            toolFinished.Task.Wait(TimeSpan.FromSeconds 5.0) |> ignore
            workTask.GetAwaiter().GetResult()

            Expect.equal api.Calls [ AckDeferred(press.QueryId, None, false) ] "the tool's own (losing) ack attempt did not send a second ack"

        testCase "a binding whose tool is no longer registered falls back to the ack-first unknown-token path" <| fun _ ->
            let token = CallbackToken.generate ()
            let press = samplePress token 1L
            // A binding exists (the keyboard was sent), but no tool named "vanished" is registered.
            let dispatch = makeDispatch token "vanished" None None
            let api = FakeBotApiClient()
            let dispatcher = FakeDispatcher()
            let observer = FakeHookObserver()
            let source = FakeUpdateSource [ ButtonPressed press ]
            let processor = UpdateProcessor(source, InMemoryHookStore(), api, dispatcher, observer, toolDispatch = dispatch)

            (processor.RunAsync CancellationToken.None).GetAwaiter().GetResult()

            Expect.equal api.Calls [ AckFirst press.QueryId ] "acked via the slice-1 ack-first path, no crash"
            Expect.isEmpty dispatcher.Enqueued "no tool and no hook ran"
            Expect.equal observer.Unknown [ press ] "the observer hears about the unresolvable press"

        testCase "a tool that calls EditTextAsync edits the pressed message in place, keyboard untouched" <| fun _ ->
            let token = CallbackToken.generate ()
            let press = samplePress token 1L
            let tool: Tool = fun ctx -> ctx.EditTextAsync "Approved!"
            let dispatch = makeDispatch token "approve" None (Some tool)
            let api = FakeBotApiClient()
            let dispatcher = FakeDispatcher()
            let observer = FakeHookObserver()
            let source = FakeUpdateSource [ ButtonPressed press ]
            let processor = UpdateProcessor(source, InMemoryHookStore(), api, dispatcher, observer, toolDispatch = dispatch)

            (processor.RunAsync CancellationToken.None).GetAwaiter().GetResult()

            match dispatcher.Enqueued with
            | [ (_, work) ] -> (work CancellationToken.None).GetAwaiter().GetResult()
            | other -> failwithf "expected exactly one enqueued item, got %d" (List.length other)

            Expect.equal
                api.Edits
                [ EditText(press.Chat, press.MessageId, "Approved!", None) ]
                "the pressed message was edited in place, current keyboard left untouched (None)"

        testCase "a tool that calls EditKeyboardAsync re-plans, saves the new binding, and edits the keyboard only" <| fun _ ->
            let token = CallbackToken.generate ()
            let press = samplePress token 1L
            let replacementPlan: ToolKeyboard = { Rows = [ [ ToolButton("Undo", "undo", None) ] ] }
            let tool: Tool = fun ctx -> ctx.EditKeyboardAsync replacementPlan
            let dispatch = makeDispatch token "approve" None (Some tool)
            let api = FakeBotApiClient()
            let dispatcher = FakeDispatcher()
            let observer = FakeHookObserver()
            let source = FakeUpdateSource [ ButtonPressed press ]
            let processor = UpdateProcessor(source, InMemoryHookStore(), api, dispatcher, observer, toolDispatch = dispatch)

            (processor.RunAsync CancellationToken.None).GetAwaiter().GetResult()

            match dispatcher.Enqueued with
            | [ (_, work) ] -> (work CancellationToken.None).GetAwaiter().GetResult()
            | other -> failwithf "expected exactly one enqueued item, got %d" (List.length other)

            match api.Edits with
            | [ EditKeyboard(chat, messageId, Some(RegisteredKeyboard [ [ Callback(label, newToken) ] ])) ] ->
                Expect.equal chat press.Chat "edited the pressed message's own chat"
                Expect.equal messageId press.MessageId "edited the pressed message's own id"
                Expect.equal (ButtonLabel.value label) "Undo" "the replacement button's label reached the wire"

                let savedBinding = (dispatch.Store.TryGet(newToken, CancellationToken.None)).GetAwaiter().GetResult()

                Expect.equal
                    savedBinding
                    (ValueSome(ToolBinding.create newToken (toolName "undo") None))
                    "the replacement keyboard's binding was saved into the SAME store, resolvable for the next tap"
            | other -> failwithf "expected exactly one EditKeyboard call with one replacement button, got %A" other

        testCase "repeated EditKeyboardAsync calls on the same message leave only the LATEST keyboard's bindings in the store (no unbounded leak)" <| fun _ ->
            let token = CallbackToken.generate ()
            let press = samplePress token 1L
            let editCount = 5

            let tool: Tool =
                fun ctx ->
                    task {
                        for i in 1 .. editCount do
                            let plan: ToolKeyboard = { Rows = [ [ ToolButton($"Page {i}", "counter", Some(string i)) ] ] }
                            do! ctx.EditKeyboardAsync plan
                    }

            let dispatch = makeDispatch token "counter" None (Some tool)
            let api = FakeBotApiClient()
            let dispatcher = FakeDispatcher()
            let observer = FakeHookObserver()
            let source = FakeUpdateSource [ ButtonPressed press ]
            let processor = UpdateProcessor(source, InMemoryHookStore(), api, dispatcher, observer, toolDispatch = dispatch)

            (processor.RunAsync CancellationToken.None).GetAwaiter().GetResult()

            match dispatcher.Enqueued with
            | [ (_, work) ] -> (work CancellationToken.None).GetAwaiter().GetResult()
            | other -> failwithf "expected exactly one enqueued item, got %d" (List.length other)

            let editedTokens =
                api.Edits
                |> List.choose (function
                    | EditKeyboard(_, _, Some(RegisteredKeyboard [ [ Callback(_, tok) ] ])) -> Some tok
                    | _ -> None)

            Expect.equal (List.length editedTokens) editCount "sanity: all N edits reached the wire"

            let latestToken = List.last editedTokens
            let staleTokens = editedTokens |> List.take (editCount - 1)

            for staleTok in staleTokens do
                let result = (dispatch.Store.TryGet(staleTok, CancellationToken.None)).GetAwaiter().GetResult()
                Expect.equal result ValueNone "a superseded keyboard's binding must be removed, not leaked forever"

            let latestResult = (dispatch.Store.TryGet(latestToken, CancellationToken.None)).GetAwaiter().GetResult()

            match latestResult with
            | ValueSome _ -> ()
            | ValueNone -> failwith "the LATEST keyboard's binding must still resolve"

        testCase "ctx.EditTextAsync/EditKeyboardAsync fail fast on the slice-1 closure (ack-first) path — caught and reported like any other hook exception" <| fun _ ->
            let token = CallbackToken.generate ()
            let press = samplePress token 1L
            let plan: ToolKeyboard = { Rows = [ [ ToolButton("x", "x", None) ] ] }

            let hook: Hook =
                fun ctx ->
                    task {
                        do! ctx.EditTextAsync "should not reach the wire"
                        do! ctx.EditKeyboardAsync plan
                    }

            let store: IHookStore = InMemoryHookStore()
            store.Register([ { Token = token; Hook = hook } ], CancellationToken.None).GetAwaiter().GetResult()
            let api = FakeBotApiClient()
            let dispatcher = FakeDispatcher()
            let observer = FakeHookObserver()
            let source = FakeUpdateSource [ ButtonPressed press ]
            let processor = UpdateProcessor(source, store, api, dispatcher, observer)

            (processor.RunAsync CancellationToken.None).GetAwaiter().GetResult()

            match dispatcher.Enqueued with
            | [ (_, work) ] -> (work CancellationToken.None).GetAwaiter().GetResult()
            | other -> failwithf "expected exactly one enqueued item, got %d" (List.length other)

            Expect.isEmpty api.Edits "the closure-path fail-fast prevented any edit from reaching the wire"

            match observer.Failed with
            | [ (failedPress, (:? InvalidOperationException as ex)) ] ->
                Expect.equal failedPress press "the observer attributes the failure to the pressed button"
                Expect.stringContains ex.Message "EditTextAsync" "the exception names the offending member"
            | other -> failwithf "expected exactly one InvalidOperationException reported, got %A" other

        testCase "ctx.Answer/EditTextAsync/EditKeyboardAsync throw InvalidOperationException with a clear message when called directly on a closure-path PressContext" <| fun _ ->
            let ctx =
                PressContext(
                    validLabel,
                    UMX.tag<chatId> 1L,
                    { Id = UMX.tag<userId> 1L; FirstName = "Alice"; Username = null },
                    UMX.tag<messageId> 1L,
                    CancellationToken.None,
                    (fun _ -> Task.FromResult(UMX.tag<messageId> 0L))
                )

            let assertThrows (memberName: string) (call: unit -> unit) =
                try
                    call ()
                    failwithf "expected %s to throw on the closure path, but it completed silently" memberName
                with
                | :? InvalidOperationException as ex ->
                    Expect.stringContains ex.Message memberName $"{memberName}'s exception names the offending member"
                | ex -> failwithf "expected %s to throw InvalidOperationException, got %A" memberName ex

            assertThrows "Answer" (fun () -> ctx.Answer("x"))
            assertThrows "EditTextAsync" (fun () -> ctx.EditTextAsync("x") |> ignore)
            assertThrows "EditKeyboardAsync" (fun () -> ctx.EditKeyboardAsync({ Rows = [] }) |> ignore)

        testCase
            "ToolKeyboardOps.deliver [review #4]: a throwing send neither strands the old (still-visible) keyboard's bindings nor orphans the just-saved new ones"
        <| fun _ ->
            let store = InMemoryBindingStore() :> IBindingStore
            let tracker = MessageBindingTracker()
            let chat = UMX.tag<chatId> 1L
            let staleMessageId = UMX.tag<messageId> 1L
            let staleToken = CallbackToken.generate ()
            let staleBinding = ToolBinding.create staleToken (toolName "old") None

            // Simulate a previously-delivered keyboard: its binding is saved and tracked, exactly
            // as a prior successful `deliver` call would leave it.
            (store.Save([ staleBinding ], CancellationToken.None)).GetAwaiter().GetResult()
            tracker.Record(chat, staleMessageId, [ staleToken ])

            let plan: ToolKeyboard = { Rows = [ [ ToolButton("New", "new", None) ] ] }
            let mutable generatedToken: CallbackToken option = None

            let tokenGen () =
                let t = CallbackToken.generate ()
                generatedToken <- Some t
                t

            let failingSend (_keyboard: RegisteredKeyboard) : Task<MessageId> =
                task { return failwith "simulated send failure" }

            let deliverTask =
                ToolKeyboardOps.deliver
                    "test"
                    tokenGen
                    store
                    tracker
                    chat
                    (Some staleMessageId)
                    Anyone
                    None
                    None
                    false
                    failingSend
                    CancellationToken.None
                    plan

            let mutable caught: exn option = None

            try
                deliverTask.GetAwaiter().GetResult() |> ignore
            with ex ->
                caught <- Some ex

            Expect.isTrue caught.IsSome "the send failure propagates to the caller rather than being swallowed"

            let staleStillLive = (store.TryGet(staleToken, CancellationToken.None)).GetAwaiter().GetResult()

            Expect.equal
                staleStillLive
                (ValueSome staleBinding)
                "the OLD binding (still the one visibly on the wire, since the edit never reached Telegram) must NOT be removed — no stranded live keyboard"

            match generatedToken with
            | Some newToken ->
                let newBindingResult = (store.TryGet(newToken, CancellationToken.None)).GetAwaiter().GetResult()

                Expect.equal
                    newBindingResult
                    ValueNone
                    "the just-saved NEW binding must be compensated (removed) after the send failed — no orphan binding"
            | None -> failwith "test setup: tokenGen was never invoked"

            Expect.equal
                (tracker.TryGetPrevious(chat, staleMessageId))
                (Some [ staleToken ])
                "the tracker must still show the pre-existing (stale) tokens — Record only runs after a successful send"

        testCase "the slice-1 closure path stays ack-first when toolDispatch is absent" <| fun _ ->
            let token = CallbackToken.generate ()
            let press = samplePress token 1L
            let hook: Hook = fun _ -> Task.CompletedTask
            let store: IHookStore = InMemoryHookStore()
            store.Register([ { Token = token; Hook = hook } ], CancellationToken.None).GetAwaiter().GetResult()
            let api = FakeBotApiClient()
            let dispatcher = FakeDispatcher()
            let observer = FakeHookObserver()
            let source = FakeUpdateSource [ ButtonPressed press ]
            // No `toolDispatch` argument at all — exactly the slice-1 5-arg construction.
            let processor = UpdateProcessor(source, store, api, dispatcher, observer)

            (processor.RunAsync CancellationToken.None).GetAwaiter().GetResult()

            Expect.equal api.Calls [ AckFirst press.QueryId ] "ack-first, via the 2-arg overload, unchanged"
            Expect.equal (List.length dispatcher.Enqueued) 1 "the resolved hook is still enqueued"

        testCase
            "ctx.Answer racing the watchdog never produces a torn (partial) ack directive (repeated to shake out the race)"
        <| fun _ ->
            // A VERY tight watchdog budget, repeated many times, so the tool's own write of its
            // ack directive (`ctx.Answer`, on the dispatcher's own thread) has a real chance of
            // landing concurrently with the watchdog's read of it (on a threadpool timer thread) —
            // exactly the two-independent-threads shape review #8 is about. Every recorded ack MUST
            // be one of the two VALID complete states (the watchdog's default, or the tool's own
            // full directive) — never a mix of one field from each (e.g. the new alert flag with
            // the OLD/default text, or vice versa).
            let iterations = 300
            let mutable tornCount = 0
            let mutable tornExamples: string list = []

            for i in 1..iterations do
                let token = CallbackToken.generate ()
                let press = samplePress token (int64 i)
                let releaseGate = new ManualResetEventSlim(false)

                let tool: Tool =
                    fun ctx ->
                        task {
                            releaseGate.Wait()
                            ctx.Answer("done", alert = true)
                        }

                let dispatch = makeDispatch token "racey" None (Some tool)
                let api = FakeBotApiClient()
                let dispatcher = FakeDispatcher()
                let observer = FakeHookObserver()
                let source = FakeUpdateSource [ ButtonPressed press ]

                let processor =
                    UpdateProcessor(
                        source,
                        InMemoryHookStore(),
                        api,
                        dispatcher,
                        observer,
                        toolDispatch = dispatch,
                        watchdogBudget = TimeSpan.FromMilliseconds 1.0
                    )

                (processor.RunAsync CancellationToken.None).GetAwaiter().GetResult()

                let work =
                    match dispatcher.Enqueued with
                    | [ (_, w) ] -> w
                    | other -> failwithf "expected exactly one enqueued item, got %d" (List.length other)

                // Invoking `work` on a THREADPOOL thread (not this test's own thread): the tool's
                // body blocks synchronously on `releaseGate.Wait()` before its first genuine await,
                // so calling it directly here (an F# `task {}` starts eagerly, on the CALLING
                // thread, up to its first suspension point) would deadlock this very thread against
                // the `releaseGate.Set()` two lines below.
                let workTask = Task.Run(fun () -> work CancellationToken.None)

                // Release the tool's write right around when the ~1ms watchdog is expected to
                // fire on its own thread — maximizes the odds of the two racing.
                Thread.Sleep 1
                releaseGate.Set()
                workTask.GetAwaiter().GetResult()

                match api.Calls with
                | [ AckDeferred(_, None, false) ]
                | [ AckDeferred(_, Some "done", true) ] -> ()
                | other ->
                    tornCount <- tornCount + 1
                    tornExamples <- sprintf "%A" other :: tornExamples

            Expect.equal
                tornCount
                0
                $"never observed a torn ack directive across {iterations} iterations; examples: %A{tornExamples}"
    ]
