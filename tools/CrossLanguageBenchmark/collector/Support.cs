using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

/// <summary>A CLI the collector can launch: a line of <c>clis.tsv</c>, a name then the command and its fixed arguments, tab-separated.</summary>
internal sealed record Cli(string Name, string[] Command)
{
    /// <summary>The listed CLIs whose command is present. An absent one, such as an x86-64-v3 build on arm64, is skipped with a note.</summary>
    public static Cli[] Read(string path)
    {
        var clis = File.ReadAllLines(path)
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split('\t'))
            .Select(f => new Cli(f[0], f[1..]))
            .ToArray();
        foreach (var missing in clis.Where(c => Path.IsPathRooted(c.Command[0]) && !File.Exists(c.Command[0])))
            Console.Error.WriteLine($"skipping {missing.Name}: {missing.Command[0]} is not in this image");
        return [.. clis.Where(c => !Path.IsPathRooted(c.Command[0]) || File.Exists(c.Command[0]))];
    }
}

internal static class Processes
{
    public static (int ExitCode, string Stdout, string Stderr) Run(string fileName, IEnumerable<string> arguments, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{fileName} did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            return (-1, stdout.Result, $"timed out after {timeout.TotalSeconds:F0} s. {stderr.Result}");
        }
        return (process.ExitCode, stdout.Result, stderr.Result);
    }

    public static (int ExitCode, string Stdout, string Stderr) Run(Cli cli, IEnumerable<string> arguments, TimeSpan timeout)
        => Run(cli.Command[0], [.. cli.Command[1..], .. arguments], timeout);
}

/// <summary>Whole-process timing, with no timing code in the process: what the outside check and the cold first call are measured with.</summary>
internal static class Hyperfine
{
    /// <summary>
    /// Times each command, in the order given, as <paramref name="runs"/> whole processes after <paramref name="warmup"/> untimed ones,
    /// and returns every run's wall-clock seconds, per command. The export is read back from <paramref name="scratch"/>, which has to be on
    /// the container's own disk: read back through Docker Desktop's Windows bind mount, a file just written there came back as zero bytes.
    /// </summary>
    public static double[][] Time(string[][] commands, int warmup, int runs, string scratch, string what)
    {
        var export = Path.Combine(scratch, $"{Guid.NewGuid():N}.json");
        string[] arguments =
        [
            "--shell=none", "--style", "none", "--warmup", warmup.ToString(CultureInfo.InvariantCulture), "--runs", runs.ToString(CultureInfo.InvariantCulture), "--export-json", export,
            .. commands.Select(command => string.Join(' ', command.Select(Quote))),
        ];
        var (code, _, error) = Processes.Run("hyperfine", arguments, TimeSpan.FromHours(1));
        if (code != 0)
            throw new InvalidOperationException($"hyperfine failed on {what}: {error}");

        var times = JsonDocument.Parse(File.ReadAllText(export)).RootElement.GetProperty("results").EnumerateArray()
            .Select(result => result.GetProperty("times").EnumerateArray().Select(t => t.GetDouble()).ToArray())
            .ToArray();
        File.Delete(export);
        return times;
    }

    // hyperfine splits a command without a shell the way a POSIX shell would, so single quotes keep an argument whole.
    private static string Quote(string argument) => argument.Any(c => char.IsWhiteSpace(c) || c is '\'' or '"' or '\\' or '$')
        ? $"'{argument.Replace("'", "'\\''")}'"
        : argument;
}

internal static class Stats
{
    /// <summary>The <paramref name="q"/> quantile, interpolated between the two nearest ranks.</summary>
    public static double Quantile(IEnumerable<double> values, double q)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
            return double.NaN;
        var position = q * (sorted.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = Math.Min(lower + 1, sorted.Length - 1);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    public static double Median(IEnumerable<double> values) => Quantile(values, 0.5);
}

/// <summary>What a result has to record to be compared with another: the image, the CPU, the kernel and the limits the run had.</summary>
internal sealed record Machine(string? Image, string? Commit, string? Kernel, string? CpusAllowed, string? CpuMax, string? MemoryMax, string Runtime, Dictionary<string, string> DotnetSettings, string? Hyperfine, string? Lscpu, DateTimeOffset Started)
{
    public static Machine Capture() => new(
        Environment.GetEnvironmentVariable("XLANG_IMAGE"),
        Environment.GetEnvironmentVariable("XLANG_COMMIT"),
        ReadFile("/proc/sys/kernel/osrelease"),
        ReadFile("/sys/fs/cgroup/cpuset.cpus.effective"),
        ReadFile("/sys/fs/cgroup/cpu.max"),
        ReadFile("/sys/fs/cgroup/memory.max"),
        $"{RuntimeInformation.FrameworkDescription} {RuntimeInformation.RuntimeIdentifier}",
        Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(k => k.StartsWith("DOTNET_", StringComparison.Ordinal)).Order()
            .ToDictionary(k => k, k => Environment.GetEnvironmentVariable(k) ?? ""),
        Output("hyperfine", "--version"),
        Output("lscpu"),
        DateTimeOffset.UtcNow);

    private static string? ReadFile(string path) => File.Exists(path) ? File.ReadAllText(path).Trim() : null;

    private static string? Output(string fileName, params string[] arguments)
    {
        try
        {
            var (exitCode, stdout, _) = Processes.Run(fileName, arguments, TimeSpan.FromSeconds(10));
            return exitCode == 0 ? stdout.Trim() : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
