/// Tests for `InMemorySessionStore`, the `ISessionStore` implementation covering durable, opaque
/// per-chat agent session state.
module TgLLM.Core.Tests.InMemorySessionStoreTests

open System
open System.Threading
open FSharp.UMX
open Expecto
open TgLLM.Core

[<Tests>]
let inMemorySessionStoreTests =
    testList "InMemorySessionStore" [

        testCase "TryGet on an unknown chat returns ValueNone" <| fun _ ->
            let store = InMemorySessionStore() :> ISessionStore
            let chat = UMX.tag<chatId> 1L

            let result = (store.TryGet(chat, CancellationToken.None)).GetAwaiter().GetResult()

            Expect.equal result ValueNone "an unsaved chat resolves to nothing"

        testCase "Save then TryGet round-trips the exact record" <| fun _ ->
            let store = InMemorySessionStore() :> ISessionStore
            let chat = UMX.tag<chatId> 1L

            let record: SessionRecord =
                { Payload = [| 1uy; 2uy; 3uy |]
                  LastActivityAt = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) }

            (store.Save(chat, record, CancellationToken.None)).GetAwaiter().GetResult()
            let result = (store.TryGet(chat, CancellationToken.None)).GetAwaiter().GetResult()

            Expect.equal result (ValueSome record) "the exact saved record round-trips"

        testCase "a second Save for the same chat overwrites the first (one record per chat)" <| fun _ ->
            let store = InMemorySessionStore() :> ISessionStore
            let chat = UMX.tag<chatId> 1L

            let first: SessionRecord =
                { Payload = [| 1uy |]
                  LastActivityAt = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) }

            let second: SessionRecord =
                { Payload = [| 2uy; 2uy |]
                  LastActivityAt = DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero) }

            (store.Save(chat, first, CancellationToken.None)).GetAwaiter().GetResult()
            (store.Save(chat, second, CancellationToken.None)).GetAwaiter().GetResult()
            let result = (store.TryGet(chat, CancellationToken.None)).GetAwaiter().GetResult()

            Expect.equal result (ValueSome second) "TryGet returns the latest saved record, not the first"

        testCase "Remove clears a chat's record so a subsequent TryGet returns ValueNone" <| fun _ ->
            let store = InMemorySessionStore() :> ISessionStore
            let chat = UMX.tag<chatId> 1L

            let record: SessionRecord =
                { Payload = [| 9uy |]
                  LastActivityAt = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) }

            (store.Save(chat, record, CancellationToken.None)).GetAwaiter().GetResult()
            (store.Remove(chat, CancellationToken.None)).GetAwaiter().GetResult()
            let result = (store.TryGet(chat, CancellationToken.None)).GetAwaiter().GetResult()

            Expect.equal result ValueNone "a removed chat's session no longer resolves"

        testCase "EvictIdle removes records at or before the cutoff, keeps the rest, and returns the exact count" <| fun _ ->
            let store = InMemorySessionStore() :> ISessionStore
            let cutoff = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)

            let idleChat = UMX.tag<chatId> 1L
            let boundaryChat = UMX.tag<chatId> 2L
            let liveChat = UMX.tag<chatId> 3L

            let idleRecord: SessionRecord =
                { Payload = [| 1uy |]
                  LastActivityAt = cutoff.AddHours -1.0 }

            let boundaryRecord: SessionRecord = { Payload = [| 2uy |]; LastActivityAt = cutoff }

            let liveRecord: SessionRecord =
                { Payload = [| 3uy |]
                  LastActivityAt = cutoff.AddHours 1.0 }

            (store.Save(idleChat, idleRecord, CancellationToken.None)).GetAwaiter().GetResult()
            (store.Save(boundaryChat, boundaryRecord, CancellationToken.None)).GetAwaiter().GetResult()
            (store.Save(liveChat, liveRecord, CancellationToken.None)).GetAwaiter().GetResult()

            let removed = (store.EvictIdle cutoff).GetAwaiter().GetResult()

            Expect.equal removed 2 "the idle record and the boundary record were both evicted"

            Expect.equal
                ((store.TryGet(idleChat, CancellationToken.None)).GetAwaiter().GetResult())
                ValueNone
                "the idle record is gone"

            Expect.equal
                ((store.TryGet(boundaryChat, CancellationToken.None)).GetAwaiter().GetResult())
                ValueNone
                "the boundary instant itself already counts as idle"

            Expect.equal
                ((store.TryGet(liveChat, CancellationToken.None)).GetAwaiter().GetResult())
                (ValueSome liveRecord)
                "the still-active record survives"
    ]
