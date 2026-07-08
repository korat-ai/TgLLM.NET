namespace TgLLM.Maf

open System.Text.Json
open Microsoft.Agents.AI
open Microsoft.Extensions.AI
open TgLLM.Core

/// Turns one agent turn's `AgentResponse` into the pending approval requests it raised, if any —
/// the "detect" step of the bridge's run-and-inspect loop, kept pure and total (no MAF call, no
/// IO): scan `response.Messages[*].Contents` for `ToolApprovalRequestContent`, matching Microsoft's
/// own tool-approval sample's `SelectMany(x => x.Contents).OfType<...>()` shape (there is no
/// `UserInputRequests` helper on `AgentResponse` in the resolved 1.13.0/10.6.0 binaries).
module ApprovalDetection =

    /// One pending request extracted from a response: the LIVE MAF request (needed later to build
    /// the resume content via `request.CreateResponse`) alongside the rendering-facing
    /// `ApprovalPrompt` a formatter works from.
    [<NoComparison; NoEquality>]
    type DetectedApproval =
        { Request: ToolApprovalRequestContent
          Prompt: ApprovalPrompt }

    /// One `"name: value"` line per argument, values JSON-rendered (`FunctionCallContent.Arguments`
    /// is `IDictionary<string, object?>` — both the value and the dictionary itself are
    /// nullable-annotated) — a plain string already passes through as its own (unquoted) text;
    /// anything else is serialized. Never throws: a value that fails to serialize falls back to its
    /// `ToString()`.
    let private renderArgumentValue (value: obj | null) : string =
        match value with
        | null -> "null"
        | :? string as s -> s
        | v ->
            try
                JsonSerializer.Serialize v
            with _ ->
                match v.ToString() with
                | null -> "null"
                | s -> s

    let private argumentLines (arguments: System.Collections.Generic.IDictionary<string, obj | null> | null) : (string * string) list =
        match arguments with
        | null -> []
        | args -> [ for kv in args -> kv.Key, renderArgumentValue kv.Value ]

    /// A pending request's tool name for reporting purposes — the SAME `FunctionCallContent.Name`
    /// (falling back to `ToolCall.CallId` for an unexpected `ToolCall` subtype) `detect` itself
    /// reads to build a `DetectedApproval.Prompt.Tool` for a FRESH detection. Exposed so a caller
    /// that only has an already-pending `ToolApprovalRequestContent` on hand (e.g. `Bridge.fs`
    /// reporting an ABANDONED sibling, which never goes through `detect` again) can build the SAME
    /// human-readable name without duplicating this match.
    let toolName (request: ToolApprovalRequestContent) : string =
        match request.ToolCall with
        | :? FunctionCallContent as call -> call.Name
        | other -> other.CallId

    /// Extracts every `ToolApprovalRequestContent` across a response's messages, in encounter
    /// order — total: a response with no pending approvals yields `[]`, never a throw. The
    /// request's own `ToolCall` is downcast to `FunctionCallContent` (the concrete subtype MAF's
    /// tool-approval loop produces); an unexpected `ToolCall` subtype still yields
    /// a usable prompt (its `CallId` stands in for a name, with no arguments) rather than crashing
    /// the turn on a future MAF release that adds one.
    let detect (chat: ChatId) (response: AgentResponse) : DetectedApproval list =
        [ for message in response.Messages do
              for content in message.Contents do
                  match content with
                  | :? ToolApprovalRequestContent as request ->
                      let args =
                          match request.ToolCall with
                          | :? FunctionCallContent as call -> argumentLines call.Arguments
                          | _ -> []

                      yield
                          { Request = request
                            Prompt = { Tool = toolName request; Arguments = args; Chat = chat } }
                  | _ -> () ]
