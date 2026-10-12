using FeatherQR.Gs1.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// The GS1 package's dependency rule, read from the loaded assembly: it references the core and the
/// framework, and nothing else. In particular no SkiaSharp, which it would inherit from the test host
/// unnoticed, and no package of its own, since the dictionary data is compiled in. The packed graph is
/// checked at the artifact level by <c>tools/check_package_deps.cs</c> (`FeatherQR.Gs1` depends on
/// `FeatherQR` alone, on every target framework).
/// </summary>
public class Gs1AssemblyDependencyTest
{
    [Test]
    public async Task LoadedGs1Assembly_ReferencesTheCoreAndTheFrameworkOnly()
    {
        var assembly = typeof(Gs1AiTable).Assembly;
        await Assert.That(assembly.GetName().Name).IsEqualTo("FeatherQR.Gs1");

        var references = assembly.GetReferencedAssemblies().Select(a => a.Name!).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var unexpected = references.Where(n => n != "FeatherQR" && !n.StartsWith("System.", StringComparison.Ordinal) && n != "System" && n != "netstandard" && n != "mscorlib").ToArray();
        await Assert.That(unexpected).IsEmpty().Because($"FeatherQR.Gs1 references: {string.Join(", ", references)}");
        await Assert.That(references.Where(n => n.Contains("Skia", StringComparison.OrdinalIgnoreCase))).IsEmpty();
    }

    /// <summary>The table is data in the assembly, not a resource or a file: nothing to load, nothing to allocate. The one resource every assembly of this repository embeds is its own LICENSE (Directory.Build.props).</summary>
    [Test]
    public async Task Gs1Assembly_CarriesNoResourceButTheLicense()
    {
        var resources = typeof(Gs1AiTable).Assembly.GetManifestResourceNames();
        await Assert.That(resources.Length).IsEqualTo(1);
        await Assert.That(resources[0]).EndsWith("LICENSE");
    }
}
