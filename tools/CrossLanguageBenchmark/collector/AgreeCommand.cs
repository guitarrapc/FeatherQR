using System.Globalization;
using System.Text;

/// <summary>
/// Holds two runs of the same commit against each other on ratios, the only numbers a hosted runner's changing CPU leaves comparable:
/// each CLI's median over the baseline CLI's on every entry, in one run against the other. The tolerance is the one the
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
        // A hosted x64 runner's CPU model changes between jobs, and ratios change with it: on the first two CI runs (2026-10-05), from Zen 3 to Zen 5,
        // entries moved by up to 45 %. Such a pair compares the CPUs, not the noise.
        if (Cpu(first.Machine) != Cpu(second.Machine))
            md.AppendLine("- The two runs ran on different CPU models, so a disagreement can be the CPU, not the noise.");

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
            var median = Stats.Median(signed);
            var pass = signed.Max(Math.Abs) <= CompareCommand.EntryTolerance && Math.Abs(median) <= CompareCommand.MedianTolerance;
            failed |= !pass;
            md.AppendLine();
            // Rounded before formatting: a small negative median would print as "-+0.0%".
            md.AppendLine(CultureInfo.InvariantCulture,
                $"{(pass ? "Agrees" : "Disagrees")}: largest {signed.Max(Math.Abs):P1}, signed median {Math.Round(median, 3) + 0.0:+0.0%;-0.0%;0.0%} (every entry within {CompareCommand.EntryTolerance:P0}, the signed median within {CompareCommand.MedianTolerance:P0}).");
        }

        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "agree.md"), md.ToString());
        Console.Out.Write(md.ToString());
        return failed ? 1 : 0;
    }

    private static string Describe(Machine machine)
        => $"{Cpu(machine) ?? "unknown CPU"}, kernel {machine.Kernel ?? "unknown"}, image {machine.Image ?? "unknown"}, commit {machine.Commit ?? "unknown"}";

    private static string? Cpu(Machine machine)
        => machine.Lscpu?.Split('\n').FirstOrDefault(l => l.StartsWith("Model name:", StringComparison.Ordinal))?["Model name:".Length..].Trim();
}
