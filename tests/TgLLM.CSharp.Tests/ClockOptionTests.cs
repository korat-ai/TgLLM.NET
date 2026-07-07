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
/// F# has <c>CommonConfig.Clock</c>/<c>WithClock</c> to make expiry/redelivery-dedup decisions
/// deterministic; <see cref="TelegramAgentOptions"/> had no C# equivalent, so a C# host could not
/// make its own expiry logic deterministic in tests. <see cref="TelegramAgentOptions.Clock"/>
/// closes that gap.
/// </summary>
public class ClockOptionTests
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
    public async Task TelegramAgentOptions_Clock_reaches_expiry_decisions_for_both_stamping_and_resolution()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeServerModule.start();
        const long chatId = 4501L;
        var runCount = 0;

        // A host-controlled, MUTABLE clock: `SendKeyboardPlanAsync`'s `expiresIn` is stamped
        // against whatever this returns AT SEND TIME; a later press's resolution reads it again.
        // Advancing `current` between those two moments — with NO real waiting at all — is only
        // possible if the C# `Clock` option genuinely reaches BOTH decisions, not just one, and
        // not the real wall clock.
        var current = DateTimeOffset.UnixEpoch.AddYears(50);
        DateTimeOffset ClockFunc() => current;

        var tools = new ToolRegistry().Register(
            "confirm",
            _ =>
            {
                Interlocked.Increment(ref runCount);
                return Task.CompletedTask;
            });

        await using var agent = await TelegramAgent.StartPollingAsync(
            new TelegramAgentOptions { BotToken = FakeToken, BaseUrl = server.BaseUrl, Tools = tools, Clock = ClockFunc },
            ct);

        var plan = new PlanBuilder().Row(r => r.Tool("Confirm", "confirm")).Build();

        // Stamped (per the injected clock) as `current + 5 minutes` — still live as of `current`.
        await agent.SendKeyboardPlanAsync(chatId, "Confirm?", plan, expiresIn: TimeSpan.FromMinutes(5), ct: ct);

        var sentKeyboard = server.RequestsFor("sendMessage").First().Body!.Value;
        var token = CallbackDataAt(sentKeyboard, 0, 0);

        // Advance the SAME injected clock well past the binding's expiry — deterministically, no
        // real sleep. If the resolver actually reads this clock, the press below must now find
        // the binding expired.
        current = current.AddHours(1);

        server.EnqueueResult(
            "getUpdates",
            TelegramJson.batch(ListOf(TelegramJson.callbackQueryUpdate(1, "q-clock-expired", token, chatId, 80, 970L, "Presser"))));

        await WaitUntilAsync(() => server.RequestsFor("answerCallbackQuery").Count() >= 1, 5000, ct);

        Assert.Equal(0, Volatile.Read(ref runCount));
    }
}
