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
/// Acceptance test for client-side buttons driven entirely through the C# façade: a keyboard
/// mixing a tool button with <see cref="PlanRowBuilder.WebApp"/> and
/// <see cref="PlanRowBuilder.CopyText"/> sends a wire-correct row — the tool button still routes on
/// tap, while the WebApp/CopyText buttons carry no <c>callback_data</c> and so reach no server-side
/// handler. Mirrors <see cref="StructuredArgTests"/>'s fake-server pattern; the F# façade equivalent
/// (over both long polling and webhooks) lives in
/// <c>tests/TgLLM.Integration.Tests/ClientButtonsTests.fs</c>.
/// </summary>
public class ClientButtonsTests
{
    private const string FakeToken = "123456789:TEST-fake-token";

    private static JsonNode ButtonAt(JsonNode sendBody, int row, int col) =>
        sendBody["reply_markup"]!["inline_keyboard"]![row]![col]!;

    private static bool HasField(JsonNode node, string key) => node[key] is not null;

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
    public async Task A_keyboard_mixing_tool_WebApp_and_CopyText_sends_a_wire_correct_row_and_only_the_tool_button_routes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeServerModule.start();
        const long chatId = 4501L;
        var toolRan = new TaskCompletionSource<bool>();

        var tools = new ToolRegistry().Register(
            "approve",
            ctx =>
            {
                toolRan.TrySetResult(true);
                return Task.CompletedTask;
            });

        await using var agent = await TelegramAgent.StartPollingAsync(
            new TelegramAgentOptions { BotToken = FakeToken, BaseUrl = server.BaseUrl, Tools = tools },
            ct);

        var plan = new PlanBuilder()
            .Row(r => r
                .Tool("Approve", "approve")
                .WebApp("Open", "https://example.test/app")
                .CopyText("Copy", "snippet-1"))
            .Build();

        await agent.SendKeyboardPlanAsync(chatId, "Deploy?", plan, ct: ct);

        var sentKeyboard = server.RequestsFor("sendMessage").First().Body!.Value;

        // The tool button: carries callback_data, no web_app/copy_text.
        var toolButton = ButtonAt(sentKeyboard, 0, 0);
        Assert.Equal("Approve", toolButton["text"]!.GetValue<string>());
        Assert.True(HasField(toolButton, "callback_data"), "the tool button carries callback_data");
        Assert.False(HasField(toolButton, "web_app"), "the tool button carries no web_app payload");
        Assert.False(HasField(toolButton, "copy_text"), "the tool button carries no copy_text payload");

        // The WebApp button: carries web_app.url, NO callback_data — nothing for the server to route.
        var webAppButton = ButtonAt(sentKeyboard, 0, 1);
        Assert.Equal("Open", webAppButton["text"]!.GetValue<string>());
        Assert.Equal("https://example.test/app", webAppButton["web_app"]!["url"]!.GetValue<string>());
        Assert.False(HasField(webAppButton, "callback_data"), "a WebApp button carries no callback_data");

        // The CopyText button: carries copy_text.text, NO callback_data — nothing for the server to route.
        var copyTextButton = ButtonAt(sentKeyboard, 0, 2);
        Assert.Equal("Copy", copyTextButton["text"]!.GetValue<string>());
        Assert.Equal("snippet-1", copyTextButton["copy_text"]!["text"]!.GetValue<string>());
        Assert.False(HasField(copyTextButton, "callback_data"), "a CopyText button carries no callback_data");

        // The tool button still routes on tap, exactly like a tool-only keyboard.
        var approveToken = toolButton["callback_data"]!.GetValue<string>();

        server.EnqueueResult(
            "getUpdates",
            TelegramJson.batch(ListOf(TelegramJson.callbackQueryUpdate(1, "q-clientbuttons-csharp", approveToken, chatId, 90, 990L, "Tester"))));

        await WaitUntilAsync(() => toolRan.Task.IsCompleted, 5000, ct);
        Assert.True(await toolRan.Task, "the tool button's tap still routed to its bound tool");
    }
}
