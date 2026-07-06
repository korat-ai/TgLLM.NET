/// Tests for `IBindingStore.EvictExpired` (research D7, folds review finding #9 — nothing was ever
/// evicted anywhere) on `InMemoryBindingStore`: removes bindings whose `ExpiresAt` is `Some` and
/// strictly at-or-before `now` (matching `Expiry.isLive`'s own boundary — the exact expiry instant
/// already counts as expired), keeps everything else, and returns the exact count removed.
module TgLLM.Core.Tests.BindingStoreEvictionTests

open System
open System.Threading
open Expecto
open TgLLM.Core

let private toolName (s: string) : ToolName =
    match ToolName.create s with
    | Ok n -> n
    | Error e -> failwithf "test setup: unreachable %A" e

[<Tests>]
let bindingStoreEvictionTests =
    testList "IBindingStore.EvictExpired" [

        testCase "EvictExpired on an empty store removes nothing and returns 0" <| fun _ ->
            let store = InMemoryBindingStore() :> IBindingStore

            let removed = (store.EvictExpired DateTimeOffset.UtcNow).GetAwaiter().GetResult()

            Expect.equal removed 0 "nothing to evict"

        testCase "a binding with no ExpiresAt (never expires) survives EvictExpired" <| fun _ ->
            let store = InMemoryBindingStore() :> IBindingStore
            let token = CallbackToken.generate ()
            let binding = ToolBinding.create token (toolName "keep") None
            (store.Save([ binding ], CancellationToken.None)).GetAwaiter().GetResult()

            let removed = (store.EvictExpired DateTimeOffset.UtcNow).GetAwaiter().GetResult()

            Expect.equal removed 0 "a binding with no expiry is never evicted"

            Expect.equal
                ((store.TryGet(token, CancellationToken.None)).GetAwaiter().GetResult())
                (ValueSome binding)
                "the live (never-expiring) binding is still resolvable"

        testCase "a binding whose ExpiresAt is in the future survives EvictExpired" <| fun _ ->
            let store = InMemoryBindingStore() :> IBindingStore
            let token = CallbackToken.generate ()
            let now = DateTimeOffset.UtcNow
            let binding = { ToolBinding.create token (toolName "keep") None with ExpiresAt = Some(now.AddMinutes 5.0) }
            (store.Save([ binding ], CancellationToken.None)).GetAwaiter().GetResult()

            let removed = (store.EvictExpired now).GetAwaiter().GetResult()

            Expect.equal removed 0 "a still-live binding is not evicted"

            Expect.equal
                ((store.TryGet(token, CancellationToken.None)).GetAwaiter().GetResult())
                (ValueSome binding)
                "the future-expiring binding is still resolvable"

        testCase "a binding whose ExpiresAt is in the past is removed by EvictExpired, and counted" <| fun _ ->
            let store = InMemoryBindingStore() :> IBindingStore
            let token = CallbackToken.generate ()
            let now = DateTimeOffset.UtcNow
            let binding = { ToolBinding.create token (toolName "expired") None with ExpiresAt = Some(now.AddMinutes -5.0) }
            (store.Save([ binding ], CancellationToken.None)).GetAwaiter().GetResult()

            let removed = (store.EvictExpired now).GetAwaiter().GetResult()

            Expect.equal removed 1 "exactly one expired binding was evicted"

            Expect.equal
                ((store.TryGet(token, CancellationToken.None)).GetAwaiter().GetResult())
                ValueNone
                "the expired binding no longer resolves"

        testCase "a binding whose ExpiresAt is EXACTLY now is evicted (boundary counts as expired, matching Expiry.isLive)" <| fun _ ->
            let store = InMemoryBindingStore() :> IBindingStore
            let token = CallbackToken.generate ()
            let now = DateTimeOffset.UtcNow
            let binding = { ToolBinding.create token (toolName "boundary") None with ExpiresAt = Some now }
            (store.Save([ binding ], CancellationToken.None)).GetAwaiter().GetResult()

            let removed = (store.EvictExpired now).GetAwaiter().GetResult()

            Expect.equal removed 1 "the exact expiry instant itself already counts as expired"

        testCase "EvictExpired removes only expired bindings, keeps live ones, and returns the exact count" <| fun _ ->
            let store = InMemoryBindingStore() :> IBindingStore
            let now = DateTimeOffset.UtcNow
            let liveToken = CallbackToken.generate ()
            let neverToken = CallbackToken.generate ()
            let expiredTokenA = CallbackToken.generate ()
            let expiredTokenB = CallbackToken.generate ()

            let liveBinding = { ToolBinding.create liveToken (toolName "live") None with ExpiresAt = Some(now.AddHours 1.0) }
            let neverBinding = ToolBinding.create neverToken (toolName "never") None
            let expiredA = { ToolBinding.create expiredTokenA (toolName "expiredA") None with ExpiresAt = Some(now.AddHours -1.0) }
            let expiredB = { ToolBinding.create expiredTokenB (toolName "expiredB") None with ExpiresAt = Some(now.AddMinutes -1.0) }

            (store.Save([ liveBinding; neverBinding; expiredA; expiredB ], CancellationToken.None))
                .GetAwaiter()
                .GetResult()

            let removed = (store.EvictExpired now).GetAwaiter().GetResult()

            Expect.equal removed 2 "exactly the two expired bindings were evicted"

            Expect.equal
                ((store.TryGet(liveToken, CancellationToken.None)).GetAwaiter().GetResult())
                (ValueSome liveBinding)
                "the future-expiring binding survives"

            Expect.equal
                ((store.TryGet(neverToken, CancellationToken.None)).GetAwaiter().GetResult())
                (ValueSome neverBinding)
                "the never-expiring binding survives"

            Expect.equal
                ((store.TryGet(expiredTokenA, CancellationToken.None)).GetAwaiter().GetResult())
                ValueNone
                "expired binding A is gone"

            Expect.equal
                ((store.TryGet(expiredTokenB, CancellationToken.None)).GetAwaiter().GetResult())
                ValueNone
                "expired binding B is gone"
    ]
