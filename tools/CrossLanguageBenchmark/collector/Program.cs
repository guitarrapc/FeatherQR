using System.Globalization;
using System.Text.Json;

// The cross-language benchmark's collector: it writes the corpus, launches every CLI, verifies what each printed, and computes every statistic,
// so every row uses the same formula. See .github/docs/plans/cross-language-benchmark-plan.md.
//
//   corpus   [--out /opt/xlang/corpus]
//   run      [--corpus DIR] [--clis FILE] [--cli a,b] [--filter text,text] [--rounds 5] [--warmup-ms 3000] [--batch-ms 20] [--batches 30] [--out /out]
//   outside  [--corpus DIR] [--clis FILE] [--run FILE] [--cli a,b] [--filter text,text] [--seconds 1] [--runs 5] [--rounds 2] [--attempts 3] [--out /out]
//   cold     [--corpus DIR] [--clis FILE] [--cli a,b] [--filter text,text] [--runs 10] [--rounds 5] [--warmup 3] [--out /out]
//   compare  [--run FILE] [--outside FILE] [--bdn FILE,FILE] [--bdn-cli featherqr-jit] [--out /out]
//
// --filter keeps the manifest entries whose key contains any of the texts, such as decode-image or qr-url.

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: collector <corpus|run|outside|cold|compare> [options]; see Program.cs");
    return 2;
}

var corpus = Option("--corpus") ?? "/opt/xlang/corpus";
var outDir = Option("--out") ?? (args[0] == "corpus" ? "/opt/xlang/corpus" : "/out");

switch (args[0])
{
    case "corpus":
        Corpus.Write(outDir);
        Console.Error.WriteLine($"wrote {ManifestEntry.Read(outDir).Length} entries to {outDir}");
        return 0;
    case "run":
        return RunCommand.Execute(corpus, Clis(), Entries(),
            new RunSettings(Int("--rounds", 5), Double("--warmup-ms", 3000), Double("--batch-ms", 20), Int("--batches", 30)), outDir);
    case "outside":
        return OutsideCommand.Execute(corpus, Clis(), Entries(), ReadRun(),
            new OutsideSettings(Double("--seconds", 1), Int("--runs", 5), Int("--rounds", 2), Int("--attempts", 3)), outDir);
    case "cold":
        return ColdCommand.Execute(corpus, Clis(), Entries(), new ColdSettings(Int("--runs", 10), Int("--rounds", 5), Int("--warmup", 3)), outDir);
    case "compare":
        {
            var outsidePath = Option("--outside");
            var outside = outsidePath is null ? null : JsonSerializer.Deserialize<OutsideReport>(File.ReadAllText(outsidePath), RunCommand.Json);
            var bdn = Option("--bdn")?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [];
            return CompareCommand.Execute(ReadRun(), outside, bdn, Option("--bdn-cli") ?? "featherqr-jit", outDir);
        }
    default:
        Console.Error.WriteLine($"unknown command {args[0]}");
        return 2;
}

Cli[] Clis()
{
    var clis = Cli.Read(Option("--clis") ?? "/opt/xlang/clis.tsv");
    var names = Option("--cli")?.Split(',', StringSplitOptions.RemoveEmptyEntries);
    return names is null ? clis : [.. names.Select(n => clis.FirstOrDefault(c => c.Name == n) ?? throw new ArgumentException($"No CLI named {n}."))];
}

ManifestEntry[] Entries()
{
    var entries = ManifestEntry.Read(corpus);
    var filters = Option("--filter")?.Split(',', StringSplitOptions.RemoveEmptyEntries);
    var selected = filters is null ? entries : [.. entries.Where(e => filters.Any(f => e.Key.Contains(f, StringComparison.Ordinal)))];
    return selected.Length > 0 ? selected : throw new ArgumentException($"--filter {Option("--filter")} keeps no entry.");
}

RunReport ReadRun() => JsonSerializer.Deserialize<RunReport>(File.ReadAllText(Option("--run") ?? Path.Combine(outDir, "run.json")), RunCommand.Json)
    ?? throw new InvalidDataException("run.json is empty.");

string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

int Int(string name, int fallback) => Option(name) is { } value ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;

double Double(string name, double fallback) => Option(name) is { } value ? double.Parse(value, CultureInfo.InvariantCulture) : fallback;
