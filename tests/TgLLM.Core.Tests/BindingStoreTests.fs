/// Tests for `InMemoryBindingStore`, the `IBindingStore` implementation covering tool bindings.
module TgLLM.Core.Tests.BindingStoreTests

open System.Threading
open Expecto
open TgLLM.Core

let private toolName (s: string) : ToolName =
    match ToolName.create s with
    | Ok n -> n
    | Error e -> failwithf "test setup: unreachable %A" e

[<Tests>]
let bindingStoreTests =
    testList "InMemoryBindingStore" [

        testCase "TryGet on an unknown token returns ValueNone" <| fun _ ->
            let store = InMemoryBindingStore() :> IBindingStore
            let token = CallbackToken.generate ()

            let result = (store.TryGet(token, CancellationToken.None)).GetAwaiter().GetResult()

            Expect.equal result ValueNone "an unsaved token resolves to nothing"

        testCase "Save then TryGet round-trips the exact binding" <| fun _ ->
            let store = InMemoryBindingStore() :> IBindingStore
            let token = CallbackToken.generate ()
            let binding: ToolBinding = ToolBinding.create token (toolName "approve") (Some "42")

            (store.Save([ binding ], CancellationToken.None)).GetAwaiter().GetResult()
            let result = (store.TryGet(token, CancellationToken.None)).GetAwaiter().GetResult()

            Expect.equal result (ValueSome binding) "the exact saved binding round-trips"

        testCase "a binding with no arg round-trips too" <| fun _ ->
            let store = InMemoryBindingStore() :> IBindingStore
            let token = CallbackToken.generate ()
            let binding: ToolBinding = ToolBinding.create token (toolName "reject") None

            (store.Save([ binding ], CancellationToken.None)).GetAwaiter().GetResult()
            let result = (store.TryGet(token, CancellationToken.None)).GetAwaiter().GetResult()

            Expect.equal result (ValueSome binding) "an argument-less binding round-trips"

        testCase "Remove makes a previously saved token resolve to ValueNone" <| fun _ ->
            let store = InMemoryBindingStore() :> IBindingStore
            let token = CallbackToken.generate ()
            let binding: ToolBinding = ToolBinding.create token (toolName "approve") None

            (store.Save([ binding ], CancellationToken.None)).GetAwaiter().GetResult()
            (store.Remove([ token ], CancellationToken.None)).GetAwaiter().GetResult()
            let result = (store.TryGet(token, CancellationToken.None)).GetAwaiter().GetResult()

            Expect.equal result ValueNone "a removed token no longer resolves"

        testCase "Save registers multiple bindings as a unit" <| fun _ ->
            let store = InMemoryBindingStore() :> IBindingStore
            let tokenA, tokenB = CallbackToken.generate (), CallbackToken.generate ()

            let bindingA: ToolBinding = ToolBinding.create tokenA (toolName "approve") None
            let bindingB: ToolBinding = ToolBinding.create tokenB (toolName "reject") (Some "x")

            (store.Save([ bindingA; bindingB ], CancellationToken.None)).GetAwaiter().GetResult()

            Expect.equal
                ((store.TryGet(tokenA, CancellationToken.None)).GetAwaiter().GetResult())
                (ValueSome bindingA)
                "the first binding of the batch is resolvable"

            Expect.equal
                ((store.TryGet(tokenB, CancellationToken.None)).GetAwaiter().GetResult())
                (ValueSome bindingB)
                "the second binding of the batch is resolvable"
    ]
