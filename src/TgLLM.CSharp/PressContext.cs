using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TgLLM.FSharp;

namespace TgLLM.CSharp;

/// <summary>
/// The context of a button press handed to a C# hook. Wraps the core press context so the C#
/// surface stays idiomatic — plain <c>string</c>/<c>long</c>, no F# single-case types
/// (Principle II).
/// </summary>
public sealed class PressContext
{
    private readonly TgLLM.Core.PressContext _core;

    internal PressContext(TgLLM.Core.PressContext core) => _core = core;

    /// <summary>The visible label of the button that was tapped.</summary>
    public string ButtonLabel => CSharpSupport.buttonLabelText(_core);

    /// <summary>The chat the press came from.</summary>
    public long Chat => _core.Chat;

    /// <summary>The message the tapped keyboard belongs to.</summary>
    public long MessageId => _core.MessageId;

    /// <summary>The end user who tapped the button.</summary>
    public TgLLM.Core.EndUser User => _core.User;

    public CancellationToken CancellationToken => _core.CancellationToken;

    /// <summary>Reply in the chat; returns the sent message id.</summary>
    public Task<long> ReplyTextAsync(string text) => _core.ReplyTextAsync(text);

    /// <summary>
    /// The bound tool argument, or <c>null</c> for a slice-1 closure-style hook or an
    /// argument-less tool button.
    /// </summary>
    public string? Arg => _core.Arg;

    /// <summary>
    /// Deserializes the bound argument as <typeparamref name="T"/> (System.Text.Json). An
    /// argument-less press, or a payload that is not valid JSON for <typeparamref name="T"/>,
    /// throws rather than returning a default value — use <see cref="TryGetArg{T}"/> for a
    /// non-throwing variant.
    /// </summary>
    public T GetArg<T>()
    {
        if (_core.Arg is not { } json)
        {
            throw new InvalidOperationException("PressContext.GetArg: this press carries no argument.");
        }

        return JsonSerializer.Deserialize<T>(json)!;
    }

    /// <summary>
    /// The safe variant of <see cref="GetArg{T}"/>: returns <c>false</c> for an argument-less press
    /// or a payload that fails to deserialize as <typeparamref name="T"/>, never an exception.
    /// </summary>
    public bool TryGetArg<T>(out T value)
    {
        if (_core.Arg is { } json)
        {
            try
            {
                value = JsonSerializer.Deserialize<T>(json)!;
                return true;
            }
            catch (JsonException)
            {
                // falls through to the "no value" return below
            }
        }

        // The `out` parameter contract requires SOME value on the false path — the BCL's own
        // `TryParse`/`TryGetValue` pattern does the same (`default(T)` on failure).
        value = default!;
        return false;
    }

    /// <summary>
    /// Sets the ack directive for a tool button's deferred-ack path: the processor sends it via
    /// <c>answerCallbackQuery</c> exactly once, after this tool returns — or after a ~2s watchdog
    /// fires, whichever is first. A directive set AFTER the watchdog has already fired is silently
    /// dropped (the ack was already sent with no directive; the tool itself is never cancelled and
    /// keeps running to completion). Calling this from a slice-1 closure-style hook — that path has
    /// already acked, ack-first, before the hook ever runs — throws
    /// <see cref="InvalidOperationException"/>: fail-fast, not a silent no-op.
    /// </summary>
    public void Answer(string text, bool alert = false) => _core.Answer(text, alert);

    /// <summary>
    /// Edit the pressed message's text in place, leaving its current keyboard untouched.
    /// Reachable only from a Tool Router tool's deferred-ack path. Calling this from a slice-1
    /// closure-style hook throws <see cref="InvalidOperationException"/> (same fail-fast convention
    /// as <see cref="Answer"/>) — a plain hook should reply/send instead.
    /// </summary>
    public Task EditTextAsync(string text) => _core.EditTextAsync(text);

    /// <summary>
    /// Replace the pressed message's keyboard with one built from a fresh <see cref="KeyboardPlan"/>.
    /// Same fail-fast convention as <see cref="EditTextAsync"/> when called outside the Tool
    /// Router's deferred-ack path (throws <see cref="InvalidOperationException"/>, never a silent
    /// no-op).
    /// </summary>
    public Task EditKeyboardAsync(KeyboardPlan plan) => _core.EditKeyboardAsync(plan.Plan);
}
