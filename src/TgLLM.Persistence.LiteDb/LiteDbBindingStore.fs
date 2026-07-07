/// A durable, embedded-LiteDB `IBindingStore`: a second durable backend
/// proving the store seam generalizes beyond the file store, using a pure-managed,
/// single-file document store — no external server, no native dependency.
namespace TgLLM.Persistence.LiteDb

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open FSharp.UMX
open LiteDB
open TgLLM.Core

/// The on-disk LiteDB document shape of one `ToolBinding`. NOT marked `private`/`internal`
/// (disclosed deviation from the usual "hide the DTO" instinct, mirroring `TgLLM.Persistence.
/// BindingDto`'s own identical disclosed deviation): LiteDB's default `BsonMapper` only reflects
/// over PUBLIC types — verified the hard way, by decompiling the installed 5.0.21 assembly
/// (Principle V) and observing that marking this type `internal` made `BsonMapper` silently treat
/// every instance as an empty object, auto-generating an `ObjectId` primary key and persisting NONE
/// of the actual fields, rather than erroring — `BsonMapper`'s own doc comment states the
/// requirement plainly ("Classes must be public with a public constructor without parameters"),
/// but the FAILURE MODE for violating it is silent data loss, not an exception, which is worth
/// recording here so nobody "helpfully" re-adds `internal` later.
///
/// `[<CLIMutable>]`: the OTHER half of that same requirement — a public constructor WITHOUT
/// parameters, plus settable properties. A plain immutable F# record has neither (only the
/// all-fields constructor); `[<CLIMutable>]` is exactly the compiler feature for this: it emits a
/// public parameterless constructor and public get/set properties for an otherwise-idiomatic F#
/// record, with no change to how F# code itself constructs/reads one (`{ Id = ...; ToolName = ... }`
/// still works).
///
/// `Id` is LiteDB's own primary-key convention (a property literally named `Id`, case-insensitive,
/// needs no `[<BsonId>]` attribute) — holds the token's canonical string, mirroring `BindingDto.
/// Token` in the file store's own DTO. `ExpiresAtUtc` is `Nullable<DateTime>`, not
/// `DateTimeOffset`: `BsonValue` has no native `DateTimeOffset` conversion (verified against the
/// same assembly — only a `DateTime` conversion exists), so the domain's `DateTimeOffset` is
/// normalized to its UTC instant at the boundary; `Expiry.isLive`'s comparison only ever cares
/// about the absolute instant, so this is lossless for the one thing that matters, even though the
/// original (irrelevant) offset itself isn't preserved.
///
/// Empirically-verified caveat (found via an FsCheck round-trip property over the REAL store, not
/// assumed — reflection against the installed 5.0.21 assembly confirms the cause, Principle V):
/// `LiteDB.BsonMapper`'s own `TrimWhitespace` AND `EmptyStringToNull` properties both default to
/// `true`, and this store maps `BindingDocument` through `BsonMapper.Global`, which never overrides
/// either. This means, on ANY string field (`Arg`, `DeniedNotice`; `ToolName` is unaffected in
/// practice, since `TgLLM.Core.ToolName.create` already trims before a value ever reaches this
/// type): leading/trailing whitespace is silently stripped, and a value that is empty (or becomes
/// empty after that trim) comes back as BSON `null` — `TryGet` on a reloaded document then sees
/// `None`, indistinguishable from a binding that never had that field set at all. A `None`/
/// already-trimmed-non-empty value round-trips exactly regardless. Not fixed here (this store's
/// `ofDomain`/`toDomain` do not special-case it, and neither reconfigures `BsonMapper.Global`) —
/// recorded so it isn't mistaken for a fresh bug, and so a future caller knows a whitespace-only or
/// empty-string override is not durably distinguishable from "unset" through this store.
[<CLIMutable; NoComparison>]
type BindingDocument =
    { Id: string
      ToolName: string
      Arg: string | null
      OwnerUserId: Nullable<int64>
      ExpiresAtUtc: Nullable<DateTime>
      SingleUse: bool
      DeniedNotice: string | null }

module BindingDocument =
    let ofDomain (binding: ToolBinding) : BindingDocument =
        { Id = CallbackToken.value binding.Token
          ToolName = ToolName.value binding.ToolName
          Arg = binding.Arg |> Option.toObj
          OwnerUserId =
            match binding.Owner with
            | Anyone -> Nullable()
            | User uid -> Nullable(UMX.untag uid)
          ExpiresAtUtc = binding.ExpiresAt |> Option.map (fun expiresAt -> expiresAt.UtcDateTime) |> Option.toNullable
          SingleUse = binding.SingleUse
          DeniedNotice = binding.DeniedNotice |> Option.toObj }

    /// `None` for a document this store could never have produced itself (hand-edited/corrupt) —
    /// skipped by the caller rather than failing the whole read, same total-read contract as
    /// `TgLLM.Persistence.BindingDto.toDomain`. A document missing the owner/expiry/single-use
    /// fields ENTIRELY (an earlier-shaped row, from before those fields existed on this shape)
    /// still loads: `Nullable<_>.HasValue` is simply `false` for an absent field, `Arg`/
    /// `DeniedNotice` are simply `null`, so every new field falls through to `ToolBinding.create`'s
    /// own defaults (`Anyone`/`None`/`false`) exactly as if the row had never carried them.
    let toDomain (doc: BindingDocument) : ToolBinding option =
        match CallbackToken.tryParse doc.Id, ToolName.create doc.ToolName with
        | ValueSome token, Ok toolName ->
            let owner = if doc.OwnerUserId.HasValue then User(UMX.tag<userId> doc.OwnerUserId.Value) else Anyone

            // `DateTime.SpecifyKind(..., Utc)`, not a bare `DateTimeOffset(dt, TimeSpan.Zero)`:
            // LiteDB's default configuration re-hydrates a stored date as `DateTimeKind.Local`
            // (verified empirically — round-tripping `ofDomain`'s `.UtcDateTime` write came back
            // `Local`-kinded, which made the naive `DateTimeOffset(dt, TimeSpan.Zero)` throw
            // whenever the process's local zone wasn't itself UTC). We already know — by
            // construction, `ofDomain` only ever writes `.UtcDateTime` — that the numeric value IS
            // the UTC instant regardless of whatever `Kind` LiteDB tags it with on the way back, so
            // forcing `Utc` here is correct, not a workaround for a value we're unsure about.
            let expiresAt =
                if doc.ExpiresAtUtc.HasValue then
                    Some(DateTimeOffset(DateTime.SpecifyKind(doc.ExpiresAtUtc.Value, DateTimeKind.Utc), TimeSpan.Zero))
                else
                    None

            Some
                { ToolBinding.create token toolName (doc.Arg |> Option.ofObj) with
                    Owner = owner
                    ExpiresAt = expiresAt
                    SingleUse = doc.SingleUse
                    DeniedNotice = doc.DeniedNotice |> Option.ofObj }
        | _ -> None

/// Durable, embedded-LiteDB `IBindingStore`. `OpenAt` is the only public constructor path (mirrors
/// `FileBindingStore.openAt`'s convention): one collection (`"bindings"`), keyed by token via each
/// document's `Id`. Implements `IDisposable` (unlike the file store, which holds no persistent
/// handle) because a `LiteDatabase` keeps the datafile open for the connection's lifetime — a
/// caller MUST dispose an instance before another can open the SAME file (e.g. a simulated restart).
[<Sealed>]
type LiteDbBindingStore
    private
    (
        db: LiteDatabase,
        collection: ILiteCollection<BindingDocument>
    ) =

    interface IBindingStore with
        member _.Save(newBindings: IReadOnlyList<ToolBinding>, _ct: CancellationToken) : ValueTask =
            newBindings |> Seq.map BindingDocument.ofDomain |> collection.Upsert |> ignore
            ValueTask.CompletedTask

        member _.TryGet(token: CallbackToken, _ct: CancellationToken) : ValueTask<ToolBinding voption> =
            match collection.FindById(BsonValue(CallbackToken.value token)) |> Option.ofObj with
            | None -> ValueTask.FromResult ValueNone
            | Some doc ->
                match BindingDocument.toDomain doc with
                | Some binding -> ValueTask.FromResult(ValueSome binding)
                | None -> ValueTask.FromResult ValueNone

        member _.Remove(tokens: IReadOnlyList<CallbackToken>, _ct: CancellationToken) : ValueTask =
            for token in tokens do
                collection.Delete(BsonValue(CallbackToken.value token)) |> ignore

            ValueTask.CompletedTask

        /// A collection delete-by-query: every document whose `ExpiresAtUtc` is
        /// set and at-or-before `now` (matching `Expiry.isLive`'s own boundary — the exact expiry
        /// instant already counts as expired). A document with no `ExpiresAtUtc` is never matched.
        member _.EvictExpired(now: DateTimeOffset) : ValueTask<int> =
            let removed = collection.DeleteMany("$.ExpiresAtUtc != null AND $.ExpiresAtUtc <= @0", [| BsonValue(now.UtcDateTime) |])
            ValueTask.FromResult removed

    interface IDisposable with
        member _.Dispose() : unit = db.Dispose()

    /// Opens (or creates) a durable binding store backed by `path`. Indexes `ExpiresAtUtc`
    /// so `EvictExpired`'s delete-by-query doesn't scan the whole collection.
    ///
    /// Sets the `UTC_DATE` engine pragma (verified against the installed 5.0.21 assembly by
    /// decompilation, Principle V — `LiteDatabase.UtcDate` itself is get-only, backed by
    /// `_engine.Pragma("UTC_DATE")`, so this `Pragma` call is the actual way to set it): LiteDB's
    /// own DEFAULT silently converts a stored date to the machine's LOCAL time zone on every read
    /// (discovered empirically, not assumed — round-tripping a `DateTimeOffset.UtcNow`-derived value
    /// came back shifted by the host's UTC offset). Since `ofDomain`/`toDomain` always write and
    /// read `ExpiresAtUtc` as UTC, this pragma is required for the field to round-trip as the SAME
    /// instant at all, not merely a style preference.
    static member OpenAt(path: string) : LiteDbBindingStore =
        let db = new LiteDatabase(path)
        db.Pragma("UTC_DATE", BsonValue true) |> ignore
        let collection = db.GetCollection<BindingDocument> "bindings"
        collection.EnsureIndex("idx_ExpiresAtUtc", BsonExpression.Create "$.ExpiresAtUtc", false) |> ignore
        new LiteDbBindingStore(db, collection)
