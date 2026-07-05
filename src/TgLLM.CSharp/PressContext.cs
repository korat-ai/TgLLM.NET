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
}
