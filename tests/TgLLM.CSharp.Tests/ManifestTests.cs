using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using TgLLM.CSharp;
using Xunit;

namespace TgLLM.CSharp.Tests;

/// <summary>
/// Structural tests for the C# façade's neutral tool manifest JSON. <see cref="ToolRegistry"/>
/// exposes only <see cref="ToolRegistry.ManifestJson"/> (a plain string) — never a structured
/// manifest type, since <c>TgLLM.Core.ToolManifestEntry</c>'s option-typed fields would leak
/// <c>FSharpOption</c> onto the C# surface.
/// </summary>
public class ManifestTests
{
    [Fact]
    public void ManifestJson_lists_every_registered_tool_as_a_neutral_name_description_parameters_object()
    {
        var registry = new ToolRegistry()
            .Register("approve", _ => Task.CompletedTask, description: "Approves a request", argSchema: "{\"type\":\"object\"}")
            .Register("reject", _ => Task.CompletedTask);

        var json = JsonNode.Parse(registry.ManifestJson())!.AsArray();

        Assert.Equal(2, json.Count);

        var approve = json.Single(e => e!["name"]!.GetValue<string>() == "approve")!;
        Assert.Equal("Approves a request", approve["description"]!.GetValue<string>());
        Assert.Equal("object", approve["parameters"]!["type"]!.GetValue<string>());

        var reject = json.Single(e => e!["name"]!.GetValue<string>() == "reject")!;
        Assert.Null(reject["description"]);
        Assert.Null(reject["parameters"]);
    }

    [Fact]
    public void ManifestJson_is_an_empty_array_for_a_registry_with_no_tools()
    {
        var registry = new ToolRegistry();
        Assert.Equal("[]", registry.ManifestJson());
    }

    [Fact]
    public void ToolRegistry_does_not_expose_a_structured_Manifest_method()
    {
        // Only ManifestJson() (a plain string) is public — a structured Manifest() would expose
        // TgLLM.Core.ToolManifestEntry's `string option` fields (FSharpOption) on the C# surface.
        var method = typeof(ToolRegistry).GetMethod("Manifest");
        Assert.Null(method);
    }
}
