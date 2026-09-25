using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// Under about 1.6 px/module a crisp finder's modules are 1 or 2 px wide, so no run is within half a module of the ratio and the pattern is accepted another way: its runs have the shape such a finder leaves, a neighbouring row repeats them, and all 49 modules read as a finder through the edges the row and the column measured. That last check is exact, so what is not a finder, module for module, stays out.
/// </summary>
public class FinderWholePatternTest
{
    public static IEnumerable<(float, float, float)> ScalesAndOffsets()
    {
        foreach (var pixelsPerModule in new[] { 1.0f, 1.1f, 1.25f, 1.4f, 1.55f })
        {
            foreach (var (offsetX, offsetY) in new[] { (0f, 0f), (0.3f, 0.7f), (0.5f, 0.5f), (0.8f, 0.2f) })
                yield return (pixelsPerModule, offsetX, offsetY);
        }
    }

    [Test]
    [MethodDataSource(nameof(ScalesAndOffsets))]
    public async Task FindCandidates_CrispFinder_IsFoundAtItsCentre(float pixelsPerModule, float offsetX, float offsetY)
    {
        var (luminance, width, height) = NearestNeighbourRenderer.Render((row, column) => Finder(row, column, -1, -1), 15, 15, pixelsPerModule, offsetX, offsetY);

        var (count, candidates) = Find(luminance, width, height);

        await Assert.That(count).IsEqualTo(1);
        // Pattern centre: 4 quiet modules + 3.5
        await Assert.That(Math.Abs(candidates[0].X - (offsetX + 7.5f * pixelsPerModule))).IsLessThanOrEqualTo(0.75f);
        await Assert.That(Math.Abs(candidates[0].Y - (offsetY + 7.5f * pixelsPerModule))).IsLessThanOrEqualTo(0.75f);
    }

    /// <summary>
    /// A finder with one module of its light ring dark, at a corner of the ring or beside one. Every line through the centre reads as a finder's, and rows repeat; only reading the whole pattern tells it from one.
    /// At 1.25 px/module, where these renders fail the ratio: a render that passes it is accepted on three lines through the centre, which never cross these modules.
    /// </summary>
    [Test]
    [Arguments(1.25f, 0f, 0f, 1, 1)]
    [Arguments(1.25f, 0.5f, 0.5f, 5, 5)]
    [Arguments(1.25f, 0.3f, 0.7f, 1, 2)]
    [Arguments(1.25f, 0.3f, 0.7f, 1, 5)]
    [Arguments(1.25f, 0.8f, 0.2f, 5, 1)]
    [Arguments(1.25f, 0.5f, 0.5f, 4, 5)]
    [Arguments(1.25f, 0f, 0f, 5, 5)]
    public async Task FindCandidates_FinderWithOneRingModuleDark_IsNotACandidate(float pixelsPerModule, float offsetX, float offsetY, int wrongRow, int wrongColumn)
    {
        var (luminance, width, height) = NearestNeighbourRenderer.Render((row, column) => Finder(row, column, wrongRow, wrongColumn), 15, 15, pixelsPerModule, offsetX, offsetY);

        var (count, _) = Find(luminance, width, height);

        await Assert.That(count).IsEqualTo(0);
    }

    /// <summary>
    /// Fine two-level noise is where a looser ratio floods the candidate list. The counts are the ones the scan gave before small crisp patterns were accepted: nothing in the noise reads as a whole finder.
    /// </summary>
    [Test]
    [Arguments(1, 0.5, 42, 2)]
    [Arguments(1, 0.5, 7, 0)]
    [Arguments(1, 0.5, 99, 2)]
    [Arguments(2, 0.5, 42, 1)]
    [Arguments(2, 0.5, 7, 2)]
    [Arguments(2, 0.5, 99, 2)]
    [Arguments(1, 0.3, 42, 0)]
    [Arguments(1, 0.4, 7, 0)]
    [Arguments(2, 0.4, 99, 0)]
    [Arguments(3, 0.5, 42, 1)]
    public async Task FindCandidates_TwoLevelNoise_GainsNoCandidate(int cellPx, double density, int seed, int expected)
    {
        const int side = 256;
        var random = new Random(seed);
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

        var (count, _) = Find(luminance, side, side);

        await Assert.That(count).IsEqualTo(expected);
    }

    /// <summary>
    /// A crisp finder 1 px a module across and 1.6 or 2 px down: its column is past the row's own 40 %, and the column's window reads it here as in the ratio path, at every sub-pixel offset.
    /// With its light ring filled in the two quadrants the rising diagonal crosses, the 49 modules refuse it at every offset, which is why this path does not ask the diagonal as well.
    /// </summary>
    [Test]
    [Arguments(1.6f, false, 16)]
    [Arguments(1.6f, true, 0)]
    [Arguments(2f, false, 16)]
    [Arguments(2f, true, 0)]
    public async Task FindCandidates_StretchedSmallFinder_WholePatternRefusesABrokenRing(float pixelsPerModuleDown, bool risingDiagonalBroken, int expectedOffsets)
    {
        const float pixelsPerModuleAcross = 1f;
        var width = (int)(15 * pixelsPerModuleAcross) + 2;
        var height = (int)(15 * pixelsPerModuleDown) + 2;
        var offsetsWithACandidate = 0;
        for (var offsetX = 0; offsetX < 4; offsetX++)
        {
            for (var offsetY = 0; offsetY < 4; offsetY++)
            {
                var luminance = new byte[width * height];
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var row = (int)Math.Floor((y + offsetY * 0.25f) / pixelsPerModuleDown) - 4;
                        var column = (int)Math.Floor((x + offsetX * 0.25f) / pixelsPerModuleAcross) - 4;
                        luminance[y * width + x] = StretchedFinder(row, column, risingDiagonalBroken) ? (byte)0 : (byte)255;
                    }
                }
                if (Find(luminance, width, height).Count > 0)
                    offsetsWithACandidate++;
            }
        }

        await Assert.That(offsetsWithACandidate).IsEqualTo(expectedOffsets);

        // A 7 × 7 finder, its light ring filled in the two quadrants the rising diagonal crosses when broken
        static bool StretchedFinder(int row, int column, bool broken)
        {
            if (row < 0 || column < 0 || row >= 7 || column >= 7)
                return false;
            var ring = Math.Min(Math.Min(row, column), Math.Min(6 - row, 6 - column));
            if (ring != 1)
                return true;
            return broken && ((row < 3 && column > 3) || (row > 3 && column < 3));
        }
    }

    private static (int Count, FinderPattern[] Candidates) Find(byte[] luminance, int width, int height)
    {
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var candidates = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        return (FinderPatternFinder.FindCandidatesFullSweep(luminance, width, height, threshold, candidates, grey), candidates);
    }

    // 4 quiet modules, then a 7 × 7 finder pattern with module (wrongRow, wrongColumn) dark whatever it should be
    private static bool Finder(int row, int column, int wrongRow, int wrongColumn)
    {
        var r = row - 4;
        var c = column - 4;
        if (r < 0 || c < 0 || r >= 7 || c >= 7)
            return false;
        if (r == wrongRow && c == wrongColumn)
            return true;
        var ring = Math.Min(Math.Min(r, c), Math.Min(6 - r, 6 - c));
        return ring != 1;
    }
}
