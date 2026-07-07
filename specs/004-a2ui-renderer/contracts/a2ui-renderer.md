# Public Surface Delta: A2UI Renderer for Telegram

The additive public API for slice 004, both façades. Slice-1/2/3 signatures are unchanged (FR-012);
everything below is new and lives in / is exposed through the `TgLLM.A2UI` leaf. Indicative signatures —
finalized via TDD.

## F# façade (`TgLLM.A2UI` + `TgLLM.FSharp`)

```fsharp
/// The outbound action sink the host provides — where a tap's A2UI `action` message goes (the host
/// relays it to its agent). A2UI carries no chat identity, so ingest takes the target chat.
type ActionSink = A2uiAction -> Task

module A2ui =
    /// Build a renderer over a running bot (long polling or webhook). Registers the internal
    /// `a2ui-action` tool into the bot's Tool Router, so a Button tap routes through the hardened engine.
    val renderer: bot: TgBot -> sink: ActionSink -> A2uiRenderer

type A2uiRenderer =
    /// Ingest one A2UI agent→renderer message for a target chat. Returns a surfaced `A2uiError`
    /// (never throws) for a malformed message / unknown catalog / unsupported component / duplicate or
    /// unknown surface. Carries out the implied send / edit-in-place / delete over the bot's transport.
    member Ingest: chat: ChatId * a2uiMessageJson: string -> Task<Result<unit, A2uiError>>

    /// The catalog this renderer advertises (telegram-basic).
    member Catalog: Catalog
```

- Wiring: `TgBot` is built as usual (with a durable binding store for restart-survivable taps);
  `A2ui.renderer bot sink` attaches the renderer; the host then calls `Ingest` as its agent streams A2UI.
- A `Button` tap flows: engine → `a2ui-action` tool → resolve context → build `A2uiAction` → `sink`.

## C# façade (`TgLLM.CSharp`)

```csharp
// Sink: a BCL delegate (no FSharpFunc on the surface)
public delegate Task ActionSink(A2uiAction action);

public sealed class A2uiRenderer
{
    public static A2uiRenderer Create(TelegramAgent agent, ActionSink sink);

    // Ingest one A2UI message for a chat. Surfaced errors are returned, not thrown (nullable/DTO — no F# idioms).
    public Task<A2uiIngestResult> IngestAsync(long chatId, string a2uiMessageJson, CancellationToken ct = default);

    public Catalog Catalog { get; }
}

// A2uiAction / A2uiIngestResult / Catalog are C#-idiomatic DTOs (no FSharpOption/FSharpValueOption/FSharpFunc).
public sealed record A2uiAction(
    string Name, string SurfaceId, string SourceComponentId,
    DateTimeOffset Timestamp, IReadOnlyDictionary<string, string> Context,
    bool WantResponse, string? ActionId);
```

## Behavioral contracts (cross-façade, both transports)

- **Render**: a `createSurface`+`updateComponents` (Text + Buttons in Rows/Columns) sends exactly one
  message whose text and inline-keyboard layout match the component tree.
- **Tap → action**: tapping a `ServerEvent` Button delivers an `A2uiAction` to the sink with the action
  name, surface id, source component id, and resolved context; a `LocalOpenUrl` Button opens its link
  client-side with no `action` emitted.
- **Update → edit-in-place**: `updateComponents`/`updateDataModel` on a live surface edits the SAME
  message; `deleteSurface` deletes it.
- **Coalescing**: a burst of messages for one surface produces one send then edits, not N messages.
- **Catalog / unsupported**: an unknown `catalogId`, a component outside telegram-basic, or a malformed
  message returns/surfaces an `A2uiError`; no corrupted render, bot keeps working.
- **Data binding**: a `Bound` Text/label resolves by absolute JSON-Pointer; an unresolved path renders
  empty.
- **Restart**: a tap on a pre-restart surface's Button still emits its `action` (durable binding);
  re-rendering a pre-restart surface on a later update is not restored (documented MVP limitation).
- **No idiom leak**: the C# surface exposes no `FSharpOption`/`FSharpValueOption`/`FSharpFunc` (canary).
- **Core untouched**: `TgLLM.Core` carries no A2UI dependency; slice-1/2/3 tests stay green.
