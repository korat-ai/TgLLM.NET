using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TgLLM.CSharp;
using Xunit;
using FakeServerModule = TgLLM.Integration.Tests.FakeBotApiServer.FakeBotApiServerModule;
using TelegramJson = TgLLM.Integration.Tests.FakeBotApiServer.TelegramJson;

namespace TgLLM.CSharp.Tests;

/// <summary>
/// Tests for <see cref="BindingStoreAdapter"/>: a C#-implemented <see cref="IBindingStoreCSharp"/>
/// (nullable DTOs only) bridged into the library's F#-facing store seam round-trips every field of
/// a binding correctly, and a keyboard backed by such a store still routes real presses.
/// </summary>
public class BindingStoreAdapterTests
{
    private const string FakeToken = "123456789:TEST-fake-token";

    /// <summary>A minimal in-memory <see cref="IBindingStoreCSharp"/> — the shape any C# host would write.</summary>
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

        public ValueTask<int> EvictExpiredAsync(DateTimeOffset now, CancellationToken ct)
        {
            var expired = _bindings.Values.Where(b => b.ExpiresAt is { } exp && exp <= now).Select(b => b.Token).ToList();

            foreach (var token in expired)
            {
                _bindings.Remove(token);
            }

            return ValueTask.FromResult(expired.Count);
        }
    }

    private static TgLLM.Core.ToolName ApproveToolName => TgLLM.Core.ToolNameModule.create("approve").ResultValue;

    [Fact]
    public async Task Save_then_TryGet_round_trips_every_field_through_the_adapter()
    {
        var coreStore = BindingStoreAdapter.ToCoreStore(new InMemoryCSharpStore());
        var token = TgLLM.Core.CallbackTokenModule.generate();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);

        var binding = new TgLLM.Core.ToolBinding(
            token,
            ApproveToolName,
            FSharpOption<string>.Some("payload"),
            TgLLM.Core.OwnerScope.NewUser(42L),
            FSharpOption<DateTimeOffset>.Some(expiresAt),
            true,
            FSharpOption<string>.Some("nope, not you"));

        await coreStore.Save(new[] { binding }, CancellationToken.None);
        var found = await coreStore.TryGet(token, CancellationToken.None);

        Assert.True(found.IsValueSome);
        var roundTripped = found.Value;
        Assert.Equal(binding.Token, roundTripped.Token);
        Assert.Equal(binding.ToolName, roundTripped.ToolName);
        // `.Value` on each option is only reached after `OptionModule.IsSome`/the equivalent
        // structural assertions above already establish it's a `Some` (never null at runtime) —
        // the `!` silences the nullable-reference-type checker's (accurate, but here irrelevant)
        // observation that an `FSharpOption<T>` reference could in general be null (`None`).
        Assert.Equal("payload", roundTripped.Arg!.Value);
        Assert.True(roundTripped.Owner.IsUser);
        Assert.Equal(42L, roundTripped.Owner.userId);
        Assert.Equal(expiresAt, roundTripped.ExpiresAt!.Value);
        Assert.True(roundTripped.SingleUse);
        Assert.Equal("nope, not you", roundTripped.DeniedNotice!.Value);
    }

    [Fact]
    public async Task An_Anyone_owned_argument_less_binding_also_round_trips()
    {
        var coreStore = BindingStoreAdapter.ToCoreStore(new InMemoryCSharpStore());
        var token = TgLLM.Core.CallbackTokenModule.generate();

        var binding = new TgLLM.Core.ToolBinding(
            token,
            ApproveToolName,
            FSharpOption<string>.None,
            TgLLM.Core.OwnerScope.Anyone,
            FSharpOption<DateTimeOffset>.None,
            false,
            FSharpOption<string>.None);

        await coreStore.Save(new[] { binding }, CancellationToken.None);
        var found = await coreStore.TryGet(token, CancellationToken.None);

        Assert.True(found.IsValueSome);
        var roundTripped = found.Value;
        Assert.True(roundTripped.Owner.IsAnyone);
        Assert.False(OptionModule.IsSome(roundTripped.Arg));
        Assert.False(OptionModule.IsSome(roundTripped.ExpiresAt));
        Assert.False(roundTripped.SingleUse);
        Assert.False(OptionModule.IsSome(roundTripped.DeniedNotice));
    }

    [Fact]
    public async Task Remove_then_TryGet_reports_no_binding()
    {
        var coreStore = BindingStoreAdapter.ToCoreStore(new InMemoryCSharpStore());
        var token = TgLLM.Core.CallbackTokenModule.generate();

        var binding = new TgLLM.Core.ToolBinding(
            token,
            ApproveToolName,
            FSharpOption<string>.None,
            TgLLM.Core.OwnerScope.Anyone,
            FSharpOption<DateTimeOffset>.None,
            false,
            FSharpOption<string>.None);

        await coreStore.Save(new[] { binding }, CancellationToken.None);
        await coreStore.Remove(new[] { token }, CancellationToken.None);
        var found = await coreStore.TryGet(token, CancellationToken.None);

        Assert.False(found.IsValueSome);
    }

    [Fact]
    public async Task EvictExpired_removes_only_the_expired_binding_and_returns_the_count()
    {
        var coreStore = BindingStoreAdapter.ToCoreStore(new InMemoryCSharpStore());
        var expiredToken = TgLLM.Core.CallbackTokenModule.generate();
        var liveToken = TgLLM.Core.CallbackTokenModule.generate();
        var now = DateTimeOffset.UtcNow;

        var expiredBinding = new TgLLM.Core.ToolBinding(
            expiredToken,
            ApproveToolName,
            FSharpOption<string>.None,
            TgLLM.Core.OwnerScope.Anyone,
            FSharpOption<DateTimeOffset>.Some(now.AddMinutes(-1)),
            false,
            FSharpOption<string>.None);

        var liveBinding = new TgLLM.Core.ToolBinding(
            liveToken,
            ApproveToolName,
            FSharpOption<string>.None,
            TgLLM.Core.OwnerScope.Anyone,
            FSharpOption<DateTimeOffset>.None,
            false,
            FSharpOption<string>.None);

        await coreStore.Save(new[] { expiredBinding, liveBinding }, CancellationToken.None);
        var removedCount = await coreStore.EvictExpired(now);

        Assert.Equal(1, removedCount);
        Assert.False((await coreStore.TryGet(expiredToken, CancellationToken.None)).IsValueSome);
        Assert.True((await coreStore.TryGet(liveToken, CancellationToken.None)).IsValueSome);
    }

    [Fact]
    public async Task A_keyboard_backed_by_a_C_sharp_implemented_store_still_routes_a_real_press()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeServerModule.start();
        const long chatId = 4201L;
        var approveRanFor = new TaskCompletionSource<long>();

        var tools = new ToolRegistry().Register(
            "approve",
            ctx =>
            {
                approveRanFor.TrySetResult(ctx.User.Id);
                return Task.CompletedTask;
            });

        var store = BindingStoreAdapter.ToCoreStore(new InMemoryCSharpStore());

        await using var agent = await TelegramAgent.StartPollingAsync(
            new TelegramAgentOptions { BotToken = FakeToken, BaseUrl = server.BaseUrl, Tools = tools, BindingStore = store },
            ct);

        var plan = new PlanBuilder().Row(r => r.Tool("Approve", "approve")).Build();
        await agent.SendKeyboardPlanAsync(chatId, "Deploy?", plan, ct: ct);

        var sentKeyboard = server.RequestsFor("sendMessage").First().Body!.Value;
        var token = sentKeyboard["reply_markup"]!["inline_keyboard"]![0]![0]!["callback_data"]!.GetValue<string>();

        server.EnqueueResult(
            "getUpdates",
            TelegramJson.batch(ListModule.OfArray(new[] { TelegramJson.callbackQueryUpdate(1, "q-approve", token, chatId, 10, 950L, "Tester") })));

        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (!approveRanFor.Task.IsCompleted && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, ct);
        }

        Assert.True(approveRanFor.Task.IsCompleted, "the tool bound through the C#-implemented store should have run");
        Assert.Equal(950L, await approveRanFor.Task);
    }
}
