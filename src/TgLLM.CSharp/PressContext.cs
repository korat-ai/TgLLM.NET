using System.Threading;
using System.Threading.Tasks;
using TgLLM.FSharp;

namespace TgLLM.CSharp;

/// <summary>
/// The context of a button press handed to a C# hook (T032, contracts/csharp-facade.md). Wraps the
/// core press context so the C# surface stays idiomatic — plain <c>string</c>/<c>long</c>, no F#
/// single-case types (Principle II).
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
    /// The bound tool argument (feature 002-llm-tool-router, FR-003), or <c>null</c> for a
    /// slice-1 closure-style hook or an argument-less tool button.
    /// </summary>
    public string? Arg => _core.Arg;

    /// <summary>
    /// Sets the ack directive for a tool button's deferred-ack path: the processor sends it via
    /// <c>answerCallbackQuery</c> exactly once, after this tool returns. Calling this from a
    /// slice-1 closure-style hook is a documented no-op — that path has already acked.
    /// </summary>
    public void Answer(string text, bool alert = false) => _core.Answer(text, alert);
}
