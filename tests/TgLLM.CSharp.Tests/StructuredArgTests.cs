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
/// Structured-argument round-trip driven through the C# façade end-to-end:
/// <see cref="PlanRowBuilder.Tool{T}"/> binds a typed payload to a button;
/// <see cref="PressContext.GetArg{T}"/> deserializes it back out when the tool runs. A plain
/// string argument keeps routing through the existing <see cref="PressContext.Arg"/> unchanged.
/// Reuses the fake Bot API server, same pattern as <see cref="OwnerAuthorizationTests"/>.
/// </summary>
public class StructuredArgTests
{
    private const string FakeToken = "123456789:TEST-fake-token";

    private sealed record ApprovalRequest(int Id, string Reason);

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
    public async Task Tool_T_binds_a_structured_payload_and_GetArg_T_returns_the_exact_value_on_press()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeServerModule.start();
        const long chatId = 4101L;
        var original = new ApprovalRequest(42, "looks good");
        var received = new TaskCompletionSource<ApprovalRequest>();

        var tools = new ToolRegistry().Register(
            "approve",
            ctx =>
            {
                received.TrySetResult(ctx.GetArg<ApprovalRequest>());
                return Task.CompletedTask;
            });

        await using var agent = await TelegramAgent.StartPollingAsync(
            new TelegramAgentOptions { BotToken = FakeToken, BaseUrl = server.BaseUrl, Tools = tools },
            ct);

        var plan = new PlanBuilder().Row(r => r.Tool("Approve", "approve", original)).Build();
        await agent.SendKeyboardPlanAsync(chatId, "Deploy?", plan, ct: ct);

        var sentKeyboard = server.RequestsFor("sendMessage").First().Body!.Value;
        var token = CallbackDataAt(sentKeyboard, 0, 0);

        server.EnqueueResult(
            "getUpdates",
            TelegramJson.batch(ListOf(TelegramJson.callbackQueryUpdate(1, "q-approve", token, chatId, 10, 900L, "Tester"))));

        await WaitUntilAsync(() => received.Task.IsCompleted, 5000, ct);
        Assert.Equal(original, await received.Task);
    }

    [Fact]
    public async Task Tool_T_binds_a_tuple_payload_and_GetArg_T_round_trips_it_exactly()
    {
        // `Plan.toolWith<T>` serializes through the SAME `JsonFSharpOptions`-configured options
        // `PressContext.GetArg<T>` must deserialize with — a plain C# tuple is exactly the shape
        // that configuration changes (a JSON array, not the BCL default's field-less `{}`), so this
        // is the case the plain-`JsonSerializer.Deserialize<T>(json)` bug actually breaks.
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeServerModule.start();
        const long chatId = 4104L;
        var original = (Id: 7, Reason: "looks good");
        var received = new TaskCompletionSource<(int Id, string Reason)>();

        var tools = new ToolRegistry().Register(
            "approve",
            ctx =>
            {
                received.TrySetResult(ctx.GetArg<(int Id, string Reason)>());
                return Task.CompletedTask;
            });

        await using var agent = await TelegramAgent.StartPollingAsync(
            new TelegramAgentOptions { BotToken = FakeToken, BaseUrl = server.BaseUrl, Tools = tools },
            ct);

        var plan = new PlanBuilder().Row(r => r.Tool("Approve", "approve", original)).Build();
        await agent.SendKeyboardPlanAsync(chatId, "Deploy?", plan, ct: ct);

        var sentKeyboard = server.RequestsFor("sendMessage").First().Body!.Value;
        var token = CallbackDataAt(sentKeyboard, 0, 0);

        server.EnqueueResult(
            "getUpdates",
            TelegramJson.batch(ListOf(TelegramJson.callbackQueryUpdate(1, "q-approve-tuple", token, chatId, 13, 903L, "Tester"))));

        await WaitUntilAsync(() => received.Task.IsCompleted, 5000, ct);
        Assert.Equal(original, await received.Task);
    }

    [Fact]
    public async Task TryGetArg_T_returns_false_for_a_payload_that_does_not_deserialize_as_T()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeServerModule.start();
        const long chatId = 4102L;
        var succeeded = new TaskCompletionSource<bool>();

        var tools = new ToolRegistry().Register(
            "approve",
            ctx =>
            {
                succeeded.TrySetResult(ctx.TryGetArg<ApprovalRequest>(out _));
                return Task.CompletedTask;
            });

        await using var agent = await TelegramAgent.StartPollingAsync(
            new TelegramAgentOptions { BotToken = FakeToken, BaseUrl = server.BaseUrl, Tools = tools },
            ct);

        // A plain (non-JSON-object) string argument does not deserialize as ApprovalRequest.
        var plan = new PlanBuilder().Row(r => r.Tool("Approve", "approve", "not-an-approval-request")).Build();
        await agent.SendKeyboardPlanAsync(chatId, "Deploy?", plan, ct: ct);

        var sentKeyboard = server.RequestsFor("sendMessage").First().Body!.Value;
        var token = CallbackDataAt(sentKeyboard, 0, 0);

        server.EnqueueResult(
            "getUpdates",
            TelegramJson.batch(ListOf(TelegramJson.callbackQueryUpdate(1, "q-mismatch", token, chatId, 11, 901L, "Tester"))));

        await WaitUntilAsync(() => succeeded.Task.IsCompleted, 5000, ct);
        Assert.False(await succeeded.Task, "a shape mismatch must be reported as false, not an exception");
    }

    [Fact]
    public async Task A_slice_2_plain_string_argument_still_routes_via_Arg_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeServerModule.start();
        const long chatId = 4103L;
        var received = new TaskCompletionSource<string?>();

        var tools = new ToolRegistry().Register(
            "approve",
            ctx =>
            {
                received.TrySetResult(ctx.Arg);
                return Task.CompletedTask;
            });

        await using var agent = await TelegramAgent.StartPollingAsync(
            new TelegramAgentOptions { BotToken = FakeToken, BaseUrl = server.BaseUrl, Tools = tools },
            ct);

        var plan = new PlanBuilder().Row(r => r.Tool("Approve", "approve", "raw-string-arg")).Build();
        await agent.SendKeyboardPlanAsync(chatId, "Deploy?", plan, ct: ct);

        var sentKeyboard = server.RequestsFor("sendMessage").First().Body!.Value;
        var token = CallbackDataAt(sentKeyboard, 0, 0);

        server.EnqueueResult(
            "getUpdates",
            TelegramJson.batch(ListOf(TelegramJson.callbackQueryUpdate(1, "q-approve-str", token, chatId, 12, 902L, "Tester"))));

        await WaitUntilAsync(() => received.Task.IsCompleted, 5000, ct);
        Assert.Equal("raw-string-arg", await received.Task);
    }
}
