namespace TgLLM.Core

/// The outcome of routing an incoming button press (data-model.md "Inbound: a press — ack-first").
/// `Hook` is a function value, so this DU can't support structural equality/comparison.
[<NoComparison; NoEquality>]
type RouteDecision =
    | RunHook of Hook
    | AcknowledgeOnly

module Routing =

    /// Total over any `press`/`resolve` pair: a token the resolver knows about ⇒ `RunHook` with
    /// exactly that hook (FR-004, FR-008); anything else — unknown, stale, or malformed — ⇒
    /// `AcknowledgeOnly`, never an error (FR-010). The runtime policy (ack immediately, then run
    /// the hook if any) lives in `UpdateProcessor`, not here — this function only decides.
    let decide (resolve: CallbackToken -> Hook voption) (press: ButtonPress) : RouteDecision =
        match resolve press.Token with
        | ValueSome hook -> RunHook hook
        | ValueNone -> AcknowledgeOnly
