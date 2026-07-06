/// FsCheck property tests for the `CallbackToken` codec.
module TgLLM.Core.Tests.CallbackTokenTests

open System
open Expecto
open TgLLM.Core

[<Tests>]
let callbackTokenTests =
    testList "CallbackToken" [

        testProperty "tryParse (value t) = ValueSome t, for any token built from a Guid" <| fun (guid: Guid) ->
            let token = CallbackToken.ofGuid guid
            CallbackToken.tryParse (CallbackToken.value token) = ValueSome token

        testProperty "generate () always round-trips too" <| fun () ->
            let token = CallbackToken.generate ()
            CallbackToken.tryParse (CallbackToken.value token) = ValueSome token

        testProperty "encoded form is always <= 64 bytes (callback_data limit)" <| fun (guid: Guid) ->
            let token = CallbackToken.ofGuid guid
            let byteLength = Text.Encoding.UTF8.GetByteCount(CallbackToken.value token)
            byteLength <= 64

        testProperty "tryParse is total over arbitrary strings (never throws)" <| fun (s: string) ->
            // `s` may be null, empty, too short/long, or contain characters outside the
            // base64url alphabet — tryParse must handle every case by returning a voption,
            // never raising.
            try
                CallbackToken.tryParse s |> ignore
                true
            with _ ->
                false

        testCase "tryParse on null is ValueNone" <| fun _ ->
            Expect.equal (CallbackToken.tryParse null) ValueNone "null is not a valid token"

        testCase "tryParse on empty string is ValueNone" <| fun _ ->
            Expect.equal (CallbackToken.tryParse "") ValueNone "empty string is not a valid token"

        testCase "tryParse on obvious garbage is ValueNone" <| fun _ ->
            Expect.equal (CallbackToken.tryParse "not-a-real-token!!") ValueNone "malformed input is rejected"

        testCase "two distinct guids yield distinct tokens" <| fun _ ->
            let a = CallbackToken.ofGuid (Guid("11111111-1111-1111-1111-111111111111"))
            let b = CallbackToken.ofGuid (Guid("22222222-2222-2222-2222-222222222222"))
            Expect.notEqual a b "distinct guids must not collide"
    ]
