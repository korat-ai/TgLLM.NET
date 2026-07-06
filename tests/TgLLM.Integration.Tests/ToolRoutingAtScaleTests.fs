/// T035 (Phase 8, closing SC-002 for the Tool Router): >=100 interleaved taps spanning MULTIPLE
/// tools and arguments each invoke exactly their own bound tool with the correct arg — zero
/// cross-invocation. Engine-level: the REAL `ToolDispatch` + `UpdateProcessor` + in-memory fakes for
/// the transport-facing ports, mirroring slice-1's `RoutingAtScaleTests.fs` (same `sourceOf`/
/// `pressOf`/`drive` shape), but driven through `InMemoryToolRegistry`/`InMemoryBindingStore`/
/// `ToolDispatch` (token -> tool name + arg) instead of raw `IHookStore`/`HookBinding` (token -> hook
/// closure) — this is the Tool Router's OWN routing-at-scale guarantee, not a re-run of slice-1's.
module TgLLM.Integration.Tests.ToolRoutingAtScaleTests

open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open System.Threading.Channels
open Expecto
open FSharp.UMX
open TgLLM.Core

let private anyLabel: ButtonLabel =
    match ButtonLabel.create "b" with
    | Ok l -> l
    | Error e -> failwithf "unreachable %A" e

let private toolName (s: string) : ToolName =
    match ToolName.create s with
    | Ok n -> n
    | Error e -> failwithf "unreachable %A" e

/// An `IUpdateSource` that yields a fixed list of events (in order) then completes.
let private sourceOf (events: AgentEvent list) : IUpdateSource =
    { new IUpdateSource with
        member _.Updates(ct: CancellationToken) =
            let channel = Channel.CreateUnbounded<AgentEvent>()
            for e in events do
                channel.Writer.TryWrite e |> ignore
            channel.Writer.TryComplete() |> ignore
            channel.Reader.ReadAllAsync ct }

/// Records `AnswerCallback` calls; sends/edits are no-ops.
type private RecordingApi() =
    let mutable acks = 0
    member _.Acks = acks

    interface IBotApiClient with
        member _.SendText(_, _, _) = Task.FromResult(UMX.tag<messageId> 1L)
        member _.SendKeyboard(_, _, _, _) = Task.FromResult(UMX.tag<messageId> 1L)

        member _.AnswerCallback(_, _) =
            Interlocked.Increment(&acks) |> ignore
            Task.CompletedTask

        /// This suite exercises the DEFERRED-ACK tool path exclusively (a `?toolDispatch` resolves
        /// every press below), so this is the overload actually used, unlike slice-1's
        /// `RoutingAtScaleTests.fs` (whose ack-first path uses the 2-arg overload instead).
        member _.AnswerCallback(_, _, _, _) =
            Interlocked.Increment(&acks) |> ignore
            Task.CompletedTask

        member _.EditMessageText(_, _, _, _, _) = Task.CompletedTask
        member _.EditMessageReplyMarkup(_, _, _, _) = Task.CompletedTask

type private CountingObserver() =
    let mutable failed = 0
    let mutable unknown = 0
    member _.Failed = failed
    member _.Unknown = unknown

    interface IHookObserver with
        member _.OnHookFailed(_, _) = Interlocked.Increment(&failed) |> ignore
        member _.OnUnknownToken(_) = Interlocked.Increment(&unknown) |> ignore

let private pressOf (token: CallbackToken) (chat: int64) : AgentEvent =
    ButtonPressed
        { Token = token
          QueryId = UMX.tag<callbackQueryId> $"q-{CallbackToken.value token}"
          Chat = UMX.tag<chatId> chat
          User =
            { Id = UMX.tag<userId> 1L
              FirstName = "T"
              Username = null }
          MessageId = UMX.tag<messageId> 1L
          ButtonLabel = anyLabel }

/// Drive the real engine (ToolDispatch + UpdateProcessor + PerChatChannelDispatcher) over `events`,
/// registering `tools` (name -> handler) and `bindings` (token -> tool name + arg) beforehand, and
/// wait (bounded) until `expectedRuns` presses have completed. Returns the acks and observer counts.
let private drive
    (tools: (string * Tool) list)
    (bindings: ToolBinding list)
    (events: AgentEvent list)
    (expectedRuns: unit -> int)
    (targetRuns: int)
    : Task<int * CountingObserver> =
    task {
        let registry = InMemoryToolRegistry() :> IToolRegistry
        for name, tool in tools do
            registry.Register(toolName name, tool)

        let store = InMemoryBindingStore() :> IBindingStore
        do! store.Save(bindings, CancellationToken.None)
        let dispatch = ToolDispatch(registry, store)

        let api = RecordingApi()
        let observer = CountingObserver()
        use dispatcher = new PerChatChannelDispatcher()
        let processor = UpdateProcessor(sourceOf events, InMemoryHookStore(), api, dispatcher, observer :> IHookObserver, toolDispatch = dispatch)
        use cts = new CancellationTokenSource()

        do! processor.RunAsync cts.Token

        let mutable tries = 0

        while expectedRuns () < targetRuns && tries < 500 do
            do! Task.Delay 10
            tries <- tries + 1

        return (api.Acks, observer)
    }

[<Tests>]
let toolRoutingAtScaleTests =
    testList
        "ToolRoutingAtScale"
        [

          testCaseAsync "≥100 interleaved taps across MULTIPLE tools and args each invoke exactly their own bound tool + arg, zero cross-invocation (SC-002)"
          <| async {
              do!
                  task {
                      // 8 distinct (tool, arg) bindings across 4 different registered tool names, 2 chats.
                      let toolNames = [ "approve"; "reject"; "escalate"; "archive" ]
                      let tokens = [ for _ in 1..8 -> CallbackToken.generate () ]

                      // Each token gets its OWN (toolName, arg) pair, so a cross-invocation would show
                      // up as a mismatch between the token's expected pair and what the tool observed.
                      let tokenAssignments =
                          tokens
                          |> List.mapi (fun i token -> token, toolNames.[i % 4], $"arg-{i}")

                      let observedRuns = ConcurrentDictionary<CallbackToken, (string * string) list>()

                      let recordingTool (expectedName: string) : Tool =
                          fun ctx ->
                              task {
                                  let arg = ctx.Arg |> Option.ofObj |> Option.defaultValue "<none>"
                                  // Find which token this press was bound to isn't directly knowable from
                                  // `ctx` (Arg/label only) — but since args are unique per token, the arg
                                  // ITSELF identifies which binding fired; record (toolName, arg) keyed by
                                  // the token that OUGHT to have produced this exact arg.
                                  let matchingToken =
                                      tokenAssignments
                                      |> List.tryFind (fun (_, name, expectedArg) -> name = expectedName && expectedArg = arg)
                                      |> Option.map (fun (token, _, _) -> token)

                                  match matchingToken with
                                  | Some token ->
                                      observedRuns.AddOrUpdate(
                                          token,
                                          [ (expectedName, arg) ],
                                          (fun _ existing -> (expectedName, arg) :: existing)
                                      )
                                      |> ignore
                                  | None -> failwithf "a tool ran with an arg (%s, %s) that matches no known binding" expectedName arg
                              }

                      let tools = [ for name in toolNames -> name, recordingTool name ]

                      let bindings =
                          [ for token, name, arg in tokenAssignments ->
                                { Token = token; ToolName = toolName name; Arg = Some arg } ]

                      // 120 presses distributed deterministically across the 8 tokens / 2 chats.
                      let events =
                          [ for i in 0..119 ->
                                let token, _, _ = tokenAssignments.[i % 8]
                                let chat = int64 (i % 2)
                                pressOf token chat ]

                      let expectedPer = 15 // 120 / 8

                      let totalRuns () =
                          observedRuns.Values |> Seq.sumBy List.length

                      let! (acks, observer) = drive tools bindings events totalRuns 120

                      Expect.equal (totalRuns ()) 120 "every press ran exactly one tool"
                      Expect.equal acks 120 "every press was acknowledged (deferred-ack tool path)"
                      Expect.equal observer.Failed 0 "no tool run was reported as failed"
                      Expect.equal observer.Unknown 0 "every press resolved to a known binding — none fell back to the ack-first unknown path"

                      for token, expectedName, expectedArg in tokenAssignments do
                          match observedRuns.TryGetValue token with
                          | true, runs ->
                              Expect.equal (List.length runs) expectedPer $"token bound to ({expectedName}, {expectedArg}) ran exactly its own {expectedPer} presses"

                              Expect.all
                                  runs
                                  (fun (name, arg) -> name = expectedName && arg = expectedArg)
                                  $"every run of this token's tool observed EXACTLY its own (tool, arg) pair — zero cross-invocation"
                          | false, _ -> failwithf "token bound to (%s, %s) never ran" expectedName expectedArg
                  }
                  |> Async.AwaitTask
          } ]
