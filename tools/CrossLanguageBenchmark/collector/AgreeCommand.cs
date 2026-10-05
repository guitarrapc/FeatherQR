using System.Globalization;
using System.Text;

/// <summary>
/// Holds two runs of the same commit against each other on ratios, the only numbers a hosted runner's changing CPU leaves comparable:
/// each CLI's median over the baseline CLI's on every entry, in one run against the other. The tolerance is phase 1's, the one the
/// outside check uses: every entry within <see cref="CompareCommand.EntryTolerance"/>, the signed median within <see cref="CompareCommand.MedianTolerance"/>.
/// </summary>
internal static class AgreeCommand
{
    public static int Execute(RunReport first, RunReport second, string baseline, string outDir)
    {
        var md = new StringBuilder();
        md.AppendLine(CultureInfo.InvariantCulture, $"Each CLI's median over {baseline}'s, entry by entry, in the second run over the first.");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"- First: {Describe(first.Machine)}");
        md.AppendLine(CultureInfo.InvariantCulture, $"- Second: {Describe(second.Machine)}");
        // The workflow builds the image in every run, so two runs of one commit have different image IDs from pinned sources and
        // toolchains. Different commits are different code.
        if (first.Machine.Commit != second.Machine.Commit)
            md.AppendLine("- The two runs are of different commits, so a disagreement can be the code, not the machine.");

        var failed = false;
        foreach (var cli in first.Results.Select(r => r.Cli).Distinct().Where(c => c != baseline))
        {
            var deviations = new List<(string Key, double First, double Second)>();
            foreach (var a in first.Results.Where(r => r.Cli == cli && r.Verified > 0))
            {
                var b = second.Results.FirstOrDefault(r => r.Cli == cli && r.Key == a.Key && r.Verified > 0);
                var baseA = first.Results.FirstOrDefault(r => r.Cli == baseline && r.Key == a.Key && r.Verified > 0);
                var baseB = second.Results.FirstOrDefault(r => r.Cli == baseline && r.Key == a.Key && r.Verified > 0);
                if (b is null || baseA is null || baseB is null)
                    continue;
                deviations.Add((a.Key, a.MedianNs / baseA.MedianNs, b.MedianNs / baseB.MedianNs));
            }
            if (deviations.Count == 0)
                continue;

            md.AppendLine();
            md.AppendLine(CultureInfo.InvariantCulture, $"## {cli}");
            md.AppendLine();
            md.AppendLine("| Entry | Ratio, first | Ratio, second | Second / first |");
            md.AppendLine("|---|---:|---:|---:|");
            foreach (var (key, a, b) in deviations)
                md.AppendLine(CultureInfo.InvariantCulture, $"| {key} | {a:F3} | {b:F3} | {b / a:F3} |");

            var signed = deviations.Select(d => d.Second / d.First - 1).ToList();
            var pass = signed.Max(Math.Abs) <= CompareCommand.EntryTolerance && Math.Abs(Stats.Median(signed)) <= CompareCommand.MedianTolerance;
            failed |= !pass;
            md.AppendLine();
            md.AppendLine(CultureInfo.InvariantCulture,
                $"{(pass ? "Agrees" : "Disagrees")}: largest {signed.Max(Math.Abs):P1}, signed median {Stats.Median(signed):+0.0%;-0.0%} (every entry within {CompareCommand.EntryTolerance:P0}, the signed median within {CompareCommand.MedianTolerance:P0}).");
        }

        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "agree.md"), md.ToString());
        Console.Out.Write(md.ToString());
        return failed ? 1 : 0;
    }

    private static string Describe(Machine machine)
    {
        var cpu = machine.Lscpu?.Split('\n').FirstOrDefault(l => l.StartsWith("Model name:", StringComparison.Ordinal))?["Model name:".Length..].Trim();
        return $"{cpu ?? "unknown CPU"}, kernel {machine.Kernel ?? "unknown"}, image {machine.Image ?? "unknown"}, commit {machine.Commit ?? "unknown"}";
    }
}
