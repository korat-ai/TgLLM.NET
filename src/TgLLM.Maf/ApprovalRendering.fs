namespace TgLLM.Maf

open TgLLM.Core

/// Pure, formatter-overridable rendering of one pending approval into a Telegram message body +
/// button labels.
module ApprovalRendering =

    /// Zero-config default: `"Approval required: {tool}"` followed by one `"{name}: {value}"` line
    /// per argument, sent as plain text (no parse mode — an agent's tool arguments are untrusted
    /// for parse-safety, so no MarkdownV2 escaping obligation exists at all), labels fixed to
    /// "Approve"/"Reject". Total on an arbitrary `ApprovalPrompt` (including an empty `Tool` or
    /// argument list) and always non-empty: the `"Approval required: "` prefix alone guarantees a
    /// non-blank body regardless of what `Tool`/`Arguments` contain.
    let defaultRender (prompt: ApprovalPrompt) : ApprovalRender =
        let header = $"Approval required: {prompt.Tool}"

        let body =
            match prompt.Arguments with
            | [] -> header
            | args -> header + "\n" + (args |> List.map (fun (name, value) -> $"{name}: {value}") |> String.concat "\n")

        { Body = body
          ApproveLabel = "Approve"
          RejectLabel = "Reject" }

    /// Validates a render (the default's, or a host formatter's) before it ever reaches the wire —
    /// body through `MessageText.create`, both labels through `ButtonLabel.create`. Never throws:
    /// an invalid render surfaces as `Result.Error` so the caller can report it via
    /// `IMafObserver.OnInvalidOutput` rather than crashing the turn (a formatter is host code, but
    /// its output reaches the wire exactly like agent-authored content).
    let validate (render: ApprovalRender) : Result<ApprovalRender, MafError> =
        let describe (error: KeyboardError) : MafError =
            match error with
            | TextTooLong(length, max) -> ReplyTooLong(length, max)
            | EmptyKeyboard
            | EmptyRow _
            | EmptyLabel _ -> BodyInvalid $"%A{error}"

        match MessageText.create render.Body with
        | Error e -> Error(describe e)
        | Ok _ ->
            match ButtonLabel.create render.ApproveLabel with
            | Error e -> Error(describe e)
            | Ok _ ->
                match ButtonLabel.create render.RejectLabel with
                | Error e -> Error(describe e)
                | Ok _ -> Ok render
