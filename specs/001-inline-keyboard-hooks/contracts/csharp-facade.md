# Contract: C# Public Façade (`TgLLM.CSharp` — NuGet package #2, authored in C#)

**Feature**: `001-inline-keyboard-hooks` | **Date**: 2026-07-04

Idiomatic C#: a fluent builder, `Func<,>` delegates, exceptions for invalid input, `Task`-returning
`Async` methods, `await using`. **No** `FSharpFunc`/`FSharpOption`/`voption` on the surface
(Principle II). A reflection-based canary test in `TgLLM.CSharp.Tests` fails the build if any F#-only
type appears on the public surface.

## Building a keyboard
```csharp
public sealed class KeyboardBuilder
{
    public KeyboardBuilder Row(Action<RowBuilder> configure);
    public Keyboard Build();          // throws TgKeyboardException on invalid layout
}

public sealed class RowBuilder
{
    public RowBuilder Button(string label, Func<PressContext, Task> handler);
}
```

## Agent lifecycle & sending
```csharp
public sealed class TelegramAgentOptions
{
    public required string BotToken { get; init; }
    // webhook: PublicUrl, SecretToken (used by StartWebhookAsync)
}

public sealed class TelegramAgent : IAsyncDisposable
{
    public static Task<TelegramAgent> StartPollingAsync(TelegramAgentOptions options, CancellationToken ct = default);
    public static Task<TelegramAgent> StartWebhookAsync(TelegramAgentOptions options, CancellationToken ct = default);
    public Task<long> SendKeyboardAsync(long chatId, string text, Keyboard keyboard, CancellationToken ct = default);
    public Task<long> SendTextAsync(long chatId, string text, CancellationToken ct = default);
}

public sealed class PressContext   // same bilingual Core type; C# sees Task + get-only props + string?
{
    public string ButtonLabel { get; }
    public long Chat { get; }
    public EndUser User { get; }
    public long MessageId { get; }
    public CancellationToken CancellationToken { get; }
    public Task<long> ReplyTextAsync(string text);
}
```

## Canonical usage (matches spec US1)
```csharp
using TgLLM.CSharp;

var keyboard = new KeyboardBuilder()
    .Row(r => r
        .Button("Yes", ctx => ctx.ReplyTextAsync("You picked Yes"))
        .Button("No",  ctx => ctx.ReplyTextAsync("You picked No")))
    .Build();   // throws TgKeyboardException if invalid

await using var agent = await TelegramAgent.StartPollingAsync(
    new TelegramAgentOptions { BotToken = botToken });   // swap for StartWebhookAsync — hooks unchanged
long messageId = await agent.SendKeyboardAsync(chatId, "Deploy?", keyboard, ct);
```

## Contract guarantees
- Switching `StartPollingAsync` ↔ `StartWebhookAsync` requires **no change** to handler bodies
  (FR-013, SC-008).
- `KeyboardBuilder.Build()` throws `TgKeyboardException` (carrying the underlying `KeyboardError`) on
  invalid layouts — the C# idiom, vs the F# façade's `Result`.
- Handlers are `Func<PressContext, Task>`; ids are `long`, optional strings are `string?`.
- **Leakage canary**: `TgLLM.CSharp.Tests` reflects over all public types and asserts no member
  signature references a type from `FSharp.Core` (no `FSharpFunc<,>`, `FSharpOption<>`,
  `FSharpValueOption<>`, single-case DU wrappers).
