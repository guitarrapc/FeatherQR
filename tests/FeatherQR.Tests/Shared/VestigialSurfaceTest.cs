using System.Reflection;

namespace FeatherQR.Tests;

/// <summary>
/// Types that outlive the feature they described. The <c>Compression</c> enum was one:
/// it named a serialization feature removed before 1.0.0, shipped unused through 1.1.1,
/// was deprecated in 1.2.0 and removed in 2.0.0.
/// </summary>
/// <remarks>
/// A shape test, not a behaviour test. What survives the removal is the general guard,
/// because the way a vestigial type gets noticed is a rule that looks for it, not a
/// reviewer remembering that it exists.
/// </remarks>
public class VestigialSurfaceTest
{
    /// <summary>
    /// An exported type with nothing a caller can touch is not an API, it is an accident:
    /// a helper class whose members all went internal, or a type left behind by a feature
    /// that was removed. <c>EciModeExtensions</c> was exactly that, and shipped that way
    /// through 1.1.1 because nothing looked for it.
    /// </summary>
    /// <remarks>
    /// Enums always carry their members and delegates their signature, so no exemption
    /// list is needed. If a genuinely empty marker type is ever wanted, this test is the
    /// place to say so out loud.
    /// </remarks>
    [Test]
    public async Task NoExportedType_IsEmpty()
    {
        const BindingFlags visible = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        static bool Callable(MethodBase? m) => m is not null && (m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly);

        var empty = new List<string>();
        foreach (var type in typeof(QRCodeGenerator).Assembly.GetExportedTypes())
        {
            var members =
                type.GetFields(visible).Count(f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly)
                + type.GetConstructors(visible).Count(Callable)
                + type.GetMethods(visible).Count(m => Callable(m) && !m.Name.Contains('<'))
                + type.GetProperties(visible).Count(p => Callable(p.GetMethod) || Callable(p.SetMethod))
                + type.GetEvents(visible).Count(e => Callable(e.AddMethod))
                + type.GetNestedTypes(BindingFlags.Public).Length;

            if (members == 0) empty.Add(type.FullName!);
        }

        await Assert.That(empty).IsEmpty()
            .Because($"exported but unusable from outside the assembly: {string.Join(", ", empty)}");
    }
}
