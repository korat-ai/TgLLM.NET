/// Tests for `ApprovalDescriptor.serialize`/`tryParse`: a round-trip law over well-formed
/// descriptors, and totality over arbitrary (including malformed) input.
module TgLLM.Maf.Tests.ApprovalDescriptorTests

open Expecto
open FsCheck
open TgLLM.Maf

/// `RequestId`/`Tool` built from a non-negative seed so they are always non-empty, non-whitespace
/// strings — exactly what `tryParse` accepts. An arbitrary FsCheck `string` (which may be `""` or
/// all-whitespace) is exactly the invalid-input case the totality tests below cover separately.
let private toDescriptor (chat: int64, requestIdSeed: NonNegativeInt, toolSeed: NonNegativeInt) : ApprovalDescriptor =
    let (NonNegativeInt reqSeed) = requestIdSeed
    let (NonNegativeInt toolSeedValue) = toolSeed

    { Chat = chat
      RequestId = $"req-{reqSeed}"
      Tool = $"tool-{toolSeedValue}" }

[<Tests>]
let approvalDescriptorTests =
    testList "ApprovalDescriptor" [

        testProperty "tryParse (serialize d) = Some d, for any well-formed descriptor" <| fun (seed: int64 * NonNegativeInt * NonNegativeInt) ->
            let descriptor = toDescriptor seed
            ApprovalDescriptor.tryParse (ApprovalDescriptor.serialize descriptor) = Some descriptor

        testCase "tryParse rejects null" <| fun _ -> Expect.isNone (ApprovalDescriptor.tryParse null) "null is not a descriptor"

        testCase "tryParse rejects the empty string" <| fun _ ->
            Expect.isNone (ApprovalDescriptor.tryParse "") "an empty string is not JSON"

        testCase "tryParse rejects garbage JSON" <| fun _ ->
            Expect.isNone (ApprovalDescriptor.tryParse "{not valid json") "malformed JSON never throws, just fails"

        testCase "tryParse rejects a JSON object missing every field" <| fun _ ->
            Expect.isNone (ApprovalDescriptor.tryParse "{}") "missing RequestId/Tool is not a valid descriptor"

        testCase "tryParse rejects an empty RequestId" <| fun _ ->
            Expect.isNone
                (ApprovalDescriptor.tryParse """{"Chat":1,"RequestId":"","Tool":"send_email"}""")
                "an empty RequestId can never correlate to a pending request"

        testCase "tryParse rejects a whitespace-only Tool" <| fun _ ->
            Expect.isNone
                (ApprovalDescriptor.tryParse """{"Chat":1,"RequestId":"r1","Tool":"   "}""")
                "a whitespace-only Tool is not a real tool name"

        testProperty "tryParse never throws, for any string input" <| fun (raw: string) ->
            // The property itself IS the assertion: if `tryParse` throws, FsCheck reports the
            // exception as a failure; returning here at all proves totality for this input.
            ApprovalDescriptor.tryParse raw |> ignore
            true

        testCase "serialize -> tryParse preserves the Chat id across the int64 range" <| fun _ ->
            let descriptor =
                { Chat = System.Int64.MinValue
                  RequestId = "req-min"
                  Tool = "tool-min" }

            Expect.equal (ApprovalDescriptor.tryParse (ApprovalDescriptor.serialize descriptor)) (Some descriptor) "extreme Chat values round-trip too"

        testCase "make/chat round-trips a ChatId through UMX tagging" <| fun _ ->
            let chat = FSharp.UMX.UMX.tag<TgLLM.Core.chatId> 12345L
            let descriptor = ApprovalDescriptor.make chat "req-1" "send_email"
            Expect.equal (ApprovalDescriptor.chat descriptor) chat "chat re-tags the same numeric id"
    ]
