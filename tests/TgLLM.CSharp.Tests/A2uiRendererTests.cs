using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using TgLLM.CSharp;
using Xunit;
using FakeServerModule = TgLLM.Integration.Tests.FakeBotApiServer.FakeBotApiServerModule;

namespace TgLLM.CSharp.Tests;

/// <summary>
/// Acceptance test for the A2UI renderer driven entirely through the C# façade:
/// <see cref="TelegramAgent"/>, <see cref="A2uiRenderer"/>. Reuses the fake Bot API server the F#
/// integration suite already proves against real Telegram.Bot request/response shapes
/// (<c>FakeBotApiServer.fs</c>), rather than reimplementing an equivalent HTTP fake here.
/// </summary>
public class A2uiRendererTests
{
    private const string FakeToken = "123456789:TEST-fake-token";

    private const string RowSurfaceJson = """
        {
          "version": "v1.0",
          "createSurface": {
            "surfaceId": "cs-row-surface",
            "catalogId": "telegram-basic",
            "dataModel": { "title": "Deploy v2?" },
            "components": [
              { "id": "root", "component": "Column", "children": [ "t1", "row1" ] },
              { "id": "t1", "component": "Text", "text": { "path": "/title" } },
              { "id": "row1", "component": "Row", "children": [ "b1", "b2" ] },
              { "id": "b1", "component": "Button", "text": "Approve", "action": { "event": { "name": "approve" } } },
              { "id": "b2", "component": "Button", "text": "Reject", "action": { "event": { "name": "reject" } } }
            ]
          }
        }
        """;

    [Fact]
    public async Task Ingest_sends_one_message_with_the_resolved_body_and_one_keyboard_row()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeServerModule.start();
        var tools = new ToolRegistry();

        await using var agent = await TelegramAgent.StartPollingAsync(
            new TelegramAgentOptions { BotToken = FakeToken, BaseUrl = server.BaseUrl, Tools = tools },
            ct);

        var renderer = A2uiRenderer.Create(agent, _ => Task.CompletedTask);

        var result = await renderer.IngestAsync(5001L, RowSurfaceJson, ct);

        Assert.True(result.Success);
        Assert.Null(result.Error);

        var sendRequests = server.RequestsFor("sendMessage");
        var request = Assert.Single(sendRequests);

        var body = request.Body!.Value;
        Assert.Equal("Deploy v2?", body["text"]!.GetValue<string>());
        Assert.Equal("MarkdownV2", body["parse_mode"]!.GetValue<string>());

        var row0 = body["reply_markup"]!["inline_keyboard"]![0]!;
        Assert.Equal(2, row0.AsArray().Count);
        Assert.Equal("Approve", row0[0]!["text"]!.GetValue<string>());
        Assert.Equal("Reject", row0[1]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Create_without_a_Tool_Router_wired_into_the_agent_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeServerModule.start();

        await using var agent = await TelegramAgent.StartPollingAsync(
            new TelegramAgentOptions { BotToken = FakeToken, BaseUrl = server.BaseUrl },
            ct);

        Assert.Throws<InvalidOperationException>(() => A2uiRenderer.Create(agent, _ => Task.CompletedTask));
    }

    [Fact]
    public async Task IngestAsync_surfaces_a_malformed_message_as_a_failed_result_without_throwing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeServerModule.start();
        var tools = new ToolRegistry();

        await using var agent = await TelegramAgent.StartPollingAsync(
            new TelegramAgentOptions { BotToken = FakeToken, BaseUrl = server.BaseUrl, Tools = tools },
            ct);

        var renderer = A2uiRenderer.Create(agent, _ => Task.CompletedTask);

        var result = await renderer.IngestAsync(5002L, """{ "not": "a2ui" }""", ct);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Empty(server.RequestsFor("sendMessage"));
    }

    [Fact]
    public async Task Catalog_advertises_telegram_basic()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeServerModule.start();
        var tools = new ToolRegistry();

        await using var agent = await TelegramAgent.StartPollingAsync(
            new TelegramAgentOptions { BotToken = FakeToken, BaseUrl = server.BaseUrl, Tools = tools },
            ct);

        var renderer = A2uiRenderer.Create(agent, _ => Task.CompletedTask);

        Assert.Equal("telegram-basic", renderer.Catalog.CatalogId);
    }
}
