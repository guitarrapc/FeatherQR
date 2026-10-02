using System.Globalization;
using System.Text;
using System.Text.Json;

/// <param name="Iterations">N: the shorter process runs N calls and the longer 2N.</param>
/// <param name="Attempts">How many times the entry was measured: a disturbed measurement is discarded and taken again.</param>
/// <param name="NSeconds">Every whole-process time of the N-call command, from the last attempt.</param>
/// <param name="TwoNSeconds">Every whole-process time of the 2N-call command, from the last attempt.</param>
/// <param name="PerCallNs">(median T(2N) - median T(N)) / N: the call's time with everything fixed cancelled.</param>
/// <param name="LowNs">(Q1 of T(2N) - Q3 of T(N)) / N, the low end of what the times allow.</param>
/// <param name="HighNs">(Q3 of T(2N) - Q1 of T(N)) / N, the high end.</param>
/// <param name="FixedMs">median T(N) - N calls: process start, runtime start, corpus load and JIT.</param>
internal sealed record OutsideResult(string Key, string Cli, string? Rejected, long Iterations, int Attempts, double[] NSeconds, double[] TwoNSeconds, double PerCallNs, double LowNs, double HighNs, double FixedMs, double SelfMedianNs)
{
    public double Ratio => PerCallNs / SelfMedianNs;

    /// <summary>The range (high - low) over the per-call time.</summary>
    public double Range => (HighNs - LowNs) / PerCallNs;

    /// <summary>
    /// Whether something else on the machine took time during the measurement, by either of two signs.
    /// The range: undisturbed, none exceeded 14 % (phase 1, 33 entries), while other load put single 2N runs at 2.5 times the rest and the range at 41 to 96 %.
    /// The fixed cost, which cannot be negative: one below zero by more than the timing noise means the longer 2N runs caught more of the load
    /// than the N runs. In phase 2, the measurements that read 4.5 % or more above their self-timed median had -23 to -523 ms, and the 72 that
    /// agreed within 3 % had -29 to +54 ms. Load on the N runs instead reads low with a fixed cost above the CLI's true one, which is not
    /// known here, so that side is not flagged. It stayed within 6.6 % in phase 2.
    /// </summary>
    public bool Disturbed => Range > OutsideCommand.MaxRange || FixedMs < -OutsideCommand.MaxNegativeFixed * Stats.Median(NSeconds) * 1e3;
}

internal sealed record OutsideSettings(double Seconds, int Runs, int Rounds, int Attempts);

internal sealed record OutsideReport(Machine Machine, OutsideSettings Settings, OutsideResult[] Results);

/// <summary>
/// The check that trusts no timing code in a CLI: hyperfine times the CLI as whole processes of N and 2N calls, and the difference is N calls.
/// N is sized from the self-timed median so that N calls take the stated seconds, which has to cover the warmup.
/// A measurement whose range shows other load is taken again, up to the stated attempts, and one still disturbed is reported as such rather than judged.
/// </summary>
internal static class OutsideCommand
{
    public const double MaxRange = 0.2;

    /// <summary>The most negative fixed cost taken as timing noise, as a share of T(N).</summary>
    public const double MaxNegativeFixed = 0.03;

    public static int Execute(string corpus, Cli[] clis, ManifestEntry[] entries, RunReport run, OutsideSettings settings, string outDir)
    {
        var machine = Machine.Capture();
        var results = new List<OutsideResult>();
        // hyperfine's exports stay on the container's own disk: read back through Docker Desktop's Windows bind mount,
        // a file just written there came back as zero bytes. Their times are kept in outside.json.
        var scratch = Directory.CreateTempSubdirectory("xlang-hyperfine").FullName;
        Directory.CreateDirectory(outDir);

        foreach (var result in run.Results.Where(r => r.Verified > 0))
        {
            var entry = entries.FirstOrDefault(e => e.Key == result.Key);
            var cli = clis.FirstOrDefault(c => c.Name == result.Cli);
            if (entry is null || cli is null)
                continue;

            var n = Math.Max(1L, (long)Math.Round(settings.Seconds * 1e9 / result.MedianNs));
            string[] Arguments(long iterations) => ["fixed", .. entry.Arguments(corpus), "--iterations", iterations.ToString(CultureInfo.InvariantCulture)];

            // hyperfine only sees the exit code, so the output is checked once here.
            var (exitCode, stdout, stderr) = Processes.Run(cli, Arguments(1), TimeSpan.FromMinutes(1));
            if (exitCode != 0 || !stdout.Contains("\"status\":\"ok\"", StringComparison.Ordinal))
            {
                results.Add(new(result.Key, result.Cli, $"fixed mode failed: exit {exitCode} {stderr.Split('\n')[0]}", n, 0, [], [], double.NaN, double.NaN, double.NaN, double.NaN, result.MedianNs));
                continue;
            }

            OutsideResult outside;
            var attempt = 0;
            do
            {
                attempt++;
                var nTimes = new List<double>();
                var twoNTimes = new List<double>();
                for (var round = 0; round < settings.Rounds; round++)
                {
                    // The order alternates, so drift during the check does not land on one side of the difference.
                    var order = round % 2 == 0 ? new[] { n, 2 * n } : [2 * n, n];
                    var export = Path.Combine(scratch, $"{result.Key.Replace('/', '_')}.{result.Cli}.{round}.json");
                    string[] arguments =
                    [
                        "--shell=none", "--style", "none", "--warmup", "1", "--runs", settings.Runs.ToString(CultureInfo.InvariantCulture), "--export-json", export,
                        .. order.Select(iterations => string.Join(' ', cli.Command.Concat(Arguments(iterations)).Select(Quote))),
                    ];
                    var (code, _, error) = Processes.Run("hyperfine", arguments, TimeSpan.FromHours(1));
                    if (code != 0)
                        throw new InvalidOperationException($"hyperfine failed on {result.Key} {result.Cli}: {error}");

                    var exported = JsonDocument.Parse(File.ReadAllText(export)).RootElement.GetProperty("results").EnumerateArray().ToArray();
                    for (var i = 0; i < order.Length; i++)
                        (order[i] == n ? nTimes : twoNTimes).AddRange(exported[i].GetProperty("times").EnumerateArray().Select(t => t.GetDouble()));
                    File.Delete(export);
                }

                var perCall = (Stats.Median(twoNTimes) - Stats.Median(nTimes)) / n * 1e9;
                var low = (Stats.Quantile(twoNTimes, 0.25) - Stats.Quantile(nTimes, 0.75)) / n * 1e9;
                var high = (Stats.Quantile(twoNTimes, 0.75) - Stats.Quantile(nTimes, 0.25)) / n * 1e9;
                var fixedMs = (Stats.Median(nTimes) - n * perCall / 1e9) * 1e3;
                outside = new OutsideResult(result.Key, result.Cli, null, n, attempt, [.. nTimes], [.. twoNTimes], perCall, low, high, fixedMs, result.MedianNs);
                Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{result.Key} {result.Cli}: outside {perCall / 1e3:F3} us ({low / 1e3:F3} to {high / 1e3:F3}), self {result.MedianNs / 1e3:F3} us, ratio {outside.Ratio:F3}, fixed {fixedMs:F0} ms{(outside.Disturbed ? ", disturbed" : "")}"));
            } while (outside.Disturbed && attempt < settings.Attempts);
            results.Add(outside);
        }

        File.WriteAllText(Path.Combine(outDir, "outside.json"), JsonSerializer.Serialize(new OutsideReport(machine, settings, [.. results]), RunCommand.Json));
        File.WriteAllText(Path.Combine(outDir, "outside.md"), Markdown(results, settings));
        Console.Error.WriteLine($"wrote {Path.Combine(outDir, "outside.json")} and outside.md");
        return results.All(r => r.Rejected is null && !r.Disturbed) ? 0 : 1;
    }

    // hyperfine splits a command without a shell the way a POSIX shell would, so single quotes keep an argument whole.
    private static string Quote(string argument) => argument.Any(c => char.IsWhiteSpace(c) || c is '\'' or '"' or '\\' or '$')
        ? $"'{argument.Replace("'", "'\\''")}'"
        : argument;

    private static string Markdown(List<OutsideResult> results, OutsideSettings settings)
    {
        var md = new StringBuilder();
        md.AppendLine(CultureInfo.InvariantCulture, $"N sized to {settings.Seconds} s of calls; {settings.Rounds} hyperfine rounds of {settings.Runs} runs per command, the order alternating. A range above {MaxRange:P0} of the call, or a fixed cost below -{MaxNegativeFixed:P0} of T(N), is disturbed and measured again, up to {settings.Attempts} attempts.");
        md.AppendLine();
        md.AppendLine("| Entry | CLI | N | Outside µs | Range µs | Self µs | Outside / self | Fixed ms | Attempts |");
        md.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var r in results)
        {
            md.AppendLine(r.Rejected is null
                ? string.Create(CultureInfo.InvariantCulture, $"| {r.Key} | {r.Cli} | {r.Iterations} | {r.PerCallNs / 1e3:F3} | {r.LowNs / 1e3:F3} to {r.HighNs / 1e3:F3}{(r.Disturbed ? " (disturbed)" : "")} | {r.SelfMedianNs / 1e3:F3} | {r.Ratio:F3} | {r.FixedMs:F0} | {r.Attempts} |")
                : $"| {r.Key} | {r.Cli} | {r.Iterations} | {r.Rejected} | | | | | |");
        }
        return md.ToString();
    }
}
