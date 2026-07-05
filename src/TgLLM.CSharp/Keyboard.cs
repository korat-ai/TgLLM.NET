using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TgLLM.FSharp;

namespace TgLLM.CSharp;

/// <summary>Thrown by <see cref="KeyboardBuilder.Build"/> when a keyboard layout is invalid.</summary>
public sealed class TgKeyboardException : Exception
{
    public TgKeyboardException(string message) : base(message) { }
}

/// <summary>A validated interactive keyboard, ready to send. Opaque; built via <see cref="KeyboardBuilder"/>.</summary>
public sealed class Keyboard
{
    internal TgLLM.Core.KeyboardSpec Spec { get; }
    internal Keyboard(TgLLM.Core.KeyboardSpec spec) => Spec = spec;
}

/// <summary>Configures one row of buttons.</summary>
public sealed class RowBuilder
{
    internal List<(string Label, Func<PressContext, Task> Handler)> Buttons { get; } = new();

    /// <summary>Add a labelled button and the handler to run when it is tapped.</summary>
    public RowBuilder Button(string label, Func<PressContext, Task> handler)
    {
        Buttons.Add((label, handler));
        return this;
    }
}

/// <summary>
/// Fluent builder for an interactive keyboard (T032, contracts/csharp-facade.md). Mirrors the F#
/// façade's <c>Keyboard.create</c>, but throws <see cref="TgKeyboardException"/> on an invalid
/// layout (the C# idiom) instead of returning a result.
/// </summary>
public sealed class KeyboardBuilder
{
    private readonly List<RowBuilder> _rows = new();

    /// <summary>Add a row, configuring its buttons.</summary>
    public KeyboardBuilder Row(Action<RowBuilder> configure)
    {
        var row = new RowBuilder();
        configure(row);
        _rows.Add(row);
        return this;
    }

    /// <summary>Validate and build the keyboard. Throws <see cref="TgKeyboardException"/> if invalid.</summary>
    public Keyboard Build()
    {
        // Adapt each C# handler (Func<CSharp.PressContext, Task>) into a core hook
        // (Func<Core.PressContext, Task>) that wraps the core context in the C# one.
        var rows = new List<IReadOnlyList<(string, Func<TgLLM.Core.PressContext, Task>)>>();

        foreach (var row in _rows)
        {
            var coreRow = new List<(string, Func<TgLLM.Core.PressContext, Task>)>();

            foreach (var (label, handler) in row.Buttons)
            {
                Func<TgLLM.Core.PressContext, Task> coreHook = core => handler(new PressContext(core));
                coreRow.Add((label, coreHook));
            }

            rows.Add(coreRow);
        }

        var result = Keyboards.Build(rows);

        if (result.IsError)
        {
            throw new TgKeyboardException($"invalid keyboard: {result.ErrorValue}");
        }

        return new Keyboard(result.ResultValue);
    }
}
