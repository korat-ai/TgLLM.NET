using System.Collections.Generic;
using System.Linq;
using TgLLM.FSharp;

namespace TgLLM.CSharp;

/// <summary>
/// A validated Tool Router plan, ready to send. Same type as the F# façade's <c>ToolKeyboard</c>
/// — <c>KeyboardPlan</c> is the idiomatic C# name (mirrors slice-1's
/// <c>KeyboardSpec</c>/<see cref="Keyboard"/> naming split), not a drift.
/// </summary>
public sealed class KeyboardPlan
{
    internal TgLLM.Core.ToolKeyboard Plan { get; }
    internal KeyboardPlan(TgLLM.Core.ToolKeyboard plan) => Plan = plan;
}

/// <summary>Configures one row of a <see cref="KeyboardPlan"/>: tool buttons and/or URL buttons.</summary>
public sealed class PlanRowBuilder
{
    internal List<TgLLM.Core.PlanButton> Buttons { get; } = new();

    /// <summary>Add a tool button. <paramref name="arg"/> is the bound string argument, if any.</summary>
    public PlanRowBuilder Tool(string label, string toolName, string? arg = null)
    {
        Buttons.Add(arg is null ? Plan.tool(label, toolName) : Plan.toolWithArg(label, toolName, arg));
        return this;
    }

    /// <summary>
    /// Add a tool button carrying a bound structured payload: <paramref name="arg"/> is serialized
    /// (System.Text.Json) into the button's opaque argument slot, and comes back out via
    /// <see cref="PressContext.GetArg{T}"/>/<see cref="PressContext.TryGetArg{T}"/> when the button
    /// is pressed.
    /// </summary>
    public PlanRowBuilder Tool<T>(string label, string toolName, T arg)
    {
        Buttons.Add(Plan.toolWith<T>(label, toolName, arg));
        return this;
    }

    /// <summary>Add a URL button: opens client-side, invokes no tool.</summary>
    public PlanRowBuilder Url(string label, string url)
    {
        Buttons.Add(Plan.url(label, url));
        return this;
    }

    /// <summary>
    /// Add a WebApp button: launches a Mini App at <paramref name="url"/> in the user's client —
    /// opens client-side, invokes no tool. <paramref name="url"/> must be https; an invalid one is
    /// rejected with <see cref="TgKeyboardException"/> when the plan is built.
    /// </summary>
    public PlanRowBuilder WebApp(string label, string url)
    {
        Buttons.Add(Plan.webApp(label, url));
        return this;
    }

    /// <summary>
    /// Add a CopyText button: copies <paramref name="text"/> to the clipboard — opens client-side,
    /// invokes no tool. <paramref name="text"/> must be 1..256 characters; an out-of-range one is
    /// rejected with <see cref="TgKeyboardException"/> when the plan is built.
    /// </summary>
    public PlanRowBuilder CopyText(string label, string text)
    {
        Buttons.Add(Plan.copyText(label, text));
        return this;
    }
}

/// <summary>
/// Fluent builder for a <see cref="KeyboardPlan"/>. Mirrors the F# façade's <c>Plan.rows</c>, but
/// throws <see cref="TgKeyboardException"/> on an invalid plan (the C# idiom) instead of
/// returning a result.
/// </summary>
public sealed class PlanBuilder
{
    private readonly List<PlanRowBuilder> _rows = new();

    /// <summary>Add a row, configuring its buttons.</summary>
    public PlanBuilder Row(System.Action<PlanRowBuilder> configure)
    {
        var row = new PlanRowBuilder();
        configure(row);
        _rows.Add(row);
        return this;
    }

    /// <summary>Validate and build the plan. Throws <see cref="TgKeyboardException"/> if invalid.</summary>
    public KeyboardPlan Build()
    {
        var rows = _rows.Select(r => (IReadOnlyList<TgLLM.Core.PlanButton>)r.Buttons).ToList();
        var result = ToolPlans.BuildRows(rows);

        if (result.IsError)
        {
            throw new TgKeyboardException($"invalid plan: {result.ErrorValue}");
        }

        return new KeyboardPlan(result.ResultValue);
    }
}
