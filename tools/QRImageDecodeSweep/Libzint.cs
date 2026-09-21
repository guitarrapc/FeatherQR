using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace QRImageDecodeSweep;

/// <summary>
/// libzint through the pinned ZXingCpp package's creator.
/// The creator dies with an access violation on some payloads, some of the time (Standard QR case 301, a 523-character byte payload at level Q, about one run in three), and a managed process cannot catch that.
/// So it runs in a worker process that appends each finished case to a file; when the worker dies the case it died on is tried again, and only after eight deaths in a row is it left out and reported, so that a run does not differ from the next by a case.
/// </summary>
internal static class Libzint
{
    private const string WorkerCommand = "libzint-worker";
    private const int Attempts = 8;

    private static readonly ConcurrentDictionary<(string, int), Symbol> symbols = new();
    private static readonly ConcurrentDictionary<(string, int), Rendered> natives = new();
    private static readonly HashSet<(string, int)> refused = [];

    public static Symbol? Encode(CaseDefinition d) => symbols.TryGetValue((d.Symbology, d.CaseId), out var cached) ? cached : null;

    public static Rendered? NativeRender(CaseDefinition d, Symbol _, Random __) => natives.TryGetValue((d.Symbology, d.CaseId), out var cached) ? cached : null;

    public static bool IsWorker(string[] args) => args.Length == 6 && args[0] == WorkerCommand;

    /// <summary>Creates every case's symbol and its own-writer image. <paramref name="nativeKindIndex"/> seeds the image as the sweep would have.</summary>
    public static void Fill(string symbology, int caseCount, int nativeKindIndex)
    {
        var file = Path.Combine(Path.GetTempPath(), "qr-sweep-libzint-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var next = 0;
            var deaths = 0;
            while (next < caseCount)
            {
                var exitCode = RunWorker(symbology, next, caseCount, nativeKindIndex, file);
                var reached = Load(symbology, file);
                if (exitCode == 0)
                    break;

                // The worker died on the case after the last one it finished
                var died = Math.Max(next, reached + 1);
                deaths = died == next ? deaths + 1 : 1;
                next = died;
                if (deaths == Attempts)
                {
                    Console.WriteLine($"libzint: {symbology} case {died} killed the creator {Attempts} times and is left out");
                    next++;
                    deaths = 0;
                }
            }
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>The worker: cases <c>from</c> to <c>to</c>, each appended and flushed as it finishes.</summary>
    public static int RunAsWorker(string[] args)
    {
        var symbology = args[1];
        var from = int.Parse(args[2], CultureInfo.InvariantCulture);
        var to = int.Parse(args[3], CultureInfo.InvariantCulture);
        var nativeKindIndex = int.Parse(args[4], CultureInfo.InvariantCulture);
        using var writer = new StreamWriter(args[5], append: true);
        for (var caseId = from; caseId < to; caseId++)
        {
            var d = Cases.Create(symbology, caseId);
            try
            {
                var symbol = CreateSymbol(d);
                var native = CreateNative(d, Sweep.RenderRandom(caseId, nativeKindIndex));
                var modules = string.Create(symbol.Modules.Length, symbol.Modules, static (span, m) =>
                {
                    for (var i = 0; i < span.Length; i++)
                        span[i] = m[i] ? '1' : '0';
                });
                writer.WriteLine(string.Join(' ', caseId, symbol.Width, symbol.Height, symbol.VersionName, modules, native.Width, native.Height, native.PixelsPerModule.ToString(CultureInfo.InvariantCulture), Convert.ToBase64String(native.Luminance)));
            }
            catch (Exception ex)
            {
                // A refusal, not a death: the case is simply not in this lineage
                writer.WriteLine(string.Join(' ', caseId, "refused", ex.Message.ReplaceLineEndings(" ")));
            }
            writer.Flush();
        }
        return 0;
    }

    private static int RunWorker(string symbology, int from, int to, int nativeKindIndex, string file)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        // Launched through the dotnet host, the first argument is this assembly
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
            startInfo.ArgumentList.Add(typeof(Libzint).Assembly.Location);
        foreach (var argument in new[] { WorkerCommand, symbology, from.ToString(CultureInfo.InvariantCulture), to.ToString(CultureInfo.InvariantCulture), nativeKindIndex.ToString(CultureInfo.InvariantCulture), file })
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)!;
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>Reads what the workers have written so far and returns the highest case among it, or -1.</summary>
    private static int Load(string symbology, string file)
    {
        var reached = -1;
        if (!File.Exists(file))
            return reached;

        foreach (var line in File.ReadLines(file))
        {
            var f = line.Split(' ', 9);
            var caseId = int.Parse(f[0], CultureInfo.InvariantCulture);
            reached = Math.Max(reached, caseId);
            if (f[1] == "refused")
            {
                if (refused.Add((symbology, caseId)))
                    Console.WriteLine($"libzint refused {symbology} case {caseId}: {string.Join(' ', f.Skip(2))}");
                continue;
            }
            if (f.Length != 9 || symbols.ContainsKey((symbology, caseId)))
                continue;

            var width = int.Parse(f[1], CultureInfo.InvariantCulture);
            var height = int.Parse(f[2], CultureInfo.InvariantCulture);
            symbols[(symbology, caseId)] = new Symbol([.. f[4].Select(static c => c == '1')], width, height, f[3]);
            natives[(symbology, caseId)] = new Rendered(Convert.FromBase64String(f[8]), int.Parse(f[5], CultureInfo.InvariantCulture), int.Parse(f[6], CultureInfo.InvariantCulture), float.Parse(f[7], CultureInfo.InvariantCulture));
        }
        return reached;
    }

    private static ZXingCpp.Barcode Create(CaseDefinition d)
    {
        // M1 has no selectable level
        var (format, options) = d.Symbology switch
        {
            Symbologies.StandardQr => (ZXingCpp.BarcodeFormat.QRCode, $"ecLevel={d.Ecc}"),
            Symbologies.MicroQr => (ZXingCpp.BarcodeFormat.MicroQRCode, d.Version == 1 ? "version=1" : $"version={d.Version},ecLevel={d.Ecc}"),
            _ => (ZXingCpp.BarcodeFormat.RMQRCode, $"version={d.Version},ecLevel={d.Ecc}"),
        };
        return new ZXingCpp.BarcodeCreator(format) { Options = options }.From(d.Text);
    }

    private static Symbol CreateSymbol(CaseDefinition d)
    {
        using var barcode = Create(d);
        using var image = barcode.ToImage(new ZXingCpp.WriterOptions { Scale = 1, AddQuietZones = false });
        var pixels = image.ToArray();
        var modules = new bool[image.Width * image.Height];
        for (var i = 0; i < modules.Length; i++)
            modules[i] = pixels[i] < 128;
        return new Symbol(modules, image.Width, image.Height, Symbologies.VersionName(d.Symbology, image.Width, image.Height));
    }

    private static Rendered CreateNative(CaseDefinition d, Random random)
    {
        using var barcode = Create(d);
        var ppm = 1 + random.Next(6);
        using var image = barcode.ToImage(new ZXingCpp.WriterOptions { Scale = ppm, AddQuietZones = true });
        return new Rendered(image.ToArray(), image.Width, image.Height, ppm);
    }
}
