/// T015: failing tests for `UpdateProcessor`'s deferred-ack tool path (data-model.md "Tool dispatch
/// (the deferred-ack tool path) + processor wiring", research.md D2/D6). Written before T016 wires
/// the optional `?toolDispatch` collaborator into `UpdateProcessor` — this file MUST fail to compile
/// until then (Red). Uses REAL in-memory collaborators (`InMemoryToolRegistry`/`InMemoryBindingStore`,
/// already green from the Foundational phase) plus in-memory fakes for the transport-facing ports,
/// mirroring `UpdateProcessorTests.fs`'s fakes.
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

type private FakeBotApiClient() =
    let calls = ResizeArray<AckCall>()
    member _.Calls: AckCall list = List.ofSeq calls

    interface IBotApiClient with
        member _.SendText(_chat, _text, _ct) = Task.FromResult(UMX.tag<messageId> 0L)
        member _.SendKeyboard(_chat, _text, _keyboard, _ct) = Task.FromResult(UMX.tag<messageId> 0L)

        member _.AnswerCallback(query, _ct) =
            calls.Add(AckFirst query)
            Task.CompletedTask

        member _.AnswerCallback(query, text, showAlert, _ct) =
            calls.Add(AckDeferred(query, text, showAlert))
            Task.CompletedTask

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

/// Wires a real `InMemoryToolRegistry` + `InMemoryBindingStore` (already green, Foundational phase)
/// behind a `ToolDispatch`, with one binding saved for `token -> (registeredToolName, arg)`.
let private makeDispatch (token: CallbackToken) (registeredToolName: string) (arg: string option) (tool: Tool option) : ToolDispatch =
    let registry = InMemoryToolRegistry() :> IToolRegistry
    tool |> Option.iter (fun t -> registry.Register(toolName registeredToolName, t))
    let store = InMemoryBindingStore() :> IBindingStore

    store
        .Save([ { Token = token; ToolName = toolName registeredToolName; Arg = arg } ], CancellationToken.None)
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
    ]
