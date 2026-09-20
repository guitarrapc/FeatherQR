using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// Crisp renders between 1 and 1.5 px/module are read through their module boundaries. The boundaries come from the timing patterns, so the path has to hold at every right angle and mirrored, and has to refuse rather than guess when a timing line does not read.
/// </summary>
public class ModuleBoundaryReaderTest
{
    private const string Content = "FQR 2.0";

    public static IEnumerable<(float, int, bool)> ScalesAndTurns()
    {
        foreach (var pixelsPerModule in new[] { 1.05f, 1.2f, 1.37f, 1.49f })
        {
            for (var quarterTurns = 0; quarterTurns < 4; quarterTurns++)
            {
                yield return (pixelsPerModule, quarterTurns, false);
                yield return (pixelsPerModule, quarterTurns, true);
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(ScalesAndTurns))]
    public async Task StandardQR_CrispTurnedOrMirrored_Decodes(float pixelsPerModule, int quarterTurns, bool mirror)
    {
        var qr = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { Version = 5 });
        var (luminance, width, height) = NearestNeighbourRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, pixelsPerModule, 0.3f, 0.7f);
        (luminance, width, height) = NearestNeighbourRenderer.Turn(luminance, width, height, quarterTurns, mirror);

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{pixelsPerModule} px/module, {quarterTurns} turns, mirror {mirror}: {info.Status}");
        await Assert.That(text).IsEqualTo(Content);
        await Assert.That(info.Corners.IsEmpty).IsFalse();
    }

    [Test]
    [MethodDataSource(nameof(ScalesAndTurns))]
    public async Task MicroQR_CrispTurnedOrMirrored_Decodes(float pixelsPerModule, int quarterTurns, bool mirror)
    {
        var qr = MicroQRCodeGenerator.Create(Content, MicroQREccLevel.L);
        var (luminance, width, height) = NearestNeighbourRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, pixelsPerModule, 0.3f, 0.7f);
        (luminance, width, height) = NearestNeighbourRenderer.Turn(luminance, width, height, quarterTurns, mirror);

        var success = MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{pixelsPerModule} px/module, {quarterTurns} turns, mirror {mirror}: {info.Status}");
        await Assert.That(text).IsEqualTo(Content);
        await Assert.That(info.Corners.IsEmpty).IsFalse();
    }

    [Test]
    [MethodDataSource(nameof(ScalesAndTurns))]
    public async Task RmQR_CrispTurnedOrMirrored_Decodes(float pixelsPerModule, int quarterTurns, bool mirror)
    {
        var qr = RmQRCodeGenerator.Create(Content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R13x77 });
        var (luminance, width, height) = NearestNeighbourRenderer.Render((row, column) => qr[row, column], qr.Width, qr.Height, pixelsPerModule, 0.3f, 0.7f);
        (luminance, width, height) = NearestNeighbourRenderer.Turn(luminance, width, height, quarterTurns, mirror);

        var success = RmQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{pixelsPerModule} px/module, {quarterTurns} turns, mirror {mirror}: {info.Status}");
        await Assert.That(text).IsEqualTo(Content);
        await Assert.That(info.Corners.IsEmpty).IsFalse();
    }

    /// <summary>
    /// One light timing module painted dark, in the row or in the column: three runs merge into one no timing pattern has, so that line does not read and the boundaries are refused, not guessed. The same render unpainted is the control.
    /// </summary>
    [Test]
    [Arguments(1.2f, true)]
    [Arguments(1.2f, false)]
    [Arguments(1.37f, true)]
    [Arguments(1.37f, false)]
    public async Task StandardQR_TimingModulePaintedOver_BoundariesAreRefused(float pixelsPerModule, bool inRow)
    {
        var qr = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { Version = 5 });
        // Module (6, 9) or (9, 6), quiet zone of 4 included, is a light timing module
        var (paintedRow, paintedColumn) = inRow ? (6 + 4, 9 + 4) : (9 + 4, 6 + 4);

        var clean = ReadStandardQRBoundaries((row, column) => qr[row, column], qr.Size, pixelsPerModule, out var dimension);
        var painted = ReadStandardQRBoundaries((row, column) => qr[row, column] || (row == paintedRow && column == paintedColumn), qr.Size, pixelsPerModule, out _);

        await Assert.That(clean).IsTrue();
        await Assert.That(dimension).IsEqualTo(qr.Size - 8);
        await Assert.That(painted).IsFalse();
    }

    /// <summary>A symbol turned a few degrees has no boundaries along the image axes to read.</summary>
    [Test]
    public async Task StandardQR_FindersOffTheImageAxes_BoundariesAreRefused()
    {
        var topLeft = new FinderPattern { X = 20, Y = 20, ModuleSize = 1.3f, Count = 3 };
        var topRight = new FinderPattern { X = 60, Y = 24, ModuleSize = 1.3f, Count = 3 };
        var bottomLeft = new FinderPattern { X = 16, Y = 60, ModuleSize = 1.3f, Count = 3 };
        var luminance = new byte[80 * 80];

        var read = FeatherQR.Internals.StandardQR.QRImageDecoder.TryReadModuleBoundaries(luminance, 80, 80, 128, topLeft, topRight, bottomLeft, new int[178], new int[178], out _, out _);

        await Assert.That(read).IsFalse();
    }

    private static bool ReadStandardQRBoundaries(Func<int, int, bool> isDark, int size, float pixelsPerModule, out int dimension)
    {
        var (luminance, width, height) = NearestNeighbourRenderer.Render(isDark, size, size, pixelsPerModule, 0.3f, 0.7f);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var patterns = new FinderPattern[3];
        if (!FinderPatternFinder.TryFind(luminance, width, height, threshold, patterns, grey))
            throw new InvalidOperationException("finders not found");
        FeatherQR.Internals.StandardQR.QRImageDecoder.OrderFinderPatterns(patterns, out var topLeft, out var topRight, out var bottomLeft);
        return FeatherQR.Internals.StandardQR.QRImageDecoder.TryReadModuleBoundaries(luminance, width, height, threshold, topLeft, topRight, bottomLeft, new int[178], new int[178], out _, out dimension);
    }

    // '#' dark, '.' light; the line starts on the first '#'
    private static bool TryRead(string pixels, float moduleSize, bool endsOnFinder, bool allowTriples, out int[] boundaries, out int count)
    {
        var luminance = new byte[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
            luminance[i] = pixels[i] == '#' ? (byte)0 : (byte)255;
        boundaries = new int[64];
        return ModuleBoundaryReader.TryReadTimingLine(luminance, pixels.Length, 1, 128, pixels.IndexOf('#'), 0, 1, 0, moduleSize, endsOnFinder, allowTriples, pixels.Length, boundaries, out count);
    }

    [Test]
    public async Task TimingLine_EndingAtTheQuietZone_IndexesEveryBoundary()
    {
        // Edge row of 8 px, then modules of 1 and 2 px: 7 + 4 = 11 modules
        var read = TryRead("..########.##.#....", 1.2f, endsOnFinder: false, allowTriples: false, out var boundaries, out var count);

        await Assert.That(read).IsTrue();
        await Assert.That(count).IsEqualTo(11);
        await Assert.That(boundaries[0]).IsEqualTo(0);
        await Assert.That(boundaries[7]).IsEqualTo(8);
        await Assert.That(boundaries[8]).IsEqualTo(9);
        await Assert.That(boundaries[9]).IsEqualTo(11);
        await Assert.That(boundaries[10]).IsEqualTo(12);
        await Assert.That(boundaries[11]).IsEqualTo(13);
        await Assert.That(boundaries[3]).IsEqualTo(ModuleBoundaryReader.Unknown);
    }

    [Test]
    public async Task TimingLine_EndingOnAFinder_CountsItsSevenModules()
    {
        var read = TryRead("..########.#.##.#.#########..", 1.2f, endsOnFinder: true, allowTriples: false, out var boundaries, out var count);

        await Assert.That(read).IsTrue();
        await Assert.That(count).IsEqualTo(7 + 7 + 7);
        await Assert.That(boundaries[14]).IsEqualTo(16);
        await Assert.That(boundaries[21]).IsEqualTo(25);
    }

    /// <summary>A dark run of three modules belongs to an rMQR timing row and to no other.</summary>
    [Test]
    public async Task TimingLine_DarkRunOfThreeModules_ReadsOnlyWhereAllowed()
    {
        const string Line = "..########.#.####.#....";

        var allowed = TryRead(Line, 1.2f, endsOnFinder: false, allowTriples: true, out var boundaries, out var count);
        var refused = TryRead(Line, 1.2f, endsOnFinder: false, allowTriples: false, out _, out _);

        await Assert.That(allowed).IsTrue();
        await Assert.That(count).IsEqualTo(7 + 1 + 1 + 1 + 3 + 1 + 1);
        await Assert.That(boundaries[11]).IsEqualTo(ModuleBoundaryReader.Unknown);
        await Assert.That(boundaries[13]).IsEqualTo(15);
        await Assert.That(refused).IsFalse();
    }

    [Test]
    [Arguments("..####.#.#....")]             // edge row too short for seven modules
    [Arguments("..########...#.#....")]       // a light run of three pixels inside the line, before a finder ends it
    [Arguments("..########.#.#.#.#.#.#")]     // runs off the image without reaching a finder
    public async Task TimingLine_ThatIsNotOne_IsRefused(string pixels)
    {
        var read = TryRead(pixels, 1.2f, endsOnFinder: true, allowTriples: false, out _, out _);

        await Assert.That(read).IsFalse();
    }

    [Test]
    public async Task FillBoundaries_Unknown_AreFittedBetweenTheirNeighbours()
    {
        int[] boundaries = [0, 1, 3, ModuleBoundaryReader.Unknown, ModuleBoundaryReader.Unknown, 6, 8, 9];

        var filled = ModuleBoundaryReader.TryFillBoundaries(boundaries, 7);

        await Assert.That(filled).IsTrue();
        await Assert.That(boundaries[3]).IsEqualTo(4);
        await Assert.That(boundaries[4]).IsEqualTo(5);
    }

    [Test]
    public async Task FillBoundaries_NotRising_IsRefused()
    {
        int[] boundaries = [0, 2, 2, 4];

        await Assert.That(ModuleBoundaryReader.TryFillBoundaries(boundaries, 3)).IsFalse();
    }

    /// <summary>
    /// Three modules over four pixels: which of the three is two pixels wide shows only where a neighbouring line changes colour. With two edges seen the boundaries are those; with one the question stays open.
    /// </summary>
    [Test]
    [Arguments("#.##", 1, 2)]
    [Arguments("##.#", 2, 3)]
    [Arguments("#..#", 1, 3)]
    public async Task CentreBoundaries_TwoEdgesInLine_AreTheBoundaries(string crossLine, int first, int second)
    {
        var (luminance, width) = TwoRows("####", crossLine);
        int[] boundaries = [0, ModuleBoundaryReader.Unknown, ModuleBoundaryReader.Unknown, 4];
        int[] rows = [0, 1, 2];

        ModuleBoundaryReader.ReadCentreBoundaries(luminance, width, 2, 128, 0, 0, 1, 0, 0, 1, boundaries, 0, rows, 0, 2);

        await Assert.That(boundaries[1]).IsEqualTo(first);
        await Assert.That(boundaries[2]).IsEqualTo(second);
    }

    [Test]
    [Arguments("##..")]
    [Arguments("####")]
    [Arguments("#.#.")]
    public async Task CentreBoundaries_NotTwoEdges_StayUnknown(string crossLine)
    {
        var (luminance, width) = TwoRows("####", crossLine);
        int[] boundaries = [0, ModuleBoundaryReader.Unknown, ModuleBoundaryReader.Unknown, 4];
        int[] rows = [0, 1, 2];

        ModuleBoundaryReader.ReadCentreBoundaries(luminance, width, 2, 128, 0, 0, 1, 0, 0, 1, boundaries, 0, rows, 0, 2);

        await Assert.That(boundaries[1]).IsEqualTo(ModuleBoundaryReader.Unknown);
        await Assert.That(boundaries[2]).IsEqualTo(ModuleBoundaryReader.Unknown);
    }

    private static (byte[] Luminance, int Width) TwoRows(string first, string second)
    {
        var luminance = new byte[first.Length * 2];
        for (var i = 0; i < first.Length; i++)
        {
            luminance[i] = first[i] == '#' ? (byte)0 : (byte)255;
            luminance[first.Length + i] = second[i] == '#' ? (byte)0 : (byte)255;
        }
        return (luminance, first.Length);
    }
}
