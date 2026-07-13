/// Tests for `ProcessedQueryTracker.TryBegin` — the at-most-once redelivery dedup guard:
/// the FIRST time a `callback_query.id` is seen, processing may proceed (true); any REPEAT
/// within its TTL is dropped (false), so a redelivered update never invokes a tool twice. Backed
/// by a bounded, TTL'd seen-set (an injected `Clock`, never ambient `DateTimeOffset.Now`), so a
/// long-lived bot's memory doesn't grow forever even if ids are never naturally reused.
module TgLLM.Core.Tests.ProcessedQueryTrackerTests

open System
open Expecto
open TgLLM.Core

[<Tests>]
let processedQueryTrackerTests =
    testList "ProcessedQueryTracker" [

        testCase "the first sighting of a query id returns true" <| fun _ ->
            let tracker = ProcessedQueryTracker(fun () -> DateTimeOffset.UnixEpoch)
            Expect.isTrue (tracker.TryBegin "q1") "first time ⇒ safe to process"

        testCase "an immediate repeat of the same query id returns false" <| fun _ ->
            let tracker = ProcessedQueryTracker(fun () -> DateTimeOffset.UnixEpoch)
            tracker.TryBegin "q1" |> ignore
            Expect.isFalse (tracker.TryBegin "q1") "a repeat within the TTL is dropped, not processed again"

        testCase "distinct query ids are each processed once, independently" <| fun _ ->
            let tracker = ProcessedQueryTracker(fun () -> DateTimeOffset.UnixEpoch)
            Expect.isTrue (tracker.TryBegin "q1") "q1 first sighting"
            Expect.isTrue (tracker.TryBegin "q2") "q2 first sighting, unrelated to q1"
            Expect.isFalse (tracker.TryBegin "q1") "q1 repeat still dropped"
            Expect.isFalse (tracker.TryBegin "q2") "q2 repeat still dropped"

        testCase "a repeat AFTER the TTL has elapsed is treated as new again" <| fun _ ->
            let mutable now = DateTimeOffset.UnixEpoch
            let tracker = ProcessedQueryTracker((fun () -> now), ttl = TimeSpan.FromMinutes 1.0)

            Expect.isTrue (tracker.TryBegin "q1") "first sighting"
            now <- now.AddMinutes 2.0
            Expect.isTrue (tracker.TryBegin "q1") "the earlier sighting aged out past the TTL, so this counts as a fresh sighting"

        testCase "eviction when over the bound: the earliest ids are forgotten and are seen as new again" <| fun _ ->
            let tracker = ProcessedQueryTracker((fun () -> DateTimeOffset.UnixEpoch), capacity = 3)

            for i in 1 .. 5 do
                Expect.isTrue (tracker.TryBegin $"q{i}") $"q{i}'s first sighting is always true"

            // The bound is 3, and 5 distinct ids were seen: q1 (the oldest) must have been evicted
            // to make room, so it is treated as unseen again — this is what makes the bounded
            // eviction observable from the outside.
            Expect.isTrue (tracker.TryBegin "q1") "q1 was evicted once the bound was exceeded, so it's treated as unseen again"

        testCase "non-positive capacity and TTL are rejected" <| fun _ ->
            Expect.throwsT<ArgumentException>
                (fun () -> ProcessedQueryTracker((fun () -> DateTimeOffset.UnixEpoch), capacity = 0) |> ignore)
                "a zero-sized dedup set cannot provide at-most-once behavior"

            Expect.throwsT<ArgumentException>
                (fun () -> ProcessedQueryTracker((fun () -> DateTimeOffset.UnixEpoch), ttl = TimeSpan.Zero) |> ignore)
                "a zero TTL would never deduplicate a redelivery"
    ]
