using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TgLLM.Maf;
using Xunit;

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
