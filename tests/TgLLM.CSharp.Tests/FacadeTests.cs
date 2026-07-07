using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using TgLLM.CSharp;
using Xunit;

namespace TgLLM.CSharp.Tests;

public class KeyboardBuilderTests
{
    [Fact]
    public void Build_succeeds_for_a_valid_layout()
    {
        var keyboard = new KeyboardBuilder()
            .Row(r => r
                .Button("Yes", ctx => ctx.ReplyTextAsync("You picked Yes"))
                .Button("No", ctx => ctx.ReplyTextAsync("You picked No")))
            .Build();

        Assert.NotNull(keyboard);
    }

    [Fact]
    public void Build_throws_TgKeyboardException_on_an_empty_label()
    {
        var builder = new KeyboardBuilder().Row(r => r.Button("", _ => Task.CompletedTask));

        Assert.Throws<TgKeyboardException>(() => builder.Build());
    }

    [Fact]
    public void Build_throws_TgKeyboardException_on_an_empty_keyboard()
    {
        Assert.Throws<TgKeyboardException>(() => new KeyboardBuilder().Build());
    }
}

/// <summary>
/// Idiom-leak canary (Principle II): no member of the C# façade's public surface may reference
/// a type from FSharp.Core (FSharpFunc / FSharpOption / FSharpValueOption / FSharpResult / FSharpList).
/// </summary>
public class IdiomLeakCanaryTests
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

    [Fact]
    public void Public_surface_references_no_FSharpCore_types()
    {
        var assembly = typeof(TelegramAgent).Assembly;

        var offenders =
            (from type in assembly.GetExportedTypes()
             from member in type.GetMembers(
                 BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
             from signatureType in SignatureTypes(member)
             from involved in Flatten(signatureType)
             where involved.Namespace?.StartsWith("Microsoft.FSharp", StringComparison.Ordinal) == true
             select $"{type.Name}.{member.Name} → {involved.FullName}")
            .Distinct()
            .ToList();

        Assert.True(offenders.Count == 0, "F# types leaked into the C# surface:\n" + string.Join("\n", offenders));
    }

    /// <summary>
    /// For each public member of <paramref name="root"/>, collects every signature type reachable
    /// via <see cref="Flatten"/> — i.e. walks ONE further member level than
    /// <see cref="Public_surface_references_no_FSharpCore_types"/> does. That test only ever sees
    /// types DIRECTLY in a <c>TgLLM.CSharp</c> member's own signature; it never looks at what a
    /// REFERENCED type's (e.g. an interface a C# host is meant to implement) own members return.
    /// Deliberately scoped to specific root types by its callers rather than run assembly-wide:
    /// <see cref="TelegramAgentOptions.BindingStore"/> already exposes the raw
    /// <c>TgLLM.Core.IBindingStore</c> on the C# surface, which is a legitimate way to hand the
    /// library a PRE-BUILT F# store (e.g. <c>FileBindingStore.OpenAt</c>) — not an invitation to
    /// IMPLEMENT that interface in C#, so running this walk over it would flag an intentional,
    /// already-accepted design as an offender. This walk targets only the store EXTENSION POINT a
    /// C# host is meant to implement (<see cref="IBindingStoreCSharp"/>/<see cref="ToolBindingDto"/>).
    /// </summary>
    private static IEnumerable<Type> OneMemberLevelDeep(Type root) =>
        from member in root.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
        from signatureType in SignatureTypes(member)
        from involved in Flatten(signatureType)
        select involved;

    [Fact]
    public void Walking_one_member_level_into_the_raw_store_seam_detects_the_known_FSharpOption_leak()
    {
        // Proves the walker below isn't a no-op: TgLLM.Core.IBindingStore.TryGet returns
        // ValueTask<FSharpValueOption<ToolBinding>> — exactly what a C# host would hit if it
        // implemented the raw F# seam directly instead of IBindingStoreCSharp. If this ever stopped
        // finding an offender here, the store-surface test below would be trusting a detector that
        // can't actually detect anything.
        var offenders = OneMemberLevelDeep(typeof(TgLLM.Core.IBindingStore))
            .Where(t => t.Namespace?.StartsWith("Microsoft.FSharp", StringComparison.Ordinal) == true)
            .ToList();

        Assert.NotEmpty(offenders);
    }

    [Fact]
    public void The_C_sharp_store_extension_point_exposes_no_FSharpCore_types_one_member_level_deep()
    {
        var offenders =
            new[] { typeof(IBindingStoreCSharp), typeof(ToolBindingDto) }
                .SelectMany(OneMemberLevelDeep)
                .Where(t => t.Namespace?.StartsWith("Microsoft.FSharp", StringComparison.Ordinal) == true)
                .Distinct()
                .ToList();

        Assert.True(
            offenders.Count == 0,
            "F# types leaked into the C# store extension point:\n" + string.Join("\n", offenders.Select(t => t.FullName)));
    }
}
