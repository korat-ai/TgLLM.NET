using System;
using System.Collections.Generic;
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
/// Façade tests for <see cref="TelegramAgent.SendKeyboardPlanAsync"/>'s <c>expiresIn</c>/
/// <c>singleUse</c> parameters: they stamp every tool binding the send produces, and a single-use
/// binding stops routing after its first successful press — driven entirely through the C# façade,
/// reusing the fake Bot API server the F# integration suite already proves against real
/// Telegram.Bot request/response shapes.
/// </summary>
public class SendKeyboardPlanExpirySingleUseTests
{
    private const string FakeToken = "123456789:TEST-fake-token";

    /// <summary>A minimal in-memory <see cref="IBindingStoreCSharp"/> that lets a test inspect a
    /// binding's stamped fields directly, the same shape as any C# host's own store.</summary>
    private sealed class InMemoryCSharpStore : IBindingStoreCSharp
    {
        private readonly Dictionary<string, ToolBindingDto> _bindings = new();

        public ValueTask SaveAsync(IReadOnlyList<ToolBindingDto> bindings, CancellationToken ct)
        {
            foreach (var binding in bindings)
            {
                _bindings[binding.Token] = binding;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask<ToolBindingDto?> TryGetAsync(string token, CancellationToken ct) =>
            ValueTask.FromResult(_bindings.TryGetValue(token, out var binding) ? binding : null);

        public ValueTask RemoveAsync(IReadOnlyList<string> tokens, CancellationToken ct)
        {
            foreach (var token in tokens)
            {
                _bindings.Remove(token);
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask<int> EvictExpiredAsync(DateTimeOffset now, CancellationToken ct) => ValueTask.FromResult(0);
    }

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
    public async Task Sending_a_keyboard_with_expiresIn_and_singleUse_stamps_both_onto_its_binding()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeServerModule.start();
        const long chatId = 4401L;

        var tools = new ToolRegistry().Register("approve", _ => Task.CompletedTask);
        var csharpStore = new InMemoryCSharpStore();
        var store = BindingStoreAdapter.ToCoreStore(csharpStore);

        await using var agent = await TelegramAgent.StartPollingAsync(
            new TelegramAgentOptions { BotToken = FakeToken, BaseUrl = server.BaseUrl, Tools = tools, BindingStore = store },
            ct);

        var plan = new PlanBuilder().Row(r => r.Tool("Approve", "approve")).Build();
        var beforeSend = DateTimeOffset.UtcNow;

        await agent.SendKeyboardPlanAsync(chatId, "Deploy?", plan, expiresIn: TimeSpan.FromMinutes(10), singleUse: true, ct: ct);

        var sentKeyboard = server.RequestsFor("sendMessage").First().Body!.Value;
        var token = CallbackDataAt(sentKeyboard, 0, 0);

        var stored = await csharpStore.TryGetAsync(token, ct);

        Assert.NotNull(stored);
        Assert.NotNull(stored!.ExpiresAt);
        Assert.True(stored.ExpiresAt!.Value >= beforeSend.AddMinutes(10), "expiresIn should be stamped at least ten minutes past the send");
        Assert.True(stored.ExpiresAt!.Value <= DateTimeOffset.UtcNow.AddMinutes(10).AddSeconds(5), "expiresIn should be stamped roughly ten minutes past the send, not further out");
        Assert.True(stored.SingleUse, "singleUse should be stamped onto the binding");
    }

    [Fact]
    public async Task A_keyboard_sent_with_singleUse_true_runs_its_tool_once_then_ignores_a_second_tap()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeServerModule.start();
        const long chatId = 4402L;
        var runCount = 0;

        var tools = new ToolRegistry().Register(
            "confirm",
            _ =>
            {
                Interlocked.Increment(ref runCount);
                return Task.CompletedTask;
            });

        await using var agent = await TelegramAgent.StartPollingAsync(
            new TelegramAgentOptions { BotToken = FakeToken, BaseUrl = server.BaseUrl, Tools = tools },
            ct);

        var plan = new PlanBuilder().Row(r => r.Tool("Confirm", "confirm")).Build();
        await agent.SendKeyboardPlanAsync(chatId, "Confirm?", plan, singleUse: true, ct: ct);

        var sentKeyboard = server.RequestsFor("sendMessage").First().Body!.Value;
        var token = CallbackDataAt(sentKeyboard, 0, 0);

        server.EnqueueResult(
            "getUpdates",
            TelegramJson.batch(ListOf(TelegramJson.callbackQueryUpdate(1, "q-single-use-first", token, chatId, 70, 960L, "Presser"))));

        await WaitUntilAsync(() => Volatile.Read(ref runCount) >= 1, 5000, ct);
        Assert.Equal(1, Volatile.Read(ref runCount));

        server.EnqueueResult(
            "getUpdates",
            TelegramJson.batch(ListOf(TelegramJson.callbackQueryUpdate(2, "q-single-use-second", token, chatId, 71, 960L, "Presser"))));

        await WaitUntilAsync(() => server.RequestsFor("answerCallbackQuery").Count() >= 2, 5000, ct);
        Assert.Equal(1, Volatile.Read(ref runCount));
    }
}
