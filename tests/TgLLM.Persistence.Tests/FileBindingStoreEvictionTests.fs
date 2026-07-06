/// Tests for `FileBindingStore.EvictExpired` (US4, research D5/D7 — the eviction seam extended to
/// the durable file store): sweeps the in-memory index for bindings whose `ExpiresAt` has passed,
/// removes them, persists the result, and returns the count. Same in-process contract as
/// `InMemoryBindingStore.EvictExpired` (`BindingStoreEvictionTests.fs` in `TgLLM.Core.Tests`) —
/// `Expiry.isLive`'s boundary semantics apply identically.
module TgLLM.Persistence.Tests.FileBindingStoreEvictionTests

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

let private tempPath () : string =
    Path.Combine(Path.GetTempPath(), $"tgllm-tool-router-eviction-tests-{Guid.NewGuid()}.json")

[<Tests>]
let fileBindingStoreEvictionTests =
    testList "FileBindingStore.EvictExpired" [

        testCase "EvictExpired removes an expired binding, keeps a live one, returns the exact count, and persists the removal" <| fun _ ->
            let path = tempPath ()

            try
                let store = FileBindingStore.openAt path :> IBindingStore
                let now = DateTimeOffset.UtcNow
                let liveToken = CallbackToken.generate ()
                let expiredToken = CallbackToken.generate ()

                let liveBinding = { ToolBinding.create liveToken (toolName "live") None with ExpiresAt = Some(now.AddHours 1.0) }
                let expiredBinding = { ToolBinding.create expiredToken (toolName "expired") None with ExpiresAt = Some(now.AddMinutes -5.0) }

                (store.Save([ liveBinding; expiredBinding ], CancellationToken.None)).GetAwaiter().GetResult()

                let removed = (store.EvictExpired now).GetAwaiter().GetResult()

                Expect.equal removed 1 "exactly the one expired binding was evicted"

                Expect.equal
                    ((store.TryGet(liveToken, CancellationToken.None)).GetAwaiter().GetResult())
                    (ValueSome liveBinding)
                    "the live binding survives"

                Expect.equal
                    ((store.TryGet(expiredToken, CancellationToken.None)).GetAwaiter().GetResult())
                    ValueNone
                    "the expired binding no longer resolves"

                // The removal itself is persisted — a fresh instance over the SAME file must not
                // resurrect the evicted binding.
                let reopened = FileBindingStore.openAt path :> IBindingStore

                Expect.equal
                    ((reopened.TryGet(expiredToken, CancellationToken.None)).GetAwaiter().GetResult())
                    ValueNone
                    "the eviction was persisted — a reopened store does not resurrect the expired binding"
            finally
                File.Delete path

        testCase "EvictExpired on a store with nothing expired removes nothing and returns 0" <| fun _ ->
            let path = tempPath ()

            try
                let store = FileBindingStore.openAt path :> IBindingStore
                let token = CallbackToken.generate ()
                let binding = ToolBinding.create token (toolName "never") None
                (store.Save([ binding ], CancellationToken.None)).GetAwaiter().GetResult()

                let removed = (store.EvictExpired DateTimeOffset.UtcNow).GetAwaiter().GetResult()

                Expect.equal removed 0 "nothing was expired"

                Expect.equal
                    ((store.TryGet(token, CancellationToken.None)).GetAwaiter().GetResult())
                    (ValueSome binding)
                    "the never-expiring binding survives"
            finally
                File.Delete path
    ]
