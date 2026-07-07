namespace TgLLM.CSharp;

/// <summary>
/// Idiomatic C# constructors for <see cref="TgLLM.Core.OwnerScope"/> (press authorization) — pass
/// the result to <see cref="TelegramAgent.SendKeyboardPlanAsync"/>'s <c>owner</c> parameter.
/// <see cref="TgLLM.Core.OwnerScope"/> itself is a plain Core type (fine to use directly on this
/// surface, like every other Core domain type — it is not an FSharp.Core idiom), so this class is
/// a small naming convenience, not a wrapper type.
/// </summary>
public static class Owner
{
    /// <summary>Any presser in the chat may tap the keyboard's tool buttons — unchanged behavior.</summary>
    public static TgLLM.Core.OwnerScope Anyone => TgLLM.Core.OwnerScope.Anyone;

    /// <summary>
    /// Only this Telegram user may tap the keyboard's tool buttons; every other (or unidentifiable)
    /// presser is refused with a notice.
    /// </summary>
    public static TgLLM.Core.OwnerScope User(long id) => TgLLM.Core.OwnerScope.NewUser(id);
}
