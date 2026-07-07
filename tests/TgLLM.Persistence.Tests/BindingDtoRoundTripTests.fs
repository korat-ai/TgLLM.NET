/// FsCheck round-trip property for `BindingDto` (`FileBindingStore.fs`): `toDomain (ofDomain b)`
/// must reproduce `b` exactly, for an ARBITRARY `ToolBinding` — every field, not just the ones the
/// hand-written example-based tests in `FileBindingStoreTests.fs` happen to cover. Exercises the
/// REAL `System.Text.Json` serialize/deserialize round trip (not just the pure `ofDomain`/
/// `toDomain` mapping functions composed in memory) — this is the shape of property that would
/// have caught a serializer-induced mismatch (e.g. the kind of `DateTimeOffset`/`DateTime`
/// normalization bug `LiteDbBindingStore`'s own `BindingDocument.toDomain` documents and fixes)
/// before it ever reached a hand-written example.
module TgLLM.Persistence.Tests.BindingDtoRoundTripTests

open System
open System.Text.Json
open Expecto
open FsCheck
open FSharp.UMX
open TgLLM.Core
open TgLLM.Persistence

/// A validated `ToolName` built from an arbitrary FsCheck string: prefixed so it is NEVER
/// empty-after-trim regardless of what FsCheck generates (`ToolName.create`'s only failure mode),
/// without needing a custom `Arbitrary`/filtering — matches this suite's existing property-test
/// convention (`ToolPlanTests.fs`) of building always-valid domain values from raw generated data.
let private arbitraryToolName (suffix: string) : ToolName =
    match ToolName.create $"tool-{suffix}" with
    | Ok n -> n
    | Error e -> failwithf "test construction produced an invalid tool name: %A" e

/// A `DateTimeOffset` built from an arbitrary `int64` of seconds, bounded well within
/// `DateTimeOffset`'s representable range regardless of the generated value's sign/magnitude.
let private arbitraryInstant (seconds: int64) : DateTimeOffset =
    DateTimeOffset.UnixEpoch.AddSeconds(float (seconds % 1_000_000_000L))

/// Builds an arbitrary, but always well-formed, `ToolBinding` from FsCheck-generated primitives —
/// every field `ToolBinding` has, not a fixed hand-picked subset.
let private arbitraryBinding
    (guid: Guid)
    (toolNameSuffix: string)
    (arg: string option)
    (ownerId: int64 option)
    (expirySeconds: int64 option)
    (singleUse: bool)
    (deniedNotice: string option)
    : ToolBinding =
    let owner =
        match ownerId with
        | None -> Anyone
        | Some uid -> User(UMX.tag<userId> uid)

    { ToolBinding.create (CallbackToken.ofGuid guid) (arbitraryToolName toolNameSuffix) arg with
        Owner = owner
        ExpiresAt = expirySeconds |> Option.map arbitraryInstant
        SingleUse = singleUse
        DeniedNotice = deniedNotice }

[<Tests>]
let bindingDtoRoundTripTests =
    testList "BindingDto round-trip [property]" [

        testProperty "toDomain (ofDomain b), through a REAL JSON serialize/deserialize round trip, reproduces b exactly"
        <| fun (guid: Guid) (toolNameSuffix: string) (arg: string option) (ownerId: int64 option) (expirySeconds: int64 option) (singleUse: bool) (deniedNotice: string option) ->
            let binding = arbitraryBinding guid toolNameSuffix arg ownerId expirySeconds singleUse deniedNotice

            let json = JsonSerializer.Serialize(BindingDto.ofDomain binding)

            // `Deserialize<BindingDto>` is nullness-annotated as possibly `null` (a JSON literal
            // `null`, which this serialized `json` never actually is) — normalized the same way
            // `FileBindingStore.openAt` itself already does for the array-typed case.
            let roundTripped =
                match JsonSerializer.Deserialize<BindingDto> json |> Option.ofObj with
                | Some dto -> BindingDto.toDomain dto
                | None -> None

            roundTripped = Some binding
    ]
