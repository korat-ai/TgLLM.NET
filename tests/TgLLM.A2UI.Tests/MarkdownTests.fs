/// Tests for `Markdown.escapeV2`: escapes arbitrary text for safe literal rendering as a
/// Telegram MarkdownV2 message body, so agent-produced text can never break parsing or inject
/// formatting.
///
/// Bot API vendor fact (core.telegram.org, "Formatting options" / MarkdownV2 style, verified
/// 2026-07-07): "Characters '_', '*', '[', ']', '(', ')', '~', '`', '>', '#', '+', '-', '=', '|',
/// '{', '}', '.', '!' must be escaped with the preceding character '\'." The same section also
/// notes: "This implies that '\' character usually must be escaped with a preceding '\'
/// character" — `escapeV2` escapes a literal backslash too, for exactly that reason (an
/// unescaped backslash in the input could otherwise recombine with the escaper's own output and
/// reopen an entity).
module TgLLM.A2UI.Tests.MarkdownTests

open Expecto
open FsCheck
open TgLLM.A2UI

/// The 18 MarkdownV2 reserved characters, quoted verbatim from the Bot API's own list.
let private reservedChars = [ '_'; '*'; '['; ']'; '('; ')'; '~'; '`'; '>'; '#'; '+'; '-'; '='; '|'; '{'; '}'; '.'; '!' ]

/// Everything `Markdown.escapeV2` is documented to escape: the 18 reserved characters plus a
/// literal backslash itself.
let private escapedChars = '\\' :: reservedChars

/// Test-only inverse of `Markdown.escapeV2`: `None` if it finds a BARE (unescaped) reserved
/// character/backslash, or a trailing lone backslash — so `tryDecode (escapeV2 s) = Some s`
/// simultaneously proves "no unescaped reserved character survives" AND "the original text is
/// always recoverable."
let private tryDecode (s: string) : string option =
    let sb = System.Text.StringBuilder()
    let mutable i = 0
    let mutable ok = true

    while ok && i < s.Length do
        let c = s[i]

        if c = '\\' then
            if i + 1 < s.Length then
                sb.Append(s[i + 1]) |> ignore
                i <- i + 2
            else
                ok <- false
        elif List.contains c escapedChars then
            ok <- false
        else
            sb.Append(c) |> ignore
            i <- i + 1

    if ok then Some(sb.ToString()) else None

[<Tests>]
let markdownTests =
    testList "Markdown.escapeV2" [

        testCase "a plain string with no reserved characters is unchanged" <| fun _ ->
            Expect.equal (Markdown.escapeV2 "hello world 123") "hello world 123" "nothing to escape"

        testCase "the empty string is unchanged" <| fun _ -> Expect.equal (Markdown.escapeV2 "") "" "nothing to escape"

        testList
            "every reserved character is individually escaped"
            [ for c in reservedChars ->
                  testCase $"'{c}'" <| fun _ -> Expect.equal (Markdown.escapeV2 (string c)) ($"\\{c}") $"'{c}' must be backslash-escaped" ]

        testCase "a literal backslash is itself escaped" <| fun _ -> Expect.equal (Markdown.escapeV2 "\\") "\\\\" "a bare backslash is escaped too"

        testCase "a mix of reserved and plain characters escapes only the reserved ones" <| fun _ ->
            Expect.equal (Markdown.escapeV2 "Deploy v2 to prod?") "Deploy v2 to prod?" "'?' is not reserved"

        testCase "a period-heavy sentence escapes every period" <| fun _ ->
            Expect.equal (Markdown.escapeV2 "v2.0.1") "v2\\.0\\.1" "every '.' is reserved"

        testCase "adjacent reserved characters are each escaped independently" <| fun _ ->
            Expect.equal (Markdown.escapeV2 "**bold**") "\\*\\*bold\\*\\*" "every '*' is reserved, regardless of adjacency"

        testCase "a raw backslash immediately before a reserved character round-trips exactly, never reinterpreted" <| fun _ ->
            // If the input's own backslash were left unescaped, a parser reading the escaper's
            // output left-to-right in escaped-pairs (backslash consumes exactly the next
            // character) would consume the input's backslash together with the FOLLOWING escaped
            // character's own prefix backslash, leaving the underscore itself bare and reopening
            // an entity. Escaping the input backslash too keeps every pair aligned: `tryDecode`
            // (which reads the same way a MarkdownV2 parser does) recovers the exact original.
            Expect.equal (tryDecode (Markdown.escapeV2 "a\\_b")) (Some "a\\_b") "round-trips to the exact original, never a different string"

        testProperty "escapeV2 output always decodes back to the original string (no unescaped reserved character survives)" <| fun (data: NonEmptyString) ->
            let (NonEmptyString s) = data
            tryDecode (Markdown.escapeV2 s) = Some s

        testProperty "escapeV2 never throws, for arbitrary input (including null)" <| fun (s: string) ->
            // FsCheck's `string` generator can hand a literal `null` through even though the
            // lambda parameter is statically annotated non-nullable (a compile-time-only check) —
            // exactly the runtime boundary `escapeV2` must stay total against (Always-Rule 5).
            try
                Markdown.escapeV2 s |> ignore
                true
            with _ ->
                false
    ]
