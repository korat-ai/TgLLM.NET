/// Tests for `LiteDbSessionStore`, the embedded-LiteDB `ISessionStore`: proves the durable
/// session-store seam generalizes beyond the file store — Save/TryGet/Remove/EvictIdle round-trip
/// the exact `SessionRecord` (raw `Payload` bytes plus `LastActivityAt`), restart persistence holds
/// (a fresh instance over the SAME file resolves what a previous instance saved, once the previous
/// instance is disposed), and the store lives in its own collection/datafile, separate from any
/// `LiteDbBindingStore`. Mirrors `LiteDbBindingStoreTests.fs` for the session-store seam.
module TgLLM.Persistence.LiteDb.Tests.LiteDbSessionStoreTests

open System
open System.IO
open System.Threading
open Expecto
open FSharp.UMX
open TgLLM.Core
open TgLLM.Persistence.LiteDb

/// A fresh temp file path (not yet created) — `LiteDbSessionStore.OpenAt` must tolerate a missing
/// file (LiteDB itself creates the datafile on first use).
let private tempPath () : string =
    Path.Combine(Path.GetTempPath(), $"tgllm-session-store-litedb-tests-{Guid.NewGuid()}.db")

/// BSON's native date type is MILLISECOND-precision (confirmed empirically for the binding store's
/// own `ExpiresAtUtc`, `LiteDbBindingStoreTests.fs`) — `LastActivityAt` round-trips through the same
/// `LiteDatabase`, so an exact structural-equality comparison after a round trip needs the same
/// truncation; `ISessionStore.EvictIdle`'s own comparison only ever needs ordering, never
/// sub-millisecond precision, so this loses nothing the on-disk format's actual contract cares about.
let private truncateToMilliseconds (instant: DateTimeOffset) : DateTimeOffset =
    instant.AddTicks(-(instant.Ticks % TimeSpan.TicksPerMillisecond))

[<Tests>]
let liteDbSessionStoreTests =
    testList "LiteDbSessionStore" [

        testCase "Save then TryGet round-trips the exact record" <| fun _ ->
            let path = tempPath ()

            try
                use store = LiteDbSessionStore.OpenAt path
                let chat = UMX.tag<chatId> 1L

                let record: SessionRecord =
                    { Payload = [| 1uy; 2uy; 3uy |]
                      LastActivityAt = truncateToMilliseconds DateTimeOffset.UtcNow }

                ((store :> ISessionStore).Save(chat, record, CancellationToken.None)).GetAwaiter().GetResult()
                let result = ((store :> ISessionStore).TryGet(chat, CancellationToken.None)).GetAwaiter().GetResult()

                Expect.equal result (ValueSome record) "the exact record — payload and last-activity instant — round-trips through LiteDB"
            finally
                File.Delete path

        testCase "a second Save for the same chat overwrites the first (one record per chat)" <| fun _ ->
            let path = tempPath ()

            try
                use store = LiteDbSessionStore.OpenAt path
                let chat = UMX.tag<chatId> 1L

                let first: SessionRecord =
                    { Payload = [| 1uy |]
                      LastActivityAt = truncateToMilliseconds (DateTimeOffset.UtcNow.AddHours -1.0) }

                let second: SessionRecord =
                    { Payload = [| 2uy; 2uy |]
                      LastActivityAt = truncateToMilliseconds DateTimeOffset.UtcNow }

                ((store :> ISessionStore).Save(chat, first, CancellationToken.None)).GetAwaiter().GetResult()
                ((store :> ISessionStore).Save(chat, second, CancellationToken.None)).GetAwaiter().GetResult()
                let result = ((store :> ISessionStore).TryGet(chat, CancellationToken.None)).GetAwaiter().GetResult()

                Expect.equal result (ValueSome second) "TryGet returns the latest saved record, not the first"
            finally
                File.Delete path

        testCase "TryGet on an unknown chat resolves to ValueNone" <| fun _ ->
            let path = tempPath ()

            try
                use store = LiteDbSessionStore.OpenAt path
                let chat = UMX.tag<chatId> 1L
                let result = ((store :> ISessionStore).TryGet(chat, CancellationToken.None)).GetAwaiter().GetResult()
                Expect.equal result ValueNone "no file/document yet ⇒ nothing resolves"
            finally
                File.Delete path

        testCase "Remove makes a previously saved chat resolve to ValueNone" <| fun _ ->
            let path = tempPath ()

            try
                use store = LiteDbSessionStore.OpenAt path
                let chat = UMX.tag<chatId> 1L

                let record: SessionRecord =
                    { Payload = [| 9uy |]
                      LastActivityAt = truncateToMilliseconds DateTimeOffset.UtcNow }

                ((store :> ISessionStore).Save(chat, record, CancellationToken.None)).GetAwaiter().GetResult()
                ((store :> ISessionStore).Remove(chat, CancellationToken.None)).GetAwaiter().GetResult()

                let result = ((store :> ISessionStore).TryGet(chat, CancellationToken.None)).GetAwaiter().GetResult()
                Expect.equal result ValueNone "a removed chat no longer resolves"
            finally
                File.Delete path

        testCase "re-opening the SAME file in a NEW instance still resolves a previously saved record (restart persistence)" <| fun _ ->
            let path = tempPath ()

            try
                let chat = UMX.tag<chatId> 1L

                let record: SessionRecord =
                    { Payload = [| 7uy; 8uy |]
                      LastActivityAt = truncateToMilliseconds DateTimeOffset.UtcNow }

                let firstInstance = LiteDbSessionStore.OpenAt path
                ((firstInstance :> ISessionStore).Save(chat, record, CancellationToken.None)).GetAwaiter().GetResult()
                (firstInstance :> IDisposable).Dispose() // release the file before reopening it

                use secondInstance = LiteDbSessionStore.OpenAt path
                let result = ((secondInstance :> ISessionStore).TryGet(chat, CancellationToken.None)).GetAwaiter().GetResult()

                Expect.equal result (ValueSome record) "a fresh instance over the same file restores the record after a restart"
            finally
                File.Delete path

        testCase "EvictIdle removes records at or before the cutoff, keeps the rest, returns the exact count, and persists the removal" <| fun _ ->
            let path = tempPath ()

            try
                let cutoff = truncateToMilliseconds DateTimeOffset.UtcNow

                let idleChat = UMX.tag<chatId> 1L
                let boundaryChat = UMX.tag<chatId> 2L
                let liveChat = UMX.tag<chatId> 3L

                let idleRecord: SessionRecord = { Payload = [| 1uy |]; LastActivityAt = cutoff.AddHours -1.0 }
                let boundaryRecord: SessionRecord = { Payload = [| 2uy |]; LastActivityAt = cutoff }
                let liveRecord: SessionRecord = { Payload = [| 3uy |]; LastActivityAt = cutoff.AddHours 1.0 }

                let store = LiteDbSessionStore.OpenAt path

                ((store :> ISessionStore).Save(idleChat, idleRecord, CancellationToken.None)).GetAwaiter().GetResult()
                ((store :> ISessionStore).Save(boundaryChat, boundaryRecord, CancellationToken.None)).GetAwaiter().GetResult()
                ((store :> ISessionStore).Save(liveChat, liveRecord, CancellationToken.None)).GetAwaiter().GetResult()

                let removed = ((store :> ISessionStore).EvictIdle cutoff).GetAwaiter().GetResult()

                Expect.equal removed 2 "the idle record and the boundary record were both evicted"

                Expect.equal
                    ((store :> ISessionStore).TryGet(idleChat, CancellationToken.None).GetAwaiter().GetResult())
                    ValueNone
                    "the idle record is gone"

                Expect.equal
                    ((store :> ISessionStore).TryGet(boundaryChat, CancellationToken.None).GetAwaiter().GetResult())
                    ValueNone
                    "the boundary instant itself already counts as idle"

                Expect.equal
                    ((store :> ISessionStore).TryGet(liveChat, CancellationToken.None).GetAwaiter().GetResult())
                    (ValueSome liveRecord)
                    "the still-active record survives"

                (store :> IDisposable).Dispose() // release the file before reopening it

                // The removal itself is persisted — a fresh instance over the SAME file must not
                // resurrect the evicted record.
                use reopened = LiteDbSessionStore.OpenAt path

                Expect.equal
                    ((reopened :> ISessionStore).TryGet(idleChat, CancellationToken.None).GetAwaiter().GetResult())
                    ValueNone
                    "the eviction was persisted — a reopened store does not resurrect the idle record"
            finally
                File.Delete path

        testCase "EvictIdle on a store with nothing idle removes nothing and returns 0" <| fun _ ->
            let path = tempPath ()

            try
                use store = LiteDbSessionStore.OpenAt path
                let chat = UMX.tag<chatId> 1L
                let record: SessionRecord = { Payload = [| 1uy |]; LastActivityAt = truncateToMilliseconds DateTimeOffset.UtcNow }

                ((store :> ISessionStore).Save(chat, record, CancellationToken.None)).GetAwaiter().GetResult()

                let removed =
                    ((store :> ISessionStore).EvictIdle(DateTimeOffset.UtcNow.AddHours -1.0))
                        .GetAwaiter()
                        .GetResult()

                Expect.equal removed 0 "nothing was idle as of a cutoff before the record's last activity"

                Expect.equal
                    ((store :> ISessionStore).TryGet(chat, CancellationToken.None).GetAwaiter().GetResult())
                    (ValueSome record)
                    "the still-live record survives"
            finally
                File.Delete path

        testCase "a session store's datafile is separate from a binding store's — both can be open over their own files at once" <| fun _ ->
            let sessionPath = tempPath ()
            let bindingPath = Path.Combine(Path.GetTempPath(), $"tgllm-tool-router-litedb-tests-{Guid.NewGuid()}.db")

            try
                use sessionStore = LiteDbSessionStore.OpenAt sessionPath
                use bindingStore = LiteDbBindingStore.OpenAt bindingPath

                let chat = UMX.tag<chatId> 1L
                let record: SessionRecord = { Payload = [| 5uy |]; LastActivityAt = truncateToMilliseconds DateTimeOffset.UtcNow }

                ((sessionStore :> ISessionStore).Save(chat, record, CancellationToken.None)).GetAwaiter().GetResult()

                let result = ((sessionStore :> ISessionStore).TryGet(chat, CancellationToken.None)).GetAwaiter().GetResult()

                Expect.equal result (ValueSome record) "the session store, over its own separate datafile, still round-trips normally"
            finally
                File.Delete sessionPath
                File.Delete bindingPath
    ]
