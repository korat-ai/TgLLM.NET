/// Tests for `RunOwner.resolve`'s resolution order: an explicit scope always wins; a
/// message-initiated turn defaults to its sender; a host-initiated run defaults to the private
/// chat's own peer, else `Anyone`.
module TgLLM.Maf.Tests.OwnerResolutionTests

open Expecto
open FsCheck
open FSharp.UMX
open TgLLM.Core
open TgLLM.Maf

let private user (id: int64) : UserId = UMX.tag<userId> id
let private chat (id: int64) : ChatId = UMX.tag<chatId> id

[<Tests>]
let ownerResolutionTests =
    testList "RunOwner" [

        testProperty "an explicit scope always wins, regardless of initiator or chat" <| fun (scopeIsAnyone: bool, uid: int64, initiatorId: int64, chatId: int64) ->
            let explicitScope = if scopeIsAnyone then Anyone else User(user uid)
            let initiator = if initiatorId % 2L = 0L then Some(user initiatorId) else None
            RunOwner.resolve (Some explicitScope) initiator (chat chatId) = explicitScope

        testProperty "a message-initiated turn (Some sender) with no explicit scope defaults to User sender, for any chat" <| fun (senderId: int64, chatId: int64) ->
            let sender = user senderId
            RunOwner.resolve None (Some sender) (chat chatId) = User sender

        testProperty "a host-initiated run (no initiator) in a private chat defaults to User peer" <| fun (positiveChatId: PositiveInt) ->
            let (PositiveInt raw) = positiveChatId
            let chatId = int64 raw
            RunOwner.resolve None None (chat chatId) = User(user chatId)

        testProperty "a host-initiated run (no initiator) in a non-private chat defaults to Anyone" <| fun (negativeChatId: NegativeInt) ->
            let (NegativeInt raw) = negativeChatId
            RunOwner.resolve None None (chat (int64 raw)) = Anyone

        testCase "chat id 0 (not a valid private chat id, not negative either) is not treated as private" <| fun _ ->
            Expect.equal (RunOwner.resolve None None (chat 0L)) Anyone "0 is neither a positive private-chat id nor inferred as one"

        testCase "explicit scope beats a message-initiated sender" <| fun _ ->
            let sender = user 1L
            let explicitScope = User(user 999L)
            Expect.equal (RunOwner.resolve (Some explicitScope) (Some sender) (chat 1L)) explicitScope "the host's own choice always wins"

        testCase "explicit scope beats the private-chat peer default" <| fun _ ->
            Expect.equal (RunOwner.resolve (Some Anyone) None (chat 555L)) Anyone "the host's own choice always wins, even over an 'obvious' inference"
    ]
