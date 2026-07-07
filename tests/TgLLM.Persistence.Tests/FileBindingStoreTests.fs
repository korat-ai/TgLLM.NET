/// Tests for `FileBindingStore`, covering the tool binding contract. Covers the plain
/// `IBindingStore` contract (Save → TryGet, Remove) PLUS the restart guarantee itself: re-opening
/// the SAME file in a brand-new instance still resolves bindings saved by a previous instance (at
/// the unit level — `RestartPersistenceTests.fs` proves it end-to-end through the real bot).
module TgLLM.Persistence.Tests.FileBindingStoreTests

open System
open System.IO
open System.Threading
open Expecto
open FSharp.UMX
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
                let binding: ToolBinding = ToolBinding.create token (toolName "approve") (Some "42")

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
                let binding: ToolBinding = ToolBinding.create token (toolName "reject") None

                (store.Save([ binding ], CancellationToken.None)).GetAwaiter().GetResult()
                let result = (store.TryGet(token, CancellationToken.None)).GetAwaiter().GetResult()

                Expect.equal result (ValueSome binding) "an argument-less binding round-trips"
            finally
                File.Delete path

        testCase "re-opening the SAME file in a NEW instance still resolves a previously saved binding" <| fun _ ->
            let path = tempPath ()

            try
                let token = CallbackToken.generate ()
                let binding: ToolBinding = ToolBinding.create token (toolName "approve") (Some "7")

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
                let binding: ToolBinding = ToolBinding.create token (toolName "approve") None

                let store = FileBindingStore.openAt path :> IBindingStore
                (store.Save([ binding ], CancellationToken.None)).GetAwaiter().GetResult()
                (store.Remove([ token ], CancellationToken.None)).GetAwaiter().GetResult()

                let reopened = FileBindingStore.openAt path :> IBindingStore
                let result = (reopened.TryGet(token, CancellationToken.None)).GetAwaiter().GetResult()

                Expect.equal result ValueNone "a removed token stays gone after a reopen — the removal itself was persisted"
            finally
                File.Delete path

        testCase "openAt on a file with truncated/garbage JSON does not throw and starts empty" <| fun _ ->
            let path = tempPath ()

            try
                File.WriteAllText(path, "{ this is not valid json at all ]")
                let store = FileBindingStore.openAt path :> IBindingStore
                let token = CallbackToken.generate ()

                let result = (store.TryGet(token, CancellationToken.None)).GetAwaiter().GetResult()

                Expect.equal result ValueNone "a corrupt file on disk must not crash openAt — best-effort empty store"
            finally
                File.Delete path

        testCase "openAt on a file containing the JSON literal null does not throw and starts empty" <| fun _ ->
            let path = tempPath ()

            try
                File.WriteAllText(path, "null")
                let store = FileBindingStore.openAt path :> IBindingStore
                let token = CallbackToken.generate ()

                let result = (store.TryGet(token, CancellationToken.None)).GetAwaiter().GetResult()

                Expect.equal result ValueNone "a JSON `null` payload must not crash openAt — best-effort empty store"
            finally
                File.Delete path

        testCase "Save registers multiple bindings as a unit, all persisted" <| fun _ ->
            let path = tempPath ()

            try
                let tokenA, tokenB = CallbackToken.generate (), CallbackToken.generate ()
                let bindingA: ToolBinding = ToolBinding.create tokenA (toolName "approve") None
                let bindingB: ToolBinding = ToolBinding.create tokenB (toolName "reject") (Some "x")

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

        testCase "a saved owner-scoped (User) binding reloads with its Owner intact after a reopen (restart)" <| fun _ ->
            let path = tempPath ()

            try
                let token = CallbackToken.generate ()
                let owner = User(UMX.tag<userId> 777L)
                let binding = { ToolBinding.create token (toolName "approve") None with Owner = owner }

                let firstInstance = FileBindingStore.openAt path :> IBindingStore
                (firstInstance.Save([ binding ], CancellationToken.None)).GetAwaiter().GetResult()

                // Simulate a restart: nothing but the file on disk connects the two instances.
                let secondInstance = FileBindingStore.openAt path :> IBindingStore
                let result = (secondInstance.TryGet(token, CancellationToken.None)).GetAwaiter().GetResult()

                Expect.equal
                    result
                    (ValueSome binding)
                    "the owner scope (not just the rest of the binding) survives a restart through the file store"
            finally
                File.Delete path

        testCase "a binding with no owner override still reloads as Anyone after a reopen (backward compat)" <| fun _ ->
            let path = tempPath ()

            try
                let token = CallbackToken.generate ()
                let binding = ToolBinding.create token (toolName "approve") None

                let firstInstance = FileBindingStore.openAt path :> IBindingStore
                (firstInstance.Save([ binding ], CancellationToken.None)).GetAwaiter().GetResult()

                let secondInstance = FileBindingStore.openAt path :> IBindingStore
                let result = (secondInstance.TryGet(token, CancellationToken.None)).GetAwaiter().GetResult()

                match result with
                | ValueSome reloaded -> Expect.equal reloaded.Owner Anyone "an unscoped binding still reloads as Anyone"
                | ValueNone -> failwith "expected the binding to reload"
            finally
                File.Delete path

        testCase "a saved custom DeniedNotice reloads intact after a reopen (restart)" <| fun _ ->
            let path = tempPath ()

            try
                let token = CallbackToken.generate ()

                let binding =
                    { ToolBinding.create token (toolName "approve") None with
                        Owner = User(UMX.tag<userId> 42L)
                        DeniedNotice = Some "Ask Alice instead." }

                let firstInstance = FileBindingStore.openAt path :> IBindingStore
                (firstInstance.Save([ binding ], CancellationToken.None)).GetAwaiter().GetResult()

                let secondInstance = FileBindingStore.openAt path :> IBindingStore
                let result = (secondInstance.TryGet(token, CancellationToken.None)).GetAwaiter().GetResult()

                Expect.equal result (ValueSome binding) "the custom notice override survives a restart alongside the owner scope"
            finally
                File.Delete path

        testCase "a saved expiry and single-use flag reload intact after a reopen (restart) — finishes the on-disk shape US4 left incomplete" <| fun _ ->
            let path = tempPath ()

            try
                let token = CallbackToken.generate ()
                let expiresAt = DateTimeOffset.UtcNow.AddHours 1.0

                let binding =
                    { ToolBinding.create token (toolName "confirm") None with
                        ExpiresAt = Some expiresAt
                        SingleUse = true }

                let firstInstance = FileBindingStore.openAt path :> IBindingStore
                (firstInstance.Save([ binding ], CancellationToken.None)).GetAwaiter().GetResult()

                // Simulate a restart: nothing but the file on disk connects the two instances.
                let secondInstance = FileBindingStore.openAt path :> IBindingStore
                let result = (secondInstance.TryGet(token, CancellationToken.None)).GetAwaiter().GetResult()

                Expect.equal
                    result
                    (ValueSome binding)
                    "the expiry instant and single-use flag survive a restart through the file store, not just for the CURRENT process"
            finally
                File.Delete path

        testCase "a binding with no expiry and SingleUse = false still reloads with those exact defaults after a reopen" <| fun _ ->
            let path = tempPath ()

            try
                let token = CallbackToken.generate ()
                let binding = ToolBinding.create token (toolName "approve") None

                let firstInstance = FileBindingStore.openAt path :> IBindingStore
                (firstInstance.Save([ binding ], CancellationToken.None)).GetAwaiter().GetResult()

                let secondInstance = FileBindingStore.openAt path :> IBindingStore
                let result = (secondInstance.TryGet(token, CancellationToken.None)).GetAwaiter().GetResult()

                match result with
                | ValueSome reloaded ->
                    Expect.equal reloaded.ExpiresAt None "a never-expiring binding still reloads with no expiry"
                    Expect.isFalse reloaded.SingleUse "a non-single-use binding still reloads as such"
                | ValueNone -> failwith "expected the binding to reload"
            finally
                File.Delete path
    ]
