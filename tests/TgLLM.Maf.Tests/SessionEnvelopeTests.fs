/// Tests for `SessionEnvelope`: `PersistedApprovalDto`/`ConversationEnvelopeDto`'s round trip
/// through a REAL JSON encode/decode (including the `null`/`Nullable` edge cases each optional
/// field carries), `decode`'s totality over arbitrary bytes, and `validate`'s format/framework-
/// version compatibility gate.
module TgLLM.Maf.Tests.SessionEnvelopeTests

open System
open Expecto
open FsCheck
open TgLLM.Maf

/// Builds an arbitrary, but always well-formed, `PersistedApprovalDto` from FsCheck-generated
/// primitives — exercises both `null`/non-null `ArgumentsJson`, both `Anyone` (`null` `OwnerUserId`)
/// and `User` (a value), and both present/absent `ExpiresAt`, matching the round-trip convention in
/// `BindingDtoRoundTripTests.fs`.
let private arbitraryApproval
    (requestIdSeed: int)
    (callIdSeed: int)
    (tool: string)
    (argumentsJson: string option)
    (ownerUserId: int64 option)
    (messageId: int64)
    (expiresAtSeconds: int64 option)
    : PersistedApprovalDto =
    { RequestId = $"req-{requestIdSeed}"
      CallId = $"call-{callIdSeed}"
      Tool = tool
      ArgumentsJson = argumentsJson |> Option.toObj
      OwnerUserId = ownerUserId |> Option.toNullable
      MessageId = messageId
      ExpiresAt =
        expiresAtSeconds
        |> Option.map (fun seconds -> DateTimeOffset.UnixEpoch.AddSeconds(float (seconds % 1_000_000_000L)))
        |> Option.toNullable }

let private envelope
    (format: int)
    (mafVersion: string)
    (meaiVersion: string)
    (sessionJson: string)
    (approvals: PersistedApprovalDto[])
    : ConversationEnvelopeDto =
    { Format = format
      MafVersion = mafVersion
      MeaiVersion = meaiVersion
      SessionJson = sessionJson
      Approvals = approvals }

/// Every fixed-literal (non-round-trip-property) test below builds an envelope that is meant to
/// pass `validate` up to the ONE marker it deliberately mismatches — so every OTHER marker is
/// stamped with the running build's own value, exactly like a freshly-persisted record.
let private validEnvelope (format: int) (mafVersion: string) (sessionJson: string) (approvals: PersistedApprovalDto[]) : ConversationEnvelopeDto =
    envelope format mafVersion SessionEnvelope.currentMeaiVersion sessionJson approvals

[<Tests>]
let sessionEnvelopeTests =
    testList "SessionEnvelope" [

        testProperty "decode (encode e) = Ok e, for any well-formed envelope"
        <| fun
            (format: int)
            (mafVersion: string)
            (meaiVersion: string)
            (sessionJson: string)
            (approvals: (int * int * string * string option * int64 option * int64 * int64 option) list) ->
            let dtos =
                approvals
                |> List.map (fun (reqSeed, callSeed, tool, argJson, owner, msgId, expiry) ->
                    arbitraryApproval reqSeed callSeed tool argJson owner msgId expiry)
                |> List.toArray

            let e = envelope format mafVersion meaiVersion sessionJson dtos
            SessionEnvelope.decode (SessionEnvelope.encode e) = Ok e

        testProperty "decode never throws, for any byte array — Ok or Error (CorruptRecord _), nothing else"
        <| fun (bytes: byte[]) ->
            match SessionEnvelope.decode bytes with
            | Ok _ -> true
            | Error(CorruptRecord _) -> true
            | Error _ -> false

        testCase "an approval's RequestId and CallId round-trip distinctly" <| fun _ ->
            let approval = arbitraryApproval 1 2 "send_email" None None 10L None
            let e = validEnvelope SessionEnvelope.CurrentFormat "1.13.0.0" "{}" [| approval |]

            match SessionEnvelope.decode (SessionEnvelope.encode e) with
            | Ok decoded ->
                Expect.equal decoded.Approvals[0].RequestId "req-1" "RequestId is preserved"
                Expect.equal decoded.Approvals[0].CallId "call-2" "CallId is preserved, distinct from RequestId"
            | Error err -> failwithf "expected Ok, got %A" err

        testCase "decode rejects null bytes without throwing" <| fun _ ->
            match SessionEnvelope.decode null with
            | Error(CorruptRecord _) -> ()
            | other -> failwithf "expected Error(CorruptRecord _), got %A" other

        testCase "decode rejects an empty byte array without throwing" <| fun _ ->
            match SessionEnvelope.decode [||] with
            | Error(CorruptRecord _) -> ()
            | other -> failwithf "expected Error(CorruptRecord _), got %A" other

        testCase "decode rejects garbage bytes without throwing" <| fun _ ->
            match SessionEnvelope.decode [| 0uy; 255uy; 17uy; 3uy |] with
            | Error(CorruptRecord _) -> ()
            | other -> failwithf "expected Error(CorruptRecord _), got %A" other

        testCase "a Format mismatch is reported as IncompatibleFormat" <| fun _ ->
            let e = validEnvelope 999 "1.13.0.0" "{}" [||]

            match SessionEnvelope.validate "1.13.0.0" e with
            | Error(IncompatibleFormat(found, expected)) ->
                Expect.equal found 999 "the persisted format is reported"
                Expect.equal expected SessionEnvelope.CurrentFormat "the expected (current) format is reported"
            | other -> failwithf "expected Error(IncompatibleFormat _), got %A" other

        testCase "a major/minor MafVersion mismatch is reported as IncompatibleMafVersion" <| fun _ ->
            let e = validEnvelope SessionEnvelope.CurrentFormat "1.13.0.0" "{}" [||]

            match SessionEnvelope.validate "2.0.0.0" e with
            | Error(IncompatibleMafVersion(found, running)) ->
                Expect.equal found "1.13.0.0" "the persisted version is reported"
                Expect.equal running "2.0.0.0" "the running version is reported"
            | other -> failwithf "expected Error(IncompatibleMafVersion _), got %A" other

        testCase "a patch-level MafVersion difference passes validation" <| fun _ ->
            let e = validEnvelope SessionEnvelope.CurrentFormat "1.13.0.0" "{}" [||]

            match SessionEnvelope.validate "1.13.9.9" e with
            | Ok validated -> Expect.equal validated e "the envelope passes through unchanged"
            | Error err -> failwithf "expected Ok, got %A" err

        testCase "a major/minor MeaiVersion mismatch is reported as IncompatibleMeaiVersion, even though Format and MafVersion both match"
        <| fun _ ->
            let e = envelope SessionEnvelope.CurrentFormat SessionEnvelope.currentMafVersion "11.0.0.0" "{}" [||]

            match SessionEnvelope.validate SessionEnvelope.currentMafVersion e with
            | Error(IncompatibleMeaiVersion(found, running)) ->
                Expect.equal found "11.0.0.0" "the persisted M.E.AI version is reported"
                Expect.equal running SessionEnvelope.currentMeaiVersion "the running M.E.AI version is reported"
            | other -> failwithf "expected Error(IncompatibleMeaiVersion _), got %A" other

        testCase "a patch-level MeaiVersion difference passes validation" <| fun _ ->
            let major, minor =
                match Version.TryParse SessionEnvelope.currentMeaiVersion with
                | true, v ->
                    match Option.ofObj v with
                    | Some parsed -> parsed.Major, parsed.Minor
                    | None -> failwith "currentMeaiVersion parsed to a null Version"
                | false, _ -> failwith "currentMeaiVersion did not parse as a Version"

            let e = envelope SessionEnvelope.CurrentFormat SessionEnvelope.currentMafVersion $"{major}.{minor}.9.9" "{}" [||]

            match SessionEnvelope.validate SessionEnvelope.currentMafVersion e with
            | Ok validated -> Expect.equal validated e "the envelope passes through unchanged"
            | Error err -> failwithf "expected Ok, got %A" err

        testCase "decodeAndValidate composes decode and validate" <| fun _ ->
            let e = validEnvelope 999 "1.13.0.0" "{}" [||]

            match SessionEnvelope.decodeAndValidate "1.13.0.0" (SessionEnvelope.encode e) with
            | Error(IncompatibleFormat(999, _)) -> ()
            | other -> failwithf "expected Error(IncompatibleFormat(999, _)), got %A" other

        testCase "currentMafVersion reports a non-empty running Microsoft.Agents.AI version" <| fun _ ->
            Expect.isNotEmpty SessionEnvelope.currentMafVersion "the running MAF assembly reports a version"

        testCase "currentMeaiVersion reports a non-empty running Microsoft.Extensions.AI.Abstractions version" <| fun _ ->
            Expect.isNotEmpty SessionEnvelope.currentMeaiVersion "the running M.E.AI.Abstractions assembly reports a version"
    ]

/// A poisoned record must never pass `validate` only to throw LATER, deep inside `restoreOrCreate`
/// (`Bridge.fs`) — a throw there lands AFTER both of that function's own remove-on-failure gates, so
/// the bad record is never removed and every subsequent turn on that chat re-faults. `validate`
/// itself cannot see as far as `restoreOrCreate`'s own `rehydrate` call (a non-object
/// `ArgumentsJson`, caught there instead — see `MafDurableReliabilityTests.fs`), but it CAN — cheaply
/// — refuse the two shapes that would otherwise throw INSIDE the `PersistedApprovalDto` ->
/// `PendingApproval` construction itself: a null `Approvals` array (`for dto in env.Approvals`
/// throwing `NullReferenceException`), and an approval whose `RequestId`/`CallId`/`Tool` is
/// null/whitespace (`ToolApprovalRequestContent`/`FunctionCallContent`'s own constructors rejecting
/// it). `Unchecked.defaultof<string>` mirrors `ManifestTests.fs`'s own idiom for reproducing exactly
/// what untrusted JSON — not a well-behaved F# caller — can still hand a field this codebase declares
/// non-nullable.
[<Tests>]
let sessionEnvelopeApprovalValidationTests =
    testList "SessionEnvelope approvals validation (a poisoned record must never brick a chat)" [

        testCase "validate rejects a null Approvals array — Error (CorruptRecord _), never a throw" <| fun _ ->
            let e = validEnvelope SessionEnvelope.CurrentFormat SessionEnvelope.currentMafVersion "{}" (Unchecked.defaultof<PersistedApprovalDto[]>)

            match SessionEnvelope.validate SessionEnvelope.currentMafVersion e with
            | Error(CorruptRecord _) -> ()
            | other -> failwithf "expected Error(CorruptRecord _), got %A" other

        testCase "validate rejects an approval with a null RequestId — Error (CorruptRecord _), never a throw" <| fun _ ->
            let approval = { arbitraryApproval 1 2 "send_email" None None 10L None with RequestId = Unchecked.defaultof<string> }
            let e = validEnvelope SessionEnvelope.CurrentFormat SessionEnvelope.currentMafVersion "{}" [| approval |]

            match SessionEnvelope.validate SessionEnvelope.currentMafVersion e with
            | Error(CorruptRecord _) -> ()
            | other -> failwithf "expected Error(CorruptRecord _), got %A" other

        testCase "validate rejects an approval with an empty/whitespace RequestId — Error (CorruptRecord _), never a throw" <| fun _ ->
            let approval = { arbitraryApproval 1 2 "send_email" None None 10L None with RequestId = "   " }
            let e = validEnvelope SessionEnvelope.CurrentFormat SessionEnvelope.currentMafVersion "{}" [| approval |]

            match SessionEnvelope.validate SessionEnvelope.currentMafVersion e with
            | Error(CorruptRecord _) -> ()
            | other -> failwithf "expected Error(CorruptRecord _), got %A" other

        testCase "validate rejects an approval with a null CallId — Error (CorruptRecord _), never a throw" <| fun _ ->
            let approval = { arbitraryApproval 1 2 "send_email" None None 10L None with CallId = Unchecked.defaultof<string> }
            let e = validEnvelope SessionEnvelope.CurrentFormat SessionEnvelope.currentMafVersion "{}" [| approval |]

            match SessionEnvelope.validate SessionEnvelope.currentMafVersion e with
            | Error(CorruptRecord _) -> ()
            | other -> failwithf "expected Error(CorruptRecord _), got %A" other

        testCase "validate rejects an approval with a null Tool — Error (CorruptRecord _), never a throw" <| fun _ ->
            let approval = { arbitraryApproval 1 2 "send_email" None None 10L None with Tool = Unchecked.defaultof<string> }
            let e = validEnvelope SessionEnvelope.CurrentFormat SessionEnvelope.currentMafVersion "{}" [| approval |]

            match SessionEnvelope.validate SessionEnvelope.currentMafVersion e with
            | Error(CorruptRecord _) -> ()
            | other -> failwithf "expected Error(CorruptRecord _), got %A" other

        testCase "validate still accepts a well-formed Approvals array (regression: the new gate doesn't over-refuse)" <| fun _ ->
            let approval = arbitraryApproval 1 2 "send_email" None None 10L None
            let e = validEnvelope SessionEnvelope.CurrentFormat SessionEnvelope.currentMafVersion "{}" [| approval |]

            match SessionEnvelope.validate SessionEnvelope.currentMafVersion e with
            | Ok validated -> Expect.equal validated e "a well-formed approval still validates"
            | Error err -> failwithf "expected Ok, got %A" err
    ]
