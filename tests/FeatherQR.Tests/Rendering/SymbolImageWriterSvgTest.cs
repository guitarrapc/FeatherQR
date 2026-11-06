using System.Buffers;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using FeatherQR.Internals;
using FeatherQR.Internals.ImageWriters;

namespace FeatherQR.Tests;

/// <summary>
/// SVG from the core: the root's frame, the strokes that draw the dark modules, the colours, and the bytes route.
/// </summary>
/// <remarks>
/// The writer draws each horizontal run of dark modules as a stroke one module wide along the run's centre line.
/// The path is read back here by a parser of its own and drawn onto a module grid, which has to equal the matrix.
/// </remarks>
public class SymbolImageWriterSvgTest
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    public static IEnumerable<(int Version, QREccLevel Level)> StandardQrVersionsAndLevels()
    {
        for (var version = 1; version <= 40; version++)
        {
            foreach (var level in new[] { QREccLevel.L, QREccLevel.M, QREccLevel.Q, QREccLevel.H })
                yield return (version, level);
        }
    }

    public static IEnumerable<(MicroQRVersion Version, MicroQREccLevel Level)> MicroQrVersionsAndLevels()
    {
        yield return (MicroQRVersion.M1, MicroQREccLevel.ErrorDetectionOnly);
        yield return (MicroQRVersion.M2, MicroQREccLevel.L);
        yield return (MicroQRVersion.M2, MicroQREccLevel.M);
        yield return (MicroQRVersion.M3, MicroQREccLevel.L);
        yield return (MicroQRVersion.M3, MicroQREccLevel.M);
        yield return (MicroQRVersion.M4, MicroQREccLevel.L);
        yield return (MicroQRVersion.M4, MicroQREccLevel.M);
        yield return (MicroQRVersion.M4, MicroQREccLevel.Q);
    }

    public static IEnumerable<(RmQRVersion Version, RmQREccLevel Level)> RmQrVersionsAndLevels()
    {
        foreach (var version in Enum.GetValues<RmQRVersion>())
        {
            foreach (var level in new[] { RmQREccLevel.M, RmQREccLevel.H })
                yield return (version, level);
        }
    }

    // ---- the path draws the matrix ------------------------------------------------------

    [Test]
    [MethodDataSource(nameof(StandardQrVersionsAndLevels))]
    public async Task StandardQr_PathReadsBackAsTheMatrix_EveryVersionAndLevel(int version, QREccLevel level)
    {
        var data = QRCodeGenerator.Create("1", level, new QRCodeGeneratorOptions { Version = version });
        await AssertDrawsTheMatrix(SymbolImageWriter.GetSvgString(data), data.Size, data.Size, (r, c) => data[r, c]);
    }

    [Test]
    [MethodDataSource(nameof(MicroQrVersionsAndLevels))]
    public async Task MicroQr_PathReadsBackAsTheMatrix_EveryVersionAndLevel(MicroQRVersion version, MicroQREccLevel level)
    {
        var data = MicroQRCodeGenerator.Create("1", level, new MicroQRCodeGeneratorOptions { Version = version });
        await AssertDrawsTheMatrix(SymbolImageWriter.GetSvgString(data), data.Size, data.Size, (r, c) => data[r, c]);
    }

    [Test]
    [MethodDataSource(nameof(RmQrVersionsAndLevels))]
    public async Task RmQr_PathReadsBackAsTheMatrix_EveryVersionAndLevel(RmQRVersion version, RmQREccLevel level)
    {
        var data = RmQRCodeGenerator.Create("1", level, new RmQRCodeGeneratorOptions { Version = version });
        await AssertDrawsTheMatrix(SymbolImageWriter.GetSvgString(data), data.Width, data.Height, (r, c) => data[r, c]);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(25)]
    public async Task QuietZone_ShiftsThePath_AndStaysLight(int quietZoneSize)
    {
        var qr = QRCodeGenerator.Create("quiet zone", QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = quietZoneSize });
        await AssertDrawsTheMatrix(SymbolImageWriter.GetSvgString(qr), qr.Size, qr.Size, (r, c) => qr[r, c]);

        var micro = MicroQRCodeGenerator.Create("12345", MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { QuietZoneSize = quietZoneSize });
        await AssertDrawsTheMatrix(SymbolImageWriter.GetSvgString(micro), micro.Size, micro.Size, (r, c) => micro[r, c]);

        var rm = RmQRCodeGenerator.Create("quiet zone", RmQREccLevel.M, new RmQRCodeGeneratorOptions { QuietZoneSize = quietZoneSize });
        await AssertDrawsTheMatrix(SymbolImageWriter.GetSvgString(rm), rm.Width, rm.Height, (r, c) => rm[r, c]);
    }

    // ---- the frame ----------------------------------------------------------------------

    /// <summary>
    /// The exact text of the frame and of the first two rows, worked by hand from a Micro QR M1 with its default quiet zone of 2.
    /// Row 0 holds the finder's top edge (7 modules), the separator, and the timing pattern's dark modules at core columns 8 and 10.
    /// Row 1 holds the finder's two sides at core columns 0 and 6.
    /// </summary>
    [Test]
    public async Task Frame_AndFirstRows_AreTheDocumentedText()
    {
        var micro = MicroQRCodeGenerator.Create("1", MicroQREccLevel.ErrorDetectionOnly, new MicroQRCodeGeneratorOptions { Version = MicroQRVersion.M1 });
        var svg = SymbolImageWriter.GetSvgString(micro);

        await Assert.That(svg).StartsWith(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 15 15\" width=\"120\" height=\"120\" shape-rendering=\"crispEdges\">"
            + "<rect width=\"100%\" height=\"100%\" fill=\"#fff\"/>"
            + "<path stroke=\"#000\" d=\"M2 2.5h7m1 0h1m1 0h1m-11 1h1m5 0h1");
        await Assert.That(svg).EndsWith("\"/></svg>");
    }

    [Test]
    public async Task Root_HasTheViewBoxInModules_AndThePixelSize()
    {
        var rm = RmQRCodeGenerator.Create("HELLO", RmQREccLevel.M);
        var root = XDocument.Parse(SymbolImageWriter.GetSvgString(rm, new SymbolImageOptions { ModulePixelSize = 3 })).Root!;

        await Assert.That(root.Name).IsEqualTo(Svg + "svg");
        await Assert.That(root.Attribute("viewBox")?.Value).IsEqualTo($"0 0 {rm.Width} {rm.Height}");
        await Assert.That(root.Attribute("width")?.Value).IsEqualTo((rm.Width * 3).ToString(CultureInfo.InvariantCulture));
        await Assert.That(root.Attribute("height")?.Value).IsEqualTo((rm.Height * 3).ToString(CultureInfo.InvariantCulture));
        await Assert.That(root.Attribute("shape-rendering")?.Value).IsEqualTo("crispEdges");
    }

    [Test]
    public async Task DefaultOptions_AreEightPixelsAModule_BlackOnWhite()
    {
        var qr = QRCodeGenerator.Create("1", QREccLevel.M, new QRCodeGeneratorOptions { Version = 1 });
        var root = XDocument.Parse(SymbolImageWriter.GetSvgString(qr)).Root!;

        // Version 1 with the quiet zone of 4 is 29 modules: 232 pixels at 8 a module.
        await Assert.That(root.Attribute("width")?.Value).IsEqualTo("232");
        await Assert.That(root.Elements().Select(e => e.Name.LocalName)).IsEquivalentTo(new[] { "rect", "path" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        var rect = root.Element(Svg + "rect")!;
        await Assert.That(rect.Attribute("fill")?.Value).IsEqualTo("#fff");
        await Assert.That(rect.Attribute("fill-opacity")).IsNull();
        var path = root.Element(Svg + "path")!;
        await Assert.That(path.Attribute("stroke")?.Value).IsEqualTo("#000");
        await Assert.That(path.Attribute("stroke-opacity")).IsNull();
        await Assert.That(SymbolImageWriter.GetSvgString(qr, SymbolImageOptions.Default)).IsEqualTo(SymbolImageWriter.GetSvgString(qr));
    }

    // ---- colours ------------------------------------------------------------------------

    [Test]
    [Arguments((byte)0x1E, (byte)0x3A, (byte)0x8A, "#1e3a8a")]
    [Arguments((byte)0x12, (byte)0x22, (byte)0x33, "#122233")]
    [Arguments((byte)0x11, (byte)0x22, (byte)0x33, "#123")]
    [Arguments((byte)0xFF, (byte)0xEE, (byte)0xDD, "#fed")]
    [Arguments((byte)0x00, (byte)0x00, (byte)0x00, "#000")]
    public async Task Color_IsHex_ShortWhereEveryChannelRepeatsItsDigit(byte r, byte g, byte b, string expected)
    {
        var micro = MicroQRCodeGenerator.Create("1", MicroQREccLevel.ErrorDetectionOnly);
        var color = new SymbolColor(r, g, b);
        var root = XDocument.Parse(SymbolImageWriter.GetSvgString(micro, new SymbolImageOptions { DarkColor = color, LightColor = color })).Root!;

        await Assert.That(root.Element(Svg + "path")!.Attribute("stroke")?.Value).IsEqualTo(expected);
        await Assert.That(root.Element(Svg + "rect")!.Attribute("fill")?.Value).IsEqualTo(expected);
    }

    [Test]
    public async Task TransparentLightColor_LeavesTheBackgroundOut()
    {
        var micro = MicroQRCodeGenerator.Create("1", MicroQREccLevel.ErrorDetectionOnly);
        var root = XDocument.Parse(SymbolImageWriter.GetSvgString(micro, new SymbolImageOptions { LightColor = new SymbolColor(255, 255, 255, 0) })).Root!;

        await Assert.That(root.Elements().Select(e => e.Name.LocalName)).IsEquivalentTo(new[] { "path" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task TransparentDarkColor_LeavesThePathOut()
    {
        var micro = MicroQRCodeGenerator.Create("1", MicroQREccLevel.ErrorDetectionOnly);
        var root = XDocument.Parse(SymbolImageWriter.GetSvgString(micro, new SymbolImageOptions { DarkColor = new SymbolColor(0, 0, 0, 0) })).Root!;

        await Assert.That(root.Elements().Select(e => e.Name.LocalName)).IsEquivalentTo(new[] { "rect" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task PartlyTransparentColors_CarryAnOpacity()
    {
        var micro = MicroQRCodeGenerator.Create("1", MicroQREccLevel.ErrorDetectionOnly);
        var options = new SymbolImageOptions { DarkColor = new SymbolColor(0, 0, 0, 128), LightColor = new SymbolColor(255, 255, 255, 51) };
        var root = XDocument.Parse(SymbolImageWriter.GetSvgString(micro, options)).Root!;

        await Assert.That(root.Element(Svg + "path")!.Attribute("stroke-opacity")?.Value).IsEqualTo("0.502");
        await Assert.That(root.Element(Svg + "rect")!.Attribute("fill-opacity")?.Value).IsEqualTo("0.2");
    }

    /// <summary>
    /// Three decimals separate all 256 alphas, since neighbours are 1/255 apart, and a viewer that scales the opacity back to a byte gets the alpha it was given.
    /// </summary>
    [Test]
    public async Task Opacity_ReadsBackAsItsAlpha_ForEveryAlphaBetweenTransparentAndOpaque()
    {
        var micro = MicroQRCodeGenerator.Create("1", MicroQREccLevel.ErrorDetectionOnly);
        var wrong = new List<string>();
        for (var alpha = 1; alpha < 255; alpha++)
        {
            var options = new SymbolImageOptions { DarkColor = new SymbolColor(0, 0, 0, (byte)alpha) };
            var opacity = XDocument.Parse(SymbolImageWriter.GetSvgString(micro, options)).Root!.Element(Svg + "path")!.Attribute("stroke-opacity")!.Value;
            var back = (int)Math.Round(double.Parse(opacity, CultureInfo.InvariantCulture) * 255, MidpointRounding.AwayFromZero);
            if (back != alpha)
                wrong.Add($"{alpha} wrote {opacity}");
        }

        await Assert.That(wrong).IsEmpty();
    }

    // ---- the bytes route ----------------------------------------------------------------

    [Test]
    public async Task WriteSvg_AppendsTheStringAsUtf8_AndReturnsItsLength()
    {
        var options = new SymbolImageOptions { ModulePixelSize = 5, DarkColor = new SymbolColor(0x1E, 0x3A, 0x8A, 200) };
        await AssertWritesTheString(QRCodeGenerator.Create("bytes route", QREccLevel.Q), options);
        await AssertWritesTheString(MicroQRCodeGenerator.Create("12345", MicroQREccLevel.L), options);
        await AssertWritesTheString(RmQRCodeGenerator.Create("bytes route", RmQREccLevel.M), options);
    }

    private static async Task AssertWritesTheString(object data, SymbolImageOptions options)
    {
        var writer = new ArrayBufferWriter<byte>();
        writer.Write("prefix"u8);
        var (count, text) = data switch
        {
            QRCodeData qr => (SymbolImageWriter.WriteSvg(qr, writer, options), SymbolImageWriter.GetSvgString(qr, options)),
            MicroQRCodeData micro => (SymbolImageWriter.WriteSvg(micro, writer, options), SymbolImageWriter.GetSvgString(micro, options)),
            RmQRCodeData rm => (SymbolImageWriter.WriteSvg(rm, writer, options), SymbolImageWriter.GetSvgString(rm, options)),
            _ => throw new ArgumentException("not a symbol", nameof(data)),
        };
        var expected = Encoding.UTF8.GetBytes(text);

        await Assert.That(count).IsEqualTo(expected.Length);
        await Assert.That(writer.WrittenSpan.ToArray()).IsEquivalentTo("prefix"u8.ToArray().Concat(expected).ToArray(), TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    // ---- arguments ----------------------------------------------------------------------

    [Test]
    public async Task NullArguments_Throw()
    {
        var writer = new ArrayBufferWriter<byte>();
        await Assert.That(() => SymbolImageWriter.GetSvgString((QRCodeData)null!)).Throws<ArgumentNullException>();
        await Assert.That(() => SymbolImageWriter.GetSvgString((MicroQRCodeData)null!)).Throws<ArgumentNullException>();
        await Assert.That(() => SymbolImageWriter.GetSvgString((RmQRCodeData)null!)).Throws<ArgumentNullException>();
        await Assert.That(() => SymbolImageWriter.WriteSvg((QRCodeData)null!, writer)).Throws<ArgumentNullException>();
        await Assert.That(() => SymbolImageWriter.WriteSvg((MicroQRCodeData)null!, writer)).Throws<ArgumentNullException>();
        await Assert.That(() => SymbolImageWriter.WriteSvg((RmQRCodeData)null!, writer)).Throws<ArgumentNullException>();
        await Assert.That(() => SymbolImageWriter.WriteSvg(QRCodeGenerator.Create("1", QREccLevel.L), null!)).Throws<ArgumentNullException>();
        await Assert.That(() => SymbolImageWriter.WriteSvg(MicroQRCodeGenerator.Create("1", MicroQREccLevel.ErrorDetectionOnly), null!)).Throws<ArgumentNullException>();
        await Assert.That(() => SymbolImageWriter.WriteSvg(RmQRCodeGenerator.Create("1", RmQREccLevel.M), null!)).Throws<ArgumentNullException>();
    }

    /// <summary>
    /// The width and height in pixels have to fit an <c>int</c>. The module size has no other bound, so any symbol reaches the limit.
    /// </summary>
    [Test]
    public async Task PixelSide_PastAnInt_Throws_AndUpToItIsWritten()
    {
        var qr = QRCodeGenerator.Create("1", QREccLevel.L, new QRCodeGeneratorOptions { Version = 40 });
        var largest = int.MaxValue / qr.Size;

        var root = XDocument.Parse(SymbolImageWriter.GetSvgString(qr, new SymbolImageOptions { ModulePixelSize = largest })).Root!;
        await Assert.That(root.Attribute("width")?.Value).IsEqualTo(((long)qr.Size * largest).ToString(CultureInfo.InvariantCulture));

        var refused = Assert.Throws<ArgumentOutOfRangeException>(() => SymbolImageWriter.GetSvgString(qr, new SymbolImageOptions { ModulePixelSize = largest + 1 }));
        await Assert.That(refused.ParamName).IsEqualTo("options");
        var written = Assert.Throws<ArgumentOutOfRangeException>(() => SymbolImageWriter.WriteSvg(qr, new ArrayBufferWriter<byte>(), new SymbolImageOptions { ModulePixelSize = largest + 1 }));
        await Assert.That(written.ParamName).IsEqualTo("options");
    }

    // ---- the runs, a word of the packed rows at a time ----------------------------------

    private static readonly (string Kind, Func<int, int, Random, bool> Dark)[] RowPatterns =
    [
        ("random", (r, c, rng) => rng.Next(2) == 0),
        ("sparse", (r, c, rng) => rng.Next(7) == 0),
        ("every module dark", (r, c, rng) => true),
        ("no module dark", (r, c, rng) => false),
        ("checkerboard", (r, c, rng) => ((r + c) & 1) == 0),
        ("runs across a word", (r, c, rng) => c % 70 is >= 55 and < 75),
        ("rows that end dark", (r, c, rng) => c >= r),
    ];

    /// <summary>
    /// The writer reads the runs a 64-bit word of the packed rows at a time, and a word starts at any bit of a byte,
    /// so the rows here are cut around a byte and a word, with runs that cross a word and rows that end dark.
    /// Drawing the path proves it covers each dark module once. A stroke count equal to the scanner's run count then proves
    /// each stroke is a whole run, as <c>GetModuleRectangles</c> reports them.
    /// </summary>
    [Test]
    public async Task Strokes_AreTheScannersRuns_OnRowsCutAroundAWord()
    {
        foreach (var width in new[] { 1, 7, 8, 9, 57, 63, 64, 65, 120, 127, 128, 129, 191, 192, 193, 300 })
        {
            foreach (var (kind, dark) in RowPatterns)
            {
                var grid = Build(width, 6, dark, seed: width);
                foreach (var quietZone in new[] { 0, 3 })
                {
                    var view = new PaddedView(grid, quietZone);
                    var root = XDocument.Parse(SvgWriter.GetString(view, SymbolImageOptions.Default)).Root!;
                    var runs = ModuleRunScanner.Count(view);
                    var path = root.Elements(Svg + "path").SingleOrDefault();
                    if (runs == 0)
                    {
                        await Assert.That(path).IsNull().Because($"{kind} {width}");
                        continue;
                    }
                    await AssertDrawsTheMatrix(root.ToString(SaveOptions.DisableFormatting), view.Width, view.Height,
                        (r, c) => (uint)(r - quietZone) < (uint)grid.Height && (uint)(c - quietZone) < (uint)grid.Width && grid[r - quietZone, c - quietZone]);
                    await Assert.That(path!.Attribute("d")!.Value.Count(ch => ch == 'h')).IsEqualTo(runs).Because($"{kind} {width} with quiet zone {quietZone}");
                }
            }
        }
    }

    /// <summary>
    /// The largest quiet zone gives the first move five-digit coordinates.
    /// Every later move is relative, so the rest of the path is the one written with no quiet zone.
    /// </summary>
    [Test]
    public async Task LargestQuietZone_MovesOnlyTheFirstMove()
    {
        var grid = Build(177, 9, RowPatterns[0].Dark, seed: 177);
        var near = Path(new PaddedView(grid, 0));
        var far = Path(new PaddedView(grid, 10_000));

        await Assert.That(far[..far.IndexOf('h')]).IsEqualTo(FirstMove(near, 10_000));
        await Assert.That(far[far.IndexOf('h')..]).IsEqualTo(near[near.IndexOf('h')..]);

        static string Path(PaddedView view) => XDocument.Parse(SvgWriter.GetString(view, SymbolImageOptions.Default)).Root!.Elements(Svg + "path").Single().Attribute("d")!.Value;

        static string FirstMove(string d, int shift)
        {
            var numbers = d[1..d.IndexOf('h')].Split(' ');
            var x = int.Parse(numbers[0], CultureInfo.InvariantCulture) + shift;
            var y = double.Parse(numbers[1], CultureInfo.InvariantCulture) + shift;
            return string.Create(CultureInfo.InvariantCulture, $"M{x} {y:0.0}");
        }
    }

    private static SymbolImageWriterPngTest.Grid Build(int width, int height, Func<int, int, Random, bool> dark, int seed)
    {
        var rng = new Random(seed);
        var cells = new bool[width * height];
        for (var r = 0; r < height; r++)
        {
            for (var c = 0; c < width; c++)
                cells[r * width + c] = dark(r, c, rng);
        }
        return new SymbolImageWriterPngTest.Grid(width, height, cells);
    }

    /// <summary>A grid as a symbol's core with a quiet zone, for sizes and patterns no symbol has.</summary>
    private readonly struct PaddedView(SymbolImageWriterPngTest.Grid grid, int quietZone) : IModuleMatrixView
    {
        private readonly byte[] _bits = grid.PackedBits();
        public int Width => grid.Width + 2 * quietZone;
        public int Height => grid.Height + 2 * quietZone;
        public int CoreWidth => grid.Width;
        public int CoreHeight => grid.Height;
        public ReadOnlySpan<byte> CoreBits => _bits;
        public bool GetCoreModule(int coreRow, int coreCol) => grid[coreRow, coreCol];
        public bool IsFinderPattern(int coreRow, int coreCol) => false;
    }

    // ---- the reader ---------------------------------------------------------------------

    private static async Task AssertDrawsTheMatrix(string svg, int width, int height, Func<int, int, bool> dark)
    {
        var root = XDocument.Parse(svg).Root!;
        await Assert.That(root.Attribute("viewBox")?.Value).IsEqualTo($"0 0 {width} {height}");

        var path = root.Elements(Svg + "path").Single();
        await Assert.That(path.Attribute("stroke")).IsNotNull();
        await Assert.That(path.Attribute("stroke-width")).IsNull().Because("the stroke is the default 1 module wide");
        await Assert.That(path.Attribute("fill")).IsNull().Because("the default fill paints nothing on a line");

        var drawn = DrawStrokes(path.Attribute("d")!.Value, width, height);
        var wrong = new List<string>();
        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                if ((drawn[row * width + col] == 1) != dark(row, col) && wrong.Count < 10)
                    wrong.Add($"({row}, {col})");
            }
        }

        await Assert.That(wrong).IsEmpty();
    }

    /// <summary>
    /// Reads M, m and h with whole or half numbers, and marks the modules each horizontal stroke covers.
    /// A stroke must lie on a row's centre line, run rightwards, start on a module edge and cover no module twice.
    /// </summary>
    private static byte[] DrawStrokes(string d, int width, int height)
    {
        var grid = new byte[width * height];
        double x = 0, y = 0;
        var i = 0;
        while (i < d.Length)
        {
            var command = d[i++];
            switch (command)
            {
                case 'M':
                    x = Number(d, ref i);
                    y = Number(d, ref i);
                    break;
                case 'm':
                    x += Number(d, ref i);
                    y += Number(d, ref i);
                    break;
                case 'h':
                    var length = Number(d, ref i);
                    var row = (int)Math.Floor(y);
                    if (y - row != 0.5 || length <= 0 || x != Math.Floor(x) || length != Math.Floor(length))
                        throw new FormatException($"a stroke at ({x}, {y}) of {length} is not a run on a row's centre line");
                    for (var col = (int)x; col < (int)(x + length); col++)
                    {
                        if (grid[row * width + col] != 0)
                            throw new FormatException($"module ({row}, {col}) is drawn twice");
                        grid[row * width + col] = 1;
                    }
                    x += length;
                    break;
                default:
                    throw new FormatException($"command '{command}' at {i - 1} is not one the writer uses");
            }
        }
        return grid;
    }

    private static double Number(string s, ref int i)
    {
        if (i < s.Length && s[i] == ' ')
            i++;
        var start = i;
        if (i < s.Length && s[i] == '-')
            i++;
        while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] == '.'))
            i++;
        return double.Parse(s.AsSpan(start, i - start), CultureInfo.InvariantCulture);
    }
}
