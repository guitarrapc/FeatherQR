using System.Diagnostics;
using System.Text;
using ZXingCpp;
using CppFormat = ZXingCpp.BarcodeFormat;

namespace QRInteropFixtures;

/// <summary>
/// Full Kanji-mode sweep. Encodes every structurally valid Shift_JIS cell in the
/// ISO/IEC 18004 Kanji-mode ranges (0x8140-0x9FFC, 0xE040-0xEBBF) through qrtool
/// (raw Shift_JIS bytes, so no encoder-side mapping is involved) and reads it
/// back with zxing-cpp, then diffs the result against .NET CP932.
///
/// This produces two things the implementation needs: the exact set of cells
/// where the JIS X 0208 mapping and CP932 disagree (which becomes the generated
/// table's override list), and the exact set of cells neither assigns. Run by
/// hand; the report is written next to the tool output.
/// </summary>
public static class KanjiSweepProbe
{
    private const int ChunkSize = 256;

    /// <summary>A swept cell: what zxing-cpp read, and what .NET CP932 says.</summary>
    public readonly record struct Cell(ushort Sjis, int Index13, string? ZXingCpp, string? Cp932);

    public static int Run(string repoRoot)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var exe = QrtoolRunner.Find(repoRoot);
        if (exe is null)
        {
            Console.Error.WriteLine("qrtool not found; run tools/QRInteropFixtures/get-qrtool.ps1 first.");
            return 1;
        }

        var cells = EnumerateCells().ToArray();
        Console.WriteLine($"sweeping {cells.Length} structurally valid Kanji-mode cells in chunks of {ChunkSize}");

        var results = new List<Cell>(cells.Length);
        var chunkIndex = 0;
        for (var offset = 0; offset < cells.Length; offset += ChunkSize)
        {
            var chunk = cells.AsSpan(offset, Math.Min(ChunkSize, cells.Length - offset)).ToArray();
            Sweep(exe, chunk, results);
            chunkIndex++;
            if (chunkIndex % 8 == 0)
                Console.WriteLine($"  ... {results.Count}/{cells.Length}");
        }

        Report(repoRoot, results);
        return 0;
    }

    /// <summary>
    /// Reads one chunk. When the reader returns a different number of characters
    /// than the chunk holds, the chunk contains at least one cell the reader does
    /// not map, so it is bisected until each such cell is isolated.
    /// </summary>
    private static void Sweep(string exe, ushort[] chunk, List<Cell> sink)
    {
        var text = Encode(exe, chunk);
        if (text is not null && text.Length == chunk.Length)
        {
            for (var i = 0; i < chunk.Length; i++)
                sink.Add(Describe(chunk[i], text[i].ToString()));
            return;
        }

        if (chunk.Length == 1)
        {
            // Isolated: either qrtool refused it or the reader dropped/replaced it.
            sink.Add(Describe(chunk[0], text is { Length: 1 } ? text : null));
            return;
        }

        var half = chunk.Length / 2;
        Sweep(exe, chunk[..half], sink);
        Sweep(exe, chunk[half..], sink);
    }

    private static Cell Describe(ushort sjis, string? read)
    {
        string? cp932;
        try
        {
            var decoded = Encoding.GetEncoding(932).GetString([(byte)(sjis >> 8), (byte)(sjis & 0xFF)]);
            // CP932 maps unassigned cells to the replacement character.
            cp932 = decoded.Length == 1 && decoded[0] != '�' ? decoded : null;
        }
        catch
        {
            cp932 = null;
        }

        var normalized = read is { Length: 1 } && read[0] != '�' ? read : null;
        return new Cell(sjis, ToIndex13(sjis), normalized, cp932);
    }

    private static string? Encode(string exe, ushort[] cells)
    {
        var bytes = new byte[cells.Length * 2];
        for (var i = 0; i < cells.Length; i++)
        {
            bytes[i * 2] = (byte)(cells[i] >> 8);
            bytes[i * 2 + 1] = (byte)(cells[i] & 0xFF);
        }

        var payloadFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(payloadFile, bytes);
            var ascii = QrtoolRunner.Run(exe, $"encode --variant normal --symbol-version 40 --error-correction-level l --mode kanji --margin 0 --type ascii --read-from \"{payloadFile}\"");
            var (modules, size) = QrtoolRunner.ParseAsciiMatrix(ascii);
            return ReadWithZXingCpp(modules, size);
        }
        catch (InvalidOperationException)
        {
            return null; // qrtool refused this cell set
        }
        finally
        {
            File.Delete(payloadFile);
        }
    }

    private static string? ReadWithZXingCpp(byte[] modules, int size)
    {
        const int Scale = 4;
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
        return results.Length == 1 ? results[0].Text : null;
    }

    /// <summary>ISO/IEC 18004 8.4.5 compaction: 13 bits, 0..8191.</summary>
    public static int ToIndex13(ushort sjis)
    {
        var shifted = sjis >= 0xE040 ? sjis - 0xC140 : sjis - 0x8140;
        return ((shifted >> 8) * 0xC0) + (shifted & 0xFF);
    }

    /// <summary>Every Shift_JIS pair the Kanji-mode ranges can express.</summary>
    public static IEnumerable<ushort> EnumerateCells()
    {
        for (var lead = 0x81; lead <= 0x9F; lead++)
            foreach (var trail in Trails(lead, 0x8140, 0x9FFC))
                yield return (ushort)((lead << 8) | trail);

        for (var lead = 0xE0; lead <= 0xEB; lead++)
            foreach (var trail in Trails(lead, 0xE040, 0xEBBF))
                yield return (ushort)((lead << 8) | trail);
    }

    private static IEnumerable<int> Trails(int lead, int rangeLow, int rangeHigh)
    {
        for (var trail = 0x40; trail <= 0xFC; trail++)
        {
            if (trail == 0x7F) continue; // never a Shift_JIS trail byte
            var value = (lead << 8) | trail;
            if (value < rangeLow || value > rangeHigh) continue;
            yield return trail;
        }
    }

    private static void Report(string repoRoot, List<Cell> cells)
    {
        var agree = 0;
        var diverge = new List<Cell>();
        var onlyZXing = new List<Cell>();
        var onlyCp932 = new List<Cell>();
        var neither = new List<Cell>();

        foreach (var cell in cells)
        {
            if (cell.ZXingCpp is not null && cell.Cp932 is not null)
            {
                if (cell.ZXingCpp == cell.Cp932) agree++;
                else diverge.Add(cell);
            }
            else if (cell.ZXingCpp is not null) onlyZXing.Add(cell);
            else if (cell.Cp932 is not null) onlyCp932.Add(cell);
            else neither.Add(cell);
        }

        Console.WriteLine();
        Console.WriteLine("=== sweep summary ===");
        Console.WriteLine($"  cells swept          : {cells.Count}");
        Console.WriteLine($"  both agree           : {agree}");
        Console.WriteLine($"  both, DIVERGE        : {diverge.Count}");
        Console.WriteLine($"  zxing-cpp only       : {onlyZXing.Count}");
        Console.WriteLine($"  CP932 only           : {onlyCp932.Count}");
        Console.WriteLine($"  neither (unassigned) : {neither.Count}");
        Console.WriteLine();

        Console.WriteLine("-- divergences (JIS X 0208 per zxing-cpp vs CP932) --");
        foreach (var c in diverge)
            Console.WriteLine($"  0x{c.Sjis:X4} idx {c.Index13,4}: zxing-cpp U+{(int)c.ZXingCpp![0]:X4} '{c.ZXingCpp}'  cp932 U+{(int)c.Cp932![0]:X4} '{c.Cp932}'");
        Console.WriteLine();

        Console.WriteLine($"-- CP932 assigns, zxing-cpp does not ({onlyCp932.Count}) --");
        foreach (var c in onlyCp932.Take(40))
            Console.WriteLine($"  0x{c.Sjis:X4} idx {c.Index13,4}: cp932 U+{(int)c.Cp932![0]:X4} '{c.Cp932}'");
        if (onlyCp932.Count > 40) Console.WriteLine($"  ... and {onlyCp932.Count - 40} more");
        Console.WriteLine();

        Console.WriteLine($"-- zxing-cpp assigns, CP932 does not ({onlyZXing.Count}) --");
        foreach (var c in onlyZXing.Take(40))
            Console.WriteLine($"  0x{c.Sjis:X4} idx {c.Index13,4}: zxing-cpp U+{(int)c.ZXingCpp![0]:X4} '{c.ZXingCpp}'");
        if (onlyZXing.Count > 40) Console.WriteLine($"  ... and {onlyZXing.Count - 40} more");
        Console.WriteLine();

        var reportPath = Path.Combine(repoRoot, "tools", "QRInteropFixtures", "kanji-sweep.tsv");
        using var writer = new StreamWriter(reportPath, append: false, new UTF8Encoding(false));
        writer.WriteLine("sjis\tindex13\tzxingcpp\tcp932");
        foreach (var c in cells)
        {
            var z = c.ZXingCpp is null ? "" : $"U+{(int)c.ZXingCpp[0]:X4}";
            var m = c.Cp932 is null ? "" : $"U+{(int)c.Cp932[0]:X4}";
            writer.WriteLine($"{c.Sjis:X4}\t{c.Index13}\t{z}\t{m}");
        }
        Console.WriteLine($"full table written to {reportPath}");
    }
}

/// <summary>Shared qrtool process helper for the probes.</summary>
public static class QrtoolRunner
{
    public static string? Find(string repoRoot)
    {
        var root = Path.Combine(repoRoot, "tools", "QRInteropFixtures", "external", "qrtool");
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateFiles(root, "qrtool.exe", SearchOption.AllDirectories).FirstOrDefault()
            ?? Directory.EnumerateFiles(root, "qrtool", SearchOption.AllDirectories).FirstOrDefault();
    }

    public static string Run(string exe, string arguments)
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

    /// <summary>qrtool ascii output is two characters per module ('##' dark); trailing light modules are trimmed.</summary>
    public static (byte[] Modules, int Size) ParseAsciiMatrix(string ascii)
    {
        var lines = ascii.Replace("\r\n", "\n").Split('\n').Where(static l => l.Length > 0).ToArray();
        var size = lines.Length;
        var modules = new byte[size * size];
        for (var row = 0; row < size; row++)
        {
            var line = lines[row];
            for (var col = 0; col < size; col++)
            {
                var at = col * 2;
                modules[row * size + col] = at < line.Length && line[at] == '#' ? (byte)1 : (byte)0;
            }
        }
        return (modules, size);
    }
}
