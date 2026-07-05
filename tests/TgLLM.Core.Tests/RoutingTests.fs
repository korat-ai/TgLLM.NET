/// T014: failing FsCheck property tests for `Routing.decide` (data-model.md "Inbound: a press").
/// Written before `TgLLM.Core.Routing` exists — this file MUST fail to compile until T015
/// implements `RouteDecision`/`Routing.decide` (Red).
module TgLLM.Core.Tests.RoutingTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open FSharp.UMX
open TgLLM.Core

let private noopHook: Hook = fun _ -> Task.CompletedTask

let private validLabel =
    match ButtonLabel.create "Yes" with
    | Ok label -> label
    | Error e -> failwithf "test setup: unreachable %A" e

/// `Routing.decide` only inspects `press.Token` (data-model.md), so the other fields are fixed,
/// valid sample data — these properties are about token resolution, not press-field validation.
let private samplePress (token: CallbackToken) : ButtonPress =
    { Token = token
      QueryId = UMX.tag<callbackQueryId> "sample-query-id"
      Chat = UMX.tag<chatId> 1L
      User = { Id = UMX.tag<userId> 1L; FirstName = "Alice"; Username = null }
      MessageId = UMX.tag<messageId> 1L
      ButtonLabel = validLabel }

/// A dummy `PressContext`, just to invoke a `Hook` under test — its own field values are
/// irrelevant to these tests.
let private samplePressContext () : PressContext =
    PressContext(validLabel, UMX.tag<chatId> 1L, { Id = UMX.tag<userId> 1L; FirstName = "Alice"; Username = null },
                 UMX.tag<messageId> 1L, CancellationToken.None, (fun _ -> Task.FromResult(UMX.tag<messageId> 0L)))

[<Tests>]
let routingTests =
    testList "Routing.decide" [

        testProperty "a token present in the resolver yields RunHook with exactly that hook" <| fun (guid: Guid) ->
            let token = CallbackToken.ofGuid guid
            let press = samplePress token
            // Two distinct hooks (behaviorally, not just by identity) — the resolver only ever
            // knows about `expectedHook`. `Routing.decide` must return *that* hook, not some
            // other/no hook, so we invoke whatever comes back and check which one ran.
            let mutable expectedHookRan = false
            let mutable decoyHookRan = false
            let expectedHook: Hook = fun _ -> expectedHookRan <- true; Task.CompletedTask
            let decoyHook: Hook = fun _ -> decoyHookRan <- true; Task.CompletedTask
            let resolve t = if t = token then ValueSome expectedHook else ValueSome decoyHook
            match Routing.decide resolve press with
            | RunHook h ->
                h(samplePressContext ()) |> ignore
                expectedHookRan && not decoyHookRan
            | AcknowledgeOnly -> false

        testProperty "a token absent from the resolver yields AcknowledgeOnly" <| fun (guid: Guid) ->
            let token = CallbackToken.ofGuid guid
            let press = samplePress token
            let resolve (_: CallbackToken) : Hook voption = ValueNone
            match Routing.decide resolve press with
            | AcknowledgeOnly -> true
            | RunHook _ -> false

        testProperty "decide is total: never throws, for any resolver outcome" <| fun (guid: Guid, returnsHook: bool) ->
            let token = CallbackToken.ofGuid guid
            let press = samplePress token
            let resolve (_: CallbackToken) = if returnsHook then ValueSome noopHook else ValueNone
            try
                Routing.decide resolve press |> ignore
                true
            with _ ->
                false

        testCase "a malformed/unknown token (resolver never asked about it) still yields AcknowledgeOnly" <| fun _ ->
            let token = CallbackToken.generate ()
            let press = samplePress token
            let resolve (_: CallbackToken) : Hook voption = ValueNone
            match Routing.decide resolve press with
            | AcknowledgeOnly -> ()
            | RunHook _ -> failwith "expected AcknowledgeOnly for an unresolved token"
    ]
