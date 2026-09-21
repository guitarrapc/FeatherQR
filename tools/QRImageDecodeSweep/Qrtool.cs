using System.Diagnostics;
using System.Text;

namespace QRImageDecodeSweep;

/// <summary>The pinned qrtool binary the interop fixtures use (<c>tools/QRInteropFixtures/get-qrtool.ps1</c>). Without it the sweep runs with one encoder fewer and says so.</summary>
internal static class Qrtool
{
    private static string? exePath;

    public static bool Available => exePath is not null;

    public static void Locate(string repoRoot)
    {
        var root = Path.Combine(repoRoot, "tools", "QRInteropFixtures", "external", "qrtool");
        if (Directory.Exists(root))
            exePath = Directory.EnumerateFiles(root, "qrtool*", SearchOption.AllDirectories).FirstOrDefault(static p => Path.GetFileNameWithoutExtension(p) == "qrtool" && Path.GetExtension(p) is "" or ".exe");
        Console.WriteLine(exePath is null ? "qrtool: not found, its lineage is skipped" : $"qrtool: {exePath}");
    }

    /// <summary>qrtool 0.13.2 writes a wrong R17x43 at level M (about 88 modules off libzint's and this library's, which agree), so that version stays out of its lineage at both levels.</summary>
    public static bool SkipsCase(CaseDefinition d) => d.Symbology == Symbologies.RmQr && d.VersionName == "R17x43";

    /// <summary>Version, level and mode are pinned wherever the variant lets them be. The crate models M1's detection-only level as L.</summary>
    public static string Arguments(CaseDefinition d) => d.Symbology switch
    {
        Symbologies.StandardQr => $"--error-correction-level {d.Ecc.ToLowerInvariant()}",
        Symbologies.MicroQr => $"--variant micro --symbol-version {d.Version} --error-correction-level {(d.Version == 1 ? "l" : d.Ecc.ToLowerInvariant())} --mode {d.Mode}",
        _ => $"--variant rmqr --symbol-version {d.RmqrHeight} {d.RmqrWidth} --error-correction-level {d.Ecc.ToLowerInvariant()} --mode {d.Mode}",
    };

    public static Symbol? Encode(CaseDefinition d)
    {
        if (SkipsCase(d))
            return null;
        var output = RunWithPayload(d.Text, payloadFile => $"encode {Arguments(d)} --margin 0 --type ascii --read-from \"{payloadFile}\"");
        var lines = output.Replace("\r\n", "\n").Split('\n').Where(static l => l.Length > 0).ToArray();
        var height = lines.Length;
        var width = d.Symbology == Symbologies.RmQr ? d.RmqrWidth : height;
        var modules = new bool[width * height];
        for (var r = 0; r < height; r++)
        {
            // Two characters a module; trailing light modules may be trimmed
            var line = lines[r];
            for (var c = 0; c < width; c++)
            {
                var index = c * 2;
                modules[r * width + c] = index < line.Length && line[index] == '#';
            }
        }
        return new Symbol(modules, width, height, Symbologies.VersionName(d.Symbology, width, height));
    }

    /// <summary>The payload goes through a file: command-line arguments are not encoding-safe on Windows.</summary>
    public static string RunWithPayload(string text, Func<string, string> arguments)
    {
        var payloadFile = Path.Combine(Path.GetTempPath(), "qr-sweep-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(payloadFile, text, new UTF8Encoding(false));
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath!,
                Arguments = arguments(payloadFile),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException("qrtool: " + stderr.Trim());
            return stdout;
        }
        finally
        {
            File.Delete(payloadFile);
        }
    }
}
