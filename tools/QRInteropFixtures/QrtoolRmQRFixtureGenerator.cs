using System.Diagnostics;
using System.Text;

namespace QRInteropFixtures;

/// <summary>
/// rMQR fixture generator backed by the Rust <c>qrtool</c> CLI (qrcode2 crate),
/// the second external encoder lineage, independent of zint and of the ZXing
/// family. Uses the prebuilt release binary pinned by version and SHA-256 (see
/// <c>get-qrtool.ps1</c>), so no Rust toolchain is required. The module matrix is
/// read from the tool's ASCII output (2 characters per module, <c>##</c> dark),
/// which is exact, no image parsing involved. rMQR is selected with
/// <c>--variant rmqr --symbol-version H W</c> (verified by <c>probe-rmqr</c>).
/// </summary>
public sealed class QrtoolRmQRFixtureGenerator : IRmQRFixtureGenerator
{
    public const string PinnedVersion = QrtoolMicroQRFixtureGenerator.PinnedVersion;

    private readonly string? _exePath;

    public QrtoolRmQRFixtureGenerator(string repoRoot)
    {
        var root = Path.Combine(repoRoot, "tools", "QRInteropFixtures", "external", "qrtool");
        if (Directory.Exists(root))
        {
            _exePath = Directory.EnumerateFiles(root, "qrtool.exe", SearchOption.AllDirectories).FirstOrDefault()
                ?? Directory.EnumerateFiles(root, "qrtool", SearchOption.AllDirectories).FirstOrDefault();
        }
    }

    public string Name => "qrtool";

    public bool IsAvailable
    {
        get
        {
            if (_exePath is null)
                return false;
            try
            {
                return Run("--version").Trim() == $"qrtool {PinnedVersion}";
            }
            catch
            {
                return false;
            }
        }
    }

    public bool SupportsCase(RmQRFixtureCaseDefinition caseDefinition) => true;

    public GeneratedFixture Generate(RmQRFixtureCaseDefinition caseDefinition)
    {
        var entry = RmQRVersionTable.Find(caseDefinition.Height, caseDefinition.Width);
        var level = caseDefinition.ErrorCorrectionLevel.ToLowerInvariant();
        var mode = caseDefinition.Mode.ToLowerInvariant();

        // Payload goes through a file: command-line arguments are not
        // encoding-safe for UTF-8 payloads on Windows.
        var payloadFile = Path.GetTempFileName();
        string output;
        try
        {
            // Kanji mode takes raw Shift_JIS bytes, and every other mode takes UTF-8 text.
            if (caseDefinition.Mode == KanjiPayload.ModeName)
                File.WriteAllBytes(payloadFile, KanjiPayload.ToShiftJisBytes(caseDefinition.PayloadText, caseDefinition.ShiftJisHex));
            else
                File.WriteAllText(payloadFile, caseDefinition.PayloadText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            output = Run($"encode --variant rmqr --symbol-version {caseDefinition.Height} {caseDefinition.Width} --error-correction-level {level} --mode {mode} --margin 0 --type ascii --read-from \"{payloadFile}\"");
        }
        finally
        {
            File.Delete(payloadFile);
        }

        var modules = ParseAsciiMatrix(output, caseDefinition.Width, caseDefinition.Height, caseDefinition.Id);

        var manifest = new FixtureManifest
        {
            Id = caseDefinition.Id,
            Generator = Name,
            GeneratorVersion = PinnedVersion,
            SymbolType = "rMQR",
            Version = entry.Number,
            VersionName = entry.Name,
            Width = caseDefinition.Width,
            Height = caseDefinition.Height,
            ErrorCorrectionLevel = caseDefinition.ErrorCorrectionLevel,
            Mode = caseDefinition.Mode,
            MaskPattern = -1, // filled in by the sanity gate from the zxing-cpp reader
            PayloadText = caseDefinition.PayloadText,
            PayloadUtf8Hex = Convert.ToHexString(Encoding.UTF8.GetBytes(caseDefinition.PayloadText)),
            EciCharset = null, // qrtool emits no ECI header (verified: reader HasECI = false)
            QuietZoneModules = FixtureWriter.RmQRQuietZoneModules,
            PixelsPerModule = FixtureWriter.PixelsPerModule,
        };

        return new GeneratedFixture(manifest, modules);
    }

    private string Run(string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _exePath!,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {_exePath}");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"qrtool {arguments} failed ({process.ExitCode}): {stderr.Trim()}");

        return stdout;
    }

    /// <summary>Parses qrtool's ASCII output: one line per row, two characters per module, '#' dark.</summary>
    private static byte[] ParseAsciiMatrix(string output, int width, int height, string caseId)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n').Where(static l => l.Length > 0).ToArray();
        if (lines.Length != height)
            throw new InvalidOperationException($"qrtool produced {lines.Length} rows for case {caseId}, expected {height}.");

        var modules = new byte[width * height];
        for (var row = 0; row < height; row++)
        {
            var line = lines[row];
            if (line.Length > width * 2)
                throw new InvalidOperationException($"qrtool produced a {line.Length / 2}-module row for case {caseId}, expected {width}.");
            for (var col = 0; col < width; col++)
            {
                // Trailing light modules may be trimmed from the line.
                var index = col * 2;
                modules[row * width + col] = index < line.Length && line[index] == '#' ? (byte)1 : (byte)0;
            }
        }

        return modules;
    }
}
