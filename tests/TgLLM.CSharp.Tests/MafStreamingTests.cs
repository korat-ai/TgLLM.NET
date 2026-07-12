using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Collections;
using TgLLM.FSharp;
using TgLLM.Maf;
using static TgLLM.Integration.Tests.MafScriptedAgent;
using Xunit;
using FakeServerModule = TgLLM.Integration.Tests.FakeBotApiServer.FakeBotApiServerModule;

namespace TgLLM.CSharp.Tests;

/// <summary>
/// Idiom-leak canary specific to the streaming opt-in's own C#-facing surface — mirrors
/// <see cref="MafSessionStoreIdiomLeakCanaryTests"/>'s own scoped-to-the-overloads-a-C#-host-
/// actually-touches pattern. <see cref="MafIdiomLeakCanaryTests"/> already walks
/// <see cref="MafSurfacedEvent"/> (the "StreamFailed" <c>Kind</c> this file's own reliability smoke
/// test below asserts on is just a new string value on an already-canaried type, so it needs no
/// separate canary entry) — this class covers the ONE surface that canary does not:
/// <see cref="TgBotConfig.WithStreaming()"/>/<see cref="TgBotConfig.WithStreaming(TimeSpan)"/> and
/// their <see cref="TgWebhookConfig"/> counterparts.
/// </summary>
public class MafStreamingIdiomLeakCanaryTests
{
    private static IEnumerable<Type> Flatten(Type type)
    {
        yield return type;

        if (type.IsGenericType)
        {
            foreach (var arg in type.GetGenericArguments())
            {
                foreach (var inner in Flatten(arg))
                {
                    yield return inner;
                }
            }
        }

        if (type.HasElementType)
        {
            var element = type.GetElementType();
            if (element is not null)
            {
                foreach (var inner in Flatten(element))
                {
                    yield return inner;
                }
            }
        }
    }

    private static IEnumerable<Type> SignatureTypes(MemberInfo member) => member switch
    {
        MethodBase method => method.GetParameters().Select(p => p.ParameterType)
            .Concat(method is MethodInfo mi ? new[] { mi.ReturnType } : Array.Empty<Type>()),
        _ => Array.Empty<Type>(),
    };

    [Fact]
    public void TgBotConfig_and_TgWebhookConfig_WithStreaming_overloads_expose_no_FSharpCore_types()
    {
        var withStreamingOverloads = typeof(TgBotConfig)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == nameof(TgBotConfig.WithStreaming))
            .Concat(typeof(TgWebhookConfig)
                .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == nameof(TgWebhookConfig.WithStreaming)));

        var offenders =
            (from member in withStreamingOverloads
             from signatureType in SignatureTypes(member)
             from involved in Flatten(signatureType)
             where involved.Namespace?.StartsWith("Microsoft.FSharp", StringComparison.Ordinal) == true
             select $"{member.DeclaringType!.Name}.{member.Name} → {involved.FullName}")
            .Distinct()
            .ToList();

        Assert.True(offenders.Count == 0, "F# types leaked into the streaming opt-in's own WithStreaming surface:\n" + string.Join("\n", offenders));

        // Both overloads are actually there (the canary above alone would pass vacuously on an
        // empty member list if the overload names ever changed out from under it).
        Assert.Equal(2, typeof(TgBotConfig).GetMember(nameof(TgBotConfig.WithStreaming)).Length);
        Assert.Equal(2, typeof(TgWebhookConfig).GetMember(nameof(TgWebhookConfig.WithStreaming)).Length);
    }
}

/// <summary>
/// A basic C#-driven streaming turn through <see cref="MafTelegramBridge"/> — <c>WithStreaming()</c>
/// called from C# turns the opt-in on with no further wiring, mirroring
/// <see cref="MafTextTurnSmokeTests"/>'s own non-streaming C#-driven acceptance pattern. Reuses the
/// fake Bot API server and <c>ScriptedAgent</c> the F# integration suite already proves the
/// streaming turn-driving loop against.
/// </summary>
public class MafStreamingSmokeTests
{
    private const string FakeToken = "123456789:TEST-fake-token";

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
    public async Task WithStreaming_turned_on_from_C_sharp_produces_the_expected_single_send_no_edit_sequence_for_an_ordinary_one_shot_reply()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeServerModule.start();
        const long chatId = 9950L;

        var steps = ListModule.OfArray(new[] { ScriptedStep.NewRepliesWith("Hello, streamed from C#!") });
        var agent = new ScriptedAgent(steps, null, null, null);

        var tools = TgLLM.FSharp.ToolRegistry.create();
        var config = TgBotConfig.create(FakeToken).WithBaseUrl(server.BaseUrl).WithTools(tools).WithStreaming();

        await using var bridge = await MafTelegramBridge.StartPollingAsync(config, agent);

        await bridge.StartRunAsync(chatId, "Hi from a C# test");

        await WaitUntilAsync(() => server.RequestsFor("sendMessage").Any(), 5000, ct);
        await Task.Delay(200, ct); // deterministic settle — nothing ever turns up a LATER edit for a one-shot reply

        var sends = server.RequestsFor("sendMessage");
        var edits = server.RequestsFor("editMessageText");

        Assert.Single(sends);
        Assert.Equal("Hello, streamed from C#!", sends.First().Body!.Value["text"]!.GetValue<string>());
        Assert.Empty(edits);
    }

    /// <summary>
    /// The one-arg <c>WithStreaming(TimeSpan)</c> overload, called from C#, still wires a working
    /// streaming bridge end to end — the custom cadence's own PACING precision is already exhaustively
    /// proven from F# (<c>MafStreamingConfigTests.fs</c>'s own clock-advance test); this is a
    /// C#-callable-overload smoke test, not a duplicate of that precision test.
    /// </summary>
    [Fact]
    public async Task WithStreaming_TimeSpan_overload_turns_streaming_on_with_a_custom_cadence_from_C_sharp()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeServerModule.start();
        const long chatId = 9952L;

        var steps = ListModule.OfArray(new[] { ScriptedStep.NewRepliesWith("Hello!") });
        var agent = new ScriptedAgent(steps, null, null, null);

        var tools = TgLLM.FSharp.ToolRegistry.create();
        var custom = TimeSpan.FromSeconds(0.75);
        var config = TgBotConfig.create(FakeToken).WithBaseUrl(server.BaseUrl).WithTools(tools).WithStreaming(custom);

        await using var bridge = await MafTelegramBridge.StartPollingAsync(config, agent);

        await bridge.StartRunAsync(chatId, "Hi from a C# test");

        await WaitUntilAsync(() => server.RequestsFor("sendMessage").Any(), 5000, ct);

        var sends = server.RequestsFor("sendMessage");
        Assert.Single(sends);
        Assert.Equal("Hello!", sends.First().Body!.Value["text"]!.GetValue<string>());
    }

    /// <summary>
    /// A mid-stream failure (after a live message is already showing) must reach a C#-registered
    /// <see cref="MafBridgeSettings.OnSurfaced"/> callback with <c>Kind == "StreamFailed"</c> — the
    /// C#-facing counterpart to <c>IMafStreamingObserver.OnStreamFailed</c>
    /// (the <c>CSharpMafObserverBridge</c> internal type's own third
    /// <c>interface IMafStreamingObserver</c> block, <c>CSharpSurface.fs</c>), mirroring how
    /// <see cref="MafSessionStoreSmokeTests"/>'s own corrupt-record test proves the
    /// <c>IMafSessionObserver</c> sibling block reaches the SAME callback channel.
    /// </summary>
    [Fact]
    public async Task A_mid_stream_failure_reaches_a_C_sharp_registered_OnSurfaced_callback_with_Kind_StreamFailed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeServerModule.start();
        const long chatId = 9951L;

        var deltas = ListModule.OfArray(new[] { Tuple.Create("Working on it", TimeSpan.Zero) });
        var throwsStep = ScriptedStep.NewThrows(new InvalidOperationException("scripted mid-stream failure"));
        var steps = ListModule.OfArray(new[] { ScriptedStep.NewStreamsThen(deltas, throwsStep) });
        var agent = new ScriptedAgent(steps, null, null, null);

        var surfaced = new List<MafSurfacedEvent>();
        var settings = new MafBridgeSettings { OnSurfaced = e => surfaced.Add(e) };

        var tools = TgLLM.FSharp.ToolRegistry.create();
        var config = TgBotConfig.create(FakeToken).WithBaseUrl(server.BaseUrl).WithTools(tools).WithStreaming();

        await using var bridge = await MafTelegramBridge.StartPollingAsync(config, agent, settings);

        await bridge.StartRunAsync(chatId, "Hi from a C# test");

        await WaitUntilAsync(() => surfaced.Any(e => e.Kind == "StreamFailed"), 5000, ct);

        var streamFailed = surfaced.Single(e => e.Kind == "StreamFailed");
        Assert.NotNull(streamFailed.Exception);
        Assert.Contains("scripted mid-stream failure", streamFailed.Exception!.Message);
        Assert.Contains(chatId.ToString(), streamFailed.Description);
    }
}
