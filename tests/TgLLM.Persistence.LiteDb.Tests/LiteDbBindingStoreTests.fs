/// Tests for `LiteDbBindingStore`, the embedded-LiteDB `IBindingStore`: proves
/// the store seam generalizes beyond the file store — Save/TryGet/Remove/EvictExpired
/// round-trip the FULL evolved `ToolBinding` (owner/expiry/single-use), restart persistence holds
/// (a fresh instance over the SAME file resolves what a previous instance saved), and a
/// minimal, earlier-shaped document (one this store never wrote itself — missing owner/expiry/
/// single-use fields entirely) still loads with `ToolBinding.create`'s own defaults.
module TgLLM.Persistence.LiteDb.Tests.LiteDbBindingStoreTests

open System
open System.IO
open System.Threading
open Expecto
open FSharp.UMX
open LiteDB
open TgLLM.Core
open TgLLM.Persistence.LiteDb

let private toolName (s: string) : ToolName =
    match ToolName.create s with
    | Ok n -> n
    | Error e -> failwithf "test setup: unreachable %A" e

/// A fresh temp file path (not yet created) — `LiteDbBindingStore.OpenAt` must tolerate a missing
/// file (LiteDB itself creates the datafile on first use).
let private tempPath () : string =
    Path.Combine(Path.GetTempPath(), $"tgllm-tool-router-litedb-tests-{Guid.NewGuid()}.db")

/// BSON's native date type is MILLISECOND-precision (a documented BSON/MongoDB-family fact, and
/// confirmed empirically here against the installed LiteDB assembly): a raw `DateTimeOffset.UtcNow`
/// carries sub-millisecond ticks that the store can never round-trip. Every timestamp these tests
/// compare for EXACT (structural) equality after a round-trip is truncated through this first — not
/// working around a bug, but not over-claiming a precision the on-disk format never promised
/// either. `Expiry.isLive`'s own comparisons only ever need ordering, never sub-millisecond
/// precision, so this loses nothing the library's actual contract cares about.
let private truncateToMilliseconds (instant: DateTimeOffset) : DateTimeOffset =
    instant.AddTicks(-(instant.Ticks % TimeSpan.TicksPerMillisecond))

[<Tests>]
let liteDbBindingStoreTests =
    testList "LiteDbBindingStore" [

        testCase "Save then TryGet round-trips the exact binding, including owner/expiry/single-use" <| fun _ ->
            let path = tempPath ()

            try
                use store = LiteDbBindingStore.OpenAt path
                let token = CallbackToken.generate ()
                let expiresAt = truncateToMilliseconds (DateTimeOffset.UtcNow.AddHours 1.0)

                let binding =
                    { ToolBinding.create token (toolName "approve") (Some "42") with
                        Owner = User(UMX.tag<userId> 777L)
                        ExpiresAt = Some expiresAt
                        SingleUse = true
                        DeniedNotice = Some "Ask Alice instead." }

                ((store :> IBindingStore).Save([ binding ], CancellationToken.None)).GetAwaiter().GetResult()
                let result = ((store :> IBindingStore).TryGet(token, CancellationToken.None)).GetAwaiter().GetResult()

                Expect.equal result (ValueSome binding) "the exact binding — every field — round-trips through LiteDB"
            finally
                File.Delete path

        testCase "a binding with no arg, Anyone owner, no expiry, and SingleUse = false round-trips too" <| fun _ ->
            let path = tempPath ()

            try
                use store = LiteDbBindingStore.OpenAt path
                let token = CallbackToken.generate ()
                let binding = ToolBinding.create token (toolName "reject") None

                ((store :> IBindingStore).Save([ binding ], CancellationToken.None)).GetAwaiter().GetResult()
                let result = ((store :> IBindingStore).TryGet(token, CancellationToken.None)).GetAwaiter().GetResult()

                Expect.equal result (ValueSome binding) "a plain, default-shaped binding round-trips"
            finally
                File.Delete path

        testCase "TryGet on an unknown token resolves to ValueNone" <| fun _ ->
            let path = tempPath ()

            try
                use store = LiteDbBindingStore.OpenAt path
                let token = CallbackToken.generate ()
                let result = ((store :> IBindingStore).TryGet(token, CancellationToken.None)).GetAwaiter().GetResult()
                Expect.equal result ValueNone "no file/document yet ⇒ nothing resolves"
            finally
                File.Delete path

        testCase "Remove makes a previously saved token resolve to ValueNone" <| fun _ ->
            let path = tempPath ()

            try
                use store = LiteDbBindingStore.OpenAt path
                let token = CallbackToken.generate ()
                let binding = ToolBinding.create token (toolName "approve") None

                ((store :> IBindingStore).Save([ binding ], CancellationToken.None)).GetAwaiter().GetResult()
                ((store :> IBindingStore).Remove([ token ], CancellationToken.None)).GetAwaiter().GetResult()

                let result = ((store :> IBindingStore).TryGet(token, CancellationToken.None)).GetAwaiter().GetResult()
                Expect.equal result ValueNone "a removed token no longer resolves"
            finally
                File.Delete path

        testCase "re-opening the SAME file in a NEW instance still resolves a previously saved binding (restart persistence)" <| fun _ ->
            let path = tempPath ()

            try
                let token = CallbackToken.generate ()

                let binding =
                    { ToolBinding.create token (toolName "approve") (Some "7") with
                        Owner = User(UMX.tag<userId> 42L)
                        ExpiresAt = Some(truncateToMilliseconds (DateTimeOffset.UtcNow.AddDays 1.0)) }

                let firstInstance = LiteDbBindingStore.OpenAt path
                ((firstInstance :> IBindingStore).Save([ binding ], CancellationToken.None)).GetAwaiter().GetResult()
                (firstInstance :> IDisposable).Dispose() // release the file before reopening it

                use secondInstance = LiteDbBindingStore.OpenAt path
                let result = ((secondInstance :> IBindingStore).TryGet(token, CancellationToken.None)).GetAwaiter().GetResult()

                Expect.equal
                    result
                    (ValueSome binding)
                    "a fresh instance over the same file restores the binding — owner and expiry included — after a restart"
            finally
                File.Delete path

        testCase "EvictExpired removes an expired binding, keeps a live one, returns the exact count, and persists the removal" <| fun _ ->
            let path = tempPath ()

            try
                use store = LiteDbBindingStore.OpenAt path
                let now = DateTimeOffset.UtcNow

                let liveToken = CallbackToken.generate ()
                let expiredToken = CallbackToken.generate ()

                let liveBinding =
                    { ToolBinding.create liveToken (toolName "live") None with
                        ExpiresAt = Some(truncateToMilliseconds (now.AddHours 1.0)) }

                let expiredBinding =
                    { ToolBinding.create expiredToken (toolName "expired") None with
                        ExpiresAt = Some(truncateToMilliseconds (now.AddMinutes -5.0)) }

                ((store :> IBindingStore).Save([ liveBinding; expiredBinding ], CancellationToken.None))
                    .GetAwaiter()
                    .GetResult()

                let removed = ((store :> IBindingStore).EvictExpired now).GetAwaiter().GetResult()

                Expect.equal removed 1 "exactly the one expired binding was evicted"

                Expect.equal
                    ((store :> IBindingStore).TryGet(liveToken, CancellationToken.None).GetAwaiter().GetResult())
                    (ValueSome liveBinding)
                    "the live binding survives"

                Expect.equal
                    ((store :> IBindingStore).TryGet(expiredToken, CancellationToken.None).GetAwaiter().GetResult())
                    ValueNone
                    "the expired binding no longer resolves"
            finally
                File.Delete path

        testCase "a binding with no expiry (never expires) survives EvictExpired" <| fun _ ->
            let path = tempPath ()

            try
                use store = LiteDbBindingStore.OpenAt path
                let token = CallbackToken.generate ()
                let binding = ToolBinding.create token (toolName "never") None

                ((store :> IBindingStore).Save([ binding ], CancellationToken.None)).GetAwaiter().GetResult()
                let removed = ((store :> IBindingStore).EvictExpired DateTimeOffset.UtcNow).GetAwaiter().GetResult()

                Expect.equal removed 0 "a binding with no ExpiresAt is never evicted"

                Expect.equal
                    ((store :> IBindingStore).TryGet(token, CancellationToken.None).GetAwaiter().GetResult())
                    (ValueSome binding)
                    "the never-expiring binding still resolves"
            finally
                File.Delete path

        testCase "the eviction is persisted — a reopened store does not resurrect an evicted binding" <| fun _ ->
            let path = tempPath ()

            try
                let token = CallbackToken.generate ()
                let now = DateTimeOffset.UtcNow
                let binding = { ToolBinding.create token (toolName "expired") None with ExpiresAt = Some(now.AddMinutes -1.0) }

                let firstInstance = LiteDbBindingStore.OpenAt path
                ((firstInstance :> IBindingStore).Save([ binding ], CancellationToken.None)).GetAwaiter().GetResult()
                ((firstInstance :> IBindingStore).EvictExpired now).GetAwaiter().GetResult() |> ignore
                (firstInstance :> IDisposable).Dispose()

                use secondInstance = LiteDbBindingStore.OpenAt path
                let result = ((secondInstance :> IBindingStore).TryGet(token, CancellationToken.None)).GetAwaiter().GetResult()

                Expect.equal result ValueNone "the eviction was persisted — a reopened store does not resurrect the evicted binding"
            finally
                File.Delete path

        testCase "a minimal document (missing owner/expiry/single-use fields entirely — never written by this slice's own code) loads with ToolBinding.create's defaults" <| fun _ ->
            let path = tempPath ()

            try
                let token = CallbackToken.generate ()

                // Insert a document by hand, bypassing this store's own DTO entirely — simulates a
                // row from before the owner/expiry/single-use fields existed on the on-disk shape:
                // missing owner/expiry fields must still default to Anyone/none.
                do
                    use db = new LiteDatabase(path)
                    let collection: ILiteCollection<BsonDocument> = db.GetCollection "bindings"
                    let minimalDoc = BsonDocument()
                    minimalDoc.Add("_id", BsonValue(CallbackToken.value token))
                    minimalDoc.Add("ToolName", BsonValue "legacy")
                    collection.Insert minimalDoc |> ignore

                use store = LiteDbBindingStore.OpenAt path
                let result = ((store :> IBindingStore).TryGet(token, CancellationToken.None)).GetAwaiter().GetResult()

                match result with
                | ValueSome binding ->
                    Expect.equal binding.Owner Anyone "a minimal document with no owner field defaults to Anyone"
                    Expect.equal binding.ExpiresAt None "a minimal document with no expiry field defaults to never-expiring"
                    Expect.isFalse binding.SingleUse "a minimal document with no single-use field defaults to false"
                    Expect.equal binding.Arg None "a minimal document with no arg field defaults to argument-less"
                | ValueNone -> failwith "expected the minimal document to still load, with defaults"
            finally
                File.Delete path
    ]
