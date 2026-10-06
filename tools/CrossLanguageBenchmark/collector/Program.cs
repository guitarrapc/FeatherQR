using System.Globalization;
using System.Text.Json;

// The cross-language benchmark's collector: it writes the corpus, launches every CLI, verifies what each printed, and computes every statistic,
// so every row uses the same formula. See .github/docs/specs/qrcode-cross-language-benchmark.md.
//
//   corpus   [--out /opt/xlang/corpus]
//   run      [--corpus DIR] [--clis FILE] [--cli a,b] [--filter text,text] [--rounds 5] [--warmup-ms 3000] [--batch-ms 20] [--batches 30] [--out /out]
//   outside  [--corpus DIR] [--clis FILE] [--run FILE] [--cli a,b] [--filter text,text] [--seconds 1] [--runs 5] [--rounds 2] [--attempts 3] [--out /out]
//   cold     [--corpus DIR] [--clis FILE] [--cli a,b] [--filter text,text] [--runs 10] [--rounds 5] [--warmup 3] [--out /out]
//   compare  [--run FILE] [--outside FILE,FILE] [--bdn FILE,FILE] [--bdn-cli featherqr-jit] [--out /out]
//   agree    --runs FIRST,SECOND [--baseline featherqr-jit] [--out /out]   two runs of one commit held against each other on ratios
//
// --filter keeps the manifest entries whose key contains any of the texts, such as decode-image or qr-url.

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: collector <corpus|run|outside|cold|compare|agree> [options]; see Program.cs");
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
            // Several outside checks of one run, such as one per group of CLIs, are read as one.
            var outsideReports = (Option("--outside")?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [])
                .Select(path => JsonSerializer.Deserialize<OutsideReport>(File.ReadAllText(path), RunCommand.Json) ?? throw new InvalidDataException($"{path} is empty."))
                .ToArray();
            var outside = outsideReports.Length == 0 ? null : outsideReports[0] with { Results = [.. outsideReports.SelectMany(r => r.Results)] };
            var bdn = Option("--bdn")?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [];
            return CompareCommand.Execute(ReadRun(), outside, bdn, Option("--bdn-cli") ?? "featherqr-jit", outDir);
        }
    case "agree":
        {
            var runs = (Option("--runs") ?? throw new ArgumentException("agree needs --runs FIRST,SECOND.")).Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (runs.Length != 2)
                throw new ArgumentException("agree takes exactly two run.json files.");
            RunReport Read(string path) => JsonSerializer.Deserialize<RunReport>(File.ReadAllText(path), RunCommand.Json) ?? throw new InvalidDataException($"{path} is empty.");
            return AgreeCommand.Execute(Read(runs[0]), Read(runs[1]), Option("--baseline") ?? "featherqr-jit", outDir);
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
