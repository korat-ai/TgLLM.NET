using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Collections;
using TgLLM.CSharp;
using Xunit;
using FakeServerModule = TgLLM.Integration.Tests.FakeBotApiServer.FakeBotApiServerModule;
using TelegramJson = TgLLM.Integration.Tests.FakeBotApiServer.TelegramJson;

namespace TgLLM.CSharp.Tests;

/// <summary>
/// Acceptance for the full A2UI tap → action → re-render loop, driven entirely through the C#
/// façade: <see cref="TelegramAgent"/>, <see cref="A2uiRenderer"/>. Reuses the fake Bot API server
/// the F# integration suite already proves against real Telegram.Bot request/response shapes
/// (<c>FakeBotApiServer.fs</c>) — mirrors <c>A2uiLoopTests.fs</c>'s scenario, proving the SAME
/// behavior is reachable without touching any F# type directly.
/// </summary>
public class A2uiLoopTests
{
    private const string FakeToken = "123456789:TEST-fake-token";
    private const string SurfaceId = "cs-loop-surface";

    private const string CreateSurfaceJson = """
        {
          "version": "v1.0",
          "createSurface": {
            "surfaceId": "cs-loop-surface",
            "catalogId": "telegram-basic",
            "dataModel": { "env": "prod" },
            "components": [
              { "id": "root", "component": "Column", "children": [ "t1", "row1" ] },
              { "id": "t1", "component": "Text", "text": "Deploy?" },
              { "id": "row1", "component": "Row", "children": [ "b1", "b2" ] },
              { "id": "b1", "component": "Button", "text": "Approve",
                "action": { "event": { "name": "approve", "context": { "env": { "path": "/env" } }, "wantResponse": true, "actionId": "a1" } } },
              { "id": "b2", "component": "Button", "text": "Docs",
                "action": { "functionCall": { "call": "openUrl", "args": { "url": "https://example.com/docs" } } } }
            ]
          }
        }
        """;

    private const string UpdateComponentsJson = """
        {
          "version": "v1.0",
          "updateComponents": {
            "surfaceId": "cs-loop-surface",
            "components": [
              { "id": "root", "component": "Column", "children": [ "t1", "b3" ] },
              { "id": "t1", "component": "Text", "text": "Deployed" },
              { "id": "b3", "component": "Button", "text": "Rollback", "action": { "event": { "name": "rollback" } } }
            ]
          }
        }
        """;

    private const string DeleteSurfaceJson = """{ "version": "v1.0", "deleteSurface": { "surfaceId": "cs-loop-surface" } }""";

    private const string MalformedActionSurfaceJson = """
        {
          "version": "v1.0",
          "createSurface": {
            "surfaceId": "cs-malformed-action",
            "catalogId": "telegram-basic",
            "components": [
              { "id": "root", "component": "Column", "children": [ "t1", "b1" ] },
              { "id": "t1", "component": "Text", "text": "Confirm?" },
              { "id": "b1", "component": "Button", "text": "Confirm",
                "action": { "event": { "name": "confirm", "wantResponse": true } } }
            ]
          }
        }
        """;

    private static FSharpList<string> ListOf(params string[] items) => ListModule.OfArray(items);

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (!predicate())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("timed out waiting for the expected condition");
            }

            await Task.Delay(10, ct);
        }
    }

    [Fact]
    public async Task Render_tap_action_then_agent_update_edits_the_same_message_then_delete_removes_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeServerModule.start();
        const long chatId = 7001L;

        var actionReceived = new TaskCompletionSource<A2uiAction>();

        await using var agent = await TelegramAgent.StartPollingAsync(
            new TelegramAgentOptions { BotToken = FakeToken, BaseUrl = server.BaseUrl, Tools = new ToolRegistry() },
            ct);

        var renderer = A2uiRenderer.Create(agent, action =>
        {
            actionReceived.TrySetResult(action);
            return Task.CompletedTask;
        });

        var createResult = await renderer.IngestAsync(chatId, CreateSurfaceJson, ct);
        Assert.True(createResult.Success);
        Assert.Null(createResult.Error);

        var sendRequests = server.RequestsFor("sendMessage");
        var sentBody = Assert.Single(sendRequests).Body!.Value;
        var row0 = sentBody["reply_markup"]!["inline_keyboard"]![0]!;
        var approveToken = row0[0]!["callback_data"]!.GetValue<string>();

        Assert.Null(row0[0]!["url"]);
        Assert.Equal("https://example.com/docs", row0[1]!["url"]!.GetValue<string>());
        Assert.Null(row0[1]!["callback_data"]);

        // `FakeBotApiServer` assigns `message_id` sequentially PER CHAT starting at 1 — this is the
        // first (and, until the edit below, only) message ever sent to this test's own chat.
        const long messageId = 1L;

        server.EnqueueResult(
            "getUpdates",
            TelegramJson.batch(ListOf(TelegramJson.callbackQueryUpdate(1, "q-cs-loop", approveToken, chatId, (int)messageId, 9101L, "Cs"))));

        await WaitUntilAsync(() => actionReceived.Task.IsCompleted, 5000, ct);
        var action = await actionReceived.Task;

        Assert.Equal("approve", action.Name);
        Assert.Equal(SurfaceId, action.SurfaceId);
        Assert.Equal("b1", action.SourceComponentId);
        Assert.True(action.WantResponse);
        Assert.Equal("a1", action.ActionId);
        Assert.Equal("prod", action.Context["env"]);

        var updateResult = await renderer.IngestAsync(chatId, UpdateComponentsJson, ct);
        Assert.True(updateResult.Success);
        Assert.Null(updateResult.Error);

        Assert.Single(server.RequestsFor("sendMessage")); // still just the one send — no new message
        var editRequest = Assert.Single(server.RequestsFor("editMessageText"));
        var editBody = editRequest.Body!.Value;
        Assert.Equal(chatId, editBody["chat_id"]!.GetValue<long>());
        Assert.Equal(messageId, editBody["message_id"]!.GetValue<long>());
        Assert.Equal("MarkdownV2", editBody["parse_mode"]!.GetValue<string>());
        Assert.Equal(
            "Rollback",
            editBody["reply_markup"]!["inline_keyboard"]![0]![0]!["text"]!.GetValue<string>());

        var deleteResult = await renderer.IngestAsync(chatId, DeleteSurfaceJson, ct);
        Assert.True(deleteResult.Success);
        Assert.Null(deleteResult.Error);

        var deleteRequest = Assert.Single(server.RequestsFor("deleteMessage"));
        var deleteBody = deleteRequest.Body!.Value;
        Assert.Equal(chatId, deleteBody["chat_id"]!.GetValue<long>());
        Assert.Equal(messageId, deleteBody["message_id"]!.GetValue<long>());
    }

    [Fact]
    public async Task Malformed_tap_time_actions_reach_the_CSharp_error_observer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeServerModule.start();
        const long chatId = 7002L;
        var observed = new TaskCompletionSource<A2uiErrorInfo>();

        await using var agent = await TelegramAgent.StartPollingAsync(
            new TelegramAgentOptions { BotToken = FakeToken, BaseUrl = server.BaseUrl, Tools = new ToolRegistry() },
            ct);

        var renderer = A2uiRenderer.Create(
            agent,
            _ => Task.CompletedTask,
            error =>
            {
                if (error.Kind == "MalformedAction") observed.TrySetResult(error);
            });

        Assert.True((await renderer.IngestAsync(chatId, MalformedActionSurfaceJson, ct)).Success);
        var body = Assert.Single(server.RequestsFor("sendMessage")).Body!.Value;
        var token = body["reply_markup"]!["inline_keyboard"]![0]![0]!["callback_data"]!.GetValue<string>();

        server.EnqueueResult(
            "getUpdates",
            TelegramJson.batch(ListOf(TelegramJson.callbackQueryUpdate(1, "q-malformed", token, chatId, 1, 9102L, "Cs"))));

        await WaitUntilAsync(() => observed.Task.IsCompleted, 5000, ct);
        var error = await observed.Task;
        Assert.Equal("MalformedAction", error.Kind);
        Assert.Contains("confirm", error.Description);
        Assert.Contains("cs-malformed-action", error.Description);
    }
}
