using System.Collections;
using System.Text;
using FeatherQR;
using Net.Codecrete.QrCodeGenerator;
using ZXing.Common;
using CppFormat = ZXingCpp.BarcodeFormat;
using CppImageFormat = ZXingCpp.ImageFormat;
using CppImageView = ZXingCpp.ImageView;
using CppReader = ZXingCpp.BarcodeReader;
using GlyphDecoder = CodeGlyphX.QrDecoder;
using GlyphMatrix = CodeGlyphX.BitMatrix;
using NetReader = ZXing.QrCode.QRCodeReader;

namespace QRInteropFixtures;

/// <summary>
/// Interop spot check for this library's Kanji output (kanji-encoding-plan.md, phase 6.6), in three parts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every encoder cell.</b> The 6,872 encoder cells (the JIS X 0208 cells of <c>kanji-sweep.tsv</c> whose zxing-cpp and CP932 readings agree) are
/// written by this library, chunk after chunk, in Standard QR at a version of each count band (8, 10 and 12 bits), Micro QR M3 and M4, and rMQR at
/// Kanji count widths 2, 3, 6 and 7. Every symbol is read by every pinned reader of its symbology: zxing-cpp for all three, ZXing.Net and CodeGlyphX
/// for Standard QR, and this library's decoder. A reader that returns other text is reported with the cells it read otherwise; under K7 a row any
/// reader misreads loses its cells.
/// </para>
/// <para>
/// <b>ASCII <c>\</c> and <c>~</c> beside Kanji runs.</b> Under <c>Optimal</c> they go in Byte runs of a Kanji plan, with no ECI. A reader that takes
/// such a Byte run as JIS X 0201 shows ¥ and ‾. Each text is checked to be a Kanji plan (its raw bytes, as zxing-cpp reports them, are the text's
/// Shift_JIS bytes), then read by every reader.
/// </para>
/// <para>
/// <b>A Kanji segment after ECI 26</b>, evidence for phase 6.7. This library does not write one, so the symbols come from QrCodeGenerator, whose
/// segments are explicit and whose Kanji table is its own; each is read by the four Standard QR readers. Micro QR has no ECI, and rMQR's only other
/// reader is zxing-cpp, whose Kanji decoding is shared across the three symbologies.
/// </para>
/// </remarks>
public static class KanjiSpotCheck
{
    private const int PixelsPerModule = 6;

    public static int Run(string repoRoot)
    {
        _ = KanjiPayload.ShiftJis; // registers the code pages ZXing.Net needs for Shift_JIS
        var cells = EncoderCells(repoRoot);
        if (cells.Length != 6872)
        {
            Console.Error.WriteLine($"FAIL: expected 6,872 encoder cells in kanji-sweep.tsv, found {cells.Length}");
            return 1;
        }

        var failures = 0;
        failures += CellsStandardQr(cells);
        if (netPureRetries > 0)
            Console.WriteLine($"  (ZXing.Net found {netPureRetries} of the Standard QR symbols only with the pure-barcode hint)");
        failures += CellsMicroQr(cells);
        failures += CellsRmQr(cells);
        failures += BackslashAndTilde();
        EciThenKanji();

        Console.WriteLine(failures == 0 ? "spot-check-kanji: every reader agrees with the input" : $"spot-check-kanji: {failures} disagreement(s)");
        return failures == 0 ? 0 : 1;
    }

    // ---- Every encoder cell ------------------------------------------------------------

    private static string[] EncoderCells(string repoRoot)
    {
        var cells = new List<string>();
        foreach (var line in File.ReadLines(Path.Combine(repoRoot, "tools", "QRInteropFixtures", "kanji-sweep.tsv")).Skip(1))
        {
            var parts = line.Split('\t');
            if (parts.Length < 4 || parts[2].Length == 0 || parts[2] != parts[3])
                continue;
            cells.Add(char.ConvertFromUtf32(Convert.ToInt32(parts[2][2..], 16)));
        }
        return [.. cells];
    }

    /// <summary>The cells in chunks of the given sizes, taken in turn, until every cell is in one.</summary>
    private static IEnumerable<(string Text, int Slot)> Chunks(string[] cells, int[] sizes)
    {
        var start = 0;
        for (var i = 0; start < cells.Length; i++)
        {
            var slot = i % sizes.Length;
            var take = Math.Min(sizes[slot], cells.Length - start);
            yield return (string.Concat(cells.AsSpan(start, take).ToArray()), slot);
            start += take;
        }
    }

    private sealed class Tally(string symbology, string[] readers)
    {
        private readonly Dictionary<string, int> agreed = readers.ToDictionary(r => r, _ => 0);
        private readonly Dictionary<string, List<string>> misread = readers.ToDictionary(r => r, _ => new List<string>());
        public int Symbols { get; private set; }
        public int Cells { get; private set; }
        public HashSet<string> Variants { get; } = [];

        public void Symbol(string text, string variant)
        {
            Symbols++;
            Cells += text.Length;
            Variants.Add(variant);
        }

        public void Read(string reader, string expected, string? actual, string variant)
        {
            if (actual == expected)
            {
                agreed[reader]++;
                return;
            }
            if (actual is null || actual.Length != expected.Length)
            {
                misread[reader].Add($"{variant}: {(actual is null ? "no read" : $"{actual.Length} characters for {expected.Length}")}");
                return;
            }
            for (var i = 0; i < expected.Length; i++)
            {
                if (actual[i] != expected[i])
                    misread[reader].Add($"{variant}: U+{(int)expected[i]:X4} {expected[i]} (Shift_JIS {Convert.ToHexString(KanjiPayload.ToShiftJisBytes(expected[i].ToString(), null))}) read as U+{(int)actual[i]:X4}");
            }
        }

        public int Report()
        {
            var failures = 0;
            Console.WriteLine($"{symbology}: {Symbols} symbols ({string.Join(", ", Variants.Order())}), {Cells} cells");
            foreach (var (reader, count) in agreed)
            {
                Console.WriteLine($"  {reader,-11} {count}/{Symbols} symbols read back exactly");
                foreach (var line in misread[reader].Take(40))
                    Console.WriteLine($"    {line}");
                if (misread[reader].Count > 40)
                    Console.WriteLine($"    ... {misread[reader].Count - 40} more");
                failures += Symbols - count;
            }
            return failures;
        }
    }

    private static int CellsStandardQr(string[] cells)
    {
        // Version 1-L holds 10 Kanji characters, 13-L 259 and 27-L 902: one version of each count band.
        (int Version, int Count)[] shapes = [(1, 10), (13, 259), (27, 902)];
        var cpp = new CppReader { Formats = CppFormat.QRCode, TryHarder = true };
        var net = new NetReader();
        var tally = new Tally("Standard QR", ["this", "zxing-cpp", "ZXing.Net", "CodeGlyphX"]);
        foreach (var (text, slot) in Chunks(cells, shapes.Select(s => s.Count).ToArray()))
        {
            var version = shapes[slot].Version;
            var variant = $"{version}-L";
            var data = QRCodeGenerator.Create(text, QREccLevel.L, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version) });
            tally.Symbol(text, variant);

            tally.Read("this", text, QRCodeDecoder.TryDecode(data, out var ours, out _) ? ours : null, variant);
            var (luminance, width) = RenderSquare(data.Size, (r, c) => data[r, c]);
            var cppResults = cpp.From(new CppImageView(luminance, width, width, CppImageFormat.Lum));
            tally.Read("zxing-cpp", text, cppResults.Length == 1 ? cppResults[0].Text : null, variant);
            var netResult = NetRead(net, luminance, width, width);
            tally.Read("ZXing.Net", text, netResult?.Text, variant);
            tally.Read("CodeGlyphX", text, GlyphDecoder.TryDecode(ToGlyphMatrix(data), out var glyph) ? glyph.Text : null, variant);
        }
        return tally.Report();
    }

    private static int CellsMicroQr(string[] cells)
    {
        // M3-L holds 6 Kanji characters (3-bit count), M4-L 9 (4-bit).
        (MicroQRVersion Version, int Count)[] shapes = [(MicroQRVersion.M3, 6), (MicroQRVersion.M4, 9)];
        var cpp = new CppReader { Formats = CppFormat.MicroQRCode, TryHarder = true };
        var tally = new Tally("Micro QR", ["this", "zxing-cpp"]);
        foreach (var (text, slot) in Chunks(cells, shapes.Select(s => s.Count).ToArray()))
        {
            var version = shapes[slot].Version;
            var variant = $"{version}-L";
            var data = MicroQRCodeGenerator.Create(text, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = version });
            tally.Symbol(text, variant);

            tally.Read("this", text, MicroQRCodeDecoder.TryDecode(data, out var ours, out _) ? ours : null, variant);
            var (luminance, width) = RenderSquare(data.Size, (r, c) => data[r, c]);
            var cppResults = cpp.From(new CppImageView(luminance, width, width, CppImageFormat.Lum));
            tally.Read("zxing-cpp", text, cppResults.Length == 1 ? cppResults[0].Text : null, variant);
        }
        return tally.Report();
    }

    private static int CellsRmQr(string[] cells)
    {
        // Kanji count widths 2, 3, 6 and 7, each version filled at level M.
        RmQRVersion[] versions = [RmQRVersion.R7x43, RmQRVersion.R7x59, RmQRVersion.R9x139, RmQRVersion.R17x139];
        var counts = versions.Select(v => LargestKanjiCount(v, cells)).ToArray();
        var cpp = new CppReader { Formats = CppFormat.RMQRCode, TryHarder = true };
        var tally = new Tally("rMQR", ["this", "zxing-cpp"]);
        foreach (var (text, slot) in Chunks(cells, counts))
        {
            var version = versions[slot];
            var variant = $"{version}-M";
            var data = RmQRCodeGenerator.Create(text, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = version });
            tally.Symbol(text, variant);

            tally.Read("this", text, RmQRCodeDecoder.TryDecode(data, out var ours, out _) ? ours : null, variant);
            var (luminance, width, height) = RenderRect(data.Width, data.Height, (r, c) => data[r, c]);
            var cppResults = cpp.From(new CppImageView(luminance, width, height, CppImageFormat.Lum));
            tally.Read("zxing-cpp", text, cppResults.Length == 1 ? cppResults[0].Text : null, variant);
        }
        return tally.Report();
    }

    private static int LargestKanjiCount(RmQRVersion version, string[] cells)
    {
        var count = 0;
        while (RmQRCodeGenerator.TryGetRequiredBufferSize(string.Concat(cells.AsSpan(0, count + 1).ToArray()), RmQREccLevel.M, out _, new RmQRCodeGeneratorOptions { Version = version }))
            count++;
        return count;
    }

    // ---- ASCII \ and ~ beside Kanji runs --------------------------------------------

    private static readonly string[] BackslashTexts =
    [
        // Long enough that the Kanji plan is the smaller symbol in Standard QR and rMQR.
        "C:\\データ\\日本語のテキスト.txt",
        "~/設定/日本語のテキスト",
        "価格は\\100~200円です、日本語のテキスト",
        // Short enough for Micro QR, where UTF-8 carries no ECI and M4 has to be out of its reach.
        "日本語日本\\",
        "~日本語日本",
    ];

    private static int netPureRetries;

    /// <summary>ZXing.Net as a phone app would run it, and when it finds no symbol, again with the pure-barcode hint, which separates finding the symbol from reading it.</summary>
    private static ZXing.Result? NetRead(NetReader net, byte[] luminance, int width, int height)
    {
        var bitmap = new ZXing.BinaryBitmap(new HybridBinarizer(new ZXing.RGBLuminanceSource(luminance, width, height, ZXing.RGBLuminanceSource.BitmapFormat.Gray8)));
        var result = net.decode(bitmap);
        if (result is not null)
            return result;
        netPureRetries++;
        return net.decode(bitmap, new Dictionary<ZXing.DecodeHintType, object> { [ZXing.DecodeHintType.PURE_BARCODE] = true });
    }

    private static int BackslashAndTilde()
    {
        var failures = 0;
        var cppQr = new CppReader { Formats = CppFormat.QRCode, TryHarder = true };
        var cppMicro = new CppReader { Formats = CppFormat.MicroQRCode, TryHarder = true };
        var cppRmQr = new CppReader { Formats = CppFormat.RMQRCode, TryHarder = true };
        var net = new NetReader();
        Console.WriteLine("\\ and ~ in Byte runs beside Kanji runs (Optimal, no ECI):");
        foreach (var text in BackslashTexts)
        {
            var shiftJis = KanjiPayload.ToShiftJisBytes(text, null);

            var qr = QRCodeGenerator.Create(text, QREccLevel.M, new QRCodeGeneratorOptions { Segmentation = QRSegmentation.Optimal });
            var (qrLuminance, qrWidth) = RenderSquare(qr.Size, (r, c) => qr[r, c]);
            var qrCpp = cppQr.From(new CppImageView(qrLuminance, qrWidth, qrWidth, CppImageFormat.Lum));
            var qrNet = NetRead(net, qrLuminance, qrWidth, qrWidth);
            failures += Line($"Standard QR {qr.Version}", text, shiftJis, qrCpp,
                ("this", QRCodeDecoder.TryDecode(qr, out var qrOurs, out _) ? qrOurs : null),
                ("zxing-cpp", qrCpp.Length == 1 ? qrCpp[0].Text : null),
                ("ZXing.Net", qrNet?.Text),
                ("CodeGlyphX", GlyphDecoder.TryDecode(ToGlyphMatrix(qr), out var glyph) ? glyph.Text : null));

            if (MicroQRCodeGenerator.TryGetRequiredBufferSize(text, MicroQREccLevel.L, out _, new MicroQRCodeGeneratorOptions { Segmentation = MicroQRSegmentation.Optimal }))
            {
                var micro = MicroQRCodeGenerator.Create(text, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Segmentation = MicroQRSegmentation.Optimal });
                var (microLuminance, microWidth) = RenderSquare(micro.Size, (r, c) => micro[r, c]);
                var microCpp = cppMicro.From(new CppImageView(microLuminance, microWidth, microWidth, CppImageFormat.Lum));
                failures += Line($"Micro QR {micro.Version}", text, shiftJis, microCpp,
                    ("this", MicroQRCodeDecoder.TryDecode(micro, out var microOurs, out _) ? microOurs : null),
                    ("zxing-cpp", microCpp.Length == 1 ? microCpp[0].Text : null));
            }

            var rmqr = RmQRCodeGenerator.Create(text, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal });
            var (rmqrLuminance, rmqrWidth, rmqrHeight) = RenderRect(rmqr.Width, rmqr.Height, (r, c) => rmqr[r, c]);
            var rmqrCpp = cppRmQr.From(new CppImageView(rmqrLuminance, rmqrWidth, rmqrHeight, CppImageFormat.Lum));
            failures += Line($"rMQR {rmqr.Version}", text, shiftJis, rmqrCpp,
                ("this", RmQRCodeDecoder.TryDecode(rmqr, out var rmqrOurs, out _) ? rmqrOurs : null),
                ("zxing-cpp", rmqrCpp.Length == 1 ? rmqrCpp[0].Text : null));
        }
        // Each symbology has to have carried both characters in a Kanji plan every reader read back.
        foreach (var family in new[] { "Standard QR", "Micro QR", "rMQR" })
        {
            foreach (var character in new[] { '\\', '~' })
            {
                if (!kanjiPlanReads.Contains((family, character)))
                {
                    Console.WriteLine($"  FAIL: no {family} Kanji plan carried {character} and was read back by every reader");
                    failures++;
                }
            }
        }
        return failures;
    }

    private static readonly HashSet<(string Family, char Character)> kanjiPlanReads = [];

    /// <summary>
    /// One symbol's line: whether it is a Kanji plan (zxing-cpp's raw bytes are the Shift_JIS bytes), and each reader's text.
    /// A text that came out UTF-8 (the smaller symbol there) is reported and does not count either way.
    /// </summary>
    private static int Line(string symbol, string text, byte[] shiftJis, ZXingCpp.Barcode[] cpp, params (string Reader, string? Text)[] reads)
    {
        var kanjiPlan = cpp.Length == 1 && cpp[0].Bytes.AsSpan().SequenceEqual(shiftJis);
        var failures = 0;
        if (kanjiPlan && reads.All(r => r.Text == text))
        {
            var family = symbol[..symbol.LastIndexOf(' ')];
            foreach (var character in text.Where(c => c is '\\' or '~'))
                kanjiPlanReads.Add((family, character));
        }
        var parts = reads.Select(r =>
        {
            if (r.Text == text)
                return $"{r.Reader} ok";
            failures++;
            return $"{r.Reader} \"{r.Text}\"";
        });
        Console.WriteLine($"  {symbol,-16} {(kanjiPlan ? "Kanji plan" : "UTF-8 (smaller)"),-16} \"{text}\": {string.Join(", ", parts)}");
        return failures;
    }

    // ---- A Kanji segment after ECI 26 --------------------------------------------------

    private static void EciThenKanji()
    {
        var cpp = new CppReader { Formats = CppFormat.QRCode, TryHarder = true };
        var net = new NetReader();
        (string Name, string Expected, List<DataSegment> Segments)[] cases =
        [
            ("Kanji, then Byte ASCII (no ECI; what this library writes)", "日本語abc", [Kanji("日本語"), Bytes("abc"u8.ToArray())]),
            ("ECI 26, Kanji", "日本語", [Eci26(), Kanji("日本語")]),
            ("ECI 26, Byte UTF-8, Kanji", "～日本語", [Eci26(), Bytes(Encoding.UTF8.GetBytes("～")), Kanji("日本語")]),
            ("ECI 26, Kanji, Byte UTF-8", "日本語é", [Eci26(), Kanji("日本語"), Bytes(Encoding.UTF8.GetBytes("é"))]),
            ("ECI 26, Byte UTF-8, Kanji, Byte UTF-8", "①日本語①", [Eci26(), Bytes(Encoding.UTF8.GetBytes("①")), Kanji("日本語"), Bytes(Encoding.UTF8.GetBytes("①"))]),
        ];
        Console.WriteLine("A Kanji segment after ECI 26 (QrCodeGenerator's symbols, 1-M or larger):");
        foreach (var (name, expected, segments) in cases)
        {
            var code = QrCode.EncodeSegments(segments, QrCode.Ecc.Medium, 1, 40, false);
            var size = code.Size;
            var (luminance, width) = RenderSquare(size, (r, c) => code.GetModule(c, r), quietZone: 4);
            var cppResults = cpp.From(new CppImageView(luminance, width, width, CppImageFormat.Lum));
            var netResult = NetRead(net, luminance, width, width);
            var matrix = new byte[size * size];
            var glyphMatrix = new GlyphMatrix(size, size);
            for (var r = 0; r < size; r++)
            {
                for (var c = 0; c < size; c++)
                {
                    matrix[r * size + c] = code.GetModule(c, r) ? (byte)1 : (byte)0;
                    glyphMatrix.Set(c, r, code.GetModule(c, r));
                }
            }
            (string Reader, string? Text)[] reads =
            [
                ("this", QRCodeDecoder.TryDecode(matrix, size, out var ours, out _) ? ours : null),
                ("zxing-cpp", cppResults.Length == 1 ? cppResults[0].Text : null),
                ("ZXing.Net", netResult?.Text),
                ("CodeGlyphX", GlyphDecoder.TryDecode(glyphMatrix, out var glyph) ? glyph.Text : null),
            ];
            Console.WriteLine($"  {name} (version {code.Version}), expected \"{expected}\":");
            foreach (var (reader, text) in reads)
                Console.WriteLine($"    {reader,-11} {(text == expected ? "ok" : text is null ? "no read" : $"\"{text}\" ({string.Join(" ", text.Select(ch => $"U+{(int)ch:X4}"))})")}");
        }
    }

    private static DataSegment Kanji(string text) => DataSegment.MakeSegment(DataSegmentMode.Kanji, new ArraySegment<byte>(KanjiPayload.ToShiftJisBytes(text, null)));

    private static DataSegment Bytes(byte[] bytes) => DataSegment.MakeSegment(DataSegmentMode.Binary, new ArraySegment<byte>(bytes));

    private static DataSegment Eci26() => DataSegment.MakeECISegment(Net.Codecrete.QrCodeGenerator.ECI.FromValue(26));

    // ---- Rendering -------------------------------------------------------------------

    private static (byte[] Luminance, int Width) RenderSquare(int size, Func<int, int, bool> dark, int quietZone = 0)
    {
        var (luminance, width, _) = RenderRect(size, size, dark, quietZone);
        return (luminance, width);
    }

    private static (byte[] Luminance, int Width, int Height) RenderRect(int width, int height, Func<int, int, bool> dark, int quietZone = 0)
    {
        var pixelWidth = (width + 2 * quietZone) * PixelsPerModule;
        var pixelHeight = (height + 2 * quietZone) * PixelsPerModule;
        var luminance = new byte[pixelWidth * pixelHeight];
        luminance.AsSpan().Fill(255);
        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                if (!dark(row, col))
                    continue;
                for (var y = 0; y < PixelsPerModule; y++)
                    luminance.AsSpan(((row + quietZone) * PixelsPerModule + y) * pixelWidth + (col + quietZone) * PixelsPerModule, PixelsPerModule).Clear();
            }
        }
        return (luminance, pixelWidth, pixelHeight);
    }

    /// <summary>The core modules (quiet zone stripped) as a CodeGlyphX matrix.</summary>
    private static GlyphMatrix ToGlyphMatrix(QRCodeData data)
    {
        var core = 17 + 4 * data.Version;
        var offset = (data.Size - core) / 2;
        var matrix = new GlyphMatrix(core, core);
        for (var row = 0; row < core; row++)
            for (var col = 0; col < core; col++)
                matrix.Set(col, row, data[row + offset, col + offset]);
        return matrix;
    }
}
