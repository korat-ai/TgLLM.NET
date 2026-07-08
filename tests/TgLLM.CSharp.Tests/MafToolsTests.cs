using Microsoft.Extensions.AI;
using TgLLM.Maf;
using Xunit;

namespace TgLLM.CSharp.Tests;

/// <summary>
/// C#-facing acceptance for <see cref="MafTools.Project"/> — the static, idiom-clean counterpart
/// to the F# <c>MafTools.project</c> (mirrors <see cref="MafIdiomLeakCanaryTests"/>'s split
/// between the leaf's F# and C# surfaces).
/// </summary>
public class MafToolsTests
{
    [Fact]
    public void Project_registers_a_well_formed_function_and_reports_no_problems()
    {
        var add = new System.Func<int, int, int>((a, b) => a + b);
        var addFn = AIFunctionFactory.Create(add, "add", "Adds two numbers");

        var registry = TgLLM.FSharp.ToolRegistry.create();
        var result = MafTools.Project(registry, new[] { addFn });

        Assert.Single(result.Registered);
        Assert.Equal("add", result.Registered[0]);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Project_surfaces_an_invalid_declared_name_without_registering_it()
    {
        var echo = new System.Func<string, string>(s => s);
        var bad = AIFunctionFactory.Create(echo, "   ", "desc");
        var good = AIFunctionFactory.Create(echo, "good_tool", "desc");

        var registry = TgLLM.FSharp.ToolRegistry.create();
        var result = MafTools.Project(registry, new[] { bad, good });

        Assert.Single(result.Registered);
        Assert.Equal("good_tool", result.Registered[0]);
        Assert.Single(result.Problems);
    }

    [Fact]
    public void ToolProjectionResult_exposes_plain_BCL_collections_no_F_sharp_idioms()
    {
        var registry = TgLLM.FSharp.ToolRegistry.create();
        var result = MafTools.Project(registry, System.Array.Empty<AIFunction>());

        Assert.IsAssignableFrom<System.Collections.Generic.IReadOnlyList<string>>(result.Registered);
        Assert.IsAssignableFrom<System.Collections.Generic.IReadOnlyList<string>>(result.Problems);
    }
}
