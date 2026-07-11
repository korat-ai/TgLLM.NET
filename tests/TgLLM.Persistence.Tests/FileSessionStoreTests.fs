/// Tests for `FileSessionStore`, the durable, JSON-on-disk `ISessionStore`: covers the plain
/// `ISessionStore` contract (Save → TryGet, overwrite, Remove, EvictIdle) PLUS the restart
/// guarantee itself — re-opening the SAME file in a brand-new instance still resolves a session
/// record saved by a previous instance — and best-effort loading of a corrupt file, mirroring
/// `FileBindingStoreTests.fs`/`FileBindingStoreEvictionTests.fs` for the session-store seam.
module TgLLM.Persistence.Tests.FileSessionStoreTests

open System
open System.IO
open System.Threading
open Expecto
open FSharp.UMX
open TgLLM.Core
open TgLLM.Persistence

/// A fresh temp file path (not yet created) — `FileSessionStore.OpenAt` must tolerate a missing file.
let private tempPath () : string =
    Path.Combine(Path.GetTempPath(), $"tgllm-session-store-tests-{Guid.NewGuid()}.json")

[<Tests>]
let fileSessionStoreTests =
    testList "FileSessionStore" [

        testCase "OpenAt on a path with no existing file starts empty" <| fun _ ->
            let path = tempPath ()

            try
                let store = FileSessionStore.OpenAt path :> ISessionStore
                let chat = UMX.tag<chatId> 1L

                let result = (store.TryGet(chat, CancellationToken.None)).GetAwaiter().GetResult()

                Expect.equal result ValueNone "no file yet ⇒ nothing resolves"
            finally
                File.Delete path

        testCase "Save then TryGet round-trips the exact record (same instance)" <| fun _ ->
            let path = tempPath ()

            try
                let store = FileSessionStore.OpenAt path :> ISessionStore
                let chat = UMX.tag<chatId> 1L

                let record: SessionRecord =
                    { Payload = [| 1uy; 2uy; 3uy |]
                      LastActivityAt = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) }

                (store.Save(chat, record, CancellationToken.None)).GetAwaiter().GetResult()
                let result = (store.TryGet(chat, CancellationToken.None)).GetAwaiter().GetResult()

                Expect.equal result (ValueSome record) "the exact saved record round-trips"
            finally
                File.Delete path

        testCase "a second Save for the same chat overwrites the first (one record per chat)" <| fun _ ->
            let path = tempPath ()

            try
                let store = FileSessionStore.OpenAt path :> ISessionStore
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
            finally
                File.Delete path

        testCase "TryGet on an unknown chat returns ValueNone" <| fun _ ->
            let path = tempPath ()

            try
                let store = FileSessionStore.OpenAt path :> ISessionStore
                let known = UMX.tag<chatId> 1L
                let unknown = UMX.tag<chatId> 2L

                let record: SessionRecord =
                    { Payload = [| 9uy |]
                      LastActivityAt = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) }

                (store.Save(known, record, CancellationToken.None)).GetAwaiter().GetResult()
                let result = (store.TryGet(unknown, CancellationToken.None)).GetAwaiter().GetResult()

                Expect.equal result ValueNone "an unsaved chat resolves to nothing"
            finally
                File.Delete path

        testCase "Remove clears a chat's record, even after a reopen" <| fun _ ->
            let path = tempPath ()

            try
                let chat = UMX.tag<chatId> 1L

                let record: SessionRecord =
                    { Payload = [| 9uy |]
                      LastActivityAt = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) }

                let store = FileSessionStore.OpenAt path :> ISessionStore
                (store.Save(chat, record, CancellationToken.None)).GetAwaiter().GetResult()
                (store.Remove(chat, CancellationToken.None)).GetAwaiter().GetResult()

                let reopened = FileSessionStore.OpenAt path :> ISessionStore
                let result = (reopened.TryGet(chat, CancellationToken.None)).GetAwaiter().GetResult()

                Expect.equal result ValueNone "a removed chat's session stays gone after a reopen — the removal itself was persisted"
            finally
                File.Delete path

        testCase "re-opening the SAME file in a NEW instance still resolves a previously saved record" <| fun _ ->
            let path = tempPath ()

            try
                let chat = UMX.tag<chatId> 1L

                let record: SessionRecord =
                    { Payload = [| 7uy; 8uy |]
                      LastActivityAt = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) }

                let firstInstance = FileSessionStore.OpenAt path :> ISessionStore
                (firstInstance.Save(chat, record, CancellationToken.None)).GetAwaiter().GetResult()

                // Simulate a restart: nothing but the file on disk connects the two instances.
                let secondInstance = FileSessionStore.OpenAt path :> ISessionStore
                let result = (secondInstance.TryGet(chat, CancellationToken.None)).GetAwaiter().GetResult()

                Expect.equal result (ValueSome record) "a fresh instance over the same file restores the record after a restart"
            finally
                File.Delete path

        testCase "EvictIdle removes records at or before the cutoff, keeps the rest, returns the exact count, and persists the removal" <| fun _ ->
            let path = tempPath ()

            try
                let store = FileSessionStore.OpenAt path :> ISessionStore
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

                // The removal itself is persisted — a fresh instance over the SAME file must not
                // resurrect the evicted records.
                let reopened = FileSessionStore.OpenAt path :> ISessionStore

                Expect.equal
                    ((reopened.TryGet(idleChat, CancellationToken.None)).GetAwaiter().GetResult())
                    ValueNone
                    "the eviction was persisted — a reopened store does not resurrect the idle record"
            finally
                File.Delete path

        testCase "EvictIdle on a store with nothing idle removes nothing and returns 0" <| fun _ ->
            let path = tempPath ()

            try
                let store = FileSessionStore.OpenAt path :> ISessionStore
                let chat = UMX.tag<chatId> 1L
                let record: SessionRecord = { Payload = [| 1uy |]; LastActivityAt = DateTimeOffset.UtcNow }

                (store.Save(chat, record, CancellationToken.None)).GetAwaiter().GetResult()

                let removed = (store.EvictIdle(DateTimeOffset.UtcNow.AddHours -1.0)).GetAwaiter().GetResult()

                Expect.equal removed 0 "nothing was idle as of a cutoff before the record's last activity"

                Expect.equal
                    ((store.TryGet(chat, CancellationToken.None)).GetAwaiter().GetResult())
                    (ValueSome record)
                    "the still-live record survives"
            finally
                File.Delete path

        testCase "OpenAt on a file with truncated/garbage JSON does not throw and starts empty" <| fun _ ->
            let path = tempPath ()

            try
                File.WriteAllText(path, "{ this is not valid json at all ]")
                let store = FileSessionStore.OpenAt path :> ISessionStore
                let chat = UMX.tag<chatId> 1L

                let result = (store.TryGet(chat, CancellationToken.None)).GetAwaiter().GetResult()

                Expect.equal result ValueNone "a corrupt file on disk must not crash OpenAt — best-effort empty store"
            finally
                File.Delete path

        testCase "OpenAt on a file containing the JSON literal null does not throw and starts empty" <| fun _ ->
            let path = tempPath ()

            try
                File.WriteAllText(path, "null")
                let store = FileSessionStore.OpenAt path :> ISessionStore
                let chat = UMX.tag<chatId> 1L

                let result = (store.TryGet(chat, CancellationToken.None)).GetAwaiter().GetResult()

                Expect.equal result ValueNone "a JSON `null` payload must not crash OpenAt — best-effort empty store"
            finally
                File.Delete path

        testCase "OpenAt on a file with a row whose PayloadBase64 is corrupt skips that row and starts empty" <| fun _ ->
            let path = tempPath ()

            try
                File.WriteAllText(
                    path,
                    """[{"ChatId":1,"PayloadBase64":"not-valid-base64!!","LastActivityAt":"2026-01-01T00:00:00+00:00"}]"""
                )

                let store = FileSessionStore.OpenAt path :> ISessionStore
                let chat = UMX.tag<chatId> 1L

                let result = (store.TryGet(chat, CancellationToken.None)).GetAwaiter().GetResult()

                Expect.equal result ValueNone "a row with unparsable Base64 is skipped, best-effort, rather than crashing OpenAt"
            finally
                File.Delete path

        testCase "OpenAt on a file with a row whose PayloadBase64 is JSON null skips that row and still loads a good row" <| fun _ ->
            let path = tempPath ()

            try
                File.WriteAllText(
                    path,
                    """[{"ChatId":1,"PayloadBase64":null,"LastActivityAt":"2026-01-01T00:00:00+00:00"},{"ChatId":2,"PayloadBase64":"AQID","LastActivityAt":"2026-01-01T00:00:00+00:00"}]"""
                )

                let store = FileSessionStore.OpenAt path :> ISessionStore
                let nullPayloadChat = UMX.tag<chatId> 1L
                let goodChat = UMX.tag<chatId> 2L

                let nullPayloadResult = (store.TryGet(nullPayloadChat, CancellationToken.None)).GetAwaiter().GetResult()
                let goodResult = (store.TryGet(goodChat, CancellationToken.None)).GetAwaiter().GetResult()

                Expect.equal
                    nullPayloadResult
                    ValueNone
                    "a row with a JSON `null` PayloadBase64 is skipped, best-effort, rather than crashing OpenAt"

                let expectedGoodRecord: SessionRecord =
                    { Payload = [| 1uy; 2uy; 3uy |]
                      LastActivityAt = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) }

                Expect.equal goodResult (ValueSome expectedGoodRecord) "the good row still loads despite the bad row alongside it"
            finally
                File.Delete path
    ]
