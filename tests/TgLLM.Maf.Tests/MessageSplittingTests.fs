/// Tests for `MessageSplitting.split`: totality on any `(maxLen, text)` pair (including `text` many
/// times longer than `maxLen`, which must LOOP rather than split once), the whitespace-preferred
/// boundary with a hard-cut fallback, and the two trivial cases (`""` and a short `text`).
module TgLLM.Maf.Tests.MessageSplittingTests

open System
open Expecto
open FsCheck
open TgLLM.Maf

/// Bounds an FsCheck-generated `PositiveInt` to a small, test-fast `maxLen` (1..40) — large enough
/// to host a real whitespace-preferred split window, small enough that "text many times longer than
/// maxLen" is cheap to generate and to loop over.
let private toSmallMaxLen (PositiveInt n) = (n % 40) + 1

/// Bounds an FsCheck-generated `PositiveInt` to a repeat count (5..24) — always at least 5x a
/// `toSmallMaxLen` result, so the generated text is guaranteed many times longer than `maxLen`.
let private toRepeatCount (PositiveInt n) = (n % 20) + 5

[<Tests>]
let messageSplittingTests =
    testList "MessageSplitting.split" [

        testProperty "every returned chunk is at most maxLen characters, for any (maxLen, text)" <| fun (maxLenSeed: PositiveInt, text: string | null) ->
            let maxLen = toSmallMaxLen maxLenSeed
            let text = text |> Option.ofObj |> Option.defaultValue ""
            MessageSplitting.split maxLen text |> List.forall (fun chunk -> chunk.Length <= maxLen)

        testProperty "every returned chunk is at most maxLen even when text is many times longer than maxLen" <| fun (maxLenSeed: PositiveInt, repeatSeed: PositiveInt) ->
            let maxLen = toSmallMaxLen maxLenSeed
            let text = String.replicate (toRepeatCount repeatSeed) "the quick brown fox jumps over "
            let chunks = MessageSplitting.split maxLen text
            chunks |> List.forall (fun chunk -> chunk.Length <= maxLen)

        testProperty "text many times longer than maxLen loops into more than one chunk (never a single oversized chunk)" <| fun (maxLenSeed: PositiveInt) ->
            let maxLen = toSmallMaxLen maxLenSeed
            let text = String.replicate (maxLen * 5) "x"
            let chunks = MessageSplitting.split maxLen text
            List.length chunks > 1

        testProperty "non-empty text no longer than maxLen returns exactly [text]" <| fun (maxLenSeed: PositiveInt, tailSeed: PositiveInt) ->
            let maxLen = toSmallMaxLen maxLenSeed
            let (PositiveInt tail) = tailSeed
            let text = String.replicate (max 1 (min tail maxLen)) "y" // guaranteed 1 <= length <= maxLen
            MessageSplitting.split maxLen text = [ text ]

        testCase "empty text returns an empty list" <| fun _ ->
            Expect.equal (MessageSplitting.split 10 "") [] "an empty input has nothing to split"

        testCase "a single-character text no longer than maxLen returns [text]" <| fun _ ->
            Expect.equal (MessageSplitting.split 10 "x") [ "x" ] "shorter than maxLen ⇒ unchanged, single chunk"

        testCase "text exactly maxLen long returns [text]" <| fun _ ->
            let text = String.replicate 10 "x"
            Expect.equal (MessageSplitting.split 10 text) [ text ] "exactly maxLen ⇒ no split needed"

        testCase "a long sentence with a space near the cap splits ON that space, not mid-word" <| fun _ ->
            // "0123456789 abc" — a space sits at index 10; maxLen 12 puts the search window
            // (indices 11..1) right over it, so the split should land there rather than mid "abc".
            let text = "0123456789 abcdefgh"
            let chunks = MessageSplitting.split 12 text

            match chunks with
            | first :: _ -> Expect.equal first "0123456789" "the first chunk stops at the space, trimmed"
            | [] -> failwith "expected at least one chunk"

        testCase "the kept chunk never ends with the whitespace it split on, and the next chunk never starts with it" <| fun _ ->
            let text = "aaaaaaaaaa bbbbbbbbbb"
            let chunks = MessageSplitting.split 12 text

            Expect.isTrue (chunks |> List.forall (fun c -> not (c.EndsWith " "))) "no chunk ends with a dangling space"
            Expect.isTrue (chunks |> List.forall (fun c -> not (c.StartsWith " "))) "no chunk starts with a leading space"

        testCase "one long unbroken token with no whitespace anywhere falls back to a hard cut at exactly maxLen" <| fun _ ->
            let text = String.replicate 25 "x"
            let chunks = MessageSplitting.split 10 text

            Expect.equal (List.length chunks) 3 "25 chars, hard-cut at 10, 10, 5"
            Expect.equal chunks [ String.replicate 10 "x"; String.replicate 10 "x"; String.replicate 5 "x" ] "each hard-cut chunk is exactly maxLen except the remainder"

        testCase "a hard cut never drops a character (no whitespace anywhere to trim)" <| fun _ ->
            let text = String.replicate 37 "z"
            let chunks = MessageSplitting.split 9 text
            Expect.equal (String.concat "" chunks) text "with nothing to trim, concatenation reconstructs the original text exactly"

        testCase "a hard cut whose boundary would land on a lone high surrogate cuts one character earlier instead, keeping the astral character intact" <| fun _ ->
            // U+1F600 (😀) is a single Unicode SCALAR VALUE but TWO UTF-16 code units (a surrogate
            // pair) — "abcd" (4 plain chars) + the emoji (2 units) is 6 chars long; maxLen = 5 puts
            // the naive cut boundary (index 4) exactly on the emoji's own HIGH surrogate, tearing its
            // LOW surrogate into the next chunk — no whitespace anywhere, so this falls straight into
            // the hard-cut branch under test, not the whitespace-preferred one.
            let emoji = "\U0001F600"
            let text = "abcd" + emoji
            let chunks = MessageSplitting.split 5 text

            Expect.equal chunks [ "abcd"; emoji ] "the cut lands one character EARLIER, keeping the whole surrogate pair together in the next chunk rather than splitting it"

            let hasLoneSurrogate (s: string) = s.EnumerateRunes() |> Seq.exists (fun r -> r.Value = 0xFFFD)
            Expect.isFalse (chunks |> List.exists hasLoneSurrogate) "neither chunk contains an unpaired surrogate — both round-trip as valid Unicode"

        testProperty "stripped of whitespace, the concatenated chunks preserve every non-whitespace character in order (a whitespace-preferred split only ever removes the whitespace run at that boundary)" <|
            fun (maxLenSeed: PositiveInt, text: string | null) ->
                let maxLen = toSmallMaxLen maxLenSeed
                let text = text |> Option.ofObj |> Option.defaultValue ""
                let chunks = MessageSplitting.split maxLen text
                let stripWhitespace (s: string) = String(s.ToCharArray() |> Array.filter (Char.IsWhiteSpace >> not))
                stripWhitespace (String.concat "" chunks) = stripWhitespace text

        testProperty "no returned chunk is ever empty, for any (maxLen, text)" <| fun (maxLenSeed: PositiveInt, text: string | null) ->
            let maxLen = toSmallMaxLen maxLenSeed
            let text = text |> Option.ofObj |> Option.defaultValue ""
            MessageSplitting.split maxLen text |> List.forall (fun chunk -> chunk.Length > 0)

        testCase "a whitespace-only run right before the found split boundary would trim the FIRST chunk to empty — it is skipped, not returned, and the real content still lands in the next chunk" <| fun _ ->
            // maxLen = 2, searchEnd = 1: the ONLY whitespace character within the search window
            // (index 1, the SECOND of two leading spaces) is itself preceded by nothing but MORE
            // whitespace — `Substring(0, 1).TrimEnd()` is "", the exact shape this test guards.
            let text = "  x"
            let chunks = MessageSplitting.split 2 text

            Expect.isTrue (chunks |> List.forall (fun c -> c.Length > 0)) "no chunk in the result is empty"
            Expect.equal (String.concat "" chunks) "x" "the real (non-whitespace) content still reaches the wire, just without a hollow leading chunk"
    ]
