/// Regression tests: Telegram `message_id` is unique only PER CHAT, so `MessageBindingTracker`
/// keying by bare `MessageId` let a paginator/counter tool's `ctx.EditKeyboardAsync` in one chat
/// find — and therefore remove — ANOTHER chat's live bindings whenever the two chats' keyboard
/// messages happened to share the same message_id (e.g. each chat's first-ever sent message). The
/// tracker is keyed by `(ChatId, MessageId)`, and `Record`/`TryGetPrevious` take a `ChatId` too.
module TgLLM.Core.Tests.MessageBindingTrackerTests

open FSharp.UMX
open Expecto
open TgLLM.Core

[<Tests>]
let messageBindingTrackerTests =
    testList "MessageBindingTracker" [

        testCase "TryGetPrevious on an unrecorded (chat, messageId) returns None" <| fun _ ->
            let tracker = MessageBindingTracker()
            let chat = UMX.tag<chatId> 1L
            let messageId = UMX.tag<messageId> 1L

            Expect.equal (tracker.TryGetPrevious(chat, messageId)) None "nothing has been recorded yet"

        testCase "Record then TryGetPrevious round-trips the exact tokens for that (chat, messageId)" <| fun _ ->
            let tracker = MessageBindingTracker()
            let chat = UMX.tag<chatId> 1L
            let messageId = UMX.tag<messageId> 1L
            let tokens = [ CallbackToken.generate (); CallbackToken.generate () ]

            tracker.Record(chat, messageId, tokens)

            Expect.equal (tracker.TryGetPrevious(chat, messageId)) (Some tokens) "the exact recorded tokens round-trip"

        testCase "the SAME message_id in two DIFFERENT chats does not collide (the bug this key shape fixes)" <| fun _ ->
            let tracker = MessageBindingTracker()
            let chatA = UMX.tag<chatId> 100L
            let chatB = UMX.tag<chatId> 200L
            let sharedMessageId = UMX.tag<messageId> 5L
            let tokensA = [ CallbackToken.generate () ]
            let tokensB = [ CallbackToken.generate () ]

            tracker.Record(chatA, sharedMessageId, tokensA)
            tracker.Record(chatB, sharedMessageId, tokensB)

            Expect.equal
                (tracker.TryGetPrevious(chatA, sharedMessageId))
                (Some tokensA)
                "chat A keeps its own tokens for message_id 5"

            Expect.equal
                (tracker.TryGetPrevious(chatB, sharedMessageId))
                (Some tokensB)
                "chat B keeps its own (different) tokens for the SAME message_id 5 — no cross-chat clobber"

        testCase "a known messageId in the WRONG chat is a miss, not a hit" <| fun _ ->
            let tracker = MessageBindingTracker()
            let chatA = UMX.tag<chatId> 1L
            let chatB = UMX.tag<chatId> 2L
            let messageId = UMX.tag<messageId> 1L
            let tokens = [ CallbackToken.generate () ]

            tracker.Record(chatA, messageId, tokens)

            Expect.equal (tracker.TryGetPrevious(chatB, messageId)) None "the same message_id in a different (never-recorded) chat is a miss"

        testCase "recording a new keyboard for the same (chat, messageId) overwrites the previous entry" <| fun _ ->
            let tracker = MessageBindingTracker()
            let chat = UMX.tag<chatId> 1L
            let messageId = UMX.tag<messageId> 1L
            let firstTokens = [ CallbackToken.generate () ]
            let secondTokens = [ CallbackToken.generate () ]

            tracker.Record(chat, messageId, firstTokens)
            tracker.Record(chat, messageId, secondTokens)

            Expect.equal (tracker.TryGetPrevious(chat, messageId)) (Some secondTokens) "the latest Record wins for the same key"

        // Eviction seam (research D7(b)): a long-lived bot must not grow this tracker's index
        // unbounded (one entry per DISTINCT (chat, messageId) forever). `capacity` bounds it; once
        // exceeded, the OLDEST-recorded distinct key is dropped first (FIFO by first sight).
        testCase "recording within capacity keeps every distinct (chat, messageId) entry" <| fun _ ->
            let tracker = MessageBindingTracker(capacity = 3)
            let chat = UMX.tag<chatId> 1L

            for messageIdValue in 1L .. 3L do
                tracker.Record(chat, UMX.tag<messageId> messageIdValue, [ CallbackToken.generate () ])

            for messageIdValue in 1L .. 3L do
                match tracker.TryGetPrevious(chat, UMX.tag<messageId> messageIdValue) with
                | Some _ -> ()
                | None -> failwithf "message_id %d should still be tracked (within capacity)" messageIdValue

        testCase "recording past capacity evicts the OLDEST distinct entry first, keeps the rest" <| fun _ ->
            let tracker = MessageBindingTracker(capacity = 3)
            let chat = UMX.tag<chatId> 1L
            let oldestMessageId = UMX.tag<messageId> 1L

            for messageIdValue in 1L .. 4L do
                tracker.Record(chat, UMX.tag<messageId> messageIdValue, [ CallbackToken.generate () ])

            Expect.equal
                (tracker.TryGetPrevious(chat, oldestMessageId))
                None
                "the oldest-recorded distinct (chat, messageId) was evicted once capacity was exceeded"

            for messageIdValue in 2L .. 4L do
                match tracker.TryGetPrevious(chat, UMX.tag<messageId> messageIdValue) with
                | Some _ -> ()
                | None -> failwithf "message_id %d should still be tracked (within capacity after eviction)" messageIdValue

        testCase "re-recording an already-tracked (chat, messageId) does not itself count as a new distinct entry" <| fun _ ->
            let tracker = MessageBindingTracker(capacity = 2)
            let chat = UMX.tag<chatId> 1L
            let messageIdA = UMX.tag<messageId> 1L
            let messageIdB = UMX.tag<messageId> 2L
            let tokensForB = [ CallbackToken.generate () ]
            let latestTokensForA = [ CallbackToken.generate () ]

            tracker.Record(chat, messageIdA, [ CallbackToken.generate () ])
            tracker.Record(chat, messageIdB, tokensForB)
            // Re-recording A repeatedly must not push the tracker over capacity by itself — it's
            // the SAME distinct key, not a new one.
            tracker.Record(chat, messageIdA, latestTokensForA)
            tracker.Record(chat, messageIdA, latestTokensForA)

            Expect.equal (tracker.TryGetPrevious(chat, messageIdA)) (Some latestTokensForA) "A's latest tokens are still tracked"
            Expect.equal (tracker.TryGetPrevious(chat, messageIdB)) (Some tokensForB) "B was NOT evicted by A's re-recordings (still within capacity)"
    ]
