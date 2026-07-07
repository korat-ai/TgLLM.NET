using System.Collections.Generic;
using System.Threading.Tasks;
using TgLLM.CSharp;
using Xunit;
using FakeServerModule = TgLLM.Integration.Tests.FakeBotApiServer.FakeBotApiServerModule;

namespace TgLLM.CSharp.Tests;

/// <summary>
/// Acceptance for the A2UI catalog/unsupported-component observer, driven entirely through the C#
/// façade: <see cref="TelegramAgent"/>, <see cref="A2uiRenderer.Create"/>'s <c>onError</c> callback.
/// Mirrors <c>A2uiUnsupportedTests.fs</c>'s scenarios, proving the SAME observability is reachable
/// without touching any F# type directly.
/// </summary>
public class A2uiUnsupportedTests
{
    private const string FakeToken = "123456789:TEST-fake-token";

    private const string UnknownCatalogJson = """
        {
          "version": "v1.0",
          "createSurface": {
            "surfaceId": "cs-unknown-catalog",
            "catalogId": "some-rich-web-catalog",
            "components": [ { "id": "root", "component": "Text", "text": "unreachable" } ]
          }
        }
        """;

    private const string ButtonAndTextFieldJson = """
        {
          "version": "v1.0",
          "createSurface": {
            "surfaceId": "cs-button-and-textfield",
            "catalogId": "telegram-basic",
            "components": [
              { "id": "root", "component": "Column", "children": [ "t1", "b1", "tf1" ] },
              { "id": "t1", "component": "Text", "text": "Pick one:" },
              { "id": "b1", "component": "Button", "text": "Go", "action": { "event": { "name": "go" } } },
              { "id": "tf1", "component": "TextField", "text": "unrenderable" }
            ]
          }
        }
        """;

    [Fact]
    public async Task IngestAsync_reports_an_unknown_catalog_to_the_observer_and_sends_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeServerModule.start();

        await using var agent = await TelegramAgent.StartPollingAsync(
            new TelegramAgentOptions { BotToken = FakeToken, BaseUrl = server.BaseUrl, Tools = new ToolRegistry() },
            ct);

        var observed = new List<A2uiErrorInfo>();
        var renderer = A2uiRenderer.Create(agent, _ => Task.CompletedTask, observed.Add);

        var result = await renderer.IngestAsync(8001L, UnknownCatalogJson, ct);

        Assert.False(result.Success);
        var reported = Assert.Single(observed);
        Assert.Equal("UnknownCatalog", reported.Kind);
        Assert.Empty(server.RequestsFor("sendMessage"));
    }

    [Fact]
    public async Task IngestAsync_reports_an_unsupported_component_to_the_observer_while_supported_siblings_still_render()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeServerModule.start();

        await using var agent = await TelegramAgent.StartPollingAsync(
            new TelegramAgentOptions { BotToken = FakeToken, BaseUrl = server.BaseUrl, Tools = new ToolRegistry() },
            ct);

        var observed = new List<A2uiErrorInfo>();
        var renderer = A2uiRenderer.Create(agent, _ => Task.CompletedTask, observed.Add);

        var result = await renderer.IngestAsync(8002L, ButtonAndTextFieldJson, ct);

        Assert.True(result.Success);
        var reported = Assert.Single(observed);
        Assert.Equal("UnsupportedComponent", reported.Kind);

        var request = Assert.Single(server.RequestsFor("sendMessage"));
        var body = request.Body!.Value;
        Assert.Equal("Pick one:", body["text"]!.GetValue<string>());
        Assert.Single(body["reply_markup"]!["inline_keyboard"]!.AsArray());
    }

    [Fact]
    public async Task IngestAsync_never_throws_and_never_invokes_the_observer_when_onError_is_omitted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeServerModule.start();

        await using var agent = await TelegramAgent.StartPollingAsync(
            new TelegramAgentOptions { BotToken = FakeToken, BaseUrl = server.BaseUrl, Tools = new ToolRegistry() },
            ct);

        var renderer = A2uiRenderer.Create(agent, _ => Task.CompletedTask);

        var result = await renderer.IngestAsync(8003L, UnknownCatalogJson, ct);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }
}
