using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Collections;
using TgLLM.Core;
using TgLLM.FSharp;
using TgLLM.Maf;
using TgLLM.Persistence;
using TgLLM.Persistence.LiteDb;
using static TgLLM.Integration.Tests.MafScriptedAgent;
using Xunit;
using FakeServerModule = TgLLM.Integration.Tests.FakeBotApiServer.FakeBotApiServerModule;
using TelegramJson = TgLLM.Integration.Tests.FakeBotApiServer.TelegramJson;

namespace TgLLM.CSharp.Tests;

/// <summary>
/// Proves a C# host wires the durable-session opt-in with ONE call —
/// <c>TgBotConfig.create(token).WithTools(tools).WithSessionStore(FileSessionStore.OpenAt(path))</c>
/// — and gets the SAME restart-survival <see cref="TgLLM.Integration.Tests.MafDurableResumeTests"/>
/// and <see cref="TgLLM.Integration.Tests.MafDurableStoreFamilyTests"/> already prove from F#.
/// Reuses the fake Bot API server and <c>ScriptedAgent</c> those suites are proven against,
/// mirroring <see cref="MafTextTurnSmokeTests"/>'s own C#-driven acceptance pattern.
/// </summary>
public class MafSessionStoreSmokeTests
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

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"tgllm-csharp-session-store-tests-{Guid.NewGuid()}.json");

    private static string CallbackDataAt(int row, int col, JsonNode sendBody) =>
        sendBody["reply_markup"]!["inline_keyboard"]![row]![col]!["callback_data"]!.GetValue<string>();

    [Fact]
    public async Task A_C_sharp_wired_FileSessionStore_survives_a_restart_and_resumes_the_agent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeServerModule.start();
        const long chatId = 9601L;
        var path = TempPath();

        try
        {
            // The BINDING store must stay a SHARED instance across the restart — same requirement
            // as the F# durable-store suite (`MafDurableStoreFamilyTests.fs`): only the durable
            // SESSION store is under test here, so the tapped button's own token still has to
            // route on the post-restart bridge.
            var bindingStore = new InMemoryBindingStore();

            var tools1 = TgLLM.FSharp.ToolRegistry.create();
            var config1 = TgBotConfig.create(FakeToken)
                .WithBaseUrl(server.BaseUrl)
                .WithTools(tools1)
                .WithBindingStore(bindingStore)
                .WithSessionStore(FileSessionStore.OpenAt(path));

            var pauseSteps = ListModule.OfArray(new[]
            {
                ScriptedStep.NewPausesFor("req-1", "send_email", ListModule.OfArray(Array.Empty<Tuple<string, object?>>())),
            });
            var agent1 = new ScriptedAgent(pauseSteps, null, null, null);

            var bridge1 = await MafTelegramBridge.StartPollingAsync(config1, agent1);
            await bridge1.StartRunAsync(chatId, "Email alice that the deploy is done.");

            await WaitUntilAsync(() => server.RequestsFor("sendMessage").Any(), 5000, ct);
            var sent = server.RequestsFor("sendMessage").First().Body!.Value;
            var approveToken = CallbackDataAt(0, 0, sent);

            await ((IAsyncDisposable)bridge1).DisposeAsync();

            // "Restart": a brand-new agent, a brand-new tool registry, and a brand-new
            // FileSessionStore instance over the SAME path — nothing in-process carries over,
            // only what reached the file.
            var tools2 = TgLLM.FSharp.ToolRegistry.create();
            var config2 = TgBotConfig.create(FakeToken)
                .WithBaseUrl(server.BaseUrl)
                .WithTools(tools2)
                .WithBindingStore(bindingStore)
                .WithSessionStore(FileSessionStore.OpenAt(path));

            var resumeSteps = ListModule.OfArray(new[] { ScriptedStep.NewRepliesWith("Email sent to alice@example.com.") });
            var agent2 = new ScriptedAgent(resumeSteps, null, null, null);

            await using var bridge2 = await MafTelegramBridge.StartPollingAsync(config2, agent2);

            server.EnqueueResult(
                "getUpdates",
                TelegramJson.batch(
                    ListModule.OfArray(new[] { TelegramJson.callbackQueryUpdate(1, "q-csharp-durable", approveToken, chatId, 1, chatId, "Tester") })));

            await WaitUntilAsync(() => server.RequestsFor("editMessageText").Any(), 5000, ct);

            Assert.Equal(1, agent2.RunCount);
            Assert.Single(server.RequestsFor("sendMessage"));

            var editBody = server.RequestsFor("editMessageText").First().Body!.Value;
            var outcome = editBody["text"]!.GetValue<string>();
            Assert.Contains("approved", outcome);
            Assert.Contains("Email sent to alice@example.com.", outcome);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A C# host that wires <see cref="MafBridgeSettings.OnSurfaced"/> plus a durable session store
    /// but NO logger must still learn about a restore failure — the dual-façade parity gap this test
    /// closes: before the fix, <c>CSharpMafObserverBridge</c> implemented only <c>IMafObserver</c>, so
    /// <c>MafBridge</c>'s three-tier <c>sessionObserver</c> resolution (<c>Bridge.fs</c>) never matched
    /// it as an <c>IMafSessionObserver</c> and fell all the way to a silent <c>NoopMafObserver</c>
    /// for a host with no logger wired. Corrupts the chat's own durable record directly on disk —
    /// valid JSON row, valid Base64, but the DECODED bytes are not a well-formed session payload —
    /// the same "garbage bytes overwrite a valid record" shape
    /// <c>MafDurableReliabilityTests.fs</c>'s own <c>overwriteRecord</c> helper drives from F#.
    /// </summary>
    [Fact]
    public async Task A_C_sharp_host_wiring_OnSurfaced_with_no_logger_receives_SessionRestoreFailed_for_a_corrupt_durable_record()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeServerModule.start();
        const long chatId = 9602L;
        var path = TempPath();

        try
        {
            var bindingStore = new InMemoryBindingStore();

            var tools1 = TgLLM.FSharp.ToolRegistry.create();
            var config1 = TgBotConfig.create(FakeToken)
                .WithBaseUrl(server.BaseUrl)
                .WithTools(tools1)
                .WithBindingStore(bindingStore)
                .WithSessionStore(FileSessionStore.OpenAt(path));

            var pauseSteps = ListModule.OfArray(new[]
            {
                ScriptedStep.NewPausesFor("req-1", "send_email", ListModule.OfArray(Array.Empty<Tuple<string, object?>>())),
            });
            var agent1 = new ScriptedAgent(pauseSteps, null, null, null);

            var bridge1 = await MafTelegramBridge.StartPollingAsync(config1, agent1);
            await bridge1.StartRunAsync(chatId, "Email alice that the deploy is done.");

            await WaitUntilAsync(() => server.RequestsFor("sendMessage").Any(), 5000, ct);
            var sent = server.RequestsFor("sendMessage").First().Body!.Value;
            var approveToken = CallbackDataAt(0, 0, sent);

            await ((IAsyncDisposable)bridge1).DisposeAsync();

            var rows = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
            var row = rows.Single(r => r!["ChatId"]!.GetValue<long>() == chatId);
            row!["PayloadBase64"] = Convert.ToBase64String(new byte[] { 0, 1, 2 });
            File.WriteAllText(path, rows.ToJsonString());

            var surfaced = new List<MafSurfacedEvent>();

            // No `.WithLogger(...)` on this config — the exact shape that, before the fix, left
            // `sessionObserver` resolution with nothing to fall back to but `NoopMafObserver`.
            var tools2 = TgLLM.FSharp.ToolRegistry.create();
            var config2 = TgBotConfig.create(FakeToken)
                .WithBaseUrl(server.BaseUrl)
                .WithTools(tools2)
                .WithBindingStore(bindingStore)
                .WithSessionStore(FileSessionStore.OpenAt(path));

            var settings = new MafBridgeSettings { OnSurfaced = e => surfaced.Add(e) };

            var resumeSteps = ListModule.OfArray(new[] { ScriptedStep.NewRepliesWith("unexpected") });
            var agent2 = new ScriptedAgent(resumeSteps, null, null, null);

            await using var bridge2 = await MafTelegramBridge.StartPollingAsync(config2, agent2, settings);

            server.EnqueueResult(
                "getUpdates",
                TelegramJson.batch(
                    ListModule.OfArray(new[] { TelegramJson.callbackQueryUpdate(1, "q-csharp-corrupt", approveToken, chatId, 1, chatId, "Tester") })));

            await WaitUntilAsync(() => surfaced.Any(e => e.Kind == "SessionRestoreFailed"), 5000, ct);

            Assert.Contains(surfaced, e => e.Kind == "SessionRestoreFailed");
            Assert.Equal(0, agent2.RunCount);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>
/// Idiom-leak canary for the durable-session opt-in's own C#-facing surface, mirroring both
/// <see cref="IdiomLeakCanaryTests"/> (the base pattern: no FSharp.Core type in a
/// public signature) and <see cref="MafIdiomLeakCanaryTests"/> (scoping the walk to the roots a
/// C# caller actually touches, rather than the whole assembly). A C# host touches exactly three
/// things to opt in: <see cref="TgBotConfig.WithSessionStore(TgLLM.Core.ISessionStore)"/> /
/// its <c>idleAfter</c> overload, and the two durable stores' own <c>OpenAt</c> factories
/// (<see cref="FileSessionStore.OpenAt"/>, <see cref="LiteDbSessionStore.OpenAt"/>) — none of
/// those three own DECLARED signatures may leak FSharp.Core. <c>TgLLM.Core.ISessionStore</c>
/// itself is NOT part of that clean surface: like <c>IBindingStore</c> (see
/// <see cref="IdiomLeakCanaryTests.Walking_one_member_level_into_the_raw_store_seam_detects_the_known_FSharpOption_leak"/>),
/// its own <c>TryGet</c> returns <c>ValueTask&lt;SessionRecord voption&gt;</c> — a raw F# seam a
/// C# host receives a PRE-BUILT store through, never implements itself. The second test below
/// documents that leak is real (not a dead detector), the same way the existing
/// <c>IBindingStore</c> canary does for its own seam.
/// </summary>
public class MafSessionStoreIdiomLeakCanaryTests
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
        PropertyInfo property => new[] { property.PropertyType },
        FieldInfo field => new[] { field.FieldType },
        _ => Array.Empty<Type>(),
    };

    private static List<string> FSharpCoreOffenders(IEnumerable<MemberInfo> members) =>
        (from member in members
         from signatureType in SignatureTypes(member)
         from involved in Flatten(signatureType)
         where involved.Namespace?.StartsWith("Microsoft.FSharp", StringComparison.Ordinal) == true
         select $"{member.DeclaringType!.Name}.{member.Name} → {involved.FullName}")
        .Distinct()
        .ToList();

    [Fact]
    public void The_C_sharp_opt_in_surface_exposes_no_FSharpCore_types()
    {
        var withSessionStoreOverloads = typeof(TgBotConfig)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == nameof(TgBotConfig.WithSessionStore));

        var openAtFactories = new[]
        {
            typeof(FileSessionStore).GetMember(nameof(FileSessionStore.OpenAt), BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Single(),
            typeof(LiteDbSessionStore).GetMember(nameof(LiteDbSessionStore.OpenAt), BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Single(),
        };

        var offenders = FSharpCoreOffenders(withSessionStoreOverloads.Concat(openAtFactories));

        Assert.True(offenders.Count == 0, "F# types leaked into the durable-session opt-in surface:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Walking_ISessionStore_own_members_detects_the_known_FSharpValueOption_leak_exactly_like_IBindingStore()
    {
        var offenders = FSharpCoreOffenders(
            typeof(ISessionStore).GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));

        Assert.NotEmpty(offenders);
    }

    [Fact]
    public void FileSessionStore_and_LiteDbSessionStore_expose_ONLY_their_OpenAt_factory_publicly_the_ISessionStore_implementation_is_explicit()
    {
        // Confirms the assumption `The_C_sharp_opt_in_surface_exposes_no_FSharpCore_types` relies
        // on: the F# `interface ISessionStore with ...` block on each concrete store compiles to
        // an EXPLICIT interface implementation, so `Save`/`TryGet`/`Remove`/`EvictIdle` (the
        // members that DO carry `voption`/`ValueTask` F#-flavored shapes) never surface on the
        // concrete type's own public, declared-only member list — only the plain `OpenAt` factory
        // does.
        var fileMembers = typeof(FileSessionStore)
            .GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Distinct()
            .ToList();

        var liteDbMembers = typeof(LiteDbSessionStore)
            .GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Distinct()
            .ToList();

        Assert.DoesNotContain(nameof(ISessionStore.Save), fileMembers);
        Assert.DoesNotContain(nameof(ISessionStore.TryGet), fileMembers);
        Assert.DoesNotContain(nameof(ISessionStore.Save), liteDbMembers);
        Assert.DoesNotContain(nameof(ISessionStore.TryGet), liteDbMembers);
    }
}
