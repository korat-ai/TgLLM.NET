# Phase 1 Data Model: LLM Tool Router

**Feature**: `002-llm-tool-router` | **Date**: 2026-07-06

All types extend slice-1's `TgLLM.Core` (which stays IO-agnostic). Everything here is **additive** —
slice-1 types keep their shape (FR-012). Signatures are F# sketches; the C# façade re-expresses them.
Pure functions (plan → registered keyboard + bindings, tool resolution) are the FsCheck targets.

## New value types

### ToolName
- **Represents**: the name a tool is registered and referenced under.
- **Invariants**: non-empty after trim.
- **Construction**: `ToolName.create : string -> Result<ToolName, ToolError>` (smart constructor).

### Tool argument
- A tool button MAY carry an optional **string** argument (D4). Modeled as `string option`
  (F#) / `string?` (C#). Stored library-side, so not bound by the 64-byte `callback_data` limit.

### ToolError (new validation outcomes; keyboard structural errors reuse slice-1 `KeyboardError`)
```fsharp
type ToolError =
    | EmptyToolName
    | UnknownTool of name: string
    | InvalidUrl of value: string
```

## Neutral keyboard plan (D7)

The host-filled, LLM-agnostic plan. Named `ToolKeyboard` to avoid clashing with slice-1's
`KeyboardPlan` module.
```fsharp
type PlanButton =
    | ToolButton of label: string * toolName: string * arg: string option
    | UrlButton  of label: string * url: string

type ToolKeyboard = { Rows: PlanButton list list }   // >=1 row, each >=1 button (validated like slice 1)
```

## Registered keyboard (slice-1 type, extended — additive)

Slice-1 `RegisteredButton` was `{ Label; Token }`. It becomes a DU so a keyboard can mix callback and
URL buttons; the callback case is unchanged in meaning, so slice-1 mapping/tests still pass.
```fsharp
type RegisteredButton =
    | Callback of label: ButtonLabel * token: CallbackToken   // was the slice-1 shape
    | Url      of label: ButtonLabel * url: string
type RegisteredKeyboard = { Rows: RegisteredButton list list }
```

## Tool binding (new; serializable — enables persistence)

```fsharp
type ToolBinding =
    { Token: CallbackToken
      ToolName: ToolName
      Arg: string option }
```
Unlike slice-1's `HookBinding` (`Token → live Hook` closure, non-serializable), a `ToolBinding` is
plain data → persistable (D5).

## Pure kernel (FsCheck targets)

```fsharp
module ToolPlan =
    /// Assign tokens to TOOL buttons (URL buttons keep their url, get no token); produce the wire
    /// keyboard and the serializable bindings. PURE.
    /// Properties: shape/label preserved; one token+binding per tool button; URL buttons carry no
    /// binding; token count = tool-button count; distinct input tokens ⇒ distinct button tokens.
    val plan : tokens: seq<CallbackToken> -> ToolKeyboard -> Result<RegisteredKeyboard * ToolBinding list, ToolError>
```

## Ports (new; in `TgLLM.Core`, Task/ValueTask)

```fsharp
/// A tool: agent-supplied logic run when its button is pressed; the bound arg is on the context.
type Tool = PressContext -> Task

type IToolRegistry =
    abstract Register   : name: ToolName * tool: Tool -> unit      // add or replace
    abstract TryResolve : name: ToolName -> Tool voption

/// Serializable button→tool bindings. In-memory default in Core; file impl in TgLLM.Persistence.
type IBindingStore =
    abstract Save   : bindings: IReadOnlyList<ToolBinding> * ct: CancellationToken -> ValueTask
    abstract TryGet : token: CallbackToken * ct: CancellationToken -> ValueTask<ToolBinding voption>
    abstract Remove : tokens: IReadOnlyList<CallbackToken> * ct: CancellationToken -> ValueTask
```

## Extended `IBotApiClient` (additive)

```fsharp
    // existing: SendText, SendKeyboard, AnswerCallback(query, ct)
    abstract EditMessageText        : chat: ChatId * message: MessageId * text: MessageText * keyboard: RegisteredKeyboard option * ct: CancellationToken -> Task
    abstract EditMessageReplyMarkup : chat: ChatId * message: MessageId * keyboard: RegisteredKeyboard option * ct: CancellationToken -> Task
    abstract AnswerCallback         : query: CallbackQueryId * text: string option * showAlert: bool * ct: CancellationToken -> Task
```
The impl catches `ApiRequestException` for `"message to edit not found"` / `"message is not modified"`
and surfaces via the observer (D1). The no-arg `AnswerCallback(query, ct)` slice-1 signature is kept
(overload) so slice-1 code is untouched.

## Extended `PressContext` (additive, bilingual)

```fsharp
    // existing: ButtonLabel, Chat, User, MessageId, CancellationToken, ReplyTextAsync
    member Arg : string | null                       // the bound tool argument (null for closures/no-arg)
    member EditTextAsync     : text: string -> Task  // edit the pressed message's text (in place)
    member EditKeyboardAsync : keyboard: ToolKeyboard -> Task   // replace the pressed message's keyboard
    member Answer            : text: string * ?alert: bool -> unit  // set the ack directive (tool path)
```
`Answer` sets a directive read by the processor after the tool returns (D2); on the closure path the
ack already fired, so `Answer` is a documented no-op there.

## Tool dispatch (the deferred-ack tool path) + processor wiring (D6)

```fsharp
[<Sealed>]
type ToolDispatch(registry: IToolRegistry, store: IBindingStore) =
    /// token → binding (from store) → tool (from registry). None ⇒ not a tool press.
    member Resolve : token: CallbackToken * ct: CancellationToken -> ValueTask<(Tool * ToolBinding) voption>

// UpdateProcessor gains an OPTIONAL collaborator; slice-1 callers omit it (F# optional parameter).
type UpdateProcessor =
    new : source * store * api * dispatcher * observer * ?toolDispatch: ToolDispatch -> UpdateProcessor
```
Per-press flow: if `toolDispatch` resolves the token → **deferred-ack tool path** (run tool with
`Arg`/edit/answer available → send one `AnswerCallback` with the tool's directive, watchdog protects
SC-003); else → slice-1 `IHookStore` **ack-first** path, unchanged. Unknown on both → ack + observer.

## Entity relationships

```
ToolKeyboard 1──* PlanButton (ToolButton | UrlButton)
ToolPlan.plan : ToolKeyboard ──▶ RegisteredKeyboard + [ToolBinding]
ToolBinding *──1 IBindingStore            (Token ▶ {ToolName, Arg})
ToolName    *──1 IToolRegistry            (ToolName ▶ Tool)
press.Token ──(store)──▶ ToolBinding ──(registry)──▶ Tool ; PressContext carries Arg
```

## Validation rules → requirement traceability

| Rule | Requirement |
|------|-------------|
| Register/replace/resolve tools by name | FR-001 |
| Build+send keyboard from a plan, no per-button glue | FR-002, FR-013 |
| Tool button: name + optional string arg | FR-003 |
| Press → resolve by name → invoke with arg | FR-004 |
| Unknown/unregistered tool → ack, no run, surfaced | FR-005 |
| Edit pressed message in place | FR-006 |
| Optional toast/alert on ack (deferred-ack path) | FR-007 |
| Bindings serializable + durable (file store) | FR-008 |
| URL buttons alongside tool buttons | FR-009 |
| Both façades, both transports | FR-010 |
| No business tools shipped | FR-011 |
| Slice-1 hook API still works | FR-012 |
