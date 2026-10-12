using System.Diagnostics;
using System.Text;
using ZXingCpp;
using CppFormat = ZXingCpp.BarcodeFormat;

namespace QRInteropFixtures;

/// <summary>
/// Shift_JIS mapping probe. ISO/IEC 18004 Kanji mode carries JIS X 0208 code
/// points, but the JIS X 0208 standard mapping and Microsoft CP932 disagree on a
/// handful of them (0x8160 wave dash U+301C vs fullwidth tilde U+FF5E, and
/// friends). This decides, empirically, which mapping each available oracle
/// applies, so the fixture corpus can avoid asserting on characters where the
/// oracles themselves disagree. Findings go in
/// .github/docs/specs/qrcode-test-fixtures.md; nothing here runs in CI.
/// </summary>
public static class KanjiMappingProbe
{
    /// <summary>Shift_JIS lead/trail pairs where JIS X 0208 and CP932 disagree, plus unambiguous controls.</summary>
    private static readonly (ushort Sjis, string Note, string JisX0208, string Cp932)[] Cases =
    [
        // 0x815C is a control, not a divergence: it is widely quoted as one, but both
        // mappings give U+2015. The sweep is what settled that, and what found 0x815F.
        (0x815C, "control: horizontal bar, both mappings agree", "U+2015", "U+2015"),
        (0x815F, "reverse solidus / fullwidth reverse solidus", "U+005C", "U+FF3C"),
        (0x8160, "wave dash / fullwidth tilde", "U+301C", "U+FF5E"),
        (0x8161, "double vertical line / parallel to", "U+2016", "U+2225"),
        (0x817C, "minus sign / fullwidth hyphen-minus", "U+2212", "U+FF0D"),
        (0x8191, "cent sign / fullwidth cent", "U+00A2", "U+FFE0"),
        (0x8192, "pound sign / fullwidth pound", "U+00A3", "U+FFE1"),
        (0x81CA, "not sign / fullwidth not", "U+00AC", "U+FFE2"),
        (0x82B1, "control: hiragana ko", "U+3053", "U+3053"),
        (0x889F, "control: first level-1 kanji", "U+4E9C", "U+4E9C"),
        (0x9FFC, "control: end of first Kanji-mode range", "?", "?"),
        (0xE040, "control: start of second Kanji-mode range", "?", "?"),
        (0xEAA4, "control: last JIS X 0208 cell", "?", "?"),
    ];

    public static int Run(string repoRoot)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cp932 = Encoding.GetEncoding(932);

        Console.WriteLine("=== Shift_JIS mapping probe (Kanji mode) ===");
        Console.WriteLine();

        var exe = FindQrtool(repoRoot);
        if (exe is null)
        {
            Console.Error.WriteLine("qrtool not found; run tools/QRInteropFixtures/get-qrtool.ps1 first.");
            return 1;
        }

        // Raw Shift_JIS bytes, so no encoder-side mapping is involved: only the
        // DECODERS get to interpret them.
        var sjisBytes = new byte[Cases.Length * 2];
        for (var i = 0; i < Cases.Length; i++)
        {
            sjisBytes[i * 2] = (byte)(Cases[i].Sjis >> 8);
            sjisBytes[i * 2 + 1] = (byte)(Cases[i].Sjis & 0xFF);
        }

        Console.WriteLine($"payload: {Cases.Length} Shift_JIS cells, bytes = {Convert.ToHexString(sjisBytes)}");
        Console.WriteLine();

        Console.WriteLine("-- .NET CP932 reference (what Encoding.GetEncoding(932) yields) --");
        for (var i = 0; i < Cases.Length; i++)
        {
            var c = cp932.GetString(sjisBytes, i * 2, 2);
            Console.WriteLine($"  0x{Cases[i].Sjis:X4}  cp932 -> {Describe(c),-12} (JIS X 0208 expects {Cases[i].JisX0208}, CP932 expects {Cases[i].Cp932})  {Cases[i].Note}");
        }
        Console.WriteLine();

        var payloadFile = Path.GetTempFileName();
        string ascii;
        try
        {
            File.WriteAllBytes(payloadFile, sjisBytes);
            ascii = RunQrtool(exe, $"encode --variant normal --symbol-version 4 --error-correction-level m --mode kanji --margin 0 --type ascii --read-from \"{payloadFile}\"");
        }
        finally
        {
            File.Delete(payloadFile);
        }

        var (modules, size) = ParseAsciiMatrix(ascii);
        Console.WriteLine($"qrtool produced a Kanji-mode Standard QR: {size}x{size} modules");
        Console.WriteLine();

        Console.WriteLine("-- zxing-cpp reader --");
        ReportZXingCpp(modules, size);
        Console.WriteLine();

        Console.WriteLine("-- ZXing.Net reader --");
        ReportZXingNet(modules, size);
        Console.WriteLine();

        return 0;
    }

    private static void ReportZXingCpp(byte[] modules, int size)
    {
        const int Scale = 8;
        const int Quiet = 4;
        var w = (size + Quiet * 2) * Scale;
        var lum = new byte[w * w];
        lum.AsSpan().Fill(255);
        for (var row = 0; row < size; row++)
        {
            for (var col = 0; col < size; col++)
            {
                if (modules[row * size + col] == 0) continue;
                for (var y = 0; y < Scale; y++)
                    lum.AsSpan(((Quiet + row) * Scale + y) * w + (Quiet + col) * Scale, Scale).Clear();
            }
        }

        var results = ZXingCppCalls.Read(new BarcodeReader { Formats = CppFormat.QRCode, TryHarder = true }, lum, w, w);
        if (results.Length != 1)
        {
            Console.WriteLine($"  reader found {results.Length} symbols");
            return;
        }

        var r = results[0];
        Console.WriteLine($"  hasEci={r.HasECI} symId={r.SymbologyIdentifier} contentType={r.ContentType}");
        Console.WriteLine($"  Bytes    = {Convert.ToHexString(r.Bytes)}");
        Console.WriteLine($"  BytesECI = {Convert.ToHexString(r.BytesECI)}");
        ReportText("zxing-cpp", r.Text);
    }

    private static void ReportZXingNet(byte[] modules, int size)
    {
        var matrix = new ZXing.Common.BitMatrix(size, size);
        for (var row = 0; row < size; row++)
            for (var col = 0; col < size; col++)
                if (modules[row * size + col] != 0)
                    matrix[col, row] = true;

        try
        {
            var result = new ZXing.QrCode.Internal.Decoder().decode(matrix, null);
            ReportText("ZXing.Net", result.Text);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ReportText(string reader, string? text)
    {
        if (text is null)
        {
            Console.WriteLine($"  {reader}: null text");
            return;
        }

        Console.WriteLine($"  {reader} text = \"{text}\" ({text.Length} chars)");
        var index = 0;
        foreach (var ch in text)
        {
            var expected = index < Cases.Length ? Cases[index] : default;
            var verdict = index >= Cases.Length
                ? ""
                : Describe(ch.ToString()) == expected.JisX0208 ? "  <= JIS X 0208"
                : Describe(ch.ToString()) == expected.Cp932 ? "  <= CP932"
                : "";
            Console.WriteLine($"    [{index,2}] 0x{(index < Cases.Length ? Cases[index].Sjis : 0):X4} -> {Describe(ch.ToString())} '{ch}'{verdict}");
            index++;
        }
    }

    private static string Describe(string s) => s.Length == 1 ? $"U+{(int)s[0]:X4}" : string.Join("+", s.Select(c => $"U+{(int)c:X4}"));

    private static (byte[] Modules, int Size) ParseAsciiMatrix(string ascii)
    {
        var lines = ascii.Replace("\r\n", "\n").Split('\n').Where(static l => l.Length > 0).ToArray();
        var size = lines.Length;
        var modules = new byte[size * size];
        for (var row = 0; row < size; row++)
        {
            var line = lines[row];
            for (var col = 0; col < size; col++)
            {
                // qrtool ascii output is two characters per module ('##' dark);
                // trailing light modules are trimmed from each line.
                var at = col * 2;
                modules[row * size + col] = at < line.Length && line[at] == '#' ? (byte)1 : (byte)0;
            }
        }
        return (modules, size);
    }

    private static string? FindQrtool(string repoRoot)
    {
        var root = Path.Combine(repoRoot, "tools", "QRInteropFixtures", "external", "qrtool");
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateFiles(root, "qrtool.exe", SearchOption.AllDirectories).FirstOrDefault()
            ?? Directory.EnumerateFiles(root, "qrtool", SearchOption.AllDirectories).FirstOrDefault();
    }

    private static string RunQrtool(string exe, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {exe}");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"qrtool {arguments} failed ({process.ExitCode}): {stderr.Trim()}");
        return stdout;
    }
}
