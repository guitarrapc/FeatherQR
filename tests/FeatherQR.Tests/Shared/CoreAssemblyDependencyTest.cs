using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;

namespace FeatherQR.Tests;

/// <summary>
/// The split's regression guard: the core assembly references no SkiaSharp, on every target
/// framework it ships for. A single <c>using SkiaSharp;</c> that slips into the core would
/// pass every functional test (the test host has SkiaSharp loaded) and reach consumers as a
/// native dependency they installed FeatherQR to avoid.
/// </summary>
/// <remarks>
/// Read from metadata, not from the loaded assembly: the loaded one is the test host's
/// framework only, and a framework-conditional reference (<c>#if NETSTANDARD2_0</c>) would
/// hide there. The four builds are found beside the core project, which <c>dotnet build</c>
/// of the solution produces before <c>dotnet test</c> runs. A build older than a source of the
/// core is not read (<see cref="SkipIfStale"/>): building the test project alone builds the core
/// for the test's frameworks only, and leaves the other builds as they were.
/// </remarks>
public class CoreAssemblyDependencyTest
{
    private const string CoreAssemblyName = "FeatherQR";

    /// <summary>The build of the core assembly for one target framework, as it sits in the project's output.</summary>
    public static IEnumerable<Func<string>> CoreTargetFrameworks()
    {
        var project = Path.Combine(RepositoryRoot(), "src", CoreAssemblyName, CoreAssemblyName + ".csproj");
        var frameworks = XDocument.Load(project)
            .Descendants("TargetFrameworks")
            .Select(e => e.Value)
            .Single()
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var tfm in frameworks)
            yield return () => tfm;
    }

    /// <summary>
    /// The one loaded into this test host. Cheap, unconditional, and the one that runs even
    /// when only the test project was built.
    /// </summary>
    [Test]
    public async Task LoadedCoreAssembly_HasNoSkiaSharpReference()
    {
        var references = typeof(QRCodeData).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

        await Assert.That(references.Where(IsSkiaSharp)).IsEmpty()
            .Because($"the core assembly must not depend on SkiaSharp; it references: {string.Join(", ", references)}");
    }

    /// <summary>
    /// The build found for this host's framework is the one this host loaded: the lookup prefers the host's configuration, so a
    /// Debug build left beside a Release run is not the one read.
    /// </summary>
    [Test]
    public async Task FindCoreBuild_ForTheHostsFramework_IsTheLoadedBuild()
    {
        var hostFramework = Path.GetFileName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var path = FindCoreBuild(hostFramework);
        await Assert.That(path).IsNotNull();

        await Assert.That(File.ReadAllBytes(path!).AsSpan().SequenceEqual(File.ReadAllBytes(typeof(QRCodeData).Assembly.Location))).IsTrue()
            .Because($"{path} is the build of {hostFramework} this host loaded");
    }

    /// <summary>
    /// The configuration the lookup prefers is the one this host was built in, whatever builds sit under the core's bin: the folder
    /// the host's own build sits in (bin/&lt;configuration&gt;/&lt;framework&gt;), which the lookup matches ignoring case too.
    /// </summary>
    [Test]
    public async Task HostConfiguration_IsTheOneThisHostWasBuiltIn()
    {
        var folder = Path.GetFileName(Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)));

        await Assert.That(string.Equals(HostConfiguration, folder, StringComparison.OrdinalIgnoreCase)).IsTrue()
            .Because($"the host is built in {HostConfiguration} and sits in {folder}");
    }

    /// <summary>The lookup takes the build of the configuration it is given, and another configuration's when that one has none.</summary>
    [Test]
    [Arguments(new[] { "Debug", "Release" }, "Release", "Release")]
    [Arguments(new[] { "Debug", "Release" }, "Debug", "Debug")]
    [Arguments(new[] { "Debug" }, "Release", "Debug")]
    [Arguments(new[] { "Release" }, "Debug", "Release")]
    public async Task FindBuild_PrefersTheGivenConfiguration(string[] built, string configuration, string expected)
    {
        var bin = Path.Combine(Path.GetTempPath(), "FeatherQR.FindBuild." + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var folder in built)
            {
                Directory.CreateDirectory(Path.Combine(bin, folder, "net10.0"));
                File.WriteAllBytes(Path.Combine(bin, folder, "net10.0", CoreAssemblyName + ".dll"), []);
            }

            await Assert.That(FindBuild(bin, "net10.0", configuration)).IsEqualTo(Path.Combine(bin, expected, "net10.0", CoreAssemblyName + ".dll"));
        }
        finally
        {
            Directory.Delete(bin, recursive: true);
        }
    }

    /// <summary>
    /// Every target framework's build, read from its metadata tables.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(CoreTargetFrameworks))]
    public async Task CoreAssembly_HasNoSkiaSharpReference_OnEveryTargetFramework(string targetFramework)
    {
        var path = FindCoreBuild(targetFramework);
        await Assert.That(path).IsNotNull()
            .Because($"no {CoreAssemblyName}.dll for {targetFramework} under src/{CoreAssemblyName}/bin; build the solution (dotnet build) before running the tests");
        SkipIfStale(path!);

        using var stream = File.OpenRead(path!);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var references = metadata.AssemblyReferences
            .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
            .ToArray();

        await Assert.That(references.Where(IsSkiaSharp)).IsEmpty()
            .Because($"{Path.GetFileName(path)} ({targetFramework}) must not depend on SkiaSharp; it references: {string.Join(", ", references)}");
    }

    private static bool IsSkiaSharp(string assemblyName)
        => assemblyName.Equals("SkiaSharp", StringComparison.OrdinalIgnoreCase)
        || assemblyName.StartsWith("SkiaSharp.", StringComparison.OrdinalIgnoreCase);

    /// <summary>The configuration this test assembly was built with, as the SDK stamps it.</summary>
    internal static string HostConfiguration { get; } = typeof(CoreAssemblyDependencyTest).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "";

    /// <summary>Prefers the configuration this test host was built with, then any other.</summary>
    internal static string? FindCoreBuild(string targetFramework)
        => FindBuild(Path.Combine(RepositoryRoot(), "src", CoreAssemblyName, "bin"), targetFramework, HostConfiguration);

    internal static string? FindBuild(string bin, string targetFramework, string configuration)
    {
        if (!Directory.Exists(bin))
            return null;

        var candidates = Directory.EnumerateDirectories(bin)
            .OrderBy(dir => string.Equals(Path.GetFileName(dir), configuration, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(dir => dir, StringComparer.Ordinal)
            .Select(dir => Path.Combine(dir, targetFramework, CoreAssemblyName + ".dll"));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// A source of the core written after <paramref name="build"/>, or one the build was compiled from that is gone, or null when
    /// the build is current: its C# files, its project, the props the repository imports into every project, and the C# files its
    /// PDB names, which a deletion or a rename leaves behind.
    /// </summary>
    internal static string? SourceNewerThan(string build)
    {
        var built = File.GetLastWriteTimeUtc(build);
        var root = RepositoryRoot();
        var project = Path.Combine(root, "src", CoreAssemblyName);
        string[] output = [Path.Combine(project, "bin") + Path.DirectorySeparatorChar, Path.Combine(project, "obj") + Path.DirectorySeparatorChar];
        return Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)
            .Where(file => !output.Any(dir => file.StartsWith(dir, StringComparison.OrdinalIgnoreCase)))
            .Append(Path.Combine(project, CoreAssemblyName + ".csproj"))
            .Concat(new[] { "Directory.Build.props", "Directory.Packages.props" }.Select(name => Path.Combine(root, name)).Where(File.Exists))
            .FirstOrDefault(file => File.GetLastWriteTimeUtc(file) > built)
            ?? CompiledSourceGone(build, project);
    }

    /// <summary>
    /// A C# file of the core that <paramref name="build"/> was compiled from and that no longer exists, read from the documents of
    /// the build's PDB. A document is found by its path below src/FeatherQR, since a CI build maps the repository to /_/; one below
    /// obj (generated, the source generators' output among it) is not a source.
    /// </summary>
    private static string? CompiledSourceGone(string build, string project)
    {
        var pdb = Path.ChangeExtension(build, ".pdb");
        if (!File.Exists(pdb))
            return null;

        using var stream = File.OpenRead(pdb);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
        var reader = provider.GetMetadataReader();
        var marker = $"/src/{CoreAssemblyName}/";
        foreach (var handle in reader.Documents)
        {
            var name = reader.GetString(reader.GetDocument(handle).Name).Replace('\\', '/');
            var at = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                continue;
            var relative = name.Substring(at + marker.Length);
            if (relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase))
                continue;
            var path = Path.Combine(project, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                return path;
        }
        return null;
    }

    /// <summary>
    /// Skips the case when <paramref name="build"/> is older than a source of the core, naming both: building the test project
    /// alone builds the core for the test's frameworks only and leaves the other builds as they were, as a run of
    /// tools/mutation_check.cs does for each fault. On CI (CI=true), which builds the solution first, a stale build fails the case
    /// instead. tools/mutation_check.cs sets CI to another value, so that SimdTiersDocTest only asserts while these cases still skip.
    /// </summary>
    internal static void SkipIfStale(string build)
    {
        if (SourceNewerThan(build) is not { } newer)
            return;
        var reason = $"{build} is older than {newer}; build the core for every framework (dotnet build src/{CoreAssemblyName} -c {HostConfiguration}) to read it";
        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(reason);
        Skip.Test(reason);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root (Directory.Build.props) not found above " + AppContext.BaseDirectory);
    }
}
