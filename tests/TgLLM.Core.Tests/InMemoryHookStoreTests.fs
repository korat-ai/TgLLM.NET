/// Tests for `InMemoryHookStore` (the `IHookStore` implementation).
module TgLLM.Core.Tests.InMemoryHookStoreTests

open System.Threading
open System.Threading.Tasks
open Expecto
open FSharp.UMX
open TgLLM.Core

// `InMemoryHookStore`'s contract guarantees synchronous completion (it completes synchronously,
// hence `ValueTask`), so blocking on the returned `ValueTask` here is a
// deliberate, low-risk test-only simplification — not a pattern for production code paths
// (Always-Rule: avoid `.Result`/blocking on genuinely asynchronous work).
let private run (vt: ValueTask) = vt.GetAwaiter().GetResult()
let private runValue (vt: ValueTask<'a>) = vt.GetAwaiter().GetResult()

let private validLabel =
    match ButtonLabel.create "Yes" with
    | Ok label -> label
    | Error e -> failwithf "test setup: unreachable %A" e

let private samplePressContext () : PressContext =
    PressContext(
        validLabel,
        UMX.tag<chatId> 1L,
        { Id = UMX.tag<userId> 1L; FirstName = "Alice"; Username = null },
        UMX.tag<messageId> 1L,
        CancellationToken.None,
        (fun _ -> Task.FromResult(UMX.tag<messageId> 0L))
    )

/// A hook that flips `ran` to `true` when invoked, so tests can tell resolved hooks apart
/// behaviorally rather than by (fragile) closure reference identity.
let private trackingHook () =
    let mutable ran = false
    let hook: Hook = fun _ -> ran <- true; Task.CompletedTask
    hook, (fun () -> ran)

[<Tests>]
let inMemoryHookStoreTests =
    testList "InMemoryHookStore" [

        testCase "Register makes every binding resolvable" <| fun _ ->
            let store: IHookStore = InMemoryHookStore()
            let hookA, ranA = trackingHook ()
            let hookB, ranB = trackingHook ()
            let tokenA = CallbackToken.generate ()
            let tokenB = CallbackToken.generate ()
            let bindings: HookBinding list = [ { Token = tokenA; Hook = hookA }; { Token = tokenB; Hook = hookB } ]

            run (store.Register(bindings, CancellationToken.None))

            match runValue (store.TryResolve(tokenA, CancellationToken.None)) with
            | ValueSome h ->
                h(samplePressContext ()) |> ignore
                Expect.isTrue (ranA ()) "resolved hook for tokenA is the one registered for tokenA"
                Expect.isFalse (ranB ()) "resolving tokenA must not run tokenB's hook"
            | ValueNone -> failwith "expected tokenA to resolve after Register"

            match runValue (store.TryResolve(tokenB, CancellationToken.None)) with
            | ValueSome h ->
                h(samplePressContext ()) |> ignore
                Expect.isTrue (ranB ()) "resolved hook for tokenB is the one registered for tokenB"
            | ValueNone -> failwith "expected tokenB to resolve after Register"

        testCase "TryResolve on an unknown token returns ValueNone, never throws" <| fun _ ->
            let store: IHookStore = InMemoryHookStore()
            let unknownToken = CallbackToken.generate ()
            match runValue (store.TryResolve(unknownToken, CancellationToken.None)) with
            | ValueNone -> ()
            | ValueSome _ -> failwith "an unregistered token must not resolve"

        testCase "Remove makes a previously-registered token unresolvable" <| fun _ ->
            let store: IHookStore = InMemoryHookStore()
            let hook, _ = trackingHook ()
            let token = CallbackToken.generate ()
            run (store.Register([ { Token = token; Hook = hook } ], CancellationToken.None))

            match runValue (store.TryResolve(token, CancellationToken.None)) with
            | ValueSome _ -> ()
            | ValueNone -> failwith "expected the token to resolve before Remove"

            run (store.Remove([ token ], CancellationToken.None))

            match runValue (store.TryResolve(token, CancellationToken.None)) with
            | ValueNone -> ()
            | ValueSome _ -> failwith "expected the token to be unresolvable after Remove"
    ]
