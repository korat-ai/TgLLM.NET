# Public Surface Delta: Tool Router Extensions

The additive public API for slice 003, both façades. Slice-1/2 signatures are unchanged (FR-019); every
entry point below is new or an overload. Indicative signatures — finalized via TDD.

## F# façade (`TgLLM.FSharp`)

### Owner-scoped send (US1)

```fsharp
// Slice-2: TgBot.SendKeyboardPlan(chat, text, plan)
// New: an optional owner scope. Default (omitted) = Anyone (unchanged behavior).
member SendKeyboardPlan: chat: ChatId * text: string * plan: ToolKeyboard * ?owner: OwnerScope
                         * ?deniedNotice: string -> Task<MessageId>   // shown to a non-owner presser

module Owner =
    val anyone: OwnerScope
    val user: int64 -> OwnerScope          // by user id
```

### Plan builders — new button types & structured args (US2/US3)

```fsharp
module Plan =
    // slice-2: tool, toolWithArg, url
    val webApp:   label: string -> url: string  -> PlanButton       // https, validated at build
    val copyText: label: string -> text: string -> PlanButton       // 1..256 chars
    val toolWith: label: string -> toolName: string -> arg: 'T -> PlanButton   // 'T serialized (STJ) → payload
```

### Tool registration with metadata & manifest (US2)

```fsharp
type ToolRegistry with
    member Register: name: string * handler: (PressContext -> Task<'a>)
                     * ?description: string * ?argSchema: string -> ToolRegistry
    member Manifest: unit -> ToolManifest
    member ManifestJson: unit -> string      // neutral [{name,description,parameters}] JSON
```

### Typed argument accessor (US2)

```fsharp
type PressContext with
    member GetArg: unit -> 'T                 // deserialize the payload (STJ); throws on shape mismatch
    member TryGetArg: unit -> 'T option       // safe variant
    // slice-2 .Arg (raw string | null) remains
```

### Expiry / single-use / durable stores (US4)

```fsharp
module Plan =
    val expiring:  TimeSpan  -> PlanButton -> PlanButton     // decorate a tool button with a TTL
    val singleUse: PlanButton -> PlanButton                  // confirm-once

// New store; same IBindingStore seam as FileBindingStore.
type LiteDbBindingStore =
    static member OpenAt: path: string -> LiteDbBindingStore  // in TgLLM.Persistence.LiteDb

// Config gains an idle-eviction knob (optional; sensible default).
type TgBotConfig with
    member WithIdleChatEviction: TimeSpan -> TgBotConfig
```

## C# façade (`TgLLM.CSharp`)

```csharp
// US1 — owner scope on send
Task<int> SendKeyboardPlanAsync(long chatId, string text, KeyboardPlan plan,
                                OwnerScope? owner = null, string? deniedNotice = null,
                                CancellationToken ct = default);      // deniedNotice → non-owner; ct honored (review #6)
public static class Owner { public static OwnerScope Anyone {get;} public static OwnerScope User(long id); }

// US2/US3 — plan builder additions
PlanRowBuilder Tool<T>(string label, string toolName, T arg);   // structured payload
PlanRowBuilder WebApp(string label, string url);
PlanRowBuilder CopyText(string label, string text);

// US2 — registration + manifest
ToolRegistry Register(string name, Func<PressContext, Task> handler,
                      string? description = null, string? argSchema = null);
ToolManifest Manifest();
string ManifestJson();

// US2 — typed arg on the C# PressContext
T GetArg<T>();
bool TryGetArg<T>(out T value);

// US4 — durable store + C#-facing store adapter (review #7: no FSharpOption on the surface)
public sealed class LiteDbBindingStore { public static LiteDbBindingStore OpenAt(string path); }
public interface IBindingStoreCSharp {                    // nullable / DTO, no F# idioms
    ValueTask<ToolBindingDto?> TryGetAsync(string token);
    ValueTask SaveAsync(ToolBindingDto binding);
    ValueTask<int> EvictExpiredAsync(DateTimeOffset now);
}
```

## Behavioral contracts (cross-façade, both transports)

- **Owner refusal**: a non-owner (or anonymous) press of an owner-scoped tool button is acked with the
  notice (default or configured), invokes no tool, and is surfaced via `IHookObserver`.
- **Manifest neutrality**: `ManifestJson()` contains every registered tool as `{name, description,
  parameters}` with no vendor wrapper; metadata-less tools appear name-only.
- **Payload round-trip**: `Plan.toolWith x` then `ctx.GetArg<T>()` returns a value equal to `x`; a raw
  slice-2 string arg is still readable via `.Arg`.
- **New buttons are client-side**: WebApp/CopyText presses reach no tool and produce no callback event.
- **Expiry/single-use/dedup**: an expired or already-consumed single-use binding, and a redelivered
  callback query id, all resolve as no-invocation + ack (not a crash).
- **Store interchangeability**: the in-memory, file, and LiteDB stores pass the same restart-persistence
  acceptance; all read slice-2 records.
- **Idempotent edits**: `not modified` is a silent success; `not found` is a soft, observed failure —
  never an exception to the tool author.
