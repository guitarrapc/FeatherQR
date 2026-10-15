using System.Diagnostics;
using System.Text;

namespace QRInteropFixtures;

/// <summary>
/// Micro QR fixture generator backed by the Rust <c>qrtool</c> CLI (which builds
/// on the <c>qrcode</c> crate), the second external encoder lineage, independent
/// of zint and of the ZXing family. Uses the prebuilt release binary pinned by
/// version and SHA-256 (see <c>tools/get_qrtool.cs</c>), so no Rust toolchain is required.
/// The module matrix is read from the tool's ASCII output (2 characters per
/// module, <c>##</c> dark), which is exact, no image parsing involved.
/// </summary>
public sealed class QrtoolMicroQRFixtureGenerator : IMicroQRFixtureGenerator
{
    public const string PinnedVersion = "0.13.2";

    private readonly string? _exePath;

    public QrtoolMicroQRFixtureGenerator(string repoRoot)
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
                var version = Run("--version").Trim();
                return version == $"qrtool {PinnedVersion}";
            }
            catch
            {
                return false;
            }
        }
    }

    public bool SupportsCase(MicroQRFixtureCaseDefinition caseDefinition) => true;

    public GeneratedFixture Generate(MicroQRFixtureCaseDefinition caseDefinition)
    {
        // The qrcode crate models M1's implicit detection-only level as L.
        var level = caseDefinition.ErrorCorrectionLevel == "ErrorDetectionOnly"
            ? "l"
            : caseDefinition.ErrorCorrectionLevel.ToLowerInvariant();
        var mode = caseDefinition.Mode.ToLowerInvariant();

        // Payload goes through a file: command-line arguments are not
        // encoding-safe for UTF-8 payloads on Windows.
        var payloadFile = Path.GetTempFileName();
        string output;
        try
        {
            // Kanji mode takes raw Shift_JIS bytes. Every other mode takes UTF-8 text.
            if (caseDefinition.Mode == KanjiPayload.ModeName)
                File.WriteAllBytes(payloadFile, KanjiPayload.ToShiftJisBytes(caseDefinition.PayloadText, caseDefinition.ShiftJisHex));
            else
                File.WriteAllText(payloadFile, caseDefinition.PayloadText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            output = Run($"encode --variant micro --symbol-version {caseDefinition.Version} --error-correction-level {level} --mode {mode} --margin 0 --type ascii --read-from \"{payloadFile}\"");
        }
        finally
        {
            File.Delete(payloadFile);
        }

        var expectedSize = 9 + 2 * caseDefinition.Version;
        var modules = ParseAsciiMatrix(output, expectedSize, caseDefinition.Id);

        var manifest = new FixtureManifest
        {
            Id = caseDefinition.Id,
            Generator = Name,
            GeneratorVersion = PinnedVersion,
            SymbolType = "MicroQR",
            Version = caseDefinition.Version,
            Width = expectedSize,
            Height = expectedSize,
            ErrorCorrectionLevel = caseDefinition.ErrorCorrectionLevel,
            Mode = caseDefinition.Mode,
            MaskPattern = -1, // filled in by the sanity gate from the zxing-cpp reader
            PayloadText = caseDefinition.PayloadText,
            PayloadUtf8Hex = Convert.ToHexString(Encoding.UTF8.GetBytes(caseDefinition.PayloadText)),
            EciCharset = null, // Micro QR has no ECI
            QuietZoneModules = FixtureWriter.MicroQRQuietZoneModules,
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
    private static byte[] ParseAsciiMatrix(string output, int expectedSize, string caseId)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n').Where(static l => l.Length > 0).ToArray();
        if (lines.Length != expectedSize)
            throw new InvalidOperationException($"qrtool produced {lines.Length} rows for case {caseId}, expected {expectedSize}.");

        var modules = new byte[expectedSize * expectedSize];
        for (var row = 0; row < expectedSize; row++)
        {
            var line = lines[row];
            for (var col = 0; col < expectedSize; col++)
            {
                // Trailing light modules may be trimmed from the line.
                var index = col * 2;
                modules[row * expectedSize + col] = index < line.Length && line[index] == '#' ? (byte)1 : (byte)0;
            }
        }

        return modules;
    }
}
