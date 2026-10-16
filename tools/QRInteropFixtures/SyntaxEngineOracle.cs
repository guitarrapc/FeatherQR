using System.Diagnostics;
using System.Text;

namespace QRInteropFixtures;

/// <summary>
/// Drives the GS1 Barcode Syntax Engine's console app (a prebuilt release asset fetched by
/// <c>tools/get_gs1_syntax_engine.cs</c>) as an oracle. The app is a stdin menu: option 1 takes raw data
/// (plain text, an unbracketed element string with <c>^</c> as FNC1, or a Digital Link URI), option 2
/// a bracketed element string, option 3 scan data with its symbology identifier. After each input it
/// prints either an error with its markup or its state: the barcode message, the bracketed AI
/// string, the canonical Digital Link URI and the HRI lines. One process runs per input, with the
/// pinned dictionary loaded through <c>--syndict</c>, so the verdict never depends on the app's
/// embedded table.
/// </summary>
public sealed class SyntaxEngineOracle
{
    public const string Version = "1.4.1";

    private readonly string exePath;
    private readonly string dictionaryPath;

    public SyntaxEngineOracle(string repoRoot, string dictionaryPath)
    {
        exePath = Path.Combine(repoRoot, "tools", "QRInteropFixtures", "external", "gs1-syntax-engine", "gs1encoders-cpp-app.exe");
        this.dictionaryPath = dictionaryPath;
    }

    public bool IsAvailable => File.Exists(exePath) && File.Exists(dictionaryPath);

    public string ExePath => exePath;

    /// <summary>The library version the app reports (<c>Library version: …</c>).</summary>
    public string ReportedVersion()
    {
        var output = RunProcess("--version", "");
        var line = output.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => l.StartsWith("Library version:", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"The Syntax Engine app printed no library version:\n{output}");
        return line.Substring("Library version:".Length).Trim();
    }

    public Gs1SyntaxEngineVerdict Run(Gs1InputKind kind, string input)
    {
        var (menu, data) = kind switch
        {
            Gs1InputKind.Bracketed => ("2", input),
            Gs1InputKind.ElementString => ("1", "^" + input.Replace('\u001D', '^')),
            Gs1InputKind.DigitalLink => ("1", input),
            Gs1InputKind.ScanData => ("3", input),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        if (data.Contains('\n') || data.Contains('\r'))
            throw new ArgumentException("The console app reads one line per input.", nameof(input));

        var output = RunProcess($"--syndict \"{dictionaryPath}\"", menu + "\n" + data + "\n0\n");
        var verdict = Parse(output, input);

        // A Digital Link URI (given directly or as scan data) is AI data whose barcode message is the URI itself.
        // Its element string comes from a second run on the bracketed form the engine derived.
        if (verdict.Valid && verdict.ElementString is null && verdict.Bracketed is not null)
        {
            var second = Parse(RunProcess($"--syndict \"{dictionaryPath}\"", "2\n" + verdict.Bracketed + "\n0\n"), verdict.Bracketed);
            if (!second.Valid)
                throw new InvalidOperationException($"The engine refuses its own bracketed form \"{verdict.Bracketed}\" of \"{Fnc1Writers.Escape(input)}\": {second.Error}");
            verdict = verdict with { ElementString = second.ElementString };
        }
        return verdict;
    }

    private string RunProcess(string arguments, string stdin)
    {
        var startInfo = new ProcessStartInfo(exePath, arguments)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {exePath}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using (var writer = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
            writer.Write(stdin);
        if (!process.WaitForExit(30_000))
        {
            process.Kill();
            throw new TimeoutException($"The Syntax Engine app did not exit for input {stdin}.");
        }
        var output = stdoutTask.Result;
        if (output.Contains("Failed to initialise", StringComparison.Ordinal))
            throw new InvalidOperationException($"The Syntax Engine app failed to initialise:\n{output}\n{stderrTask.Result}");
        return output;
    }

    private static Gs1SyntaxEngineVerdict Parse(string output, string input)
    {
        var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        var errorLine = lines.FirstOrDefault(l => l.StartsWith("ERROR message:", StringComparison.Ordinal));
        if (errorLine is not null)
        {
            var markupLine = lines.FirstOrDefault(l => l.StartsWith("ERROR markup:", StringComparison.Ordinal));
            return new Gs1SyntaxEngineVerdict
            {
                Valid = false,
                Error = errorLine.Substring("ERROR message:".Length).Trim(),
                ErrorMarkup = markupLine?.Substring("ERROR markup:".Length).Trim(),
            };
        }

        // Two state blocks are printed: the empty one before the input and the one after it.
        var stateStart = Array.FindLastIndex(lines, l => l.StartsWith("Current state:", StringComparison.Ordinal));
        if (stateStart < 0)
            throw new InvalidOperationException($"No state block in the Syntax Engine output for \"{Fnc1Writers.Escape(input)}\":\n{output}");
        var message = Field(lines, stateStart, "Barcode message:");
        var aiString = Field(lines, stateStart, "AI element string:");
        var dlUri = Field(lines, stateStart, "GS1 Digital Link URI:");
        var hriIndex = Array.FindIndex(lines, stateStart, l => l.TrimStart().StartsWith("HRI:", StringComparison.Ordinal));
        var hri = new List<string>();
        for (var i = hriIndex + 1; i < lines.Length && lines[i].StartsWith("        ", StringComparison.Ordinal); i++)
            hri.Add(lines[i].Trim());

        var notAi = aiString.StartsWith("⧚", StringComparison.Ordinal); // "⧚ Not AI-based data ⧚"
        string? elementString = null;
        if (message.StartsWith("^", StringComparison.Ordinal))
            elementString = message.Substring(1).Replace('^', '\u001D');
        var dlError = dlUri.StartsWith("⧚", StringComparison.Ordinal) ? dlUri.Trim('⧚', ' ') : null;

        return new Gs1SyntaxEngineVerdict
        {
            Valid = true,
            DataString = message,
            ElementString = elementString,
            Bracketed = notAi ? null : aiString,
            Hri = notAi ? null : hri.ToArray(),
            DigitalLink = dlError is null && dlUri.Length > 0 ? dlUri : null,
            DigitalLinkError = dlError,
        };
    }

    private static string Field(string[] lines, int start, string label)
    {
        var line = lines.Skip(start).FirstOrDefault(l => l.TrimStart().StartsWith(label, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"No '{label}' line in the Syntax Engine state block.");
        return line.TrimStart().Substring(label.Length).Trim();
    }
}
