using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// A finder printed with its dark rings too thick or too thin keeps the distances between edges of the same polarity: a dark run gains what the light runs beside it lose.
/// The cross-checks read those distances, 2:4:4:2 modules, when a line's runs miss 1:1:3:1:1, and take the line only while the edges moved by under a quarter of a module, the module is at least 2 px along the line, and the shift has the sign of the row the scan found.
/// </summary>
public class FinderLikeEdgesTest
{
    [Test]
    // Thin dark rings (blurred photographs), lines the whole-pixel ratio refuses
    [Arguments(3, 7, 13, 6, 3, 1)]
    [Arguments(2, 6, 11, 6, 2, 1)]
    [Arguments(4, 7, 14, 8, 3, 1)]
    [Arguments(3, 6, 11, 5, 3, 1)]
    // Thick dark rings (ink spread, a glowing screen read inverted)
    [Arguments(4, 3, 11, 1, 5, -1)]
    [Arguments(5, 1, 11, 3, 4, -1)]
    [Arguments(5, 3, 14, 2, 6, -1)]
    [Arguments(6, 3, 15, 2, 6, -1)]
    public async Task ShiftedRing_RefusedByTheRatio_ReadByLikeEdges(int r0, int r1, int r2, int r3, int r4, int sign)
    {
        int[] runs = [r0, r1, r2, r3, r4];
        await Assert.That(FinderPatternFinder.IsFinderRatio(runs)).IsFalse();

        await Assert.That(FinderPatternFinder.IsFinderRatioByLikeEdges(r0, r1, r2, r3, r4, out var shiftSign)).IsTrue();
        await Assert.That(shiftSign).IsEqualTo(sign);
    }

    [Test]
    // Edges moved by a quarter module or more: a run of half its width is not measured, it is lost
    [Arguments(2, 6, 10, 6, 2)]
    [Arguments(6, 2, 14, 2, 6)]
    [Arguments(7, 2, 17, 2, 7)]
    // Under 2 px a module: half a module is under a pixel, which whole-pixel runs cannot tell from their own rounding
    [Arguments(1, 2, 3, 2, 1)]
    [Arguments(2, 1, 3, 1, 2)]
    // Same-polarity distances a module or more off
    [Arguments(4, 4, 12, 8, 4)]
    [Arguments(8, 4, 12, 4, 8)]
    [Arguments(4, 8, 12, 4, 4)]
    [Arguments(4, 4, 20, 4, 4)]
    // An empty run
    [Arguments(0, 6, 12, 6, 6)]
    public async Task NotAFinderLine_IsRefused(int r0, int r1, int r2, int r3, int r4)
    {
        await Assert.That(FinderPatternFinder.IsFinderRatioByLikeEdges(r0, r1, r2, r3, r4, out _)).IsFalse();
    }

    [Test]
    [Arguments(2, 6, 18, 6, 6, 0)]
    [Arguments(4, 6, 18, 3, 6, 1)]
    [Arguments(6, 2, 18, 6, 5, 2)]
    [Arguments(6, 6, 18, 3, 5, 3)]
    public async Task OneDistanceHalfAModuleOff_IsRefused(int r0, int r1, int r2, int r3, int r4, int off)
    {
        // Premise, in modules of a twelfth of r0 + 2·(r1 + r2 + r3) + r4: that distance half a module to a module off, the other three within half a module, the edges moved by under a quarter of one
        var module = (r0 + 2 * (r1 + r2 + r3) + r4) / 12.0;
        int[] distances = [r0 + r1, r1 + r2, r2 + r3, r3 + r4];
        int[] nominal = [2, 4, 4, 2];
        for (var i = 0; i < 4; i++)
        {
            var deviation = Math.Abs(distances[i] / module - nominal[i]);
            await Assert.That(i == off ? deviation >= 0.5 && deviation < 1 : deviation < 0.5).IsTrue().Because($"distance {i} is {deviation:F2} modules off");
        }
        await Assert.That(Math.Abs((r1 + r3 - r0 - r2 - r4) / module + 3) / 10).IsLessThan(0.25);
        await Assert.That(module).IsGreaterThanOrEqualTo(2);

        await Assert.That(FinderPatternFinder.IsFinderRatioByLikeEdges(r0, r1, r2, r3, r4, out _)).IsFalse();
    }

    [Test]
    public async Task ShiftBound_IsAQuarterModuleAtEachEdge()
    {
        // m = 4 px: runs 4 - s, 4 + s, 12 - s, 4 + s, 4 - s. The least-squares shift given the module is exactly s.
        await Assert.That(FinderPatternFinder.IsFinderRatioByLikeEdges(3, 5, 11, 5, 3, out var thin)).IsTrue();
        await Assert.That(thin).IsEqualTo(1);
        await Assert.That(FinderPatternFinder.IsFinderRatioByLikeEdges(5, 3, 13, 3, 5, out var thick)).IsTrue();
        await Assert.That(thick).IsEqualTo(-1);
        // s = 2 = half the module is refused both ways
        await Assert.That(FinderPatternFinder.IsFinderRatioByLikeEdges(2, 6, 10, 6, 2, out _)).IsFalse();
        await Assert.That(FinderPatternFinder.IsFinderRatioByLikeEdges(6, 2, 14, 2, 6, out _)).IsFalse();
        // Scaled to m = 8, s = 3 is inside and s = 4 is not
        await Assert.That(FinderPatternFinder.IsFinderRatioByLikeEdges(5, 11, 21, 11, 5, out _)).IsTrue();
        await Assert.That(FinderPatternFinder.IsFinderRatioByLikeEdges(4, 12, 20, 12, 4, out _)).IsFalse();
    }

    [Test]
    public async Task ModuleFloor_IsTwoPixels()
    {
        // Same-polarity distances summing to 24 are a module of 2 px; 23 and 22 are under it
        await Assert.That(FinderPatternFinder.IsFinderRatioByLikeEdges(2, 2, 6, 2, 2, out _)).IsTrue();
        await Assert.That(FinderPatternFinder.IsFinderRatioByLikeEdges(2, 2, 6, 2, 1, out _)).IsFalse();
        await Assert.That(FinderPatternFinder.IsFinderRatioByLikeEdges(2, 2, 5, 2, 2, out _)).IsFalse();
    }

    [Test]
    public async Task ExactRatio_HasNoShift()
    {
        await Assert.That(FinderPatternFinder.IsFinderRatioByLikeEdges(3, 3, 9, 3, 3, out var sign)).IsTrue();
        await Assert.That(sign).IsEqualTo(0);
    }

    [Test]
    public async Task SameSpreadOnEveryLine_ReadsWherever()
    {
        // The row's shift sign against a line's
        await Assert.That(FinderPatternFinder.IsLikeEdgeLine(3, 7, 13, 6, 3, rowShiftSign: 1)).IsTrue();
        await Assert.That(FinderPatternFinder.IsLikeEdgeLine(3, 7, 13, 6, 3, rowShiftSign: 0)).IsTrue();
        await Assert.That(FinderPatternFinder.IsLikeEdgeLine(3, 7, 13, 6, 3, rowShiftSign: -1)).IsFalse();
        await Assert.That(FinderPatternFinder.IsLikeEdgeLine(4, 3, 11, 1, 5, rowShiftSign: -1)).IsTrue();
        await Assert.That(FinderPatternFinder.IsLikeEdgeLine(4, 3, 11, 1, 5, rowShiftSign: 1)).IsFalse();
    }

    /// <summary>
    /// A lone finder drawn with every dark edge moved outward or inward by a fraction of a module, at a density and turn where the whole-pixel ratio refuses at least one of its lines: a candidate, within a module of its drawn centre.
    /// </summary>
    [Test]
    [Arguments(3.5f, 20f, -0.22f, true)]
    [Arguments(4.5f, 20f, -0.18f, false)]
    [Arguments(5f, 45f, -0.15f, false)]
    [Arguments(6f, 30f, 0.22f, true)]
    public async Task SpreadFinder_IsACandidate(float pixelsPerModule, float degrees, float spread, bool binarized)
    {
        var (luminance, side, _) = SpreadRenderer.Render(IsFinderModule, 7, 7, pixelsPerModule, degrees, spread, binarized);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var (x, y) = SpreadRenderer.ToImage(7, 7, pixelsPerModule, degrees, side, 3.5f, 3.5f);

        // The premise: a line through the drawn centre that the ratio refuses
        await Assert.That(AnyLineRefusedByTheRatio(luminance, side, threshold, (int)x, (int)y)).IsTrue();

        var candidates = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var count = FinderPatternFinder.FindCandidatesFullSweep(luminance, side, side, threshold, candidates, grey);

        await Assert.That(count).IsEqualTo(1);
        await Assert.That(MathF.Abs(candidates[0].X - x)).IsLessThan(pixelsPerModule);
        await Assert.That(MathF.Abs(candidates[0].Y - y)).IsLessThan(pixelsPerModule);
    }

    /// <summary>Edges moved by a third of a module shift every run by two thirds of one: not a candidate.</summary>
    [Test]
    [Arguments(4f, 0f, -0.33f, true)]
    [Arguments(4f, 0f, 0.33f, true)]
    [Arguments(5f, 20f, -0.33f, false)]
    [Arguments(5f, 20f, 0.33f, false)]
    public async Task FinderSpreadPastAQuarterModule_IsNotACandidate(float pixelsPerModule, float degrees, float spread, bool binarized)
    {
        var (luminance, side, _) = SpreadRenderer.Render(IsFinderModule, 7, 7, pixelsPerModule, degrees, spread, binarized);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);

        var candidates = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var count = FinderPatternFinder.FindCandidatesFullSweep(luminance, side, side, threshold, candidates, grey);

        await Assert.That(count).IsEqualTo(0);
    }

    /// <summary>
    /// A finder read by like edges must read along both diagonals, where a ring that is not closed shows: with two light-ring modules on one diagonal filled, the spread finder is refused, while the same fill without the spread is a candidate (the ratio reads its row, column and falling diagonal, and it is not asked the rising one), and the spread finder with its ring whole is a candidate too.
    /// </summary>
    [Test]
    [Arguments(6f, 30f, 0.22f, true)]
    [Arguments(6f, 30f, 0.2f, false)]
    [Arguments(6f, 25f, 0.22f, false)]
    public async Task SpreadFinder_RisingDiagonalBroken_IsNotACandidate(float pixelsPerModule, float degrees, float spread, bool binarized)
    {
        var candidates = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        await Assert.That(Candidates(IsFinderModuleRisingFilled, pixelsPerModule, degrees, spread, binarized, candidates)).IsEqualTo(0);

        await Assert.That(Candidates(IsFinderModuleRisingFilled, pixelsPerModule, degrees, 0f, binarized, candidates)).IsEqualTo(1);
        await Assert.That(Candidates(IsFinderModule, pixelsPerModule, degrees, spread, binarized, candidates)).IsEqualTo(1);
    }

    /// <summary>
    /// The rule above on a finder where it decides alone: its dark rings thinned vertically only, so each row reads the ratio and the column only by like edges, with two light-ring corners on the rising diagonal filled.
    /// Without the rising diagonal it would be a candidate: its row, column and falling diagonal read.
    /// </summary>
    [Test]
    [Arguments(24, 5)]
    [Arguments(30, 7)]
    public async Task ColumnByLikeEdges_RisingDiagonalBroken_IsNotACandidate(int module, int thinning)
    {
        var (whole, side) = ThinnedFinder(module, thinning, risingFilled: false);
        var (broken, _) = ThinnedFinder(module, thinning, risingFilled: true);
        var centre = side / 2;
        var runs = new int[5];

        // The premises, on the broken finder: the row reads the ratio, the column only like edges, the falling diagonal reads, the rising one does not
        await Assert.That(FinderPatternFinder.MeasureRuns(broken, side, side, 128, centre, centre, 1, 0, FinderPatternFinder.NoRunCap, runs, out _)).IsTrue();
        await Assert.That(FinderPatternFinder.IsFinderRatio(runs)).IsTrue();
        await Assert.That(FinderPatternFinder.MeasureRuns(broken, side, side, 128, centre, centre, 0, 1, FinderPatternFinder.NoRunCap, runs, out _)).IsTrue();
        await Assert.That(FinderPatternFinder.IsFinderRatio(runs)).IsFalse();
        await Assert.That(FinderPatternFinder.IsFinderRatioByLikeEdges(runs[0], runs[1], runs[2], runs[3], runs[4], out _)).IsTrue();
        await Assert.That(FinderPatternFinder.MeasureRuns(broken, side, side, 128, centre, centre, 1, 1, FinderPatternFinder.NoRunCap, runs, out _)).IsTrue();
        await Assert.That(FinderPatternFinder.IsFinderRatio(runs) || FinderPatternFinder.IsFinderRatioByLikeEdges(runs[0], runs[1], runs[2], runs[3], runs[4], out _)).IsTrue();
        await Assert.That(FinderPatternFinder.MeasureRuns(broken, side, side, 128, centre, centre, 1, -1, FinderPatternFinder.NoRunCap, runs, out _)).IsTrue();
        await Assert.That(FinderPatternFinder.IsFinderRatio(runs) || FinderPatternFinder.IsFinderRatioByLikeEdges(runs[0], runs[1], runs[2], runs[3], runs[4], out _)).IsFalse();

        var candidates = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        await Assert.That(FinderPatternFinder.FindCandidatesFullSweep(broken, side, side, 128, candidates, default)).IsEqualTo(0);
        await Assert.That(FinderPatternFinder.FindCandidatesFullSweep(whole, side, side, 128, candidates, default)).IsEqualTo(1);
    }

    /// <summary>A lone finder in a four-module quiet zone, 0 and 255, its dark edges across the columns moved inward by <paramref name="thinning"/> px and those across the rows left.</summary>
    private static (byte[] Luminance, int Side) ThinnedFinder(int module, int thinning, bool risingFilled)
    {
        var side = 15 * module;
        var origin = 4 * module;
        var luminance = new byte[side * side];
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var column = Band(x - origin, module, 0);
                var row = Band(y - origin, module, thinning);
                var dark = false;
                if (column >= 0 && row >= 0)
                {
                    var ring = Math.Min(Math.Min(column, 4 - column), Math.Min(row, 4 - row));
                    dark = ring != 1 || (risingFilled && ((column == 1 && row == 3) || (column == 3 && row == 1)));
                }
                luminance[y * side + x] = dark ? (byte)0 : (byte)255;
            }
        }
        return (luminance, side);

        // The band 0-4 (outer dark, light, centre, light, outer dark) a coordinate falls in, each dark band shrunk by the thinning at both of its edges; -1 outside
        static int Band(int p, int module, int thinning)
        {
            int[] edges = [thinning, module - thinning, 2 * module + thinning, 5 * module - thinning, 6 * module + thinning, 7 * module - thinning];
            for (var band = 0; band < 5; band++)
            {
                if (p >= edges[band] && p < edges[band + 1])
                    return band;
            }
            return -1;
        }
    }

    private static int Candidates(Func<int, int, bool> isDark, float pixelsPerModule, float degrees, float spread, bool binarized, FinderPattern[] candidates)
    {
        var (luminance, side, _) = SpreadRenderer.Render(isDark, 7, 7, pixelsPerModule, degrees, spread, binarized);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        return FinderPatternFinder.FindCandidatesFullSweep(luminance, side, side, threshold, candidates, grey);
    }

    private static bool AnyLineRefusedByTheRatio(byte[] luminance, int side, byte threshold, int x, int y)
    {
        Span<int> runs = stackalloc int[5];
        foreach (var (stepX, stepY) in new[] { (1, 0), (0, 1), (1, 1), (1, -1) })
        {
            if (FinderPatternFinder.MeasureRuns(luminance, side, side, threshold, x, y, stepX, stepY, FinderPatternFinder.NoRunCap, runs, out _)
                && !FinderPatternFinder.IsFinderRatio(runs))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsFinderModule(int row, int column)
    {
        var ring = Math.Min(Math.Min(row, column), Math.Min(6 - row, 6 - column));
        return ring != 1;
    }

    // The light ring's two corner modules on the rising diagonal, (5, 1) and (1, 5), filled dark
    private static bool IsFinderModuleRisingFilled(int row, int column)
        => IsFinderModule(row, column) || (row == 5 && column == 1) || (row == 1 && column == 5);
}
