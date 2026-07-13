/// FsCheck round-trip property for `LiteDbBindingStore`/`BindingDocument`: an ARBITRARY
/// `ToolBinding`, saved and re-read through the REAL embedded LiteDB engine (not just the pure
/// `BindingDocument.ofDomain`/`toDomain` mapping functions composed in memory), must reproduce the
/// original binding exactly, modulo `ExpiresAt` truncating to BSON's native millisecond precision.
/// This is the shape of property that would have caught the `DateTimeKind` mismatch
/// `BindingDocument.toDomain`'s own doc comment describes and fixes (LiteDB's default engine
/// re-hydrates a stored date as `DateTimeKind.Local`) — that bug was only observable by actually
/// round-tripping through the real engine, never by composing the two mapping functions in memory
/// alone. It's also exactly how the trim/empty-string caveat above was discovered in the first
/// place.
module TgLLM.Persistence.LiteDb.Tests.BindingDocumentRoundTripTests

open System
open System.IO
open System.Threading
open Expecto
open FsCheck
open FSharp.UMX
open TgLLM.Core
open TgLLM.Persistence.LiteDb

let private tempPath () : string =
    Path.Combine(Path.GetTempPath(), $"tgllm-tool-router-litedb-roundtrip-tests-{Guid.NewGuid()}.db")

/// Matches `LiteDbBindingStoreTests.fs`'s own helper (duplicated here since that one is
/// module-private, same convention this suite already uses elsewhere) — see its doc comment for
/// why comparing `ExpiresAt` needs this rather than being a bug in itself.
let private truncateToMilliseconds (instant: DateTimeOffset) : DateTimeOffset =
    instant.AddTicks(-(instant.Ticks % TimeSpan.TicksPerMillisecond))

let private arbitraryToolName (suffix: string) : ToolName =
    match ToolName.create $"tool-{suffix}" with
    | Ok n -> n
    | Error e -> failwithf "test construction produced an invalid tool name: %A" e

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
        ExpiresAt = expirySeconds |> Option.map (fun s -> DateTimeOffset.UnixEpoch.AddSeconds(float (s % 1_000_000_000L)))
        SingleUse = singleUse
        DeniedNotice = deniedNotice }

/// Applies the one BSON precision limitation before exact comparison.
let private asLiteDbCanRepresent (binding: ToolBinding) : ToolBinding =
    { binding with
        ExpiresAt = binding.ExpiresAt |> Option.map truncateToMilliseconds }

[<Tests>]
let bindingDocumentRoundTripTests =
    testList "BindingDocument round-trip [property]" [

        // A REAL LiteDB datafile is opened per property run (100 by default) — bounded to a
        // smaller run count than this suite's example-based tests so the property still finishes
        // quickly; still comfortably enough runs to shake out a systemic (not input-dependent)
        // engine-level mismatch like the historical DateTimeKind bug.
        testPropertyWithConfig
            { FsCheckConfig.defaultConfig with maxTest = 40 }
            "Save then TryGet, through the REAL LiteDbBindingStore, reproduces the original binding exactly modulo BSON date precision"
        <| fun (guid: Guid) (toolNameSuffix: string) (arg: string option) (ownerId: int64 option) (expirySeconds: int64 option) (singleUse: bool) (deniedNotice: string option) ->
            let binding = arbitraryBinding guid toolNameSuffix arg ownerId expirySeconds singleUse deniedNotice
            let path = tempPath ()

            try
                use store = LiteDbBindingStore.OpenAt path
                ((store :> IBindingStore).Save([ binding ], CancellationToken.None)).GetAwaiter().GetResult()
                let roundTripped = ((store :> IBindingStore).TryGet(binding.Token, CancellationToken.None)).GetAwaiter().GetResult()

                roundTripped = ValueSome(asLiteDbCanRepresent binding)
            finally
                File.Delete path
    ]
