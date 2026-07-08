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
using TelegramJson = TgLLM.Integration.Tests.FakeBotApiServer.TelegramJson;

namespace TgLLM.CSharp.Tests;

/// <summary>
/// Idiom-leak canary for the MAF bridge's C#-idiomatic surface (Principle II, applied to the leaf
/// per its own design: the leaf ships BOTH an idiomatic F# surface and a C#-clean one in the SAME
/// assembly, so — unlike <see cref="IdiomLeakCanaryTests"/>'s assembly-wide scan of
/// <c>TgLLM.CSharp.dll</c> — this canary is scoped to the specific C#-facing types, not the whole
/// <c>TgLLM.Maf.dll</c> (which also legitimately exposes F#-idiomatic types like <c>MafBridge</c>
/// and <c>ApprovalFormatter = ApprovalPrompt -&gt; ApprovalRender</c>, i.e. an
/// <c>FSharpFunc</c>, by design for its F# surface).
/// </summary>
public class MafIdiomLeakCanaryTests
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
        EventInfo evt when evt.EventHandlerType is not null => new[] { evt.EventHandlerType },
        _ => Array.Empty<Type>(),
    };

    private static readonly Type[] CSharpSurfaceRoots =
    {
        typeof(MafTelegramBridge),
        typeof(MafBridgeSettings),
        typeof(ApprovalPromptInfo),
        typeof(ApprovalRenderInfo),
        typeof(MafSurfacedEvent),
        typeof(MafTools),
        typeof(ToolProjectionResult),
    };

    [Fact]
    public void The_MAF_bridge_C_sharp_surface_exposes_no_FSharpCore_types()
    {
        var offenders =
            (from type in CSharpSurfaceRoots
             from member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
             from signatureType in SignatureTypes(member)
             from involved in Flatten(signatureType)
             where involved.Namespace?.StartsWith("Microsoft.FSharp", StringComparison.Ordinal) == true
             select $"{type.Name}.{member.Name} → {involved.FullName}")
            .Distinct()
            .ToList();

        Assert.True(offenders.Count == 0, "F# types leaked into the MAF bridge's C# surface:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void MafTelegramBridge_exposes_the_expected_static_factories_and_StartRunAsync_overloads()
    {
        Assert.NotNull(typeof(MafTelegramBridge).GetMethod(
            nameof(MafTelegramBridge.StartPollingAsync),
            new[] { typeof(TgLLM.FSharp.TgBotConfig), typeof(Microsoft.Agents.AI.AIAgent) }));

        Assert.NotNull(typeof(MafTelegramBridge).GetMethod(
            nameof(MafTelegramBridge.StartWebhookAsync),
            new[] { typeof(TgLLM.FSharp.TgWebhookConfig), typeof(Microsoft.Agents.AI.AIAgent) }));

        Assert.NotNull(typeof(MafTelegramBridge).GetMethod(
            nameof(MafTelegramBridge.StartRunAsync),
            new[] { typeof(long), typeof(string) }));

        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(typeof(MafTelegramBridge)));
    }

    [Fact]
    public void MafBridgeSettings_properties_are_plain_BCL_shapes()
    {
        var settings = new MafBridgeSettings();

        Assert.Null(settings.Formatter);
        Assert.False(settings.DefaultOwner.HasValue);
        Assert.False(settings.ApprovalExpiry.HasValue);
        Assert.Null(settings.OnSurfaced);

        settings.DefaultOwner = TgLLM.CSharp.Owner.Anyone;
        Assert.True(settings.DefaultOwner.HasValue);
    }

    [Fact]
    public void ApprovalRenderInfo_one_arg_constructor_fills_in_the_fixed_default_labels()
    {
        var render = new ApprovalRenderInfo("Allow send_email?");

        Assert.Equal("Allow send_email?", render.Body);
        Assert.Equal("Approve", render.ApproveLabel);
        Assert.Equal("Reject", render.RejectLabel);
    }
}

/// <summary>
/// A basic C#-driven text turn through <see cref="MafTelegramBridge"/> — the message seam's
/// <c>OnMessage</c> wiring is entirely automatic (<c>Maf.startPollingWith</c>'s own
/// <c>config.WithOnMessage</c>), so a C# host gets it "for free" from
/// <see cref="MafTelegramBridge.StartPollingAsync(TgBotConfig, Microsoft.Agents.AI.AIAgent)"/> with
/// no separate opt-in, unlike the plain (non-MAF) <see cref="TelegramAgentOptions.OnMessage"/>
/// delta on <see cref="TelegramAgent"/>. Reuses the fake Bot API server and <c>ScriptedAgent</c>
/// the F# integration suite already proves against, mirroring
/// <see cref="OwnerAuthorizationTests"/>'s own C#-driven acceptance pattern.
/// </summary>
public class MafTextTurnSmokeTests
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
    public async Task A_basic_C_sharp_text_turn_works_through_MafTelegramBridge()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeServerModule.start();
        const long chatId = 9501L;

        var steps = ListModule.OfArray(new[] { ScriptedStep.NewRepliesWith("Hello from C#!") });
        var agent = new ScriptedAgent(steps, null);

        var tools = TgLLM.FSharp.ToolRegistry.create();
        var config = TgBotConfig.create(FakeToken).WithBaseUrl(server.BaseUrl).WithTools(tools);

        await using var bridge = await MafTelegramBridge.StartPollingAsync(config, agent);

        server.EnqueueResult(
            "getUpdates",
            TelegramJson.batch(
                ListModule.OfArray(new[] { TelegramJson.textMessageUpdate(1, chatId, 5, 4501L, "Nadia", "Hi from a C# test") })));

        await WaitUntilAsync(() => server.RequestsFor("sendMessage").Any(), 5000, ct);

        var sent = server.RequestsFor("sendMessage").First().Body!.Value;
        Assert.Equal("Hello from C#!", sent["text"]!.GetValue<string>());
    }
}
