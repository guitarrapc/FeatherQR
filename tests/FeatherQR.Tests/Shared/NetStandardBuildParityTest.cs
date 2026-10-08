using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;

namespace FeatherQR.Tests;

/// <summary>
/// The netstandard builds' span outputs against this host's, on a dirty destination: each symbology, both segmentations, quiet zones
/// from 0 up. No test host runs a netstandard build, and Standard QR's quiet-zone span path on netstandard2.0 is code of its
/// own (a pooled core copied into the cleared destination, where the other builds move the core in place), so this loads the build
/// beside the core project into a load context of its own and calls its public span API.
/// </summary>
/// <remarks>
/// The builds are found and checked for staleness as <see cref="CoreAssemblyDependencyTest"/> finds them: <c>dotnet build</c> of the
/// solution produces them before <c>dotnet test</c> runs, and a build older than a source of the core is skipped (failed on CI).
/// The span API takes spans, which reflection cannot pass, so each entry is called through a dynamic method that makes them from arrays.
/// </remarks>
public class NetStandardBuildParityTest
{
    private delegate int SpanCreate(string text, int eccLevel, byte[] destination, object options);

    // Lazy, so that cases starting together load a build once: a load context is not unloaded.
    private static readonly ConcurrentDictionary<string, Lazy<Assembly>> Builds = new();

    public static IEnumerable<Func<string>> Frameworks() =>
        CoreAssemblyDependencyTest.CoreTargetFrameworks().Select(f => f()).Where(f => f.StartsWith("netstandard", StringComparison.Ordinal)).Select(f => (Func<string>)(() => f));

    public static IEnumerable<Func<(string TargetFramework, string Symbology)>> Cases()
    {
        foreach (var framework in Frameworks().Select(f => f()))
        {
            foreach (var symbology in new[] { "QR", "MicroQR", "RmQR" })
                yield return () => (framework, symbology);
        }
    }

    /// <summary>
    /// Which netstandard build keeps Standard QR's old quiet-zone path. netstandard2.0, which .NET Framework runs, keeps the pooled core
    /// copied into the cleared destination, since the move in place measured slower on .NET Framework 4.8. netstandard2.1, which .NET 6
    /// and 7 run, moves the core in place as .NET 8 and later do. The old path's row copy, <c>CopyIntoWindow</c>, is compiled only into a
    /// build that keeps the path, so its presence tells the two apart; <see cref="SpanCreate_DirtyDestination_MatchesThisHostsBuild"/>
    /// holds either path's output to this host's.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Frameworks))]
    public async Task StandardQRQuietZone_KeepsTheOldPathOnNetStandard20Only(string targetFramework)
    {
        var (build, _) = await LoadBuild(targetFramework);
        var copyIntoWindow = build.GetType("FeatherQR.QRCodeGenerator", throwOnError: true)!.GetMethod("CopyIntoWindow", BindingFlags.NonPublic | BindingFlags.Static);

        await Assert.That(copyIntoWindow is not null).IsEqualTo(targetFramework == "netstandard2.0")
            .Because($"{targetFramework}: only the netstandard2.0 build keeps the pooled core copied into the cleared destination; the others move the core in place");
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task SpanCreate_DirtyDestination_MatchesThisHostsBuild(string targetFramework, string symbology)
    {
        var (build, path) = await LoadBuild(targetFramework);

        // Texts of one to many versions, and of mixed modes that Optimal segments into a smaller symbol, so that the generator's planned
        // path runs: the URL for Standard QR (its planned span site), "a1234567890" for Micro QR, and the mixed text for rMQR. Standard QR's
        // longest texts take the scalar mask selection's two-word rows (500 bytes, version 17) and three-word rows (1,200 bytes, version 29).
        // Standard QR's and Micro QR's quiet zones bracket a margin of one module and one wider than a version 1 or M1 core, and each
        // symbology's reach each form of the gap between two rows (Standard QR's on the builds that move its core in place). From q = 9 the
        // netstandard builds clear a gap in one call where the host's writes two 16-byte stores up to q = 16 and four up to q = 32, and from
        // q = 33 both clear it.
        var (texts, quietZones, eccLevel, host) = symbology switch
        {
            "QR" => (new[] { "HELLO WORLD", new string('A', 40), "https://example.com/item?id=123456789012345678901234567890", "HELLO WORLD 1234567890123456789012345678901234567890", new string('7', 300), new string('z', 500), new string('z', 1200) },
                new[] { 0, 1, 2, 4, 7, 16, 17, 25, 32, 33 }, (int)QREccLevel.M, (Func<string, int, bool, byte[]>)HostQR),
            "MicroQR" => (new[] { "12345", "HELLO", "a1234567890", "0123456789012345678901234567890" },
                new[] { 0, 1, 2, 4, 5, 8, 9, 16, 17, 25, 32, 33 }, (int)MicroQREccLevel.L, HostMicroQR),
            _ => (new[] { "HELLO WORLD", "1234567890123", "HELLO WORLD 1234567890123456789012345678901234567890", new string('k', 120) },
                new[] { 0, 1, 2, 4, 5, 8, 9, 16, 17, 32, 33 }, (int)RmQREccLevel.M, (Func<string, int, bool, byte[]>)HostRmQR),
        };
        await Assert.That(texts.Count(text => host(text, 0, true).Length < host(text, 0, false).Length)).IsGreaterThan(0)
            .Because($"a {symbology} text must segment under Optimal into a smaller symbol than Single, so that the planned path runs");

        var create = BindSpanCreate(build, symbology, path);
        var optionsType = build.GetType($"FeatherQR.{symbology}CodeGeneratorOptions", throwOnError: true)!;
        var segmentationType = build.GetType($"FeatherQR.{symbology}Segmentation", throwOnError: true)!;

        foreach (var text in texts)
        {
            foreach (var optimal in new[] { false, true })
            {
                foreach (var quietZone in quietZones)
                {
                    var expected = host(text, quietZone, optimal);
                    var options = Activator.CreateInstance(optionsType)!;
                    optionsType.GetProperty("QuietZoneSize")!.SetValue(options, quietZone);
                    optionsType.GetProperty("Segmentation")!.SetValue(options, Enum.ToObject(segmentationType, optimal ? 1 : 0));

                    var destination = new byte[expected.Length + 7];
                    destination.AsSpan().Fill(0xA5);
                    var written = create(text, eccLevel, destination, options);

                    var context = $"{targetFramework} {symbology} \"{(text.Length > 20 ? text[..20] + "..." : text)}\" {(optimal ? "Optimal" : "Single")} quiet zone {quietZone}";
                    if (written != expected.Length)
                        Assert.Fail($"{context}: wrote {written} bytes, this host's build {expected.Length}");
                    var matrix = destination.AsSpan(0, written);
                    if (!matrix.SequenceEqual(expected))
                    {
                        var first = matrix.CommonPrefixLength(expected);
                        Assert.Fail($"{context}: byte {first} is {matrix[first]}, this host's build {expected[first]}");
                    }
                    if (destination.AsSpan(written).IndexOfAnyExcept((byte)0xA5) >= 0)
                        Assert.Fail($"{context}: wrote past the {written} bytes of the matrix");
                }
            }
        }
    }

    /// <summary>The core's build for <paramref name="targetFramework"/>, loaded once into a load context of its own; a stale build skips the case.</summary>
    private static async Task<(Assembly Build, string Path)> LoadBuild(string targetFramework)
    {
        var path = CoreAssemblyDependencyTest.FindCoreBuild(targetFramework);
        await Assert.That(path).IsNotNull()
            .Because($"no FeatherQR.dll for {targetFramework} under src/FeatherQR/bin; build the solution (dotnet build) before running the tests");
        CoreAssemblyDependencyTest.SkipIfStale(path!);
        return (Builds.GetOrAdd(path!, p => new Lazy<Assembly>(() => new AssemblyLoadContext("FeatherQR " + targetFramework).LoadFromStream(new MemoryStream(File.ReadAllBytes(p))))).Value, path!);
    }

    private static byte[] HostQR(string text, int quietZone, bool optimal)
    {
        var options = new QRCodeGeneratorOptions { QuietZoneSize = quietZone, Segmentation = optimal ? QRSegmentation.Optimal : QRSegmentation.Single };
        var matrix = new byte[Sizing.Required(text.AsSpan(), QREccLevel.M, options).BufferSize];
        QRCodeGenerator.Create(text.AsSpan(), QREccLevel.M, matrix, options);
        return matrix;
    }

    private static byte[] HostMicroQR(string text, int quietZone, bool optimal)
    {
        var options = new MicroQRCodeGeneratorOptions { QuietZoneSize = quietZone, Segmentation = optimal ? MicroQRSegmentation.Optimal : MicroQRSegmentation.Single };
        var matrix = new byte[Sizing.Required(text.AsSpan(), MicroQREccLevel.L, options).BufferSize];
        MicroQRCodeGenerator.Create(text.AsSpan(), MicroQREccLevel.L, matrix, options);
        return matrix;
    }

    private static byte[] HostRmQR(string text, int quietZone, bool optimal)
    {
        var options = new RmQRCodeGeneratorOptions { QuietZoneSize = quietZone, Segmentation = optimal ? RmQRSegmentation.Optimal : RmQRSegmentation.Single };
        var matrix = new byte[Sizing.Required(text.AsSpan(), RmQREccLevel.M, options).BufferSize];
        RmQRCodeGenerator.Create(text.AsSpan(), RmQREccLevel.M, matrix, options);
        return matrix;
    }

    /// <summary>The loaded build's <c>{symbology}CodeGenerator.Create(ReadOnlySpan&lt;char&gt;, ecc, Span&lt;byte&gt;, in options)</c>, taking arrays.</summary>
    private static SpanCreate BindSpanCreate(Assembly build, string symbology, string path)
    {
        var generator = build.GetType($"FeatherQR.{symbology}CodeGenerator", throwOnError: true)!;
        var eccLevel = build.GetType($"FeatherQR.{symbology}EccLevel", throwOnError: true)!;
        var options = build.GetType($"FeatherQR.{symbology}CodeGeneratorOptions", throwOnError: true)!;
        var target = generator.GetMethod("Create", [typeof(ReadOnlySpan<char>), eccLevel, typeof(Span<byte>), options.MakeByRefType()])
            ?? throw new InvalidOperationException($"{generator.FullName}.Create(ReadOnlySpan<char>, {eccLevel.Name}, Span<byte>, in {options.Name}) not found in {path}");

        var method = new DynamicMethod($"{symbology}.Create", typeof(int), [typeof(string), typeof(int), typeof(byte[]), typeof(object)], typeof(NetStandardBuildParityTest).Module, skipVisibility: true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(MemoryExtensions).GetMethod(nameof(MemoryExtensions.AsSpan), [typeof(string)])!);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Newobj, typeof(Span<byte>).GetConstructor([typeof(byte[])])!);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Unbox, options);
        il.Emit(OpCodes.Call, target);
        il.Emit(OpCodes.Ret);
        return method.CreateDelegate<SpanCreate>();
    }
}
