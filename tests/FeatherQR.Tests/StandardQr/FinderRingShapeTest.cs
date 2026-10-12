using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// Finders whose dark ring is not the whole ring the cross-checks expect: a speck of light on the ring where the centre row crosses it, as a binarization leaves on a washed-out print, and a ring drawn as four bars with its corners left out, as a styled symbol draws it.
/// The first is read through a row beside the centre row, the second through the rows and columns beside the centre in place of the diagonals, which cross no ring.
/// </summary>
public class FinderRingShapeTest
{
    private const int QuietZone = 4;

    /// <summary>
    /// A speck one pixel wide on the right side of the top-left finder's ring, a pixel from its inner edge, across the centre row: every scanned row's column cross-check ends on the centre row, which no longer reads, and a row a quarter of the centre square above or below it does.
    /// The speck is three pixels tall, or reaches past the row above (<paramref name="reach"/> −1) or the row below (1), so that only the other one reads.
    /// </summary>
    [Test]
    [Arguments(2, 6, 0)]
    [Arguments(4, 5, 0)]
    [Arguments(7, 4, 0)]
    [Arguments(2, 6, -1)]
    [Arguments(2, 6, 1)]
    [Arguments(7, 4, -1)]
    [Arguments(7, 4, 1)]
    public async Task FinderWithASpeckOnItsCentreRow_IsACandidate(int version, int pixelsPerModule, int reach)
    {
        var qr = Symbol(version);
        var (luminance, side) = Draw((row, column) => qr[row, column], qr.Size, pixelsPerModule);
        var centre = Centre(3.5f, pixelsPerModule);
        Speck(luminance, side, pixelsPerModule, reach);

        // The premise: the centre row through the finder is not a finder's any more
        var runs = new int[5];
        await Assert.That(FinderPatternFinder.MeasureRuns(luminance, side, side, 128, (int)centre, (int)centre, 1, 0, FinderPatternFinder.NoRunCap, runs, out _)).IsTrue();
        await Assert.That(FinderPatternFinder.IsFinderRatio(runs)).IsFalse();

        await Assert.That(NearestCandidate(luminance, side, centre)).IsLessThan(pixelsPerModule);
    }

    /// <summary>A hole through the whole centre square's height is no speck: the ring is open, and the finder is not a candidate.</summary>
    [Test]
    public async Task FinderWithItsRingOpenBesideTheCentreSquare_IsNoCandidate()
    {
        const int PixelsPerModule = 6;
        var qr = Symbol(2);
        // The ring's right side light over the three rows of the centre square
        var (luminance, side) = Draw((row, column) => !(column == 6 && row is >= 2 and <= 4) && qr[row, column], qr.Size, PixelsPerModule);

        await Assert.That(NearestCandidate(luminance, side, Centre(3.5f, PixelsPerModule))).IsGreaterThan(2f * PixelsPerModule);
    }

    /// <summary>
    /// A finder read through a row beside its centre row is held to both diagonals, and is never a ring without corners: with the two corners on the rising diagonal light, or all four, the finder is a candidate, and with a speck on its centre row as well it is none.
    /// </summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FinderWithASpeck_IsHeldToBothDiagonals(bool allCorners)
    {
        const int PixelsPerModule = 6;
        var qr = Symbol(2);
        bool IsLightCorner(int row, int column) => allCorners ? IsRingCorner(row, column, qr.Size) : (row, column) is (0, 6) or (6, 0);
        var (luminance, side) = Draw((row, column) => !IsLightCorner(row, column) && qr[row, column], qr.Size, PixelsPerModule);
        var centre = Centre(3.5f, PixelsPerModule);

        // The premise: the rising diagonal does not read, and the finder is a candidate
        var runs = new int[5];
        await Assert.That(FinderPatternFinder.MeasureRuns(luminance, side, side, 128, (int)centre, (int)centre, 1, -1, FinderPatternFinder.NoRunCap, runs, out _) && FinderPatternFinder.IsFinderRatio(runs)).IsFalse();
        await Assert.That(NearestCandidate(luminance, side, centre)).IsLessThan(PixelsPerModule);

        Speck(luminance, side, PixelsPerModule, reach: 0);

        await Assert.That(NearestCandidate(luminance, side, centre)).IsGreaterThan(2f * PixelsPerModule);
    }

    /// <summary>
    /// Under 4 px a module the row a quarter of the centre square away is the centre row's neighbour, and the finder with a speck on its centre row is not a candidate, though the rows above and below the speck read.
    /// </summary>
    [Test]
    public async Task FinderWithASpeckUnderFourPixelsAModule_IsNoCandidate()
    {
        const int PixelsPerModule = 3;
        var qr = Symbol(3);
        var (luminance, side) = Draw((row, column) => qr[row, column], qr.Size, PixelsPerModule);
        var centre = Centre(3.5f, PixelsPerModule);
        Speck(luminance, side, PixelsPerModule, reach: 0);

        // The premise: the rows two pixels above and below the centre row, a quarter of the nine the centre square is tall, read
        var runs = new int[5];
        foreach (var y in new[] { (int)centre - 2, (int)centre + 2 })
            await Assert.That(FinderPatternFinder.MeasureRuns(luminance, side, side, 128, (int)centre, y, 1, 0, FinderPatternFinder.NoRunCap, runs, out _) && FinderPatternFinder.IsFinderRatio(runs)).IsTrue();

        await Assert.That(NearestCandidate(luminance, side, centre)).IsGreaterThan(2f * PixelsPerModule);
    }

    /// <summary>
    /// The three finders' rings drawn as four bars each, their corner modules light: no diagonal through a centre crosses a ring, and the symbol reads.
    /// </summary>
    [Test]
    [Arguments(2, 6)]
    [Arguments(3, 8)]
    [Arguments(6, 4)]
    [Arguments(10, 3)]
    public async Task RingsWithoutCorners_Read(int version, int pixelsPerModule)
    {
        var qr = Symbol(version);
        var (luminance, side) = Draw((row, column) => !IsRingCorner(row, column, qr.Size) && qr[row, column], qr.Size, pixelsPerModule);

        // The premise: neither diagonal through the top-left finder's centre reads
        var centre = (int)Centre(3.5f, pixelsPerModule);
        var runs = new int[5];
        foreach (var stepY in new[] { 1, -1 })
            await Assert.That(FinderPatternFinder.MeasureRuns(luminance, side, side, 128, centre, centre, 1, stepY, FinderPatternFinder.NoRunCap, runs, out _) && FinderPatternFinder.IsFinderRatio(runs)).IsFalse();

        var success = QRCodeDecoder.TryDecodeImage(luminance, side, side, out var text, out var info);

        await Assert.That(success).IsTrue().Because(info.Status.ToString());
        await Assert.That(text).IsEqualTo(Text(version));
    }

    /// <summary>
    /// The same rings anti-aliased at 2.5 px a module, where a third of the centre is two pixels: the smallest size the rows and columns beside the centre are read at.
    /// </summary>
    [Test]
    [Arguments(2)]
    [Arguments(5)]
    [Arguments(9)]
    public async Task RingsWithoutCorners_AntiAliasedAtTwoAndAHalfPixelsAModule_Read(int version)
    {
        var qr = QRCodeGenerator.Create(Text(version), QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version), QuietZoneSize = QuietZone });
        var inner = qr.Size - 2 * QuietZone;
        var (luminance, width, height) = SupersampledRenderer.Render((row, column) => !IsRingCorner(row - QuietZone, column - QuietZone, inner) && qr[row, column], qr.Size, qr.Size, 2.5f, 0f);

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because(info.Status.ToString());
        await Assert.That(text).IsEqualTo(Text(version));
    }

    /// <summary>
    /// Under 2 px a module the rows and columns beside the centre are the centre line's own neighbours, and a ring without corners is not a candidate.
    /// </summary>
    [Test]
    public async Task RingWithoutCornersAtOnePixelAModule_IsNoCandidate()
    {
        var qr = Symbol(2);
        var centre = Centre(3.5f, 1);

        // The premise: with its corners the finder is a candidate
        var (whole, side) = Draw((row, column) => qr[row, column], qr.Size, 1);
        await Assert.That(NearestCandidate(whole, side, centre)).IsLessThan(1.5f);

        var (luminance, _) = Draw((row, column) => !IsRingCorner(row, column, qr.Size) && qr[row, column], qr.Size, 1);

        await Assert.That(NearestCandidate(luminance, side, centre)).IsGreaterThan(3f);
    }

    /// <summary>
    /// A bar thinned to a pixel where a row beside the centre crosses it: that row keeps its five runs and its length and loses the ratio, and the finder is not a candidate.
    /// </summary>
    [Test]
    public async Task RingWithoutCornersAndABarThinnedBesideTheCentre_IsNoCandidate()
    {
        const int PixelsPerModule = 6;
        var qr = Symbol(2);
        var (luminance, side) = Draw((row, column) => !IsRingCorner(row, column, qr.Size) && qr[row, column], qr.Size, PixelsPerModule);
        var centre = (int)Centre(3.5f, PixelsPerModule);

        // The right bar's module in the row of the centre square's top modules, all of it but its outer pixel column
        for (var y = (QuietZone + 2) * PixelsPerModule; y < (QuietZone + 3) * PixelsPerModule; y++)
            luminance.AsSpan(y * side + (QuietZone + 6) * PixelsPerModule, PixelsPerModule - 1).Fill(255);

        // The premise: the row a module above the centre has five runs seven modules long that are not a finder's, and the centre's own row and column read
        var runs = new int[5];
        await Assert.That(FinderPatternFinder.MeasureRuns(luminance, side, side, 128, centre, centre - PixelsPerModule, 1, 0, FinderPatternFinder.NoRunCap, runs, out _)).IsTrue();
        await Assert.That(runs.Sum()).IsEqualTo(7 * PixelsPerModule);
        await Assert.That(FinderPatternFinder.IsFinderRatio(runs)).IsFalse();
        foreach (var (stepX, stepY) in new[] { (1, 0), (0, 1) })
            await Assert.That(FinderPatternFinder.MeasureRuns(luminance, side, side, 128, centre, centre, stepX, stepY, FinderPatternFinder.NoRunCap, runs, out _) && FinderPatternFinder.IsFinderRatio(runs)).IsTrue();

        await Assert.That(NearestCandidate(luminance, side, centre)).IsGreaterThan(2f * PixelsPerModule);
    }

    /// <summary>
    /// A ring without corners is held to all four of its bars on both sides of the centre: with half of one bar gone as well, from one end to the centre's own line, the centre's row and column still read, a row or a column beside them does not, and the finder is not a candidate.
    /// </summary>
    [Test]
    [MatrixDataSource]
    public async Task RingWithoutCornersAndHalfABar_IsNoCandidate([Matrix(0, 1, 2, 3)] int bar, [Matrix(false, true)] bool farHalf)
    {
        const int PixelsPerModule = 6;
        var qr = Symbol(2);
        // Bars 0 and 1 are the top and the bottom, 2 and 3 the left and the right; a half is the three modules before the centre line or the three after it
        bool IsMissing(int row, int column)
        {
            var (across, along) = bar < 2 ? (row, column) : (column, row);
            return across == (bar % 2 == 0 ? 0 : 6) && (farHalf ? along is > 3 and < 7 : along < 3);
        }
        var (luminance, side) = Draw((row, column) => !IsRingCorner(row, column, qr.Size) && !IsMissing(row, column) && qr[row, column], qr.Size, PixelsPerModule);
        var centre = Centre(3.5f, PixelsPerModule);

        // The premise: the centre's own row and column read
        var runs = new int[5];
        foreach (var (stepX, stepY) in new[] { (1, 0), (0, 1) })
            await Assert.That(FinderPatternFinder.MeasureRuns(luminance, side, side, 128, (int)centre, (int)centre, stepX, stepY, FinderPatternFinder.NoRunCap, runs, out _) && FinderPatternFinder.IsFinderRatio(runs)).IsTrue();

        await Assert.That(NearestCandidate(luminance, side, centre)).IsGreaterThan(2f * PixelsPerModule);
    }

    private static string Text(int version) => $"FQR RING v{version}";

    private static QRCodeData Symbol(int version)
        => QRCodeGenerator.Create(Text(version), QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version), QuietZoneSize = 0 });

    /// <summary>The four corner modules of each finder's outer ring.</summary>
    private static bool IsRingCorner(int row, int column, int size)
    {
        foreach (var (top, left) in new[] { (0, 0), (0, size - 7), (size - 7, 0) })
        {
            if ((row == top || row == top + 6) && (column == left || column == left + 6))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Lightens a column of pixels on the right side of the top-left finder's ring, a pixel from its inner edge: the centre row and the rows next to it, and with a <paramref name="reach"/> of −1 or 1 every row up or down to two past the one a quarter of the centre square away.
    /// </summary>
    private static void Speck(byte[] luminance, int side, int pixelsPerModule, int reach)
    {
        var centre = (int)Centre(3.5f, pixelsPerModule);
        var quarter = 3 * pixelsPerModule / 4;
        var x = (QuietZone + 6) * pixelsPerModule + 1;
        for (var y = centre - 1 - (reach < 0 ? quarter + 1 : 0); y <= centre + 1 + (reach > 0 ? quarter + 1 : 0); y++)
            luminance[y * side + x] = 255;
    }

    /// <summary>The pixel coordinate of grid coordinate <paramref name="grid"/> in a drawing with the quiet zone.</summary>
    private static float Centre(float grid, int pixelsPerModule) => (QuietZone + grid) * pixelsPerModule;

    /// <summary>The symbol drawn crisp, a whole number of pixels a module, black on white with a quiet zone.</summary>
    private static (byte[] Luminance, int Side) Draw(Func<int, int, bool> isDark, int size, int pixelsPerModule)
    {
        var side = (size + 2 * QuietZone) * pixelsPerModule;
        var luminance = new byte[side * side];
        Array.Fill(luminance, (byte)255);
        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
            {
                if (!isDark(row, column))
                    continue;
                for (var y = 0; y < pixelsPerModule; y++)
                    luminance.AsSpan(((QuietZone + row) * pixelsPerModule + y) * side + (QuietZone + column) * pixelsPerModule, pixelsPerModule).Clear();
            }
        }
        return (luminance, side);
    }

    /// <summary>The distance from a point to the nearest candidate of the full sweep over a square image.</summary>
    private static float NearestCandidate(byte[] luminance, int side, float x, float y)
    {
        var candidates = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var count = FinderPatternFinder.FindCandidatesFullSweep(luminance, side, side, 128, candidates, default);
        var nearest = float.MaxValue;
        for (var i = 0; i < count; i++)
            nearest = MathF.Min(nearest, MathF.Sqrt((candidates[i].X - x) * (candidates[i].X - x) + (candidates[i].Y - y) * (candidates[i].Y - y)));
        return nearest;
    }

    private static float NearestCandidate(byte[] luminance, int side, float centre) => NearestCandidate(luminance, side, centre, centre);
}
