using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// The grey-level measurement is a second look at runs that fail the whole-pixel 1:1:3:1:1 check by about a pixel. It holds them to the same ratio, so what it may change is bounded: a pattern that is not a finder stays refused, and an image with no grey in it is scanned exactly as before.
/// </summary>
public class FinderRatioCoverageTest
{
    /// <summary>
    /// Concentric squares 2:1:3:1:2, which fail the ratio on their outer ring however finely it is measured. At 2 px per unit the whole-pixel runs are within a pixel and a half of passing, on rows, columns and the diagonal alike, so the measurement runs and has to refuse them.
    /// </summary>
    [Test]
    [Arguments(2.0f, 0.5f, 0.5f)]
    [Arguments(2.0f, 0.25f, 0.75f)]
    [Arguments(2.0f, 0.5f, 0f)]
    [Arguments(2.1f, 0.5f, 0.5f)]
    [Arguments(2.1f, 0f, 0.25f)]
    [Arguments(2.2f, 0.75f, 0.5f)]
    public async Task FindCandidates_WideOuterRing_AntiAliased_IsNotACandidate(float pixelsPerUnit, float offsetX, float offsetY)
    {
        var (luminance, width, height) = AntiAliasedRenderer.Render(WideRingPattern, 17, 17, pixelsPerUnit, offsetX, offsetY);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        await Assert.That(grey.IsEnabled).IsTrue();

        var candidates = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var count = FinderPatternFinder.FindCandidatesFullSweep(luminance, width, height, threshold, candidates, grey);

        await Assert.That(count).IsEqualTo(0);
    }

    /// <summary>The same squares at the true ratio, as the control: found, and only with the grey levels.</summary>
    [Test]
    [Arguments(2.1f, 0.5f, 0.5f)]
    [Arguments(2.0f, 0.5f, 0.5f)]
    public async Task FindCandidates_FinderPattern_AntiAliased_IsFoundOnlyByCoverage(float pixelsPerModule, float offsetX, float offsetY)
    {
        var (luminance, width, height) = AntiAliasedRenderer.Render(FinderPatternOnly, 15, 15, pixelsPerModule, offsetX, offsetY);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);

        var candidates = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var wholePixels = FinderPatternFinder.FindCandidatesFullSweep(luminance, width, height, threshold, candidates);
        var byCoverage = FinderPatternFinder.FindCandidatesFullSweep(luminance, width, height, threshold, candidates, grey);

        await Assert.That(wholePixels).IsEqualTo(0);
        await Assert.That(byCoverage).IsEqualTo(1);
        // Pattern centre: 4 quiet modules + 3.5
        await Assert.That(Math.Abs(candidates[0].X - (offsetX + 7.5f * pixelsPerModule))).IsLessThan(pixelsPerModule / 2f);
        await Assert.That(Math.Abs(candidates[0].Y - (offsetY + 7.5f * pixelsPerModule))).IsLessThan(pixelsPerModule / 2f);
    }

    /// <summary>
    /// Two levels only: every pixel is wholly dark or wholly light, the measurement could only repeat the whole-pixel runs, and it is off. Noise at 1-2 px is where a looser check would flood the candidate list.
    /// </summary>
    [Test]
    [Arguments(1, 0.5)]
    [Arguments(2, 0.5)]
    [Arguments(1, 0.3)]
    public async Task FindCandidates_TwoLevelNoise_IsScannedAsBefore(int cellPx, double density)
    {
        const int side = 256;
        var random = new Random(42);
        var luminance = new byte[side * side];
        var cellsPerRow = side / cellPx + 1;
        var cells = new bool[cellsPerRow * cellsPerRow];
        for (var i = 0; i < cells.Length; i++)
            cells[i] = random.NextDouble() < density;
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
                luminance[y * side + x] = cells[y / cellPx * cellsPerRow + x / cellPx] ? (byte)20 : (byte)230;
        }

        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);

        await Assert.That(grey.IsEnabled).IsFalse();
        var before = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var after = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var countBefore = FinderPatternFinder.FindCandidatesFullSweep(luminance, side, side, threshold, before);
        var countAfter = FinderPatternFinder.FindCandidatesFullSweep(luminance, side, side, threshold, after, grey);
        await Assert.That(countAfter).IsEqualTo(countBefore);
    }

    /// <summary>
    /// From 3 px/module a finder's run a pixel off is inside the half-module tolerance, so the grey levels have nothing to add. What is still re-measured is short data runs such as 1:1:1:1:1, which must stay refused: no candidate appears, moves or is confirmed on another row.
    /// </summary>
    [Test]
    [Arguments(3.0f)]
    [Arguments(3.4f)]
    [Arguments(4.5f)]
    public async Task FindCandidates_ThreePixelsPerModuleUp_GreyLevelsChangeNothing(float pixelsPerModule)
    {
        var qr = QRCodeGenerator.Create("FQR 2.0", QREccLevel.M, new QRCodeGeneratorOptions { Version = 3 });
        var (luminance, width, height) = AntiAliasedRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, pixelsPerModule, 0.5f, 0.25f);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        await Assert.That(grey.IsEnabled).IsTrue();

        var before = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var after = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var countBefore = FinderPatternFinder.FindCandidatesFullSweep(luminance, width, height, threshold, before);
        var countAfter = FinderPatternFinder.FindCandidatesFullSweep(luminance, width, height, threshold, after, grey);

        await Assert.That(countAfter).IsEqualTo(countBefore);
        for (var i = 0; i < countBefore; i++)
        {
            await Assert.That(after[i].X).IsEqualTo(before[i].X);
            await Assert.That(after[i].Y).IsEqualTo(before[i].Y);
            await Assert.That(after[i].Count).IsEqualTo(before[i].Count);
        }
    }

    /// <summary>The vector row scan and the scalar one take the same second look.</summary>
    [Test]
    [Arguments(2.0f, 0.5f)]
    [Arguments(2.1f, 0.25f)]
    [Arguments(2.3f, 0.75f)]
    public async Task TryFind_AntiAliased_ScalarAndVectorAgree(float pixelsPerModule, float offset)
    {
        var qr = QRCodeGenerator.Create("FQR 2.0", QREccLevel.M, new QRCodeGeneratorOptions { Version = 3 });
        var (luminance, width, height) = AntiAliasedRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, pixelsPerModule, offset, offset);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);

        var vector = new FinderPattern[3];
        var scalar = new FinderPattern[3];
        var vectorFound = FinderPatternFinder.TryFind(luminance, width, height, threshold, vector, grey);
        var scalarFound = FinderPatternFinder.TryFindScalar(luminance, width, height, threshold, scalar, grey);

        await Assert.That(vectorFound).IsTrue();
        await Assert.That(scalarFound).IsTrue();
        for (var i = 0; i < 3; i++)
        {
            await Assert.That(vector[i].X).IsEqualTo(scalar[i].X);
            await Assert.That(vector[i].Y).IsEqualTo(scalar[i].Y);
            await Assert.That(vector[i].ModuleSize).IsEqualTo(scalar[i].ModuleSize);
            await Assert.That(vector[i].Count).IsEqualTo(scalar[i].Count);
        }
    }

    [Test]
    public async Task GreyLevels_TwoLevels_AreDisabled()
    {
        var histogram = new int[256];
        histogram[0] = 1000;
        histogram[255] = 3000;

        await Assert.That(GreyLevels.FromHistogram(histogram, 128).IsEnabled).IsFalse();
    }

    [Test]
    public async Task GreyLevels_OneClass_AreDisabled()
    {
        var histogram = new int[256];
        histogram[200] = 1000;
        histogram[210] = 1000;

        await Assert.That(GreyLevels.FromHistogram(histogram, 128).IsEnabled).IsFalse();
    }

    /// <summary>Two classes too close for a level between them to mean coverage.</summary>
    [Test]
    public async Task GreyLevels_LowContrast_AreDisabled()
    {
        var histogram = new int[256];
        histogram[100] = 1000;
        histogram[110] = 50;
        histogram[120] = 1000;

        await Assert.That(GreyLevels.FromHistogram(histogram, 111).IsEnabled).IsFalse();
    }

    /// <summary>The levels are the flat modules', not dragged inward by the edge pixels between them.</summary>
    [Test]
    public async Task GreyLevels_EdgePixelsBetweenTheClasses_ReadAsCoverage()
    {
        var histogram = new int[256];
        histogram[0] = 1000;
        histogram[255] = 3000;
        for (var level = 16; level < 255; level += 16)
            histogram[level] = 40;

        var grey = GreyLevels.FromHistogram(histogram, 144);

        await Assert.That(grey.IsEnabled).IsTrue();
        await Assert.That(grey.Darkness(0)).IsEqualTo(1f);
        await Assert.That(grey.Darkness(255)).IsEqualTo(0f);
        await Assert.That(Math.Abs(grey.Darkness(128) - 0.5f)).IsLessThan(0.01f);
        await Assert.That(Math.Abs(grey.Darkness(64) - 0.75f)).IsLessThan(0.01f);
    }

    // 4 quiet units, then rings 2:1:3:1:2
    private static bool WideRingPattern(int row, int column)
    {
        var r = row - 4;
        var c = column - 4;
        if (r < 0 || c < 0 || r >= 9 || c >= 9)
            return false;
        var ring = Math.Min(Math.Min(r, c), Math.Min(8 - r, 8 - c));
        return ring != 2;
    }

    // 4 quiet modules, then a 7 × 7 finder pattern
    private static bool FinderPatternOnly(int row, int column)
    {
        var r = row - 4;
        var c = column - 4;
        if (r < 0 || c < 0 || r >= 7 || c >= 7)
            return false;
        var ring = Math.Min(Math.Min(r, c), Math.Min(6 - r, 6 - c));
        return ring != 1;
    }
}
