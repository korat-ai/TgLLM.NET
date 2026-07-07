namespace TgLLM.A2UI

open System.Text

/// Escapes arbitrary text for safe literal rendering as a Telegram MarkdownV2 message body, so
/// agent-produced text can never break parsing or inject formatting.
module Markdown =

    /// The 18 MarkdownV2 reserved characters (Bot API, "Formatting options" / MarkdownV2 style,
    /// core.telegram.org, verified 2026-07-07): "Characters '_', '*', '[', ']', '(', ')', '~',
    /// '`', '>', '#', '+', '-', '=', '|', '{', '}', '.', '!' must be escaped with the preceding
    /// character '\'."
    [<Literal>]
    let private ReservedChars = "_*[]()~`>#+-=|{}.!"

    let private isReserved (c: char) : bool = ReservedChars.IndexOf c >= 0

    /// Escapes every MarkdownV2 reserved character, plus a literal backslash. The same Bot API
    /// section also notes: "This implies that '\' character usually must be escaped with a
    /// preceding '\' character" — without that, a raw backslash already present in the input
    /// could recombine with this function's own escaping of a FOLLOWING reserved character (e.g.
    /// input `a\_b` would otherwise become `a\\_b`, which a MarkdownV2 parser reads as an escaped
    /// backslash followed by a BARE, unescaped underscore — reopening exactly the entity this
    /// function exists to neutralize).
    ///
    /// Total: a `null` input (the leaf boundary — Always-Rule 5) resolves to the empty string,
    /// never a throw. A string containing none of the escaped characters passes through
    /// unchanged.
    let escapeV2 (text: string | null) : string =
        match text with
        | null -> ""
        | t ->
            let sb = StringBuilder(t.Length)

            for c in t do
                if c = '\\' || isReserved c then
                    sb.Append('\\') |> ignore

                sb.Append(c) |> ignore

            sb.ToString()
