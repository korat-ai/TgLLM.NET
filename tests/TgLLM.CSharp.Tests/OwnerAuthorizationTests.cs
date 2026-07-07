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
/// Acceptance test for press authorization (owner scoping) driven entirely through the C# façade:
/// <see cref="TelegramAgent"/>, <see cref="Owner"/>, <see cref="PlanBuilder"/>. Reuses the fake Bot
/// API server the F# integration suite already proves against real Telegram.Bot request/response
/// shapes (<c>FakeBotApiServer.fs</c>), rather than reimplementing an equivalent HTTP fake here.
/// </summary>
public class OwnerAuthorizationTests
{
    private const string FakeToken = "123456789:TEST-fake-token";

    private static string CallbackDataAt(JsonNode sendBody, int row, int col) =>
        sendBody["reply_markup"]!["inline_keyboard"]![row]![col]!["callback_data"]!.GetValue<string>();

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
    public async Task An_owner_scoped_keyboard_refuses_a_non_owner_and_runs_for_the_owner()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeServerModule.start();
        const long ownerId = 501L;
        const long nonOwnerId = 502L;
        const long chatId = 4001L;
        var toolRanFor = new TaskCompletionSource<long>();

        var tools = new ToolRegistry().Register(
            "approve",
            ctx =>
            {
                toolRanFor.TrySetResult(ctx.User.Id);
                return Task.CompletedTask;
            });

        await using var agent = await TelegramAgent.StartPollingAsync(
            new TelegramAgentOptions { BotToken = FakeToken, BaseUrl = server.BaseUrl, Tools = tools },
            ct);

        var plan = new PlanBuilder().Row(r => r.Tool("Approve", "approve")).Build();

        await agent.SendKeyboardPlanAsync(chatId, "Deploy?", plan, Owner.User(ownerId), ct: ct);

        var sentKeyboard = server.RequestsFor("sendMessage").First().Body!.Value;
        var token = CallbackDataAt(sentKeyboard, 0, 0);

        // A non-owner taps first: acked, but the tool must never run.
        server.EnqueueResult(
            "getUpdates",
            TelegramJson.batch(ListOf(TelegramJson.callbackQueryUpdate(1, "q-nonowner", token, chatId, 10, nonOwnerId, "Mallory"))));

        await WaitUntilAsync(() => server.RequestsFor("answerCallbackQuery").Any(), 5000, ct);
        Assert.False(toolRanFor.Task.IsCompleted, "a non-owner press must never run the bound tool");

        // deniedNotice was omitted at send time, so the refusal must show the built-in default
        // notice — NOT an empty string. A C# caller omitting the argument reaches the F# optional
        // parameter as `Some null`; the façade must normalize that to "unset" so the default wins.
        var refusalAck = server.RequestsFor("answerCallbackQuery").First().Body!.Value;
        Assert.Equal("This button isn't for you.", refusalAck["text"]!.GetValue<string>());

        // The owner taps next: the SAME button now runs the tool.
        server.EnqueueResult(
            "getUpdates",
            TelegramJson.batch(ListOf(TelegramJson.callbackQueryUpdate(2, "q-owner", token, chatId, 11, ownerId, "Owner"))));

        var completed = await Task.WhenAny(toolRanFor.Task, Task.Delay(5000, ct));
        Assert.Same(toolRanFor.Task, completed);
        var ranForUserId = await toolRanFor.Task;
        Assert.Equal(ownerId, ranForUserId);
    }
}
