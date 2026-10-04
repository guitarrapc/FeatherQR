using System.Globalization;
using System.Text;
using System.Text.Json;

/// <param name="Build">The CLI's <c>build</c> member, from its verified cold output.</param>
/// <param name="ColdSeconds">Every whole-process time of the <c>cold</c> command: start, input load, one call, its result printed.</param>
/// <param name="NoopSeconds">Every whole-process time of the <c>noop</c> command: start and input load, no call.</param>
/// <param name="StartMs">Median of the noop times: process start, runtime start and input load.</param>
/// <param name="FirstCallMs">Median of the cold times minus median of the noop times: the first call, with its JIT if any, and printing its result.</param>
/// <param name="LowMs">Q1 of cold minus Q3 of noop, the low end of what the times allow.</param>
/// <param name="HighMs">Q3 of cold minus Q1 of noop, the high end.</param>
internal sealed record ColdResult(string Key, string Cli, string? Rejected, string? Build, double[] ColdSeconds, double[] NoopSeconds, double StartMs, double FirstCallMs, double LowMs, double HighMs);

internal sealed record ColdSettings(int Runs, int Rounds, int Warmup);

internal sealed record ColdReport(Machine Machine, ColdSettings Settings, ColdResult[] Results);

/// <summary>
/// The cold first call: hyperfine times each CLI's <c>cold</c> and <c>noop</c> commands as whole processes, and the difference is the first call
/// from a fresh process. Interleaved as <c>run</c> is: within a round each entry runs on every CLI in turn, the CLI that goes first rotates
/// from round to round, and the order of cold and noop alternates. The untimed warmup runs leave the binaries and the input in the page cache,
/// so the number is the runtime's and the library's first-call cost, not the disk's.
/// </summary>
internal static class ColdCommand
{
    public static int Execute(string corpus, Cli[] clis, ManifestEntry[] entries, ColdSettings settings, string outDir)
    {
        var machine = Machine.Capture();
        var scratch = Directory.CreateTempSubdirectory("xlang-hyperfine").FullName;
        var times = new Dictionary<(string Key, string Cli), (List<double> Cold, List<double> Noop)>();
        var builds = new Dictionary<(string Key, string Cli), string?>();
        var rejected = new Dictionary<(string Key, string Cli), string>();
        var unsupported = new HashSet<(string Key, string Cli)>();

        for (var round = 0; round < settings.Rounds; round++)
        {
            foreach (var entry in entries)
            {
                for (var i = 0; i < clis.Length; i++)
                {
                    var cli = clis[(i + round) % clis.Length];
                    var id = (entry.Key, cli.Name);
                    if (unsupported.Contains(id) || rejected.ContainsKey(id))
                        continue;

                    // hyperfine only sees the exit code, so both commands' output is checked once, before the first timing.
                    if (!times.ContainsKey(id))
                    {
                        var (status, reason, build) = Check(cli, entry, corpus);
                        if (status == "unsupported")
                        {
                            unsupported.Add(id);
                            Console.Error.WriteLine($"{entry.Key} {cli.Name}: unsupported");
                            continue;
                        }
                        if (reason is not null)
                        {
                            rejected[id] = reason;
                            Console.Error.WriteLine($"{entry.Key} {cli.Name}: rejected, {reason}");
                            continue;
                        }
                        builds[id] = build;
                        times[id] = ([], []);
                    }

                    string[] cold = [.. cli.Command, "cold", .. entry.Arguments(corpus)];
                    string[] noop = [.. cli.Command, "noop", .. entry.Arguments(corpus)];
                    var coldFirst = round % 2 == 0;
                    var measured = Hyperfine.Time(coldFirst ? [cold, noop] : [noop, cold], settings.Warmup, settings.Runs, scratch, $"{entry.Key} {cli.Name}");
                    var (c, n) = coldFirst ? (measured[0], measured[1]) : (measured[1], measured[0]);
                    times[id].Cold.AddRange(c);
                    times[id].Noop.AddRange(n);
                    Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"round {round} {entry.Key} {cli.Name}: cold {Stats.Median(c) * 1e3:F2} ms, noop {Stats.Median(n) * 1e3:F2} ms"));
                }
            }
        }

        var results = new List<ColdResult>();
        foreach (var entry in entries)
        {
            foreach (var cli in clis)
            {
                var id = (entry.Key, cli.Name);
                if (rejected.TryGetValue(id, out var reason))
                {
                    results.Add(new(entry.Key, cli.Name, reason, null, [], [], double.NaN, double.NaN, double.NaN, double.NaN));
                    continue;
                }
                if (!times.TryGetValue(id, out var t))
                    continue;
                var start = Stats.Median(t.Noop);
                results.Add(new(entry.Key, cli.Name, null, builds[id], [.. t.Cold], [.. t.Noop], start * 1e3, (Stats.Median(t.Cold) - start) * 1e3,
                    (Stats.Quantile(t.Cold, 0.25) - Stats.Quantile(t.Noop, 0.75)) * 1e3, (Stats.Quantile(t.Cold, 0.75) - Stats.Quantile(t.Noop, 0.25)) * 1e3));
            }
        }

        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "cold.json"), JsonSerializer.Serialize(new ColdReport(machine, settings, [.. results]), RunCommand.Json));
        File.WriteAllText(Path.Combine(outDir, "cold.md"), Markdown(results, settings));
        Console.Error.WriteLine($"wrote {Path.Combine(outDir, "cold.json")} and cold.md");
        return results.All(r => r.Rejected is null) ? 0 : 1;
    }

    /// <summary>Runs <c>cold</c> once and verifies its result as <c>run</c> does, then <c>noop</c> once. Returns the cold status, why it does not count if it does not, and the build.</summary>
    private static (string? Status, string? Rejected, string? Build) Check(Cli cli, ManifestEntry entry, string corpus)
    {
        var (exitCode, stdout, stderr) = Processes.Run(cli, ["cold", .. entry.Arguments(corpus)], TimeSpan.FromMinutes(1));
        if (exitCode != 0)
            return (null, $"cold exit {exitCode}: {stderr.Split('\n')[0].Trim()}", null);
        JsonElement output;
        try
        {
            output = JsonDocument.Parse(stdout).RootElement.Clone();
        }
        catch (JsonException e)
        {
            return (null, $"cold output is not JSON: {e.Message}", null);
        }
        var status = RunCommand.Text(output, "status");
        if (status == "unsupported")
            return (status, null, null);
        if (Verify.Check(entry, output) is { } rejected)
            return (status, rejected, null);

        (exitCode, stdout, stderr) = Processes.Run(cli, ["noop", .. entry.Arguments(corpus)], TimeSpan.FromMinutes(1));
        if (exitCode != 0 || !stdout.Contains("\"status\":\"ok\"", StringComparison.Ordinal))
            return (status, $"noop failed: exit {exitCode} {stderr.Split('\n')[0].Trim()}", null);
        return (status, null, RunCommand.Text(output, "build"));
    }

    private static string Markdown(List<ColdResult> results, ColdSettings settings)
    {
        var md = new StringBuilder();
        md.AppendLine(CultureInfo.InvariantCulture, $"{settings.Rounds} rounds of hyperfine, {settings.Warmup} untimed and {settings.Runs} timed runs per command. Start is the noop process (start and input load); first call is cold minus noop, with the range Q1 to Q3 allows.");
        md.AppendLine();
        md.AppendLine("| Entry | CLI | Build | Start ms | First call ms | Range ms | Rejected |");
        md.AppendLine("|---|---|---|---:|---:|---:|---|");
        foreach (var r in results)
        {
            md.AppendLine(r.Rejected is null
                ? string.Create(CultureInfo.InvariantCulture, $"| {r.Key} | {r.Cli} | {r.Build} | {r.StartMs:F2} | {r.FirstCallMs:F2} | {r.LowMs:F2} to {r.HighMs:F2} | |")
                : $"| {r.Key} | {r.Cli} | | | | | {r.Rejected} |");
        }

        // Start plus first call, which is what an application waits for, against the first CLI listed.
        var clis = results.Select(r => r.Cli).Distinct().ToArray();
        if (clis.Length > 1)
        {
            md.AppendLine();
            md.AppendLine(CultureInfo.InvariantCulture, $"Start plus first call in ms (the cold process), and in brackets the first call alone. An empty cell is an operation the library does not offer.");
            md.AppendLine();
            md.AppendLine($"| Entry | {string.Join(" | ", clis)} |");
            md.AppendLine($"|---|{string.Concat(clis.Select(_ => "---:|"))}");
            foreach (var key in results.Select(r => r.Key).Distinct())
            {
                var cells = clis.Select(cli => results.FirstOrDefault(r => r.Key == key && r.Cli == cli) switch
                {
                    null => "",
                    { Rejected: not null } => "rejected",
                    var r => string.Create(CultureInfo.InvariantCulture, $"{r.StartMs + r.FirstCallMs:F2} ({r.FirstCallMs:F2})"),
                });
                md.AppendLine($"| {key} | {string.Join(" | ", cells)} |");
            }
        }
        return md.ToString();
    }
}
