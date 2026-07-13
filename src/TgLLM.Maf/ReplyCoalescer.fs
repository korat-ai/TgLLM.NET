namespace TgLLM.Maf

open System
open TgLLM.Core

/// Splits text that would exceed the platform's per-message length cap into a SEQUENCE of chunks
/// each `<= maxLen` — the leaf's own splitting rule for a reply that spans more than one live
/// message once it exceeds Telegram's `sendMessage`/`editMessageText` length limit.
module MessageSplitting =

    /// Splits `text` into chunks each `<= maxLen` characters, preferring to end a chunk at the LAST
    /// whitespace character (`Char.IsWhiteSpace` — covers both spaces and newlines, "the last
    /// whitespace/newline before the cap" is one rule, not two) at or before position `maxLen`,
    /// trimming the chosen boundary (the kept chunk's trailing whitespace, the remainder's leading
    /// whitespace) so a finalized message never ends on a dangling space and the next one never
    /// starts with one. Falls back to a HARD CUT at exactly `maxLen` when no whitespace exists in
    /// that window at all (one very long unbroken token). Total on any input: `text = ""` -> `[]`;
    /// `text.Length <= maxLen` -> `[text]`; every returned chunk satisfies `chunk.Length <= maxLen`
    /// by construction, including when `text` is many times longer than `maxLen` (loops, not a
    /// single split).
    let split (maxLen: int) (text: string) : string list =
        let rec loop (remaining: string) (acc: string list) : string list =
            if String.IsNullOrEmpty remaining then
                List.rev acc
            elif remaining.Length <= maxLen then
                List.rev (remaining :: acc)
            else
                let searchEnd = maxLen - 1

                let splitIndex =
                    seq { searchEnd .. -1 .. 1 } // never split AT index 0 — would leave an empty `keep`
                    |> Seq.tryFind (fun i -> Char.IsWhiteSpace remaining[i])

                match splitIndex with
                | Some i ->
                    let keep = remaining.Substring(0, i).TrimEnd()
                    let rest = remaining.Substring(i).TrimStart()

                    // The found boundary (index `i`) can itself be preceded by NOTHING but MORE
                    // whitespace (e.g. `remaining = "  x"`, `maxLen = 2` finds its only candidate at
                    // the SECOND leading space) — `keep`, once trimmed, is then empty. Skip emitting
                    // it rather than returning a hollow chunk a caller would try (and fail) to send
                    // as a message: `rest` is still strictly shorter than `remaining` (`i >= 1`), so
                    // the loop still makes progress and totality holds regardless.
                    if String.IsNullOrEmpty keep then
                        loop rest acc
                    else
                        loop rest (keep :: acc)
                | None ->
                    // A hard cut at exactly `maxLen` can itself land in the MIDDLE of a UTF-16
                    // surrogate pair (an astral character, e.g. most emoji, straddling the boundary)
                    // — `remaining[maxLen - 1]` being a HIGH surrogate means its own LOW surrogate is
                    // the very next char, at index `maxLen`, about to be torn into the NEXT chunk.
                    // Cutting one character earlier instead keeps the whole pair together in `rest`.
                    // Guarded to `maxLen > 1` so `cutAt` can never reach 0 (an empty `keep`, the SAME
                    // pathology `Some i`'s own guard above refuses) — at `maxLen <= 1` no cut can ever
                    // keep a 2-unit character intact regardless, so the ORIGINAL cut is kept rather
                    // than degrading into an empty chunk or a stuck loop.
                    let cutAt =
                        if maxLen > 1 && Char.IsHighSurrogate remaining[maxLen - 1] then
                            maxLen - 1
                        else
                            maxLen

                    let keep = remaining.Substring(0, cutAt)
                    let rest = remaining.Substring cutAt
                    loop rest (keep :: acc)

        loop text []

/// Buffers ONE message's own slice of a streaming turn's incrementally-arriving text and decides
/// WHEN a live-edit is due — coalesces many small updates into a safe Bot API edit cadence. A turn
/// that spills across more than one message constructs a FRESH `ReplyCoalescer` per message, seeded
/// with the overflow text, rather than one coalescer accumulating the whole turn. Purely reactive:
/// checked only when the turn's own update loop visits it (no independent background timer).
[<Sealed>]
type ReplyCoalescer(clock: Clock, interval: TimeSpan, ?seed: string) =
    let mutable running = defaultArg seed ""
    let mutable lastEmitted: string voption = ValueNone
    let mutable nextAllowedAt: DateTimeOffset voption = ValueNone

    do
        if interval <= TimeSpan.Zero then
            invalidArg (nameof interval) "reply coalescing interval must be positive"

    /// Appends one streamed delta (already a per-update delta, never cumulative) to this message's
    /// own running slice. A null/empty delta is a no-op — `AgentResponseUpdate.Text` is itself a
    /// nullable-string boundary this leaf never trusts blindly.
    member _.Append(delta: string | null) : unit =
        if not (String.IsNullOrEmpty delta) then
            running <- running + delta

    /// The slice accumulated so far for the CURRENT message.
    member _.RunningText: string = running

    /// Whether a NEW live-edit is due right now: the text changed since the last emit, AND either
    /// nothing has ever gated the next emit yet or `nextAllowedAt` has passed.
    member _.IsDue(now: DateTimeOffset) : bool =
        (lastEmitted <> ValueSome running)
        && (match nextAllowedAt with
            | ValueNone -> true
            | ValueSome gate -> now >= gate)

    /// Records that `RunningText` (as of now) was just sent/edited onto the wire — re-arms the gate
    /// for the ORDINARY cadence, so a later, unchanged `IsDue` check becomes false until new text
    /// arrives (skip-if-unchanged). NOT called after a failed send/edit (`NotifyRateLimited` below
    /// covers the 429 case specifically) so the SAME pending text is retried later, never dropped.
    member _.MarkEmitted(now: DateTimeOffset) : unit =
        lastEmitted <- ValueSome running
        nextAllowedAt <- ValueSome(now + interval)

    /// A 429 arrived for the pending edit — does NOT touch `lastEmitted` (the SAME pending text is
    /// retried, never dropped) but re-arms the gate FURTHER OUT than the ordinary cadence would,
    /// using the platform's own `Retry-After` hint when present (never LESS than the ordinary
    /// interval, so a tiny/zero hint can't defeat the coalescing cadence itself):
    /// `nextAllowedAt <- now + max interval retryAfter`. Never blocks — the NEXT arriving update's
    /// own `IsDue` check (or, at end of stream, a mandatory flush) is what actually retries.
    member _.NotifyRateLimited(now: DateTimeOffset, retryAfter: TimeSpan voption) : unit =
        let waitFor =
            match retryAfter with
            | ValueSome ra when ra > interval -> ra
            | _ -> interval

        nextAllowedAt <- ValueSome(now + waitFor)

module StreamingDefaults =
    /// A default coalescing interval safe against Telegram's edit rate limit without host tuning.
    let defaultCoalesceInterval: TimeSpan = TimeSpan.FromSeconds 1.5
