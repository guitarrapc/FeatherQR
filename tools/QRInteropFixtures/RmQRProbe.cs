using System.Diagnostics;
using System.Text;
using ZXingCpp;

namespace QRInteropFixtures;

/// <summary>
/// Diagnostic for the rMQR oracle facts that must be established before fixtures can
/// be generated; the answers are recorded in specs/qrcode-test-fixtures.md:
/// (1) how libzint's <c>version=N</c> option maps to rMQR sizes,
/// (2) what the zxing-cpp reader reports in <c>Extra("Version"/"EcLevel"/"DataMask")</c>
///     for rMQR symbols, and
/// (3) whether zxing-cpp reads qrtool's rMQR output (second encoder lineage).
/// Results are recorded in .github/docs/specs/qrcode-test-fixtures.md.
/// </summary>
public static class RmQRProbe
{
    // Height-major version table (index 0-31), see specs/rmqr-encoder.md.
    private static readonly (int Height, int Width)[] Versions =
    [
        (7, 43), (7, 59), (7, 77), (7, 99), (7, 139),
        (9, 43), (9, 59), (9, 77), (9, 99), (9, 139),
        (11, 27), (11, 43), (11, 59), (11, 77), (11, 99), (11, 139),
        (13, 27), (13, 43), (13, 59), (13, 77), (13, 99), (13, 139),
        (15, 43), (15, 59), (15, 77), (15, 99), (15, 139),
        (17, 43), (17, 59), (17, 77), (17, 99), (17, 139),
    ];

    public static int Run(string repoRoot)
    {
        Console.WriteLine("== libzint version=N mapping (BarcodeCreator RMQRCode, scale 1, no quiet zone) ==");
        var mappingOk = 0;
        for (var n = 1; n <= 40; n++)
        {
            try
            {
                using var barcode = ZXingCppCalls.Create(new BarcodeCreator(BarcodeFormat.RMQRCode) { Options = $"version={n}" }, "1");
                using var image = ZXingCppCalls.ToImage(barcode, new WriterOptions { Scale = 1, AddQuietZones = false });
                var expected = n is >= 1 and <= 32 ? $"R{Versions[n - 1].Height}x{Versions[n - 1].Width}" : "(auto)";
                var actual = $"R{image.Height}x{image.Width}";
                var match = n <= 32 && actual == expected;
                if (match) mappingOk++;
                Console.WriteLine($"version={n,-2} -> {actual,-8} expected {expected}{(n <= 32 ? (match ? "  OK" : "  MISMATCH") : "")}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"version={n,-2} -> error: {ex.Message}");
            }
        }
        Console.WriteLine($"mapping OK: {mappingOk}/32");

        Console.WriteLine();
        Console.WriteLine("== libzint automatic version choice (no version option): which fit policy? ==");
        foreach (var payload in new[] { "012345678901", "0123456789012", "012345678901234", new string('0', 100) })
        {
            using var barcode = ZXingCppCalls.Create(new BarcodeCreator(BarcodeFormat.RMQRCode) { Options = "ecLevel=M" }, payload);
            using var image = ZXingCppCalls.ToImage(barcode, new WriterOptions { Scale = 1, AddQuietZones = false });
            Console.WriteLine($"{payload.Length,3} digits at M -> R{image.Height}x{image.Width}");
        }

        Console.WriteLine();
        Console.WriteLine("== zxing-cpp reader Extra(...) for libzint-created rMQR (all versions x M/H, payload \"12345\") ==");
        var readOk = 0;
        for (var i = 0; i < Versions.Length; i++)
        {
            foreach (var ecc in new[] { "M", "H" })
            {
                using var barcode = ZXingCppCalls.Create(new BarcodeCreator(BarcodeFormat.RMQRCode) { Options = $"version={i + 1},ecLevel={ecc}" }, "12345");
                using var image = ZXingCppCalls.ToImage(barcode, new WriterOptions { Scale = 4, AddQuietZones = true });
                var results = ZXingCppCalls.Read(new BarcodeReader { Formats = BarcodeFormat.RMQRCode, TryHarder = true }, image.ToArray(), image.Width, image.Height, image.Format);
                var expectedVersion = $"R{Versions[i].Height}x{Versions[i].Width}";
                if (results.Length == 1 && results[0].Text == "12345")
                {
                    var r = results[0];
                    var ok = r.Extra("Version") == expectedVersion && r.Extra("EcLevel") == ecc;
                    if (ok) readOk++;
                    if (!ok || i is 0 or 10 or 31)
                        Console.WriteLine($"{expectedVersion}-{ecc}: Version=\"{r.Extra("Version")}\" EcLevel=\"{r.Extra("EcLevel")}\" DataMask=\"{r.Extra("DataMask")}\" Format={r.Format}{(ok ? "" : "  <<< unexpected")}");
                }
                else
                {
                    Console.WriteLine($"{expectedVersion}-{ecc}: NOT READ ({results.Length} results)");
                }
            }
        }
        Console.WriteLine($"read with expected Version/EcLevel: {readOk}/64 (only the first/middle/last and unexpected rows are printed)");

        Console.WriteLine();
        Console.WriteLine("== zxing-cpp reads qrtool --variant rmqr output (ASCII, module-exact) ==");
        var exe = FindQrtool(repoRoot);
        if (exe is null)
        {
            Console.WriteLine("qrtool not found under tools/QRInteropFixtures/external/qrtool (run get-qrtool.ps1)");
            return 0;
        }

        var qrtoolOk = 0;
        foreach (var (i, ecc, payload) in new[] { (0, "m", "12345"), (10, "h", "AB"), (4, "m", "HELLO WORLD"), (31, "h", "こんにちは"), (28, "m", "0123456789") })
        {
            var (h, w) = Versions[i];
            var expectedVersion = $"R{h}x{w}";
            var ascii = RunQrtool(exe, $"encode --variant rmqr -v {h} {w} -l {ecc} --type ascii -m 0 \"{payload}\"");
            var lines = ascii.Replace("\r\n", "\n").Split('\n').Where(static l => l.Length > 0).ToArray();
            if (lines.Length != h || lines[0].Length != w * 2)
            {
                Console.WriteLine($"{expectedVersion}: qrtool output {lines.Length} rows x {lines[0].Length / 2} cols, expected {h}x{w}");
                continue;
            }

            const int ppm = 4, qz = 2;
            var pw = (w + 2 * qz) * ppm;
            var ph = (h + 2 * qz) * ppm;
            var lum = new byte[pw * ph];
            lum.AsSpan().Fill(255);
            for (var r = 0; r < h; r++)
            {
                for (var c = 0; c < w; c++)
                {
                    if (lines[r][c * 2] != '#') continue;
                    for (var y = 0; y < ppm; y++)
                        lum.AsSpan(((qz + r) * ppm + y) * pw + (qz + c) * ppm, ppm).Clear();
                }
            }

            var results = ZXingCppCalls.Read(new BarcodeReader { Formats = BarcodeFormat.RMQRCode, TryHarder = true }, lum, pw, ph);
            if (results.Length == 1)
            {
                var r = results[0];
                // The wrapper exposes no charset hint, so non-ASCII byte-mode content without ECI is
                // compared on raw bytes (zxing-cpp may guess a legacy charset for the Text view).
                var bytesMatch = r.Bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(payload));
                var ok = (r.Text == payload || bytesMatch) && r.Extra("Version") == expectedVersion && r.Extra("EcLevel") == ecc.ToUpperInvariant();
                if (ok) qrtoolOk++;
                Console.WriteLine($"{expectedVersion}-{ecc.ToUpperInvariant()} \"{payload}\": text=\"{r.Text}\" bytesMatchUtf8={bytesMatch} HasECI={r.HasECI} Version=\"{r.Extra("Version")}\" EcLevel=\"{r.Extra("EcLevel")}\"{(ok ? "  OK" : "  <<< unexpected")}");
            }
            else
            {
                Console.WriteLine($"{expectedVersion}-{ecc.ToUpperInvariant()} \"{payload}\": NOT READ ({results.Length} results)");
            }
        }
        Console.WriteLine($"qrtool rMQR read by zxing-cpp: {qrtoolOk}/5");
        return 0;
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
