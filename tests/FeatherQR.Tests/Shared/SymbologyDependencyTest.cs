using System.Text.RegularExpressions;

namespace FeatherQR.Tests;

/// <summary>
/// The dependency rule of the internals (qrcode-symbologies.md, Internal organization): shared code never references a
/// symbology namespace, and symbology namespaces never reference each other. What two symbologies both need moves to
/// the shared namespaces instead.
/// </summary>
/// <remarks>
/// A type of a sibling namespace is only reachable through its namespace spelled out (a <c>using</c> directive of any kind,
/// an alias, a fully qualified name) or a name qualified by it, so those are what is read. Comments are not code, so a
/// documentation link to another symbology is allowed.
/// </remarks>
public class SymbologyDependencyTest
{
    private static readonly string[] Symbologies = ["StandardQR", "MicroQR", "RmQR"];

    [Test]
    public async Task NoInternalFile_ReferencesAnotherSymbology()
    {
        var violations = new List<string>();
        foreach (var (relative, path) in InternalFiles())
        {
            var folder = relative.Contains('/') ? relative.Substring(0, relative.IndexOf('/')) : "";
            foreach (var symbology in Symbologies)
            {
                if (symbology == folder)
                    continue;
                foreach (var line in References(File.ReadAllLines(path), symbology))
                    violations.Add($"{relative}: {line}");
            }
        }

        await Assert.That(violations).IsEmpty().Because(string.Join("; ", violations));
    }

    [Test]
    [Arguments("using FeatherQR.Internals.StandardQR;", true)]
    [Arguments("using static FeatherQR.Internals.StandardQR.QRCodeConstants;", true)]
    [Arguments("using Sampler = FeatherQR.Internals.StandardQR.QRImageDecoder;", true)]
    [Arguments("using FeatherQR.Internals.ImageDecoders; using FeatherQR.Internals.StandardQR;", true)]
    [Arguments("        FeatherQR.Internals.StandardQR.QRImageDecoder.SampleGrid(luminance);", true)]
    [Arguments("        StandardQR.QRImageDecoder.SampleGrid(luminance);", true)]
    [Arguments("/// <see cref=\"StandardQR.QRImageDecoder\"/>", false)]
    [Arguments("        // as StandardQR.QRImageDecoder does", false)]
    [Arguments("using FeatherQR.Internals.ImageDecoders;", false)]
    [Arguments("        var standardQRLike = 1;", false)]
    public async Task References_AreTheNamespaceAndQualifiedNames_NotComments(string line, bool expected)
    {
        await Assert.That(References([line], "StandardQR").Any()).IsEqualTo(expected);
    }

    private static IEnumerable<string> References(string[] lines, string symbology)
    {
        // The namespace spelled out (a using of any kind, an alias, a fully qualified name), or a name qualified from a sibling namespace.
        var fullName = new Regex($@"FeatherQR\.Internals\.{symbology}\b");
        var qualifiedName = new Regex($@"(?<![\w.]){symbology}\.[A-Z]");
        foreach (var line in lines)
        {
            var code = line.TrimStart();
            if (code.StartsWith("//", StringComparison.Ordinal))
                continue;
            var comment = code.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
                code = code.Substring(0, comment);
            if (fullName.IsMatch(code) || qualifiedName.IsMatch(code))
                yield return line.Trim();
        }
    }

    /// <summary>Files under src/FeatherQR/Internals, with their path below it.</summary>
    private static IEnumerable<(string Relative, string Path)> InternalFiles()
    {
        var internals = Path.Combine(RepositoryRoot(), "src", "FeatherQR", "Internals");
        foreach (var file in Directory.EnumerateFiles(internals, "*.cs", SearchOption.AllDirectories))
            yield return (Path.GetRelativePath(internals, file).Replace('\\', '/'), file);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root (Directory.Build.props) not found above " + AppContext.BaseDirectory);
    }
}
