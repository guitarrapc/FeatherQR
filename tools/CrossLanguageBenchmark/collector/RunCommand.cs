using System.Globalization;
using System.Text;
using System.Text.Json;

internal sealed record RunSettings(int Rounds, double WarmupMs, double BatchMs, int Batches);

/// <summary>One CLI process: what it printed about its loop, and its per-call times (each batch's time over its calls).</summary>
internal sealed record ProcessRun(int Round, string? Rejected, long BatchCalls, long WarmupCalls, double WarmupMs, double[] PerCallNs, double MedianNs, double MinNs, double Q1Ns, double Q3Ns);

/// <summary>Every process of one entry on one CLI, and the statistics over their medians.</summary>
/// <param name="Isa">The CLI's <c>isa</c> member as JSON, if it prints one: the instruction sets its build's code sees.</param>
internal sealed record EntryResult(string Key, string Cli, string? Library, string? LibraryVersion, string? Runtime, string? Build, string? Isa, ProcessRun[] Processes)
{
    private IEnumerable<double> Medians => Processes.Where(p => p.Rejected is null).Select(p => p.MedianNs);

    public int Verified => Processes.Count(p => p.Rejected is null);
    public double MedianNs => Stats.Median(Medians);
    public double MinNs => Verified == 0 ? double.NaN : Medians.Min();
    public double MaxNs => Verified == 0 ? double.NaN : Medians.Max();

    /// <summary>The range of the process medians over their median: how far one process of the same build can land from another.</summary>
    public double Spread => (MaxNs - MinNs) / MedianNs;
}

internal sealed record RunReport(Machine Machine, RunSettings Settings, EntryResult[] Results);

/// <summary>
/// Runs every selected entry on every selected CLI, one process each, interleaved: within a round each entry runs on every CLI in turn,
/// and the CLI that goes first rotates from round to round, so drift during the run does not favour one CLI.
/// </summary>
internal static class RunCommand
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

    public static int Execute(string corpus, Cli[] clis, ManifestEntry[] entries, RunSettings settings, string outDir)
    {
        var machine = Machine.Capture();
        var processes = new Dictionary<(string Key, string Cli), List<ProcessRun>>();
        var identity = new Dictionary<(string Key, string Cli), JsonElement>();
        var unsupported = new HashSet<(string Key, string Cli)>();

        for (var round = 0; round < settings.Rounds; round++)
        {
            foreach (var entry in entries)
            {
                for (var i = 0; i < clis.Length; i++)
                {
                    var cli = clis[(i + round) % clis.Length];
                    if (unsupported.Contains((entry.Key, cli.Name)))
                        continue;

                    string[] arguments =
                    [
                        "run", .. entry.Arguments(corpus),
                        "--warmup-ms", settings.WarmupMs.ToString(CultureInfo.InvariantCulture),
                        "--batch-ms", settings.BatchMs.ToString(CultureInfo.InvariantCulture),
                        "--batches", settings.Batches.ToString(CultureInfo.InvariantCulture),
                    ];
                    var (run, output) = Launch(cli, arguments, entry, round);
                    if (output is { } o && o.TryGetProperty("status", out var status) && status.GetString() == "unsupported")
                    {
                        unsupported.Add((entry.Key, cli.Name));
                        Console.Error.WriteLine($"round {round} {entry.Key} {cli.Name}: unsupported");
                        continue;
                    }
                    if (output is { } first)
                        identity.TryAdd((entry.Key, cli.Name), first.Clone());
                    (processes.TryGetValue((entry.Key, cli.Name), out var list) ? list : processes[(entry.Key, cli.Name)] = []).Add(run);
                    Console.Error.WriteLine(run.Rejected is null
                        ? string.Create(CultureInfo.InvariantCulture, $"round {round} {entry.Key} {cli.Name}: {run.MedianNs / 1e3:F3} us ({run.BatchCalls} calls/batch)")
                        : $"round {round} {entry.Key} {cli.Name}: rejected, {run.Rejected}");
                }
            }
        }

        var results = processes.Select(p =>
        {
            var id = identity.TryGetValue(p.Key, out var o) ? o : default;
            return new EntryResult(p.Key.Key, p.Key.Cli, Text(id, "library"), Text(id, "libraryVersion"), Text(id, "runtime"), Text(id, "build"), Text(id, "isa"), [.. p.Value]);
        }).ToArray();

        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "run.json"), JsonSerializer.Serialize(new RunReport(machine, settings, results), Json));
        File.WriteAllText(Path.Combine(outDir, "run.md"), Markdown(results, settings));
        Console.Error.WriteLine($"wrote {Path.Combine(outDir, "run.json")} and run.md");
        return results.All(r => r.Verified == r.Processes.Length) ? 0 : 1;
    }

    private static (ProcessRun Run, JsonElement? Output) Launch(Cli cli, string[] arguments, ManifestEntry entry, int round)
    {
        var (exitCode, stdout, stderr) = Processes.Run(cli, arguments, TimeSpan.FromMinutes(5));
        if (exitCode != 0)
            return (Rejected(round, $"exit {exitCode}: {FirstLine(stderr)}"), null);

        JsonElement output;
        try
        {
            output = JsonDocument.Parse(stdout).RootElement.Clone();
        }
        catch (JsonException e)
        {
            return (Rejected(round, $"output is not JSON: {e.Message}"), null);
        }

        var rejected = Verify.Check(entry, output) ?? Verify.FailedCalls(output);
        if (rejected is not null)
            return (Rejected(round, rejected), output);

        var batchCalls = output.GetProperty("batchCalls").GetInt64();
        var perCall = output.GetProperty("batchNs").EnumerateArray().Select(b => b.GetDouble() / batchCalls).ToArray();
        return (new ProcessRun(round, null, batchCalls, output.GetProperty("warmupCalls").GetInt64(), output.GetProperty("warmupNs").GetDouble() / 1e6,
            perCall, Stats.Median(perCall), perCall.Min(), Stats.Quantile(perCall, 0.25), Stats.Quantile(perCall, 0.75)), output);
    }

    private static ProcessRun Rejected(int round, string reason) => new(round, reason, 0, 0, 0, [], double.NaN, double.NaN, double.NaN, double.NaN);

    internal static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value.ToString() : null;

    /// <summary>The <c>isa</c> member's true flags, and its numbers as name=value.</summary>
    private static string IsaCell(string? isa)
    {
        if (isa is null)
            return "";
        return string.Join(' ', JsonDocument.Parse(isa).RootElement.EnumerateObject().Select(p => p.Value.ValueKind switch
        {
            JsonValueKind.True => p.Name,
            JsonValueKind.Number => $"{p.Name}={p.Value}",
            _ => null,
        }).OfType<string>());
    }

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();

    private static string Markdown(EntryResult[] results, RunSettings settings)
    {
        var md = new StringBuilder();
        md.AppendLine(CultureInfo.InvariantCulture, $"{settings.Rounds} rounds, {settings.WarmupMs} ms warmup, {settings.Batches} batches of {settings.BatchMs} ms. Times per call; the range is that of the process medians.");
        md.AppendLine();
        md.AppendLine("| CLI | Library | Runtime | Build | Instruction sets the code sees |");
        md.AppendLine("|---|---|---|---|---|");
        foreach (var r in results.Where(r => r.Library is not null).DistinctBy(r => r.Cli))
            md.AppendLine($"| {r.Cli} | {r.Library} {r.LibraryVersion} | {r.Runtime} | {r.Build} | {IsaCell(r.Isa)} |");
        md.AppendLine();
        md.AppendLine("| Entry | CLI | Median µs | Process medians µs | Spread | Calls per batch | Rejected |");
        md.AppendLine("|---|---|---:|---:|---:|---:|---|");
        foreach (var r in results)
        {
            var calls = r.Processes.Where(p => p.Rejected is null).Select(p => p.BatchCalls).DefaultIfEmpty().Min();
            var rejected = string.Join("; ", r.Processes.Where(p => p.Rejected is not null).Select(p => $"round {p.Round}: {p.Rejected}"));
            md.AppendLine(CultureInfo.InvariantCulture, $"| {r.Key} | {r.Cli} | {r.MedianNs / 1e3:F3} | {r.MinNs / 1e3:F3} to {r.MaxNs / 1e3:F3} | {r.Spread:P1} | {calls} | {rejected} |");
        }

        // Every CLI against the first one listed, entry by entry, from processes interleaved in the same rounds.
        var clis = results.Select(r => r.Cli).Distinct().ToArray();
        if (clis.Length > 1)
        {
            md.AppendLine();
            md.AppendLine(CultureInfo.InvariantCulture, $"Median µs, and in brackets the median over {clis[0]}'s (above 1 is slower). An empty cell is an operation the library does not offer.");
            md.AppendLine();
            md.AppendLine($"| Entry | {string.Join(" | ", clis)} |");
            md.AppendLine($"|---|{string.Concat(clis.Select(_ => "---:|"))}");
            foreach (var key in results.Select(r => r.Key).Distinct())
            {
                var baseline = results.FirstOrDefault(r => r.Key == key && r.Cli == clis[0] && r.Verified > 0)?.MedianNs;
                var cells = clis.Select(cli => results.FirstOrDefault(r => r.Key == key && r.Cli == cli) switch
                {
                    null => "",
                    { Verified: 0 } => "rejected",
                    var r when baseline is { } b => string.Create(CultureInfo.InvariantCulture, $"{r.MedianNs / 1e3:F3} ({r.MedianNs / b:F2})"),
                    var r => string.Create(CultureInfo.InvariantCulture, $"{r.MedianNs / 1e3:F3}"),
                });
                md.AppendLine($"| {key} | {string.Join(" | ", cells)} |");
            }
        }
        return md.ToString();
    }
}
