using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Core;

namespace TgLLM.CSharp;

/// <summary>
/// A plain-data snapshot of a tool binding for a C#-implemented store: nullable fields and no F#
/// option/union types anywhere in its own shape. <see cref="OwnerUserId"/> is
/// <c>null</c> for an "anyone" scope and the specific user id otherwise.
/// </summary>
public sealed class ToolBindingDto
{
    /// <summary>The opaque token the library assigned this binding's button.</summary>
    public required string Token { get; init; }

    /// <summary>The name of the tool this binding routes a press to.</summary>
    public required string ToolName { get; init; }

    /// <summary>The bound argument (plain string or serialized structured payload), if any.</summary>
    public string? Arg { get; init; }

    /// <summary><c>null</c> means "anyone may press this button"; otherwise the one user id who may.</summary>
    public long? OwnerUserId { get; init; }

    /// <summary>When this binding stops resolving, if it ever does.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Whether the first successful press consumes this binding.</summary>
    public bool SingleUse { get; init; }

    /// <summary>This binding's own override of the notice a refused non-owner presser sees, if any.</summary>
    public string? DeniedNotice { get; init; }
}

/// <summary>
/// A C#-idiomatic extension point for a custom binding store: nullable <see cref="ToolBindingDto"/>
/// and <see cref="ValueTask"/> throughout, so a C# host writing a store never has to construct an
/// <c>FSharpOption</c>/<c>FSharpValueOption</c> by hand — the raw F# <c>TgLLM.Core.IBindingStore</c>
/// seam's <c>TryGet</c> returns <c>ValueTask&lt;FSharpValueOption&lt;ToolBinding&gt;&gt;</c> and its
/// <c>ToolBinding.Arg</c> is <c>FSharpOption&lt;string&gt;</c>, exactly the idioms this interface
/// exists to keep off the C# surface. Bridge an implementation into the library via
/// <see cref="BindingStoreAdapter.ToCoreStore"/> and pass the result to
/// <see cref="TelegramAgentOptions.BindingStore"/>; the library's own built-in stores keep
/// implementing the F# seam directly — this interface is for a HOST writing its own store from C#.
/// </summary>
public interface IBindingStoreCSharp
{
    /// <summary>Persists every binding in <paramref name="bindings"/> (add-or-replace by token).</summary>
    ValueTask SaveAsync(IReadOnlyList<ToolBindingDto> bindings, CancellationToken ct);

    /// <summary>Looks up a binding by its token; <c>null</c> if none is stored for it.</summary>
    ValueTask<ToolBindingDto?> TryGetAsync(string token, CancellationToken ct);

    /// <summary>Removes every binding named by <paramref name="tokens"/> (a miss is a no-op).</summary>
    ValueTask RemoveAsync(IReadOnlyList<string> tokens, CancellationToken ct);

    /// <summary>Removes every binding whose expiry is at or before <paramref name="now"/>; returns the count removed.</summary>
    ValueTask<int> EvictExpiredAsync(DateTimeOffset now, CancellationToken ct);
}

/// <summary>Bridges a C#-implemented <see cref="IBindingStoreCSharp"/> into the library's F#-facing store seam.</summary>
public static class BindingStoreAdapter
{
    /// <summary>
    /// Wraps <paramref name="store"/> as a <c>TgLLM.Core.IBindingStore</c> the library's transports
    /// can use directly (e.g. via <see cref="TelegramAgentOptions.BindingStore"/>) — every F#
    /// option/voption value the underlying seam needs is constructed and unwrapped inside this
    /// bridge, never by <paramref name="store"/>'s own implementation.
    /// </summary>
    public static TgLLM.Core.IBindingStore ToCoreStore(IBindingStoreCSharp store) => new CSharpBindingStoreBridge(store);
}

/// <summary>
/// Implements the F# <c>TgLLM.Core.IBindingStore</c> seam by delegating to a C#-authored
/// <see cref="IBindingStoreCSharp"/>, translating every F# option/voption/union value at this ONE
/// boundary. Internal: this class's own members return F# idioms dictated by the interface it
/// implements, so it must never be part of the public surface the idiom-leak canary walks.
/// </summary>
internal sealed class CSharpBindingStoreBridge : TgLLM.Core.IBindingStore
{
    private readonly IBindingStoreCSharp _inner;

    public CSharpBindingStoreBridge(IBindingStoreCSharp inner) => _inner = inner;

    private static ToolBindingDto ToDto(TgLLM.Core.ToolBinding binding) =>
        new()
        {
            Token = TgLLM.Core.CallbackTokenModule.value(binding.Token),
            ToolName = TgLLM.Core.ToolNameModule.value(binding.ToolName),
            Arg = OptionModule.ToObj(binding.Arg),
            OwnerUserId = binding.Owner.IsUser ? binding.Owner.userId : null,
            ExpiresAt = OptionModule.ToNullable(binding.ExpiresAt),
            SingleUse = binding.SingleUse,
            DeniedNotice = OptionModule.ToObj(binding.DeniedNotice),
        };

    /// <summary>
    /// Reconstructs a <c>ToolBinding</c> from a DTO the host store returned; <c>null</c> if the
    /// token or tool name isn't a value this library could ever have written itself (a hand-edited
    /// or corrupted host-side record) — treated as a miss rather than a thrown exception, the same
    /// best-effort-load contract the library's own file store uses.
    /// </summary>
    private static TgLLM.Core.ToolBinding? ToDomain(ToolBindingDto dto)
    {
        var token = TgLLM.Core.CallbackTokenModule.tryParse(dto.Token);
        var toolName = TgLLM.Core.ToolNameModule.create(dto.ToolName);

        if (!token.IsValueSome || !toolName.IsOk)
        {
            return null;
        }

        var owner = dto.OwnerUserId.HasValue ? TgLLM.Core.OwnerScope.NewUser(dto.OwnerUserId.Value) : TgLLM.Core.OwnerScope.Anyone;

        // `OptionModule.OfObj` is null-safe (null -> None, non-null -> Some) regardless of what the
        // C# nullable-annotation checker infers for its generic result; the `!` below only silences
        // its overly strict `FSharpOption<string?>` vs. `FSharpOption<string>` inference, not an
        // actual null-safety gap.
        return new TgLLM.Core.ToolBinding(
            token.Value,
            toolName.ResultValue,
            OptionModule.OfObj(dto.Arg)!,
            owner,
            OptionModule.OfNullable(dto.ExpiresAt),
            dto.SingleUse,
            OptionModule.OfObj(dto.DeniedNotice)!);
    }

    public ValueTask Save(IReadOnlyList<TgLLM.Core.ToolBinding> bindings, CancellationToken ct) =>
        _inner.SaveAsync(bindings.Select(ToDto).ToList(), ct);

    public async ValueTask<FSharpValueOption<TgLLM.Core.ToolBinding>> TryGet(TgLLM.Core.CallbackToken token, CancellationToken ct)
    {
        var dto = await _inner.TryGetAsync(TgLLM.Core.CallbackTokenModule.value(token), ct);

        if (dto is null)
        {
            return FSharpValueOption<TgLLM.Core.ToolBinding>.None;
        }

        var domain = ToDomain(dto);

        return domain is null
            ? FSharpValueOption<TgLLM.Core.ToolBinding>.None
            : FSharpValueOption<TgLLM.Core.ToolBinding>.Some(domain);
    }

    public ValueTask Remove(IReadOnlyList<TgLLM.Core.CallbackToken> tokens, CancellationToken ct) =>
        _inner.RemoveAsync(tokens.Select(TgLLM.Core.CallbackTokenModule.value).ToList(), ct);

    public ValueTask<int> EvictExpired(DateTimeOffset now) => _inner.EvictExpiredAsync(now, CancellationToken.None);
}
