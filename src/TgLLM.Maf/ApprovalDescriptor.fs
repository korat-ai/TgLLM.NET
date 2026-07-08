namespace TgLLM.Maf

open System
open System.Text.Json
open System.Text.Json.Serialization
open FSharp.UMX
open TgLLM.Core

/// What a decision button (`maf-approve`/`maf-reject`) carries as its Tool Router STRUCTURED
/// ARGUMENT (`ToolBinding.Arg`, stored server-side in the binding store — never in Telegram's
/// 64-byte `callback_data`). A plain, flat DTO, deliberately `System.Text.Json`-friendly: readable
/// in a stale/failure report even after the process that created it is gone. The decision itself
/// (approve vs. reject) is NOT carried here — it is the NAME of the tapped tool.
///
/// Compiles before every other file in this leaf (including `Types.fs`): `IMafObserver`
/// (`Types.fs`) reports this type on its `OnStaleDecision`/`OnResumeFailed` members, so this type
/// must already exist by the time `Types.fs` compiles.
type ApprovalDescriptor =
    { /// `ChatId` untagged to a plain `int64` for the wire — retagged with UMX on parse
      /// (`ApprovalDescriptor.chat`).
      Chat: int64
      /// MAF's `ToolApprovalRequestContent.RequestId` — correlates a tap back to its pending
      /// request.
      RequestId: string
      /// The MAF tool's own name, carried only for a human-readable stale/failure report — the
      /// descriptor itself is not routing on it.
      Tool: string }

module ApprovalDescriptor =

    /// `JsonFSharpConverter` is required for a round-trip through an F# record: plain
    /// `System.Text.Json` cannot deserialize the compiler-generated shape (mirrors
    /// `TgLLM.FSharp.StructuredArgJson.options` and the A2UI leaf's own `descriptorJsonOptions` for
    /// `ActionDescriptor` — the exact same class of DTO).
    let private jsonOptions =
        let options = JsonSerializerOptions()
        options.Converters.Add(JsonFSharpConverter())
        options

    /// Builds a descriptor from a UMX-tagged `ChatId` — the one call site (`Bridge.fs`) that
    /// creates a descriptor from the bridge's own live state never has to untag `Chat` by hand.
    let make (chat: ChatId) (requestId: string) (tool: string) : ApprovalDescriptor =
        { Chat = UMX.untag chat
          RequestId = requestId
          Tool = tool }

    /// `Chat`, retagged as a `ChatId` — the counterpart to `make`.
    let chat (descriptor: ApprovalDescriptor) : ChatId = UMX.tag<chatId> descriptor.Chat

    let serialize (descriptor: ApprovalDescriptor) : string =
        JsonSerializer.Serialize(descriptor, jsonOptions)

    /// Total: `null`/malformed JSON, a missing field, or an empty/whitespace `RequestId`/`Tool` all
    /// yield `None` — never a throw inside a tool handler. `ToolApprovalRequestContent`'s own
    /// constructor already rejects an empty/whitespace `requestId` at creation time, but the string
    /// coming back off a button tap is untrusted input by the time it reaches here, so this
    /// re-validates rather than trusting the wire.
    let tryParse (raw: string | null) : ApprovalDescriptor option =
        match Option.ofObj raw with
        | None -> None
        | Some json ->
            try
                match JsonSerializer.Deserialize<ApprovalDescriptor>(json, jsonOptions) with
                | null -> None
                | descriptor when
                    not (String.IsNullOrWhiteSpace descriptor.RequestId) && not (String.IsNullOrWhiteSpace descriptor.Tool)
                    ->
                    Some descriptor
                | _ -> None
            with
            | :? JsonException -> None
            | :? NotSupportedException -> None
