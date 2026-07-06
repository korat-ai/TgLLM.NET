using System;
using System.Threading.Tasks;
using TgLLM.CSharp;
using Xunit;

namespace TgLLM.CSharp.Tests;

/// <summary>C# façade behavior tests for the Tool Router surface.</summary>
public class ToolRegistryTests
{
    [Fact]
    public void Register_returns_the_same_registry_for_fluent_chaining()
    {
        var registry = new ToolRegistry();

        var chained = registry
            .Register("approve", _ => Task.CompletedTask)
            .Register("reject", _ => Task.CompletedTask);

        Assert.Same(registry, chained);
    }

    [Fact]
    public void Register_throws_ArgumentException_for_an_empty_tool_name()
    {
        var registry = new ToolRegistry();

        Assert.Throws<ArgumentException>(() => registry.Register("", _ => Task.CompletedTask));
    }

    [Fact]
    public void Register_throws_ArgumentException_for_a_whitespace_only_tool_name()
    {
        var registry = new ToolRegistry();

        Assert.Throws<ArgumentException>(() => registry.Register("   ", _ => Task.CompletedTask));
    }
}

public class PlanBuilderTests
{
    [Fact]
    public void Build_succeeds_for_a_valid_layout_mixing_tool_and_url_buttons()
    {
        var plan = new PlanBuilder()
            .Row(r => r
                .Tool("Approve", "approve", "42")
                .Tool("Reject", "reject"))
            .Row(r => r.Url("Docs", "https://example.test/docs"))
            .Build();

        Assert.NotNull(plan);
    }

    [Fact]
    public void Build_throws_TgKeyboardException_on_an_empty_plan()
    {
        Assert.Throws<TgKeyboardException>(() => new PlanBuilder().Build());
    }

    [Fact]
    public void Build_throws_TgKeyboardException_on_an_empty_row()
    {
        var builder = new PlanBuilder().Row(_ => { });

        Assert.Throws<TgKeyboardException>(() => builder.Build());
    }

    [Fact]
    public void Build_throws_TgKeyboardException_on_an_empty_button_label()
    {
        var builder = new PlanBuilder().Row(r => r.Tool("", "approve"));

        Assert.Throws<TgKeyboardException>(() => builder.Build());
    }
}

/// <summary>
/// Idiom-leak canary re-run over the EXTENDED surface (Principle II): the shared canary in
/// <see cref="IdiomLeakCanaryTests"/> already scans every exported type/member of the
/// <c>TgLLM.CSharp</c> assembly by reflection — no hardcoded type list — so it automatically covers
/// the new Tool Router types (<see cref="ToolRegistry"/>, <see cref="KeyboardPlan"/>,
/// <see cref="PlanBuilder"/>, <see cref="PlanRowBuilder"/>, the extended
/// <see cref="TelegramAgentOptions.Tools"/>/<see cref="PressContext.Arg"/>/
/// <see cref="PressContext.Answer"/>/<see cref="TelegramAgent.SendKeyboardPlanAsync"/> members) once
/// they exist. This test only pins down that those specific members are present and public, so a
/// future refactor that accidentally drops one fails loudly here rather than only via the broad scan.
/// </summary>
public class ToolRouterSurfaceTests
{
    [Fact]
    public void Tool_Router_types_are_part_of_the_public_TgLLM_CSharp_surface()
    {
        var assembly = typeof(TelegramAgent).Assembly;
        var exported = assembly.GetExportedTypes();

        Assert.Contains(typeof(ToolRegistry), exported);
        Assert.Contains(typeof(KeyboardPlan), exported);
        Assert.Contains(typeof(PlanBuilder), exported);
        Assert.Contains(typeof(PlanRowBuilder), exported);
    }

    [Fact]
    public void TelegramAgentOptions_exposes_a_public_Tools_property()
    {
        var property = typeof(TelegramAgentOptions).GetProperty(nameof(TelegramAgentOptions.Tools));

        Assert.NotNull(property);
        Assert.Equal(typeof(ToolRegistry), property!.PropertyType);
    }

    [Fact]
    public void PressContext_exposes_public_Arg_and_Answer_members()
    {
        var argProperty = typeof(PressContext).GetProperty(nameof(PressContext.Arg));
        var answerMethod = typeof(PressContext).GetMethod(nameof(PressContext.Answer));

        Assert.NotNull(argProperty);
        Assert.Equal(typeof(string), argProperty!.PropertyType);
        Assert.NotNull(answerMethod);
    }

    [Fact]
    public void TelegramAgent_exposes_a_public_SendKeyboardPlanAsync_method()
    {
        var method = typeof(TelegramAgent).GetMethod(nameof(TelegramAgent.SendKeyboardPlanAsync));

        Assert.NotNull(method);
    }
}
