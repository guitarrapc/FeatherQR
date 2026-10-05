using System.Reflection;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

// BenchmarkDotNet over the FeatherQR CLI's calls and corpus, for the cross-language benchmark's first check:
// the CLI's self-timed medians must agree with these. The corpus is read from XLANG_CORPUS (default /opt/xlang/corpus).
//
//   dotnet run -c Release --project tools/CrossLanguageBenchmark/dotnet/reference -- --filter "*" [--launchCount 3]
BenchmarkSwitcher.FromAssembly(Assembly.GetEntryAssembly()!).Run(args, DefaultConfig.Instance
    .AddExporter(JsonExporter.Full)
    .WithSummaryStyle(SummaryStyle.Default.WithMaxParameterColumnWidth(64)));

public class CorpusEntries
{
    private static string CorpusDir => Environment.GetEnvironmentVariable("XLANG_CORPUS") ?? "/opt/xlang/corpus";

    public static IEnumerable<string> Keys() => ManifestEntry.Read(CorpusDir).Select(e => e.Key);

    [ParamsSource(nameof(Keys))]
    public string Case { get; set; } = "";

    private Func<ulong> _call = default!;

    [GlobalSetup]
    public void Setup()
    {
        var entry = ManifestEntry.Read(CorpusDir).Single(e => e.Key == Case);
        _call = Operations.Load(entry.Op, entry.Symbology, Path.Combine(CorpusDir, entry.Input), entry.Ecc, entry.Version).Call;
    }

    [Benchmark]
    public ulong Run() => _call();
}

/// <summary>
/// zxing-cpp's .NET package over the same corpus, through the calls its CLI makes: the wrapper's cost is this against the native zxing-cpp CLI.
/// The package has no matrix decoder, so only the encode and image decode entries run.
/// </summary>
public class ZXingCppEntries
{
    private static string CorpusDir => Environment.GetEnvironmentVariable("XLANG_CORPUS") ?? "/opt/xlang/corpus";

    public static IEnumerable<string> Keys() => ManifestEntry.Read(CorpusDir).Where(e => e.Op != "decode-matrix").Select(e => e.Key);

    [ParamsSource(nameof(Keys))]
    public string Case { get; set; } = "";

    private Func<ulong> _call = default!;

    [GlobalSetup]
    public void Setup()
    {
        var entry = ManifestEntry.Read(CorpusDir).Single(e => e.Key == Case);
        _call = ZXingCppOperations.Load(entry.Op, entry.Symbology, Path.Combine(CorpusDir, entry.Input), entry.Ecc, entry.Version).Call;
    }

    [Benchmark]
    public ulong Run() => _call();
}
