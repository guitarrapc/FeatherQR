#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0

using System.IO.Compression;
using System.Xml.Linq;

// Asserts the dependency graph of the four packed packages, at the artifact level.
//
//   dotnet run tools/check_package_deps.cs -- <directory-or-nupkg>...
//
// Opens each .nupkg, reads the nuspec dependency groups per target framework, and compares
// them against the expectation below, exactly: nothing missing, nothing extra, in every group.
// It also checks that the metapackage carries no lib/ folder and that no package but the
// rendering one depends on anything named SkiaSharp. All four packages must be present.
//
// This is the second regression guard for the core split. CoreAssemblyDependencyTest reads the
// assembly references from metadata. This reads what a consumer's restore would resolve,
// which is what the split exists to control: a dependency that slips into a csproj (a
// PackageReference without PrivateAssets, a transitive promotion) reaches the nuspec and
// nothing else in CI would notice.
//
// The expectation is deliberately a literal table, not derived from the csproj files, so that
// a change to the graph is a diff here that someone accepts on purpose.

var expected = new Dictionary<string, Dictionary<string, string[]>>(StringComparer.OrdinalIgnoreCase)
{
    // The core has no dependencies on the frameworks that carry Span, ArrayPool and Unsafe
    // themselves. On netstandard those live in BCL shim packages, the same ones every
    // netstandard library that uses Span depends on. Nothing here is a library dependency.
    ["FeatherQR"] = new(StringComparer.OrdinalIgnoreCase)
    {
        [".NETStandard2.0"] = ["System.Memory"],
        [".NETStandard2.1"] = ["System.Runtime.CompilerServices.Unsafe"],
        ["net8.0"] = [],
        ["net10.0"] = [],
    },
    // The rendering package depends on the core at the lockstep version and on SkiaSharp,
    // nothing else: SkiaSharp brings its own native assets, and the netstandard shims arrive
    // through FeatherQR.
    ["FeatherQR.SkiaSharp"] = new(StringComparer.OrdinalIgnoreCase)
    {
        [".NETStandard2.0"] = ["FeatherQR", "SkiaSharp"],
        [".NETStandard2.1"] = ["FeatherQR", "SkiaSharp"],
        ["net8.0"] = ["FeatherQR", "SkiaSharp"],
        ["net10.0"] = ["FeatherQR", "SkiaSharp"],
    },
    // The GS1 data package depends on the core at the lockstep version and nothing else: it works on text,
    // adds its entry points to the core generators, and brings the Apache-2.0 notice for its generated table
    // (gs1-support-plan.md, G2 and G8). The netstandard shims arrive through FeatherQR.
    ["FeatherQR.Gs1"] = new(StringComparer.OrdinalIgnoreCase)
    {
        [".NETStandard2.0"] = ["FeatherQR"],
        [".NETStandard2.1"] = ["FeatherQR"],
        ["net8.0"] = ["FeatherQR"],
        ["net10.0"] = ["FeatherQR"],
    },
    // The compatibility metapackage is a dependency and nothing else.
    ["SkiaSharp.QrCode"] = new(StringComparer.OrdinalIgnoreCase)
    {
        [".NETStandard2.0"] = ["FeatherQR.SkiaSharp"],
        [".NETStandard2.1"] = ["FeatherQR.SkiaSharp"],
        ["net8.0"] = ["FeatherQR.SkiaSharp"],
        ["net10.0"] = ["FeatherQR.SkiaSharp"],
    },
};

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: dotnet run tools/check_package_deps.cs -- <directory-or-nupkg>...");
    return 2;
}

var packages = args
    .SelectMany(arg => Directory.Exists(arg) ? Directory.EnumerateFiles(arg, "*.nupkg") : [arg])
    .Where(path => !path.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase))
    .OrderBy(path => path, StringComparer.Ordinal)
    .ToArray();

var failures = new List<string>();
var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

foreach (var path in packages)
{
    using var zip = ZipFile.OpenRead(path);
    var nuspecEntry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains('/'));
    if (nuspecEntry is null)
    {
        failures.Add($"{Path.GetFileName(path)}: no nuspec at the package root.");
        continue;
    }

    XDocument nuspec;
    using (var stream = nuspecEntry.Open()) nuspec = XDocument.Load(stream);
    XNamespace ns = nuspec.Root!.Name.Namespace;
    var metadata = nuspec.Root.Element(ns + "metadata")!;
    var id = metadata.Element(ns + "id")!.Value;
    var version = metadata.Element(ns + "version")!.Value;
    seen.Add(id);

    if (!expected.TryGetValue(id, out var expectedGroups))
    {
        failures.Add($"{id} {version}: not one of the four packages this repository ships.");
        continue;
    }

    // Dependency groups as the nuspec spells them. A package with no <dependencies> element at
    // all, or a flat list without groups, is a shape this repository never produces. It is
    // reported as "no groups" and fails against every expected framework.
    var groups = metadata.Element(ns + "dependencies")?.Elements(ns + "group")
        .ToDictionary(
            g => g.Attribute("targetFramework")?.Value ?? "",
            g => g.Elements(ns + "dependency").Select(d => d.Attribute("id")?.Value ?? "").OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToArray(),
            StringComparer.OrdinalIgnoreCase)
        ?? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

    foreach (var (framework, want) in expectedGroups)
    {
        var wanted = want.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!groups.TryGetValue(framework, out var got))
        {
            failures.Add($"{id} {version}: no dependency group for {framework} (expected [{string.Join(", ", wanted)}]).");
            continue;
        }
        if (!got.SequenceEqual(wanted, StringComparer.OrdinalIgnoreCase))
            failures.Add($"{id} {version}: {framework} depends on [{string.Join(", ", got)}], expected [{string.Join(", ", wanted)}].");
    }
    foreach (var framework in groups.Keys.Where(f => !expectedGroups.ContainsKey(f)))
        failures.Add($"{id} {version}: unexpected dependency group {framework} [{string.Join(", ", groups[framework])}].");

    // Lockstep: every dependency on a sibling package names this same version.
    foreach (var dependency in metadata.Element(ns + "dependencies")?.Descendants(ns + "dependency") ?? [])
    {
        var dependencyId = dependency.Attribute("id")?.Value ?? "";
        var range = dependency.Attribute("version")?.Value ?? "";
        if (expected.ContainsKey(dependencyId) && range != version)
            failures.Add($"{id} {version}: depends on {dependencyId} {range}, expected the lockstep version {version}.");
    }

    var hasLib = zip.Entries.Any(e => e.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase));
    var isMetapackage = id.Equals("SkiaSharp.QrCode", StringComparison.OrdinalIgnoreCase);
    if (isMetapackage && hasLib)
        failures.Add($"{id} {version}: the metapackage must carry no lib/ folder.");
    if (!isMetapackage && !hasLib)
        failures.Add($"{id} {version}: no lib/ folder; the assembly did not get packed.");

    Console.WriteLine($"{id} {version}: {string.Join("; ", groups.OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} [{string.Join(", ", g.Value)}]"))}{(hasLib ? "" : "; no lib/")}");
}

foreach (var id in expected.Keys.Where(id => !seen.Contains(id)))
    failures.Add($"{id}: package not found among {packages.Length} nupkg file(s).");

if (failures.Count > 0)
{
    Console.Error.WriteLine();
    foreach (var failure in failures) Console.Error.WriteLine($"error: {failure}");
    return 1;
}

Console.WriteLine($"{seen.Count} package(s) match the expected dependency graph.");
return 0;
