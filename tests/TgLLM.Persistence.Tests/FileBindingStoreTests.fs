/// T025: failing tests for `FileBindingStore` (data-model.md "Tool binding", research.md D5).
/// Written before `TgLLM.Persistence.FileBindingStore` exists — this file MUST fail to compile until
/// T026 implements it (Red). Covers the plain `IBindingStore` contract (Save → TryGet, Remove) PLUS
/// the restart guarantee itself: re-opening the SAME file in a brand-new instance still resolves
/// bindings saved by a previous instance (SC-004, at the unit level — `RestartPersistenceTests.fs`,
/// T028, proves it end-to-end through the real bot).
module TgLLM.Persistence.Tests.FileBindingStoreTests

open System
open System.IO
open System.Threading
open Expecto
open TgLLM.Core
open TgLLM.Persistence

let private toolName (s: string) : ToolName =
    match ToolName.create s with
    | Ok n -> n
    | Error e -> failwithf "test setup: unreachable %A" e

/// A fresh temp file path (not yet created) — `FileBindingStore.openAt` must tolerate a missing file.
let private tempPath () : string =
    Path.Combine(Path.GetTempPath(), $"tgllm-tool-router-tests-{Guid.NewGuid()}.json")

[<Tests>]
let fileBindingStoreTests =
    testList "FileBindingStore" [

        testCase "openAt on a path with no existing file starts empty" <| fun _ ->
            let path = tempPath ()

            try
                let store = FileBindingStore.openAt path :> IBindingStore
                let token = CallbackToken.generate ()

                let result = (store.TryGet(token, CancellationToken.None)).GetAwaiter().GetResult()

                Expect.equal result ValueNone "no file yet ⇒ nothing resolves"
            finally
                File.Delete path

        testCase "Save then TryGet round-trips the exact binding (same instance)" <| fun _ ->
            let path = tempPath ()

            try
                let store = FileBindingStore.openAt path :> IBindingStore
                let token = CallbackToken.generate ()
                let binding: ToolBinding = { Token = token; ToolName = toolName "approve"; Arg = Some "42" }

                (store.Save([ binding ], CancellationToken.None)).GetAwaiter().GetResult()
                let result = (store.TryGet(token, CancellationToken.None)).GetAwaiter().GetResult()

                Expect.equal result (ValueSome binding) "the exact saved binding round-trips"
            finally
                File.Delete path

        testCase "a binding with no arg round-trips too" <| fun _ ->
            let path = tempPath ()

            try
                let store = FileBindingStore.openAt path :> IBindingStore
                let token = CallbackToken.generate ()
                let binding: ToolBinding = { Token = token; ToolName = toolName "reject"; Arg = None }

                (store.Save([ binding ], CancellationToken.None)).GetAwaiter().GetResult()
                let result = (store.TryGet(token, CancellationToken.None)).GetAwaiter().GetResult()

                Expect.equal result (ValueSome binding) "an argument-less binding round-trips"
            finally
                File.Delete path

        testCase "re-opening the SAME file in a NEW instance still resolves a previously saved binding (SC-004)" <| fun _ ->
            let path = tempPath ()

            try
                let token = CallbackToken.generate ()
                let binding: ToolBinding = { Token = token; ToolName = toolName "approve"; Arg = Some "7" }

                let firstInstance = FileBindingStore.openAt path :> IBindingStore
                (firstInstance.Save([ binding ], CancellationToken.None)).GetAwaiter().GetResult()

                // Simulate a restart: nothing but the file on disk connects the two instances.
                let secondInstance = FileBindingStore.openAt path :> IBindingStore
                let result = (secondInstance.TryGet(token, CancellationToken.None)).GetAwaiter().GetResult()

                Expect.equal result (ValueSome binding) "a fresh instance over the same file restores the binding after a restart"
            finally
                File.Delete path

        testCase "Remove makes a previously saved token resolve to ValueNone, even after a reopen" <| fun _ ->
            let path = tempPath ()

            try
                let token = CallbackToken.generate ()
                let binding: ToolBinding = { Token = token; ToolName = toolName "approve"; Arg = None }

                let store = FileBindingStore.openAt path :> IBindingStore
                (store.Save([ binding ], CancellationToken.None)).GetAwaiter().GetResult()
                (store.Remove([ token ], CancellationToken.None)).GetAwaiter().GetResult()

                let reopened = FileBindingStore.openAt path :> IBindingStore
                let result = (reopened.TryGet(token, CancellationToken.None)).GetAwaiter().GetResult()

                Expect.equal result ValueNone "a removed token stays gone after a reopen — the removal itself was persisted"
            finally
                File.Delete path

        testCase "Save registers multiple bindings as a unit, all persisted" <| fun _ ->
            let path = tempPath ()

            try
                let tokenA, tokenB = CallbackToken.generate (), CallbackToken.generate ()
                let bindingA: ToolBinding = { Token = tokenA; ToolName = toolName "approve"; Arg = None }
                let bindingB: ToolBinding = { Token = tokenB; ToolName = toolName "reject"; Arg = Some "x" }

                let store = FileBindingStore.openAt path :> IBindingStore
                (store.Save([ bindingA; bindingB ], CancellationToken.None)).GetAwaiter().GetResult()

                let reopened = FileBindingStore.openAt path :> IBindingStore

                Expect.equal
                    ((reopened.TryGet(tokenA, CancellationToken.None)).GetAwaiter().GetResult())
                    (ValueSome bindingA)
                    "the first binding of the batch persisted"

                Expect.equal
                    ((reopened.TryGet(tokenB, CancellationToken.None)).GetAwaiter().GetResult())
                    (ValueSome bindingB)
                    "the second binding of the batch persisted"
            finally
                File.Delete path
    ]
