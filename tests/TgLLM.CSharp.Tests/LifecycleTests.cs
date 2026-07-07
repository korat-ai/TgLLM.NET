using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Collections;
using TgLLM.CSharp;
using TgLLM.Persistence.LiteDb;
using Xunit;
using FakeServerModule = TgLLM.Integration.Tests.FakeBotApiServer.FakeBotApiServerModule;
using TelegramJson = TgLLM.Integration.Tests.FakeBotApiServer.TelegramJson;

namespace TgLLM.CSharp.Tests;

/// <summary>
/// Acceptance test for the lifecycle store seam (US4) driven entirely through the C# façade: a
/// keyboard sent before a simulated restart still routes — and its owner scope is still enforced —
/// through a REOPENED <see cref="LiteDbBindingStore"/>. Mirrors <see cref="OwnerAuthorizationTests"/>'s
/// structure and reuses the same fake Bot API server.
/// </summary>
public class LifecycleTests
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
    public async Task A_keyboard_sent_before_a_restart_still_routes_through_a_reopened_LiteDbBindingStore()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"tgllm-csharp-lifecycle-litedb-restart-{Guid.NewGuid()}.db");

        try
        {
            await using var server = await FakeServerModule.start();
            const long ownerId = 921L;
            const long nonOwnerId = 922L;
            const long chatId = 4101L;

            var store1 = (TgLLM.Core.IBindingStore)LiteDbBindingStore.OpenAt(path);
            var tools1 = new ToolRegistry().Register("approve", _ => Task.CompletedTask);

            {
                await using var agent1 = await TelegramAgent.StartPollingAsync(
                    new TelegramAgentOptions { BotToken = FakeToken, BaseUrl = server.BaseUrl, Tools = tools1, BindingStore = store1 },
                    ct);

                var plan = new PlanBuilder().Row(r => r.Tool("Approve", "approve")).Build();
                await agent1.SendKeyboardPlanAsync(chatId, "Deploy?", plan, Owner.User(ownerId), ct: ct);
            }
            // agent1 is now disposed — release the LiteDB file before reopening it.
            ((IDisposable)store1).Dispose();

            var sentKeyboard = server.RequestsFor("sendMessage").First().Body!.Value;
            var token = CallbackDataAt(sentKeyboard, 0, 0);

            // --- Simulate a restart: a BRAND NEW LiteDbBindingStore instance over the SAME file,
            // and a BRAND NEW tool registry with "approve" re-registered fresh. Nothing but the
            // file connects the two halves of this test. ---
            var store2 = (TgLLM.Core.IBindingStore)LiteDbBindingStore.OpenAt(path);
            var toolRanFor = new TaskCompletionSource<long>();

            var tools2 = new ToolRegistry().Register(
                "approve",
                ctx =>
                {
                    toolRanFor.TrySetResult(ctx.User.Id);
                    return Task.CompletedTask;
                });

            await using var agent2 = await TelegramAgent.StartPollingAsync(
                new TelegramAgentOptions { BotToken = FakeToken, BaseUrl = server.BaseUrl, Tools = tools2, BindingStore = store2 },
                ct);

            // A non-owner taps the PRE-restart button: still refused (owner scope survived).
            server.EnqueueResult(
                "getUpdates",
                TelegramJson.batch(ListOf(TelegramJson.callbackQueryUpdate(1, "q-restart-nonowner", token, chatId, 60, nonOwnerId, "Mallory"))));

            await WaitUntilAsync(() => server.RequestsFor("answerCallbackQuery").Any(), 5000, ct);
            Assert.False(toolRanFor.Task.IsCompleted, "a non-owner's tap on a pre-restart button is still refused post-restart");

            // The owner's tap on the SAME pre-restart button still runs the tool.
            server.EnqueueResult(
                "getUpdates",
                TelegramJson.batch(ListOf(TelegramJson.callbackQueryUpdate(2, "q-restart-owner", token, chatId, 61, ownerId, "Owner"))));

            var completed = await Task.WhenAny(toolRanFor.Task, Task.Delay(5000, ct));
            Assert.Same(toolRanFor.Task, completed);
            var ranForUserId = await toolRanFor.Task;
            Assert.Equal(ownerId, ranForUserId);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
