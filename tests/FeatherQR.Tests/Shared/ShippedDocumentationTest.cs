using System.Xml.Linq;
using FeatherQR.SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// The XML documentation files that ship beside the two assemblies, which is what gives
/// consumers IntelliSense.
/// </summary>
/// <remarks>
/// Trimming them to the reachable surface is tools/filter_public_docs.cs, which the workflows
/// run after the build and which checks its own work. Only what holds on an untrimmed file
/// belongs here, or a local build would be red by default.
/// </remarks>
public class ShippedDocumentationTest
{
    /// <summary>One shipped assembly and the type that identifies it.</summary>
    public sealed record ShippedAssembly(string AssemblyName, Type Anchor)
    {
        public override string ToString() => AssemblyName;
    }

    public static IEnumerable<Func<ShippedAssembly>> ShippedAssemblies()
    {
        yield return () => new ShippedAssembly("FeatherQR", typeof(QRCodeData));
        yield return () => new ShippedAssembly("FeatherQR.SkiaSharp", typeof(SymbolRenderer));
    }

    private static string DocumentationPath(string assemblyName) => Path.Combine(AppContext.BaseDirectory, assemblyName + ".xml");

    private static IEnumerable<string> MemberIds(string assemblyName)
        => XDocument.Load(DocumentationPath(assemblyName))
            .Descendants("member")
            .Select(m => (string?)m.Attribute("name"))
            .Where(name => name is not null)!;

    /// <summary>
    /// Without this file a consumer sees bare signatures and no documentation at all, which
    /// is the state the library shipped in through 1.1.1.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(ShippedAssemblies))]
    public async Task DocumentationFile_ShipsBesideTheAssembly(ShippedAssembly shipped)
    {
        var path = DocumentationPath(shipped.AssemblyName);
        await Assert.That(File.Exists(path)).IsTrue()
            .Because($"expected the XML documentation at {path}");
    }

    /// <summary>
    /// Every publicly reachable type is documented. Undocumented, or dropped by an over-eager
    /// trim, both leave a consumer hovering a name and seeing nothing.
    /// </summary>
    /// <remarks>
    /// Nested protected types count, so this walks the assembly rather than calling
    /// <c>GetExportedTypes</c>, which leaves them out.
    /// </remarks>
    [Test]
    [MethodDataSource(nameof(ShippedAssemblies))]
    public async Task EveryPublicType_IsDocumented(ShippedAssembly shipped)
    {
        var documented = MemberIds(shipped.AssemblyName).ToHashSet(StringComparer.Ordinal);

        var undocumented = shipped.Anchor.Assembly.GetTypes()
            .Where(IsVisibleType)
            .Select(type => $"T:{(type.FullName ?? type.Name).Replace('+', '.')}")
            .Where(id => !documented.Contains(id))
            .ToArray();

        await Assert.That(undocumented).IsEmpty()
            .Because($"{undocumented.Length} publicly reachable type(s) in {shipped.AssemblyName} have no documentation, starting with: {string.Join(", ", undocumented.Take(5))}");
    }

    /// <summary>
    /// Compiler-generated types are skipped: a C# 14 extension block emits public nested marker
    /// types with unspeakable names (<c>&lt;G&gt;$hash</c>, <c>&lt;M&gt;$hash</c>) that no consumer
    /// can name or document. tools/filter_public_docs.cs applies the same rule.
    /// </summary>
    private static bool IsVisibleType(Type type) => type.IsNested
        ? (type.IsNestedPublic || type.IsNestedFamily || type.IsNestedFamORAssem) && !type.Name.Contains('<') && IsVisibleType(type.DeclaringType!)
        : type.IsPublic;
}
