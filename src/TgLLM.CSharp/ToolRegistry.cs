using System;
using System.Threading.Tasks;
using TgLLM.FSharp;

namespace TgLLM.CSharp;

/// <summary>
/// A fluent, mutable tool registry. Wraps the F# façade's <see cref="TgLLM.FSharp.ToolRegistry"/>
/// so <c>TelegramAgent</c> can wire it into <c>TgBotConfig.WithTools</c>, while registration
/// itself goes through the C#-friendly <see cref="ToolRegistrations"/> bridge (a
/// <see cref="Func{PressContext, Task}"/> delegate, never an F# curried function — Principle II).
/// </summary>
public sealed class ToolRegistry
{
    internal TgLLM.FSharp.ToolRegistry Inner { get; } = TgLLM.FSharp.ToolRegistry.create();

    /// <summary>Registers (or replaces) a tool under <paramref name="name"/>.</summary>
    public ToolRegistry Register(string name, Func<PressContext, Task> handler)
    {
        ToolRegistrations.Register(Inner.Registry, name, core => handler(new PressContext(core)));
        return this;
    }
}
