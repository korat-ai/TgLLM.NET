/// Tests for `UpdateProcessor`'s owner check (US1): after a tool press resolves to a bound tool,
/// but BEFORE that tool is ever enqueued, the presser is compared to the binding's `Owner`. A
/// non-owner press is refused — acked with a notice, no tool runs, the observer hears about it; the
/// owner's own press (and any press on an `Anyone`-scoped binding) runs the tool normally.
///
/// The "anonymous/unidentifiable presser" refusal (`OwnerScope.isAllowed scope None`) is already
/// covered at the pure-kernel level in `OwnerScopeTests.fs` — `ButtonPress.User` is a mandatory
/// field in the current transport mapping (`CallbackQuery.From` is a required Bot API field), so
/// there is no wiring-level path that produces an unidentifiable presser to exercise here.
module TgLLM.Core.Tests.OwnerRoutingTests

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

let private pressFrom (token: CallbackToken) (userId: int64) : ButtonPress =
    { Token = token
      QueryId = UMX.tag<callbackQueryId> $"query-{CallbackToken.value token}"
      Chat = UMX.tag<chatId> 1L
      User = { Id = UMX.tag<userId> userId; FirstName = "Presser"; Username = null }
      MessageId = UMX.tag<messageId> 1L
      ButtonLabel = validLabel }

/// A finite `IUpdateSource` fake that yields a fixed list of events, then completes (same shape as
/// `ToolDispatchProcessorTests.fs`'s private fake — duplicated here since that one is
/// module-private).
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
/// Router's deferred-ack (text/alert) overload — an owner refusal uses the LATTER, with the notice
/// text set.
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

        member _.EditMessageText(_chat, _message, _text, _keyboard, _ct) = Task.CompletedTask
        member _.EditMessageReplyMarkup(_chat, _message, _keyboard, _ct) = Task.CompletedTask

/// Records enqueued work instead of running it — a refused non-owner press must enqueue NOTHING at
/// all (same pattern as `ToolDispatchProcessorTests.fs`'s private fake).
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
        member _.OnRunLoopFailed(_error) = ()

/// Wires a real `InMemoryToolRegistry` + `InMemoryBindingStore` behind a `ToolDispatch`, with one
/// binding saved for `token -> (registeredToolName, owner, deniedNotice)`.
let private makeDispatch (token: CallbackToken) (owner: OwnerScope) (deniedNotice: string option) (tool: Tool) : ToolDispatch =
    let registry = InMemoryToolRegistry() :> IToolRegistry
    registry.Register(toolName "approve", tool)
    let store = InMemoryBindingStore() :> IBindingStore

    let binding =
        { ToolBinding.create token (toolName "approve") None with
            Owner = owner
            DeniedNotice = deniedNotice }

    store.Save([ binding ], CancellationToken.None).GetAwaiter().GetResult()
    ToolDispatch(registry, store)

let private run (source: FakeUpdateSource) (api: FakeBotApiClient) (dispatcher: FakeDispatcher) (observer: FakeHookObserver) (dispatch: ToolDispatch) =
    let processor = UpdateProcessor(source, InMemoryHookStore(), api, dispatcher, observer, toolDispatch = dispatch)
    (processor.RunAsync CancellationToken.None).GetAwaiter().GetResult()

[<Tests>]
let ownerRoutingTests =
    testList "UpdateProcessor owner routing" [

        testCase "a non-owner press of a User-scoped binding is refused: acked with the default notice, no tool runs, surfaced" <| fun _ ->
            let token = CallbackToken.generate ()
            let ownerId = 1L
            let presserId = 2L
            let mutable toolRan = false
            let tool: Tool = fun _ -> toolRan <- true; Task.CompletedTask
            let dispatch = makeDispatch token (User(UMX.tag<userId> ownerId)) None tool
            let press = pressFrom token presserId
            let api = FakeBotApiClient()
            let dispatcher = FakeDispatcher()
            let observer = FakeHookObserver()

            run (FakeUpdateSource [ ButtonPressed press ]) api dispatcher observer dispatch

            Expect.isFalse toolRan "a non-owner press must never run the tool"
            Expect.isEmpty dispatcher.Enqueued "no work is enqueued for a refused non-owner press"
            Expect.equal api.Calls [ AckDeferred(press.QueryId, Some OwnerScope.DefaultDeniedNotice, false) ] "acked with the built-in default notice"
            Expect.equal observer.Unknown [ press ] "the refusal is surfaced via the observer"

        testCase "the owner's own press runs the tool normally" <| fun _ ->
            let token = CallbackToken.generate ()
            let ownerId = 1L
            let mutable toolRan = false
            let tool: Tool = fun _ -> toolRan <- true; Task.CompletedTask
            let dispatch = makeDispatch token (User(UMX.tag<userId> ownerId)) None tool
            let press = pressFrom token ownerId
            let api = FakeBotApiClient()
            let dispatcher = FakeDispatcher()
            let observer = FakeHookObserver()

            run (FakeUpdateSource [ ButtonPressed press ]) api dispatcher observer dispatch

            match dispatcher.Enqueued with
            | [ (chat, work) ] ->
                Expect.equal chat press.Chat "the owner's press is enqueued on its own chat"
                (work CancellationToken.None).GetAwaiter().GetResult()
            | other -> failwithf "expected exactly one enqueued item, got %d" (List.length other)

            Expect.isTrue toolRan "the owner's press runs the bound tool"
            Expect.isEmpty observer.Unknown "the owner's press is not a refusal"

        testCase "an Anyone-scoped binding runs the tool for any presser" <| fun _ ->
            let token = CallbackToken.generate ()
            let mutable toolRan = false
            let tool: Tool = fun _ -> toolRan <- true; Task.CompletedTask
            let dispatch = makeDispatch token Anyone None tool
            let press = pressFrom token 999L
            let api = FakeBotApiClient()
            let dispatcher = FakeDispatcher()
            let observer = FakeHookObserver()

            run (FakeUpdateSource [ ButtonPressed press ]) api dispatcher observer dispatch

            match dispatcher.Enqueued with
            | [ (_, work) ] -> (work CancellationToken.None).GetAwaiter().GetResult()
            | other -> failwithf "expected exactly one enqueued item, got %d" (List.length other)

            Expect.isTrue toolRan "Anyone-scoped bindings are unaffected — slice-2 behavior"

        testCase "a non-owner press is acked with the binding's own DeniedNotice override, when the host set one" <| fun _ ->
            let token = CallbackToken.generate ()
            let ownerId = 1L
            let presserId = 2L
            let tool: Tool = fun _ -> Task.CompletedTask
            let dispatch = makeDispatch token (User(UMX.tag<userId> ownerId)) (Some "Ask Alice instead.") tool
            let press = pressFrom token presserId
            let api = FakeBotApiClient()
            let dispatcher = FakeDispatcher()
            let observer = FakeHookObserver()

            run (FakeUpdateSource [ ButtonPressed press ]) api dispatcher observer dispatch

            Expect.equal
                api.Calls
                [ AckDeferred(press.QueryId, Some "Ask Alice instead.", false) ]
                "the keyboard's own notice override wins over the built-in default"
    ]
