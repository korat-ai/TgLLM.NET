/// T008: failing tests for `InMemoryToolRegistry` (data-model.md "Ports"). Written before
/// `TgLLM.Core.Tools` exists — this file MUST fail to compile until T009 implements
/// `IToolRegistry`/`InMemoryToolRegistry` (Red).
module TgLLM.Core.Tests.ToolRegistryTests

open System.Threading
open System.Threading.Tasks
open FSharp.UMX
open Expecto
open TgLLM.Core

let private name (s: string) : ToolName =
    match ToolName.create s with
    | Ok n -> n
    | Error e -> failwithf "test setup: unreachable %A" e

let private validLabel: ButtonLabel =
    match ButtonLabel.create "b" with
    | Ok l -> l
    | Error e -> failwithf "test setup: unreachable %A" e

/// A minimal, real `PressContext` — these tests only care whether a `Tool` runs, not what it
/// observes, so the exact field values are arbitrary but valid (no `Unchecked.defaultof`).
let private samplePressContext () : PressContext =
    PressContext(
        validLabel,
        UMX.tag<chatId> 1L,
        { Id = UMX.tag<userId> 1L; FirstName = "Test"; Username = null },
        UMX.tag<messageId> 1L,
        CancellationToken.None,
        (fun _ -> Task.FromResult(UMX.tag<messageId> 0L))
    )

[<Tests>]
let toolRegistryTests =
    testList "InMemoryToolRegistry" [

        testCase "resolving an unregistered name returns ValueNone" <| fun _ ->
            // `Tool` is a function type, so `Tool voption` can't derive equality (Expect.equal); the
            // ValueNone/ValueSome shape is asserted by pattern match instead.
            let registry = InMemoryToolRegistry() :> IToolRegistry

            match registry.TryResolve(name "approve") with
            | ValueNone -> ()
            | ValueSome _ -> failtest "expected an unregistered name to resolve to nothing"

        testCase "a registered tool resolves to the exact same tool" <| fun _ ->
            let registry = InMemoryToolRegistry() :> IToolRegistry
            let mutable ran = false
            let tool: Tool = fun _ -> task { ran <- true }

            registry.Register(name "approve", tool)

            match registry.TryResolve(name "approve") with
            | ValueSome resolved ->
                (resolved (samplePressContext ())).GetAwaiter().GetResult()
                Expect.isTrue ran "the resolved tool is the exact tool that was registered"
            | ValueNone -> failtest "expected the registered tool to resolve"

        testCase "registering the same name again replaces the previous tool" <| fun _ ->
            let registry = InMemoryToolRegistry() :> IToolRegistry
            let mutable calls = []
            let first: Tool = fun _ -> task { calls <- "first" :: calls }
            let second: Tool = fun _ -> task { calls <- "second" :: calls }

            registry.Register(name "approve", first)
            registry.Register(name "approve", second)

            match registry.TryResolve(name "approve") with
            | ValueSome resolved ->
                (resolved (samplePressContext ())).GetAwaiter().GetResult()
                Expect.equal calls [ "second" ] "the later registration replaces the earlier one"
            | ValueNone -> failtest "expected the registered tool to resolve"

        testCase "two distinct names resolve independently" <| fun _ ->
            let registry = InMemoryToolRegistry() :> IToolRegistry
            let approveRan = ref false
            let rejectRan = ref false
            registry.Register(name "approve", fun _ -> task { approveRan.Value <- true })
            registry.Register(name "reject", fun _ -> task { rejectRan.Value <- true })

            match registry.TryResolve(name "reject") with
            | ValueSome resolved -> (resolved (samplePressContext ())).GetAwaiter().GetResult()
            | ValueNone -> failtest "expected 'reject' to resolve"

            Expect.isFalse approveRan.Value "resolving 'reject' does not run 'approve'"
            Expect.isTrue rejectRan.Value "resolving 'reject' runs 'reject'"
    ]
