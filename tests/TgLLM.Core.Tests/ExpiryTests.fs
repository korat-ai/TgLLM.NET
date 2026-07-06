/// Tests for the expiry decision (research D5): `Expiry.isLive` is a pure function of `now` and an
/// optional expiry instant — no ambient `DateTimeOffset.Now` anywhere in Core; "now" is always fed
/// in by the caller (the future resolve/dispatch step injects it via `Clock`, US4, out of scope
/// here). `None` (no expiry) always lives; `Some exp` lives strictly BEFORE `exp` and is refused AT
/// and after `exp` — the boundary instant itself already counts as expired, not live (a deliberate
/// choice, not left implicit: "expires at 5:00" reads as "no longer valid from 5:00 onward").
module TgLLM.Core.Tests.ExpiryTests

open System
open Expecto
open FsCheck
open TgLLM.Core

[<Tests>]
let expiryTests =
    testList "Expiry.isLive" [

        testCase "no expiry (None) always lives" <| fun _ ->
            Expect.isTrue (Expiry.isLive DateTimeOffset.UtcNow None) "a binding with no ExpiresAt never expires"

        testCase "now strictly before the expiry instant lives" <| fun _ ->
            let expiresAt = DateTimeOffset.UtcNow.AddMinutes 5.0
            let now = expiresAt.AddMinutes -1.0
            Expect.isTrue (Expiry.isLive now (Some expiresAt)) "still before expiry ⇒ live"

        testCase "now strictly after the expiry instant is refused" <| fun _ ->
            let expiresAt = DateTimeOffset.UtcNow
            let now = expiresAt.AddMinutes 1.0
            Expect.isFalse (Expiry.isLive now (Some expiresAt)) "past expiry ⇒ refused, like an unknown tool"

        testCase "now EXACTLY at the expiry instant is refused (the boundary counts as expired)" <| fun _ ->
            let expiresAt = DateTimeOffset.UtcNow
            Expect.isFalse (Expiry.isLive expiresAt (Some expiresAt)) "the exact expiry instant itself already counts as expired"

        testProperty "a binding is live iff now is strictly before its expiry instant" <| fun (offsetSeconds: int) ->
            let baseline = DateTimeOffset.UnixEpoch
            let expiresAt = baseline.AddSeconds 1000.0
            let now = baseline.AddSeconds(1000.0 + float offsetSeconds)

            Expiry.isLive now (Some expiresAt) = (now < expiresAt)

        testProperty "no expiry always lives, for any now" <| fun (offsetSeconds: int) ->
            let now = DateTimeOffset.UnixEpoch.AddSeconds(float offsetSeconds)
            Expiry.isLive now None
    ]
