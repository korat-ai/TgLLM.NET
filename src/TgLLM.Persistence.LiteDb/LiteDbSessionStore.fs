/// A durable, embedded-LiteDB `ISessionStore`, mirroring `LiteDbBindingStore.fs`'s own structure
/// for the session-store seam: a second durable backend proving the store seam generalizes beyond
/// the file store, using the same pure-managed, single-file document store — no external server, no
/// native dependency.
namespace TgLLM.Persistence.LiteDb

open System
open System.Threading
open System.Threading.Tasks
open FSharp.UMX
open LiteDB
open TgLLM.Core

/// The on-disk LiteDB document shape of one `SessionRecord`. NOT marked `private`/`internal` (same
/// disclosed deviation as `BindingDocument`, for the same reason: LiteDB's default `BsonMapper` only
/// reflects over PUBLIC types with a public parameterless constructor — see `BindingDocument`'s own
/// doc comment for the empirically-verified failure mode, silent data loss rather than an
/// exception). `[<CLIMutable>]` supplies exactly that public parameterless constructor plus
/// settable properties for this otherwise-idiomatic F# record.
///
/// `Id` is LiteDB's own primary-key convention (a property literally named `Id`, case-insensitive) —
/// holds `UMX.untag chat`, the chat's raw `int64`, mirroring `BindingDocument.Id`'s role as the
/// natural key of its store. `Payload` is `byte[]` directly, unlike `TgLLM.Persistence.
/// SessionRowDto`'s Base64-`string` encoding: LiteDB's `BsonMapper` natively converts a `byte[]`
/// property to/from the BSON Binary subtype (verified against the installed 5.0.21 assembly), so no
/// intermediate text encoding is needed on this backend. `LastActivityAtUtc` is `DateTime`, not
/// `DateTimeOffset` — `BsonValue` has no native `DateTimeOffset` conversion (the same fact
/// `BindingDocument.ExpiresAtUtc`'s own doc comment records), so `SessionRecord.LastActivityAt` is
/// normalized to its UTC instant at the boundary; `ISessionStore.EvictIdle`'s comparison only ever
/// cares about the absolute instant, so this is lossless for the one thing that matters.
[<CLIMutable; NoComparison>]
type SessionDocument =
    { Id: int64
      Payload: byte[]
      LastActivityAtUtc: DateTime }

module SessionDocument =
    let ofDomain (chat: ChatId) (record: SessionRecord) : SessionDocument =
        { Id = UMX.untag chat
          Payload = record.Payload
          LastActivityAtUtc = record.LastActivityAt.UtcDateTime }

    let toDomain (doc: SessionDocument) : ChatId * SessionRecord =
        let chat = UMX.tag<chatId> doc.Id

        // `DateTime.SpecifyKind(..., Utc)`, not a bare `DateTimeOffset(dt, TimeSpan.Zero)`: LiteDB's
        // default configuration re-hydrates a stored date as `DateTimeKind.Local` (the same
        // empirically-verified fact `BindingDocument.toDomain`'s own comment records for
        // `ExpiresAtUtc`) — `ofDomain` only ever writes `.UtcDateTime`, so the numeric value IS the
        // UTC instant regardless of whatever `Kind` LiteDB tags it with on the way back.
        let lastActivityAt = DateTimeOffset(DateTime.SpecifyKind(doc.LastActivityAtUtc, DateTimeKind.Utc), TimeSpan.Zero)

        chat, { Payload = doc.Payload; LastActivityAt = lastActivityAt }

/// Durable, embedded-LiteDB `ISessionStore`. `OpenAt` is the only public constructor path (mirrors
/// `LiteDbBindingStore.OpenAt`'s convention): its own collection (`"sessions"`), keyed by chat via
/// each document's `Id`, in a datafile SEPARATE from any `LiteDbBindingStore` — the two backends
/// share no file or collection. Implements `IDisposable` (same reason as `LiteDbBindingStore`): a
/// `LiteDatabase` keeps the datafile open for the connection's lifetime, so dispose an instance
/// before opening another over the SAME file (e.g. a simulated restart). Use a SEPARATE datafile per
/// store: sharing one file between this and a `LiteDbBindingStore` (or a second session store) is
/// unsupported — some platforms silently permit two engines over one file rather than rejecting it,
/// which risks corruption, so the separation is the caller's responsibility, not an enforced lock.
[<Sealed>]
type LiteDbSessionStore
    private
    (
        db: LiteDatabase,
        collection: ILiteCollection<SessionDocument>
    ) =

    interface ISessionStore with
        member _.Save(chat: ChatId, record: SessionRecord, _ct: CancellationToken) : ValueTask =
            collection.Upsert(SessionDocument.ofDomain chat record) |> ignore
            ValueTask.CompletedTask

        member _.TryGet(chat: ChatId, _ct: CancellationToken) : ValueTask<SessionRecord voption> =
            match collection.FindById(BsonValue(UMX.untag chat)) |> Option.ofObj with
            | None -> ValueTask.FromResult ValueNone
            | Some doc -> ValueTask.FromResult(ValueSome(snd (SessionDocument.toDomain doc)))

        member _.Remove(chat: ChatId, _ct: CancellationToken) : ValueTask =
            collection.Delete(BsonValue(UMX.untag chat)) |> ignore
            ValueTask.CompletedTask

        /// A collection delete-by-query: every document whose `LastActivityAtUtc` is at-or-before
        /// `olderThan` (matching `ISessionStore.EvictIdle`'s own boundary — the exact idle instant
        /// already counts as idle), mirroring `LiteDbBindingStore.EvictExpired`'s own delete-by-query
        /// shape.
        member _.EvictIdle(olderThan: DateTimeOffset) : ValueTask<int> =
            let removed = collection.DeleteMany("$.LastActivityAtUtc <= @0", [| BsonValue(olderThan.UtcDateTime) |])
            ValueTask.FromResult removed

    interface IDisposable with
        member _.Dispose() : unit = db.Dispose()

    /// Opens (or creates) a durable session store backed by `path`. Indexes `LastActivityAtUtc` so
    /// `EvictIdle`'s delete-by-query doesn't scan the whole collection.
    ///
    /// Sets the `UTC_DATE` engine pragma (same requirement, and same verified mechanism, as
    /// `LiteDbBindingStore.OpenAt`'s own doc comment: `LiteDatabase.UtcDate` is get-only, so `Pragma`
    /// is the actual way to set it) — LiteDB's own default silently converts a stored date to the
    /// machine's LOCAL time zone on every read; since `ofDomain`/`toDomain` always write and read
    /// `LastActivityAtUtc` as UTC, this pragma is required for the field to round-trip as the SAME
    /// instant at all.
    static member OpenAt(path: string) : LiteDbSessionStore =
        let db = new LiteDatabase(path)
        db.Pragma("UTC_DATE", BsonValue true) |> ignore
        let collection = db.GetCollection<SessionDocument> "sessions"
        collection.EnsureIndex("idx_LastActivityAtUtc", BsonExpression.Create "$.LastActivityAtUtc", false) |> ignore
        new LiteDbSessionStore(db, collection)
