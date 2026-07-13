using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Core;

namespace TgLLM.CSharp;

/// <summary>
/// Where an outbound A2UI <c>action</c> message goes — the host relays it to its agent over
/// whatever transport it uses. The library ships no agent-side A2UI transport.
/// </summary>
public delegate Task ActionSink(A2uiAction action);

/// <summary>The Telegram-representable catalog an <see cref="A2uiRenderer"/> advertises.</summary>
public sealed record Catalog(string CatalogId);

/// <summary>
/// One outbound A2UI <c>action</c> message: a Button tap's name, surface, source component, a
/// deterministic timestamp, and its resolved context (already stringified — a JSON string leaf
/// passes through as-is, any other JSON shape uses its JSON text, an unresolved path is empty).
/// </summary>
public sealed record A2uiAction(
    string Name,
    string SurfaceId,
    string SourceComponentId,
    DateTimeOffset Timestamp,
    IReadOnlyDictionary<string, string> Context,
    bool WantResponse,
    string? ActionId);

/// <summary>
/// The outcome of ingesting one A2UI message. <see cref="Error"/> is a plain, readable
/// description — never an F# <c>A2uiError</c> DU on this surface.
/// </summary>
public sealed record A2uiIngestResult
{
    /// <summary>Whether the message was accepted and (if renderable) carried out.</summary>
    public required bool Success { get; init; }

    /// <summary><c>null</c> on success; otherwise a human-readable description of the surfaced error.</summary>
    public string? Error { get; init; }

    internal static A2uiIngestResult Ok() => new() { Success = true };

    internal static A2uiIngestResult Failed(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// One A2UI-level condition an <see cref="A2uiRenderer"/> surfaces — an unknown catalog, a
/// duplicate/unknown surface, a malformed message, or a component outside <c>telegram-basic</c>.
/// Delivered to an <see cref="A2uiErrorObserver"/> independent of any single
/// <see cref="A2uiRenderer.IngestAsync"/> call's own <see cref="A2uiIngestResult"/>: an unsupported
/// component sitting next to supported siblings that still render, for instance, never appears in
/// that call's own result (the call itself succeeds), only here.
/// </summary>
/// <param name="Kind">
/// A stable tag for the condition (<c>"MalformedMessage"</c>/<c>"UnknownCatalog"</c>/
/// <c>"UnsupportedComponent"</c>/<c>"DuplicateSurface"</c>/<c>"UnknownSurface"</c>/
/// <c>"MalformedAction"</c>/<c>"StaleSurfaceAction"</c>) — for a host
/// that wants to branch on the condition without parsing <see cref="Description"/>.
/// </param>
/// <param name="Description">The same human-readable text <see cref="A2uiIngestResult.Error"/> uses.</param>
public sealed record A2uiErrorInfo(string Kind, string Description);

/// <summary>Receives every A2UI-level condition an <see cref="A2uiRenderer"/> surfaces.</summary>
public delegate void A2uiErrorObserver(A2uiErrorInfo error);

/// <summary>
/// Bridges a C#-supplied <see cref="A2uiErrorObserver"/> (or none) into the F# façade's
/// <c>IA2uiObserver</c> seam, translating the F# <c>A2uiError</c>/<c>ActionDescriptor</c> types at
/// this ONE boundary. Internal: its own members carry F# types dictated by the interface it
/// implements, so it must never be part of the public surface the idiom-leak canary walks — mirrors
/// <c>CSharpBindingStoreBridge</c>'s role for the binding-store seam.
/// </summary>
internal sealed class CSharpA2uiObserverBridge : TgLLM.A2UI.IA2uiObserver
{
    private readonly A2uiErrorObserver? _onError;

    public CSharpA2uiObserverBridge(A2uiErrorObserver? onError) => _onError = onError;

    public void OnA2uiError(TgLLM.A2UI.A2uiError error) =>
        _onError?.Invoke(new A2uiErrorInfo(
            TgLLM.FSharp.A2uiErrorBridge.kind(error),
            TgLLM.FSharp.A2uiErrorBridge.description(error)));

    public void OnMalformedAction(TgLLM.A2UI.ActionDescriptor descriptor) =>
        _onError?.Invoke(new A2uiErrorInfo(
            "MalformedAction",
            $"A2UI action '{descriptor.Name}' on surface '{descriptor.SurfaceId}' requested a response but has no actionId."));

    public void OnStaleSurfaceAction(TgLLM.A2UI.ActionDescriptor descriptor) =>
        _onError?.Invoke(new A2uiErrorInfo(
            "StaleSurfaceAction",
            $"A2UI action '{descriptor.Name}' targets surface '{descriptor.SurfaceId}', which is no longer tracked."));
}

/// <summary>
/// The C# public façade for the A2UI renderer: ingests A2UI agent-&gt;renderer messages for a
/// target chat and renders the <c>telegram-basic</c> subset as a Telegram message + inline
/// keyboard, reusing the same Tool Router a <see cref="TelegramAgent"/> already wires up.
/// </summary>
public sealed class A2uiRenderer
{
    private readonly TgLLM.FSharp.A2uiRenderer _inner;

    private A2uiRenderer(TgLLM.FSharp.A2uiRenderer inner) => _inner = inner;

    /// <summary>
    /// Attaches an A2UI renderer to <paramref name="agent"/>: registers the internal
    /// <c>a2ui-action</c> tool into the agent's OWN Tool Router, so tapping a server-bound Button
    /// routes through the hardened engine, resolves its context, and hands the result to
    /// <paramref name="sink"/>. Requires <paramref name="agent"/> to have been started with a Tool
    /// Router (<see cref="TelegramAgentOptions.Tools"/>) — without one, this throws
    /// <see cref="InvalidOperationException"/>, the same condition
    /// <see cref="TelegramAgent.SendKeyboardPlanAsync"/> itself already fails fast on for any tool
    /// button.
    /// </summary>
    /// <param name="agent">The running agent to attach to.</param>
    /// <param name="sink">Where outbound A2UI <c>action</c> messages go.</param>
    /// <param name="onError">
    /// Receives every A2UI-level condition this renderer surfaces (an unknown catalog, an
    /// unsupported component, a malformed message, a duplicate/unknown surface, or a malformed/
    /// stale tap-time action) — including ones
    /// that don't fail the triggering <see cref="IngestAsync"/> call outright, such as an
    /// unsupported component next to supported siblings that still render. Omitted (<c>null</c>,
    /// the default) means these conditions are surfaced only through each call's own
    /// <see cref="A2uiIngestResult"/>, not observed independently.
    /// </param>
    public static A2uiRenderer Create(TelegramAgent agent, ActionSink sink, A2uiErrorObserver? onError = null)
    {
        Task InvokeSink(TgLLM.A2UI.A2uiAction fsAction)
        {
            var context = new Dictionary<string, string>();

            foreach (var (key, value) in TgLLM.FSharp.A2uiActionBridge.contextEntries(fsAction))
            {
                context[key] = value;
            }

            var dto = new A2uiAction(
                fsAction.Name,
                fsAction.SurfaceId,
                fsAction.SourceComponentId,
                fsAction.Timestamp,
                context,
                fsAction.WantResponse,
                TgLLM.FSharp.A2uiActionBridge.actionId(fsAction));

            return sink(dto);
        }

        // `A2ui.renderer`'s `sink` parameter is an F# `ActionSink` (`FSharpFunc<A2uiAction, Task>`)
        // — a local C# function has no implicit conversion to that, unlike a BCL delegate; wrap it
        // explicitly, mirroring `TelegramAgent.ToFSharpClock`'s own `Func<...> -> FSharpFunc<...>`
        // adaptation.
        var fsharpSink = FSharpFunc<TgLLM.A2UI.A2uiAction, Task>.FromConverter(new Converter<TgLLM.A2UI.A2uiAction, Task>(InvokeSink));
        var observer = new CSharpA2uiObserverBridge(onError);
        var inner = TgLLM.FSharp.A2ui.rendererWithObserver(agent.Bot, fsharpSink, observer);
        return new A2uiRenderer(inner);
    }

    /// <summary>
    /// Ingests one A2UI agent-&gt;renderer message for <paramref name="chatId"/>. Never throws for
    /// a malformed message, an unknown catalog, an unsupported component, or a duplicate/unknown
    /// surface — those come back as a failed <see cref="A2uiIngestResult"/> instead.
    /// </summary>
    /// <param name="chatId">The target chat — A2UI carries no chat identity of its own.</param>
    /// <param name="a2uiMessageJson">One A2UI agent-&gt;renderer JSON envelope.</param>
    /// <param name="ct">Cancels the WAIT for this call to complete; see <see cref="TelegramAgent.SendKeyboardAsync"/>'s <c>ct</c>.</param>
    public async Task<A2uiIngestResult> IngestAsync(long chatId, string a2uiMessageJson, CancellationToken ct = default)
    {
        var result = await _inner.Ingest(chatId, a2uiMessageJson).WaitAsync(ct);

        return result.IsOk
            ? A2uiIngestResult.Ok()
            : A2uiIngestResult.Failed(TgLLM.A2UI.A2uiErrorModule.describe(result.ErrorValue));
    }

    /// <summary>The catalog this renderer advertises (telegram-basic).</summary>
    public Catalog Catalog => new(_inner.Catalog.CatalogId);
}
