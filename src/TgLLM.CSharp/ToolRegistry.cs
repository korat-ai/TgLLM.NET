using System;
using System.Threading.Tasks;
using TgLLM.FSharp;

namespace TgLLM.CSharp;

/// <summary>
/// A fluent, mutable tool registry. Wraps the F# façade's <see cref="TgLLM.FSharp.ToolRegistry"/>
/// so <c>TelegramAgent</c> can wire it into <c>TgBotConfig.WithTools</c>, while registration
/// itself goes through the C#-friendly <see cref="ToolRegistrations"/> bridge (a
/// <see cref="Func{PressContext, Task}"/> delegate, never an F# curried function).
/// </summary>
public sealed class ToolRegistry
{
    internal TgLLM.FSharp.ToolRegistry Inner { get; } = TgLLM.FSharp.ToolRegistry.create();

    /// <summary>
    /// Registers (or replaces) a tool under <paramref name="name"/>. <paramref name="description"/>
    /// and <paramref name="argSchema"/> are advisory metadata: they only affect what
    /// <see cref="ManifestJson"/> reports for this name, never routing — a tool registered without
    /// either still registers and routes identically.
    /// </summary>
    public ToolRegistry Register(string name, Func<PressContext, Task> handler, string? description = null, string? argSchema = null)
    {
        // `description`/`argSchema` reach the F# optional parameters as `Some <value>` — a C# call
        // has no source-level "omitted" sugar, so passing `null` here arrives on the F# side as
        // `Some null`, not `None`. `ToolRegistrations.Register` normalizes that itself (a `null`
        // is treated the same as never having passed the argument at all); the `!` here only
        // silences a nullable-reference-type false positive on the implicit
        // `string -> FSharpOption<string>` conversion.
        ToolRegistrations.Register(Inner.Registry, name, core => handler(new PressContext(core)), description!, argSchema!);
        return this;
    }

    /// <summary>
    /// The registry's neutral wire JSON — <c>[{ name, description, parameters }]</c>, no
    /// vendor-specific wrapping — ready to feed to an LLM's function-calling API. This is the ONLY
    /// manifest accessor on the C# surface: a structured equivalent would expose
    /// <c>TgLLM.Core.ToolManifestEntry</c>'s <c>string option</c> fields (<c>FSharpOption</c>).
    /// </summary>
    public string ManifestJson() => Inner.ManifestJson();
}
