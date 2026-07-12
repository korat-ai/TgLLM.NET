/// Tests for `ReplyCoalescer`: `IsDue` gates on two independent conditions (the running text changed
/// since the last emit, and the cadence gate has cleared); `MarkEmitted` re-arms the gate for the
/// ordinary interval; `NotifyRateLimited` re-arms it further out (never closer than the ordinary
/// interval) WITHOUT marking the pending text emitted, so a later tick still retries it; the optional
/// `seed` constructor parameter seeds `RunningText` for a fresh per-message coalescer.
module TgLLM.Maf.Tests.ReplyCoalescerTests

open System
open Expecto
open FsCheck
open TgLLM.Maf

let private epoch = DateTimeOffset.UnixEpoch
let private interval = TimeSpan.FromSeconds 1.5
let private clock: TgLLM.Core.Clock = fun () -> epoch

/// A guaranteed-non-empty delta for the wide-range property below — an FsCheck-generated
/// `string | null` collapses to this fallback when null/empty, so appending it is always guaranteed
/// to change `RunningText` (the property under test needs that guarantee, not the delta's exact
/// content).
let private nonEmptyDelta (s: string | null) : string =
    match s |> Option.ofObj with
    | Some s when s.Length > 0 -> s
    | _ -> "x"

[<Tests>]
let replyCoalescerTests =
    testList "ReplyCoalescer" [

        testCase "a freshly constructed coalescer (no seed) has empty RunningText" <| fun _ ->
            let c = ReplyCoalescer(clock, interval)
            Expect.equal c.RunningText "" "nothing was ever appended"

        testCase "the seeded constructor seeds RunningText" <| fun _ ->
            let c = ReplyCoalescer(clock, interval, seed = "already streamed so far")
            Expect.equal c.RunningText "already streamed so far" "the seed becomes the starting RunningText, for a fresh per-message rollover coalescer"

        testCase "IsDue is true before anything has ever been marked emitted, even with no gate set" <| fun _ ->
            let c = ReplyCoalescer(clock, interval, seed = "hello")
            Expect.isTrue (c.IsDue epoch) "the text changed from 'nothing emitted yet' to the seeded text, and no gate has ever been armed"

        testProperty "Append concatenates deltas, in order" <| fun (deltas: (string | null) list) ->
            let c = ReplyCoalescer(clock, interval)
            let safeDeltas = deltas |> List.map (Option.ofObj >> Option.defaultValue "")

            for d in safeDeltas do
                c.Append d

            c.RunningText = String.concat "" safeDeltas

        testCase "Append skips a null delta — RunningText is unaffected" <| fun _ ->
            let c = ReplyCoalescer(clock, interval, seed = "kept")
            c.Append null
            Expect.equal c.RunningText "kept" "a null delta must not concatenate 'null' or throw"

        testCase "Append skips an empty delta — RunningText is unaffected" <| fun _ ->
            let c = ReplyCoalescer(clock, interval, seed = "kept")
            c.Append ""
            Expect.equal c.RunningText "kept" "an empty delta is a no-op"

        testCase "MarkEmitted makes IsDue false for the SAME text, at the SAME instant" <| fun _ ->
            let c = ReplyCoalescer(clock, interval)
            c.Append "hello"
            c.MarkEmitted epoch
            Expect.isFalse (c.IsDue epoch) "the just-emitted text is unchanged, so no new edit is due yet"

        testCase "MarkEmitted's gate is exclusive of NOW but inclusive of the boundary instant (now >= nextAllowedAt)" <| fun _ ->
            let c = ReplyCoalescer(clock, interval)
            c.Append "hello"
            c.MarkEmitted epoch
            c.Append " world" // text changed — only the gate can still block IsDue now

            Expect.isFalse (c.IsDue epoch) "still inside the interval — the gate has not cleared yet"
            Expect.isFalse (c.IsDue(epoch + interval - TimeSpan.FromMilliseconds 1.0)) "one tick before the gate clears — still false"
            Expect.isTrue (c.IsDue(epoch + interval)) "exactly at the gate — the boundary instant itself counts as due"
            Expect.isTrue (c.IsDue(epoch + interval + TimeSpan.FromSeconds 10.0)) "well past the gate — still due"

        testCase "IsDue stays false once the gate clears if the text never changed after MarkEmitted" <| fun _ ->
            let c = ReplyCoalescer(clock, interval)
            c.Append "hello"
            c.MarkEmitted epoch
            Expect.isFalse (c.IsDue(epoch + interval + TimeSpan.FromSeconds 100.0)) "the gate cleared, but nothing new was appended — skip-if-unchanged"

        testCase "a second MarkEmitted re-arms the gate from ITS OWN instant, not the first's" <| fun _ ->
            let c = ReplyCoalescer(clock, interval)
            c.Append "hello"
            c.MarkEmitted epoch
            c.Append " world"
            let secondEmitAt = epoch + interval
            c.MarkEmitted secondEmitAt
            c.Append "!" // text changes again — only NOW can a later IsDue go true, once THIS gate clears

            Expect.isFalse (c.IsDue secondEmitAt) "just re-armed — still inside the SECOND gate, even though the text changed again"
            Expect.isFalse (c.IsDue(secondEmitAt + interval - TimeSpan.FromMilliseconds 1.0)) "still inside the SECOND gate"
            Expect.isTrue (c.IsDue(secondEmitAt + interval)) "the second gate (anchored on secondEmitAt, not epoch) has now cleared"

        testCase "NotifyRateLimited does NOT mark the pending text emitted — a later tick against the SAME text can still go true" <| fun _ ->
            let c = ReplyCoalescer(clock, interval)
            c.Append "hello"
            c.MarkEmitted epoch
            c.Append " world" // now pending, never yet emitted
            c.NotifyRateLimited(epoch, ValueNone)

            Expect.isFalse (c.IsDue epoch) "the back-off gate has not cleared yet"
            Expect.isTrue (c.IsDue(epoch + interval)) "once the back-off gate clears, the SAME still-pending text is due again — never dropped"

        testCase "NotifyRateLimited with no retryAfter hint re-arms exactly the ordinary interval out" <| fun _ ->
            let c = ReplyCoalescer(clock, interval)
            c.Append "hello"
            c.NotifyRateLimited(epoch, ValueNone)

            Expect.isFalse (c.IsDue(epoch + interval - TimeSpan.FromMilliseconds 1.0)) "one tick before the ordinary interval — still gated"
            Expect.isTrue (c.IsDue(epoch + interval)) "exactly the ordinary interval out — the gate has cleared"

        testCase "NotifyRateLimited never re-arms CLOSER than the ordinary interval, even for a zero retryAfter hint" <| fun _ ->
            let c = ReplyCoalescer(clock, interval)
            c.Append "hello"
            c.NotifyRateLimited(epoch, ValueSome TimeSpan.Zero)

            Expect.isFalse (c.IsDue(epoch + interval - TimeSpan.FromMilliseconds 1.0)) "a zero hint must not defeat the coalescing cadence"
            Expect.isTrue (c.IsDue(epoch + interval)) "still gated for at least the ordinary interval"

        testCase "NotifyRateLimited never re-arms CLOSER than the ordinary interval, even for a small retryAfter hint" <| fun _ ->
            let c = ReplyCoalescer(clock, interval)
            c.Append "hello"
            c.NotifyRateLimited(epoch, ValueSome(TimeSpan.FromMilliseconds 100.0))

            Expect.isFalse (c.IsDue(epoch + interval - TimeSpan.FromMilliseconds 1.0)) "a hint smaller than the interval must not shorten the gate"
            Expect.isTrue (c.IsDue(epoch + interval)) "the ordinary interval still wins"

        testCase "NotifyRateLimited honors a retryAfter hint LARGER than the ordinary interval" <| fun _ ->
            let c = ReplyCoalescer(clock, interval)
            let retryAfter = TimeSpan.FromSeconds 10.0
            c.Append "hello"
            c.NotifyRateLimited(epoch, ValueSome retryAfter)

            Expect.isFalse (c.IsDue(epoch + retryAfter - TimeSpan.FromMilliseconds 1.0)) "still inside the platform's own longer back-off"
            Expect.isTrue (c.IsDue(epoch + retryAfter)) "the platform's own hint wins once it exceeds the ordinary interval"

        testProperty "NotifyRateLimited's own gate is always at least `interval` out, regardless of the retryAfter hint" <|
            fun (retryAfterMillis: NonNegativeInt) ->
                let (NonNegativeInt millis) = retryAfterMillis
                let retryAfter = TimeSpan.FromMilliseconds(float (millis % 20_000))
                let c = ReplyCoalescer(clock, interval)
                c.Append "hello"
                c.NotifyRateLimited(epoch, ValueSome retryAfter)

                let boundary = epoch + (if retryAfter > interval then retryAfter else interval)
                not (c.IsDue(boundary - TimeSpan.FromMilliseconds 1.0)) && c.IsDue boundary

        // Every test above pins `interval` to the module-level 1.5s constant and checks IsDue/
        // MarkEmitted at hand-picked instants ("hello"/" world", specific offsets). This property
        // generalizes both across a WIDE FsCheck-generated range of coalescing intervals (1ms..10s
        // — covering intervals far smaller than the 1.5s default, not just that one fixed value)
        // and elapsed durations (0..20s, straddling both sides of the gate for most runs), so the
        // joint invariant itself — not just a handful of fixed instants — is what is under test.
        testProperty "IsDue/MarkEmitted jointly gate on (text changed since the last mark) AND (the coalescing interval has cleared), across arbitrary intervals and elapsed durations" <|
            fun (intervalMillisSeed: PositiveInt, delta1: string | null, delta2: string | null, elapsedMillisSeed: NonNegativeInt) ->
                let (PositiveInt rawIntervalMillis) = intervalMillisSeed
                let interval = TimeSpan.FromMilliseconds(float ((rawIntervalMillis % 10_000) + 1)) // 1ms..10s
                let (NonNegativeInt rawElapsedMillis) = elapsedMillisSeed
                let elapsed = TimeSpan.FromMilliseconds(float (rawElapsedMillis % 20_000)) // 0..~20s

                let c = ReplyCoalescer(clock, interval)
                c.Append(nonEmptyDelta delta1)
                c.MarkEmitted epoch
                let checkAt = epoch + elapsed

                // No further text arrives — skip-if-unchanged must hold no matter how much time
                // passes, regardless of how the gate itself is set.
                let staysFalseWhenUnchanged = not (c.IsDue checkAt)

                // `nonEmptyDelta delta2` is itself non-empty, so appending it always strictly
                // lengthens `RunningText` — the text is GUARANTEED to have changed from what
                // `MarkEmitted` last recorded.
                c.Append(nonEmptyDelta delta2)
                let actualDue = c.IsDue checkAt
                let expectedDue = elapsed >= interval

                staysFalseWhenUnchanged && actualDue = expectedDue
    ]
