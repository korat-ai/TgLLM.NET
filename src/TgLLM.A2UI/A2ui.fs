namespace TgLLM.A2UI

/// The A2UI renderer: parses A2UI agent→renderer messages, renders the telegram-basic catalog into the
/// Tool Router's neutral keyboard plan, tracks live surfaces, and builds outbound A2UI action messages.
/// Depends only on Core (façade-agnostic, no transport). The real model/parser/renderer land with the
/// foundational work; this placeholder keeps the leaf compiling until then.
module internal Placeholder =
    let internal reserved = ()
