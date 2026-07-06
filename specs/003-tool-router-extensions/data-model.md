# Data Model: Tool Router Extensions

Additive domain model for slice 003. Type sketches are indicative F# (the real signatures land via TDD).
Everything is additive on the slice-1/2 model; the binding record evolves **once**, read-compatible with
slice-2 records.

## Owner scope (US1)

```fsharp
/// Who may press a tool button. Default is Anyone (slice-1/2 behavior).
[<Struct>]
type OwnerScope =
    | Anyone
    | User of userId: UserId        // UMX: int64<userId>
```

- Attached to a keyboard at send time; written into each **tool** binding it produces.
- Compared to the callback query's `from` user at resolve time. `Anyone` → always allow.
  `User uid` → allow only that user; a different or absent (`from = null`, anonymous) presser → refuse.
- Applies to tool (callback) buttons only; client-side buttons carry no scope (research D4).

## Tool metadata & manifest (US2)

```fsharp
/// Optional, advisory metadata supplied at registration; used only to build the manifest.
type ToolMetadata =
    { Description: string option
      ArgSchema: string option }    // opaque JSON Schema text; the library does not parse/validate it

type ToolManifestEntry =
    { Name: string                  // the registered tool name
      Description: string option
      Parameters: string option }   // = ArgSchema, emitted under the neutral "parameters" key

type ToolManifest = { Tools: ToolManifestEntry list }
```

- `IToolRegistry` gains registration-with-metadata and `Manifest() : ToolManifest`.
- Emission serializes to neutral JSON `[{ "name", "description", "parameters" }]` (research D2); no
  vendor wrapper. Tools registered without metadata appear name-only (`description`/`parameters` = null).

## Button descriptors (US3)

```fsharp
/// Neutral plan button (façade-facing). Slice-2 had ToolButton | UrlButton.
type PlanButton =
    | ToolButton   of label: string * toolName: string * arg: string option   // arg = opaque payload (D3)
    | UrlButton    of label: string * url: string
    | WebAppButton of label: string * url: string      // https; launches a Mini App (private-chat oriented)
    | CopyTextButton of label: string * text: string   // 1..256 chars; copies to clipboard

/// Wire-facing button (post-validation, tokens assigned). Slice-2 had Callback | Url.
type RegisteredButton =
    | Callback of label: ButtonLabel * token: CallbackToken
    | Url      of label: ButtonLabel * url: string
    | WebApp   of label: ButtonLabel * url: string
    | CopyText of label: ButtonLabel * text: string
```

**Validation** (plan build, returns `Result<_, ToolError>` as slice 2):
- WebApp `url`: non-empty and **https** scheme → else `InvalidUrl`.
- CopyText `text`: length **1..256** → else a new `InvalidCopyText` (or reuse the label/empty errors).
- Only `ToolButton` is assigned a `CallbackToken` and produces a binding; the other three pass through
  as client-side buttons (no token, no binding) — same seam slice 2 used for `UrlButton`.

## Binding record — evolved once (US1/US2/US4 foundation)

```fsharp
/// Slice-2 was { Token; ToolName; Arg: string option }. Evolved additively:
type ToolBinding =
    { Token: CallbackToken
      ToolName: ToolName
      Arg: string option              // opaque, possibly-JSON payload (D3) — string stays valid
      Owner: OwnerScope               // NEW — default Anyone
      ExpiresAt: DateTimeOffset option// NEW — default None (never expires)
      SingleUse: bool }               // NEW — default false (confirm-once mode, D6)
```

- **Backward compatibility (FR-017)**: a slice-2 record (no `Owner`/`ExpiresAt`/`SingleUse`) loads with
  `Owner = Anyone`, `ExpiresAt = None`, `SingleUse = false`. The file store's JSON gains optional fields
  (absent ⇒ defaults); the SQLite store's columns are nullable with the same defaults.
- The record stays fully **serializable** (persistence unchanged in shape — just more fields).

## Resolution context (Core, IO-agnostic)

```fsharp
/// Injected so expiry/dedup stay deterministic and property-testable (research D5, D6).
type Clock = unit -> DateTimeOffset

/// Bounded, TTL'd set of recently processed callback-query ids (redelivery dedup, D6).
type ProcessedQueryTracker =
    member TryBegin: queryId: string -> bool   // false if already seen (drop); true = first time
```

Resolution order for a tool press becomes: dedup (`TryBegin`) → resolve binding → **expiry check**
(`clock`) → **owner check** (`from` vs `Owner`) → invoke tool (or ack-only + surface on any refusal).

## Binding store seam (US4)

```fsharp
/// Existing seam (slice 2), F#-idiomatic. Gains eviction.
type IBindingStore =
    // ... slice-2 members (Save / TryGet / Remove) ...
    abstract EvictExpired: now: DateTimeOffset -> ValueTask<int>   // NEW — returns count removed

/// NEW C#-facing adapter so a C# host writing a custom store never touches F# idioms (review #7).
/// Nullable ToolBinding? / plain DTOs instead of FSharpValueOption / FSharpOption.
type ICSharpBindingStore = ...   // in the C# façade; bridged to IBindingStore internally
```

- Implementations: `InMemoryBindingStore` (Core), `FileBindingStore` (`TgLLM.Persistence`),
  **`SqliteBindingStore`** (`TgLLM.Persistence.Sqlite`, NEW). All interchangeable; all read slice-2 data.
- `SqliteBindingStore` schema: one table `bindings(token PK, tool_name, arg, owner_user_id NULLABLE,
  expires_at NULLABLE, single_use)`; `EvictExpired` = `DELETE WHERE expires_at < now`.

## Dispatcher (US4)

- Per-chat channel/worker gains an **idle deadline**; a reclaim pass removes idle chats' resources
  without dropping or reordering in-flight presses (FR-012). In-flight work always drains first.

## Extended reaction surface (folded review findings)

- `PressContext` unchanged in shape from slice 2 (Arg/Answer/EditText*/EditKeyboard*), plus a typed
  `GetArg<'T>()` accessor at the façades (D3). Edit operations classify Telegram edit errors
  (`not modified` = success no-op; `not found` = soft, observed) instead of throwing (FR-015).
- `ToolKeyboardOps.deliver` reorders to remove-old-tokens **after** a successful send and compensates on
  send failure (review #4).
- Transports emit an **ack-only event** (query id, no token) for callback queries they drop as
  non-canonical, so every press is acked (review #8).
