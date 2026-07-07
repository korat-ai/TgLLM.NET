# Data Model: A2UI Renderer for Telegram

Domain model for slice 004, all inside the new `TgLLM.A2UI` leaf (Core stays A2UI-agnostic). Type
sketches are indicative F# (real signatures land via TDD). Reuses slice-1/2/3 types (`ChatId`,
`MessageId`, `ToolKeyboard`, `ToolPlan.plan`, the binding store, edit-in-place, `Clock`, `IHookObserver`).

## A2UI messages (parsed from JSON)

```fsharp
/// An agent→renderer A2UI message envelope (version "v1.0"). Parsed from JSON at the leaf boundary;
/// Core never sees these.
type A2uiMessage =
    | CreateSurface of surfaceId: string * catalogId: string * components: Component list * dataModel: JsonNode option
    | UpdateComponents of surfaceId: string * components: Component list
    | UpdateDataModel of surfaceId: string * path: string * value: JsonNode option   // value=None ⇒ delete
    | DeleteSurface of surfaceId: string

/// Result of parsing: a valid message, or a surfaced parse/validation error (never a throw to the host).
type A2uiParse = Result<A2uiMessage, A2uiError>

type A2uiError =
    | MalformedMessage of detail: string      // missing version/required field, bad JSON
    | UnknownCatalog of catalogId: string
    | UnsupportedComponent of componentType: string * id: string
    | DuplicateSurface of surfaceId: string   // second createSurface for a live id
    | UnknownSurface of surfaceId: string      // update/delete for a surface that isn't live
```

## Components (the `telegram-basic` catalog)

```fsharp
/// One node of the A2UI adjacency-list tree, narrowed to what telegram-basic renders. An input/rich
/// component parses as `Unsupported` (surfaced, never rendered).
type Component =
    { Id: string
      Node: ComponentNode }

and ComponentNode =
    | Text of value: DynString                         // Markdown body (resolved to text)
    | Button of label: DynString * action: ButtonAction
    | Row of children: string list                     // child ids — one keyboard row
    | Column of children: string list                  // child ids — stacked
    | Divider
    | Image of url: DynString
    | Unsupported of componentType: string             // anything outside telegram-basic

/// A dynamic string: a literal, or an absolute JSON-Pointer binding into the data model.
and DynString =
    | Literal of string
    | Bound of jsonPointer: string                     // "/user/name"; unresolved ⇒ empty string

and ButtonAction =
    | ServerEvent of name: string * context: (string * string) list * wantResponse: bool * actionId: string option
                                                        // context = (key, json-pointer) pairs, resolved at tap
    | LocalOpenUrl of url: string                       // client-side URL button, no callback
```

## Action descriptor & outbound action message

```fsharp
/// Carried as a Button's Tool Router STRUCTURED ARGUMENT (slice-2 payload), so a tap routes through the
/// hardened engine and survives a restart via the durable binding store.
type ActionDescriptor =
    { SurfaceId: string
      SourceComponentId: string
      Name: string
      Context: (string * string) list       // (key, json-pointer) — resolved at tap time
      WantResponse: bool
      ActionId: string option }

/// The outbound A2UI `action` message handed to the host sink (context resolved, timestamp stamped).
type A2uiAction =
    { Name: string
      SurfaceId: string
      SourceComponentId: string
      Timestamp: DateTimeOffset             // from the injected Clock, not ambient
      Context: (string * JsonNode) list     // resolved values
      WantResponse: bool
      ActionId: string option }

/// Host-provided: where outbound actions go (the host relays to its agent). No F# idioms on the C# side.
type ActionSink = A2uiAction -> Task
```

## The pure renderer

```fsharp
/// PURE: a resolved surface (components + data model) → the Telegram message content. Property-testable:
/// Row ⇒ one keyboard row, Column ⇒ stacked, Text ⇒ concatenated Markdown body, Button ⇒ callback/url,
/// Unsupported ⇒ recorded in `unsupported`, supported siblings intact.
type RenderedSurface =
    { Text: string                          // MarkdownV2-escaped body
      Keyboard: ToolKeyboard                 // reuses the slice-2/3 plan type (tool buttons carry the descriptor)
      Unsupported: (string * string) list }  // (componentType, id) surfaced, not dropped

module Renderer =
    val render: catalog: Catalog -> dataModel: JsonNode -> components: Component list -> Result<RenderedSurface, A2uiError>
```

- `render` resolves every `DynString` (Literal / Bound → data-model lookup, empty on miss), builds the
  MarkdownV2 body, and turns Buttons into `ToolKeyboard` entries — a `ServerEvent` button into a tool
  button whose structured arg is its `ActionDescriptor`, a `LocalOpenUrl` into a URL button.
- Unsupported components are collected in `Unsupported` and surfaced via the observer, never rendered.

## Surface registry (in-memory, per bot)

```fsharp
/// Coalesces the incoming stream per surface and tracks each live surface's Telegram identity + state.
/// Thread-safe (concurrent ingests across chats). In-memory only — a tap on a pre-restart surface still
/// routes (durable binding), but re-rendering a pre-restart surface on a later update is not restored.
type LiveSurface =
    { SurfaceId: string
      Chat: ChatId
      MessageId: MessageId option           // None until first render
      CatalogId: string
      Components: Map<string, Component>     // id → node (adjacency list)
      DataModel: JsonNode }

type SurfaceRegistry =
    member Apply: chat: ChatId * msg: A2uiMessage -> Result<RenderEffect, A2uiError>

/// What applying a message implies for Telegram — carried out by the façade over the reused transport.
and RenderEffect =
    | SendNew of chat: ChatId * RenderedSurface        // first render of a surface
    | EditExisting of MessageId * RenderedSurface      // update on a live surface (edit-in-place)
    | DeleteMessage of MessageId                        // deleteSurface
    | NoEffect                                          // buffered, not yet renderable (no root)
```

## Catalog

```fsharp
/// The renderer advertises only telegram-basic. `createSurface.catalogId` is matched against it.
type Catalog =
    { CatalogId: string                     // the telegram-basic catalog id/URL
      Supports: string -> bool }            // component type name → in telegram-basic?
```

## Reuse (unchanged slice-1/2/3 types)

- `ToolKeyboard` / `ToolPlan.plan` — build the keyboard; a tool button's structured arg is the
  `ActionDescriptor` (slice-2 payload).
- The internal **`a2ui-action` tool** — registered once; its handler reads the descriptor, resolves the
  context against the live surface's data model, builds the `A2uiAction`, and calls the `ActionSink`.
- The durable **binding store** (slices 2/3) — persists the button bindings (surface survives a restart
  for taps).
- **Edit-in-place** (`EditMessageText`/`EditMessageReplyMarkup`) + soft edit errors (slice 3) — carry out
  `EditExisting`; a vanished surface message is a soft failure.
- The injected **`Clock`** (slice 3) — stamps `A2uiAction.Timestamp`.
- **`IHookObserver`** (or a small A2UI observer) — surfaces `A2uiError` (unsupported/unknown/malformed).
