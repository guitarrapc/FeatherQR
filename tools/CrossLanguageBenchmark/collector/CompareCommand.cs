using System.Globalization;
using System.Text;
using System.Text.Json;

/// <summary>
/// Holds each CLI's self-timed medians against the outside check (whole processes, no timing code in the CLI), and the BenchmarkDotNet CLI's
/// also against BenchmarkDotNet's reference project (same code, its own harness and statistics). A CLI passes the outside check under the
/// tolerance phase 1 measured: every entry within <see cref="EntryTolerance"/>, and the signed median over its entries within <see cref="MedianTolerance"/>.
/// </summary>
/// <remarks>
/// The second test is for a bias, such as a cost per call the loop adds, which moves every entry the same way. The median of the
/// disagreements' sizes measures noise instead: in phase 2 it exceeded 2 % on CLIs with two to four undisturbed entries,
/// while the signed median stayed within 1.3 % on every CLI in every run.
/// </remarks>
internal static class CompareCommand
{
    public const double EntryTolerance = 0.07;
    public const double MedianTolerance = 0.02;

    public static int Execute(RunReport run, OutsideReport? outside, string[] bdnReports, string bdnCli, string outDir)
    {
        var bdn = new Dictionary<string, double>();
        foreach (var report in bdnReports)
        {
            foreach (var benchmark in JsonDocument.Parse(File.ReadAllText(report)).RootElement.GetProperty("Benchmarks").EnumerateArray())
            {
                // The reference project's one parameter, read from FullName (CorpusEntries.Run(Case: "<key>")): the Parameters field truncates long values.
                var fullName = benchmark.GetProperty("FullName").GetString() ?? "";
                var start = fullName.IndexOf("Case: \"", StringComparison.Ordinal);
                if (start < 0)
                    continue;
                bdn[fullName[(start + 7)..fullName.LastIndexOf('"')]] = benchmark.GetProperty("Statistics").GetProperty("Median").GetDouble();
            }
        }

        var md = new StringBuilder();
        md.AppendLine("Per-call times in µs. Self is the median of the CLI's process medians; BenchmarkDotNet's is its Median; outside is hyperfine's difference of N and 2N calls.");
        var failed = false;
        foreach (var cli in run.Results.Select(r => r.Cli).Distinct())
        {
            md.AppendLine();
            md.AppendLine(CultureInfo.InvariantCulture, $"## {cli}");
            md.AppendLine();
            md.AppendLine("| Entry | Self | Process spread | BenchmarkDotNet | Self / BDN | Outside | Outside / self |");
            md.AppendLine("|---|---:|---:|---:|---:|---:|---:|");

            var bdnRatios = new List<double>();
            var outsideRatios = new List<double>();
            var spreads = new List<double>();
            var disturbed = 0;
            foreach (var result in run.Results.Where(r => r.Cli == cli && r.Verified > 0))
            {
                spreads.Add(result.Spread);
                var (bdnCell, bdnRatioCell, outsideCell, outsideRatioCell) = ("", "", "", "");
                if (cli == bdnCli && bdn.TryGetValue(result.Key, out var b))
                {
                    bdnRatios.Add(result.MedianNs / b);
                    (bdnCell, bdnRatioCell) = (F(b / 1e3), F(result.MedianNs / b));
                }
                if (outside?.Results.FirstOrDefault(o => o.Key == result.Key && o.Cli == cli && o.Rejected is null) is { } o)
                {
                    // A measurement still disturbed after its attempts says nothing about the loop, so it stays out of the verdict.
                    if (o.Disturbed)
                        disturbed++;
                    else
                        outsideRatios.Add(o.Ratio);
                    (outsideCell, outsideRatioCell) = (F(o.PerCallNs / 1e3), o.Disturbed ? $"{F(o.Ratio)} (disturbed)" : F(o.Ratio));
                }
                md.AppendLine(CultureInfo.InvariantCulture, $"| {result.Key} | {result.MedianNs / 1e3:F3} | {result.Spread:P1} | {bdnCell} | {bdnRatioCell} | {outsideCell} | {outsideRatioCell} |");
            }

            md.AppendLine();
            md.AppendLine("| Measure | Entries | Median | Largest |");
            md.AppendLine("|---|---:|---:|---:|");
            Summary("Process spread (range of process medians / median)", spreads);
            Summary("|Self / BDN - 1|", bdnRatios.Select(r => Math.Abs(r - 1)).ToList());
            var deviations = outsideRatios.Select(r => r - 1).ToList();
            Summary("|Outside / self - 1|", deviations.Select(Math.Abs).ToList());
            Summary("Outside / self - 1, signed", deviations);
            if (deviations.Count > 0 || disturbed > 0)
            {
                var pass = deviations.Count > 0 && deviations.Max(Math.Abs) <= EntryTolerance && Math.Abs(Stats.Median(deviations)) <= MedianTolerance;
                var verdict = !pass ? "fails" : disturbed > 0 ? $"inconclusive, {disturbed} entries disturbed and the rest pass" : "passes";
                failed |= verdict != "passes";
                md.AppendLine();
                md.AppendLine(CultureInfo.InvariantCulture, $"Outside check: {verdict} (every entry within {EntryTolerance:P0}, the signed median within {MedianTolerance:P0}).");
            }
        }

        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "agreement.md"), md.ToString());
        Console.Out.Write(md.ToString());
        return failed ? 1 : 0;

        void Summary(string name, List<double> values)
        {
            if (values.Count == 0)
                return;
            md.AppendLine(CultureInfo.InvariantCulture, $"| {name} | {values.Count} | {Stats.Median(values):P1} | {values.Max():P1} |");
        }

        static string F(double value) => value.ToString("F3", CultureInfo.InvariantCulture);
    }
}
