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
/// Idiom-leak canary (T033, Principle II): no member of the C# façade's public surface may reference
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
}
