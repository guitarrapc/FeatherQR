using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// Symbols past 20 % keystone, where the stages in front of the finders' frame decide: the finder a perspective draws taller than wide has to become a candidate, the dimension has to come from each finder line's own sizes, and a triple holding a false candidate has to give way to the next confirmed one.
/// Every decode render was chosen from a search to fail on the commit before and to fail with its own mechanism switched off, and to hold under nudges (turn ±0.5°, density ±0.02, keystone ±0.005) but for two: the second alternative-triple render, which reads at one nudge of seven with that piece off, and the one whose first alternative is false, whose premise holds at its exact parameters, as its assertions check.
/// </summary>
public class KeystoneFinderDecodeTest
{
    private const string Content = "FQR KEYSTONE 0123";

    /// <summary>The finder at the wide edge is drawn up to twice as tall as wide, and its column is refused by a 40 % window on the row's total.</summary>
    [Test]
    [Arguments(1, 5.21f, 337.2f, 0.471f)]
    [Arguments(7, 4.12f, 350.5f, 0.399f)]
    [Arguments(15, 5.11f, 163.7f, 0.4f)]
    [Arguments(25, 3.56f, 247.4f, 0.436f)]
    [Arguments(30, 3.49f, 17.1f, 0.466f)]
    [Arguments(35, 5.96f, 324.8f, 0.302f)]
    [Arguments(40, 4.45f, 329.8f, 0.297f)]
    public async Task StretchedFinder_Decodes(int version, float pixelsPerModule, float degrees, float keystone)
        => await KeystoneFrameDecodeTest.AssertDecodes(version, pixelsPerModule, degrees, keystone, binarized: false);

    /// <summary>
    /// The mechanism under the decodes above: the drawn finder whose column the row's own window refuses is a candidate, beside the other two.
    /// </summary>
    [Test]
    [Arguments(25, 3.56f, 247.4f, 0.436f)]
    [Arguments(7, 4.12f, 350.5f, 0.399f)]
    public async Task StretchedFinder_IsACandidateThoughItsColumnIsPastTheRowsWindow(int version, float pixelsPerModule, float degrees, float keystone)
    {
        var (luminance, width, height, truth, size) = Render(version, pixelsPerModule, degrees, keystone);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);

        // The premise: at one drawn finder the column is past 40 % of the row, each reading 1:1:3:1:1 on its own
        var stretched = 0;
        var row = new int[5];
        var column = new int[5];
        foreach (var (u, v) in DrawnFinders(size))
        {
            truth.Transform(u, v, out var x, out var y);
            if (FinderPatternFinder.MeasureRuns(luminance, width, height, threshold, (int)x, (int)y, 1, 0, FinderPatternFinder.NoRunCap, row, out _)
                && FinderPatternFinder.MeasureRuns(luminance, width, height, threshold, (int)x, (int)y, 0, 1, FinderPatternFinder.NoRunCap, column, out _)
                && FinderPatternFinder.IsFinderRatio(row) && FinderPatternFinder.IsFinderRatio(column)
                && !FinderPatternFinder.IsTotalInWindow(Sum(column), Sum(row), acrossAxes: false))
            {
                stretched++;
            }
        }
        await Assert.That(stretched).IsGreaterThan(0);

        var candidates = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var count = FinderPatternFinder.FindCandidatesFullSweep(luminance, width, height, threshold, candidates, grey);

        foreach (var (u, v) in DrawnFinders(size))
            await Assert.That(NearestModules(truth, candidates.AsSpan(0, count), u, v)).IsLessThan(1.5f).Because($"the drawn finder at grid ({u}, {v})");
    }

    /// <summary>
    /// A finder drawn 1.8 times as tall as wide, alone on a light field: a candidate, though the row's own window refuses its column.
    /// With the light ring filled where the rising diagonal crosses it, every other line still reads 1:1:3:1:1, and it is refused: past the row's window a column is taken only as the finder a perspective stretches, whose every line through the centre is a finder's.
    /// Drawn square, the same broken finder is a candidate as before, because inside the row's window the rising diagonal is not asked.
    /// </summary>
    [Test]
    [Arguments(7.2f, false, true)]
    [Arguments(7.2f, true, false)]
    [Arguments(4f, true, true)]
    [Arguments(4f, false, true)]
    public async Task StretchedFinder_IsACandidateOnlyIfItsRisingDiagonalReads(float pixelsPerModuleDown, bool risingDiagonalBroken, bool candidate)
    {
        const float pixelsPerModuleAcross = 4f;
        const int modules = 15;
        var width = (int)(modules * pixelsPerModuleAcross);
        var height = (int)(modules * pixelsPerModuleDown);
        var luminance = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var dark = IsFinderModule((int)(y / pixelsPerModuleDown) - 4, (int)(x / pixelsPerModuleAcross) - 4, risingDiagonalBroken);
                luminance[y * width + x] = dark ? (byte)20 : (byte)230;
            }
        }
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);

        var candidates = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var count = FinderPatternFinder.FindCandidatesFullSweep(luminance, width, height, threshold, candidates, grey);

        await Assert.That(count > 0).IsEqualTo(candidate);

        // A 7 × 7 finder whose light ring is filled in the two quadrants the rising diagonal crosses, clear of the centre row, the centre column and the falling diagonal
        static bool IsFinderModule(int row, int column, bool broken)
        {
            if (row < 0 || column < 0 || row >= 7 || column >= 7)
                return false;
            var ring = Math.Min(Math.Min(row, column), Math.Min(6 - row, 6 - column));
            if (ring != 1)
                return true;
            return broken && ((row < 3 && column > 3) || (row > 3 && column < 3));
        }
    }

    /// <summary>A mean of the four sizes is short by more than the timing match searches, and each line's own sizes count it right.</summary>
    [Test]
    [Arguments(40, 3.06f, 106.9f, 0.476f)]
    [Arguments(35, 3.64f, 2.9f, 0.5f)]
    public async Task DimensionFromEachLine_Decodes(int version, float pixelsPerModule, float degrees, float keystone)
        => await KeystoneFrameDecodeTest.AssertDecodes(version, pixelsPerModule, degrees, keystone, binarized: false);

    /// <summary>The selected triple holds a false candidate inside the symbol, and the next triple by confirmation reads its timing patterns and decodes.</summary>
    [Test]
    [Arguments(40, 4.11f, 268.9f, 0.444f)]
    [Arguments(35, 4f, 250.6f, 0.469f)]
    public async Task FalseCandidateInTheSelectedTriple_TheNextConfirmedTripleDecodes(int version, float pixelsPerModule, float degrees, float keystone)
    {
        var (luminance, width, height, truth, size) = Render(version, pixelsPerModule, degrees, keystone);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);

        // The premise: the triple the selection makes holds a candidate that is no drawn finder, and its timing patterns do not read
        var selected = new FinderPattern[3];
        await Assert.That(FinderPatternFinder.TryFind(luminance, width, height, threshold, selected, grey)).IsTrue();
        var falseMembers = 0;
        foreach (var candidate in selected)
        {
            if (NearestDrawnFinder(truth, size, candidate) >= 1.5f)
                falseMembers++;
        }
        await Assert.That(falseMembers).IsGreaterThan(0);
        QRImageDecoder.OrderFinderPatterns(selected, out var topLeft, out var topRight, out var bottomLeft);
        await Assert.That(QRImageDecoder.TimingPatternsRead(luminance, width, height, threshold, grey, topLeft, topRight, bottomLeft)).IsFalse();

        await KeystoneFrameDecodeTest.AssertDecodes(version, pixelsPerModule, degrees, keystone, binarized: false);
    }

    /// <summary>
    /// The stride pass selected from a third of the rows and returned, and the list it hands back counts each finder once or twice, so the best confirmed other triple holds a false candidate and does not read its timing patterns.
    /// The next one is the drawn triple, which reads them, and the symbol decodes: why a failed decode verifies two triples, not one.
    /// </summary>
    [Test]
    [Arguments(38, 3.45f, 93.3f, 0.466f)]
    public async Task FirstAlternativeFalse_TheSecondIsTheDrawnTriple(int version, float pixelsPerModule, float degrees, float keystone)
    {
        var (luminance, width, height, truth, size) = Render(version, pixelsPerModule, degrees, keystone);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var candidates = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var selected = new FinderPattern[3];
        await Assert.That(FinderPatternFinder.TryFind(luminance, width, height, threshold, selected, grey, candidates, out var count)).IsTrue();
        var (first, second) = FirstTwoAlternatives(candidates, count, selected);

        await Assert.That(first.Length == 3 && second.Length == 3).IsTrue();
        await Assert.That(first.Any(candidate => NearestDrawnFinder(truth, size, candidate) >= 1.5f)).IsTrue().Because("the first alternative has to miss for the second to be what reads");
        QRImageDecoder.OrderFinderPatterns(first, out var topLeft, out var topRight, out var bottomLeft);
        await Assert.That(QRImageDecoder.TimingPatternsRead(luminance, width, height, threshold, grey, topLeft, topRight, bottomLeft)).IsFalse();
        foreach (var candidate in second)
            await Assert.That(NearestDrawnFinder(truth, size, candidate)).IsLessThan(1.5f);
        QRImageDecoder.OrderFinderPatterns(second, out topLeft, out topRight, out bottomLeft);
        await Assert.That(QRImageDecoder.TimingPatternsRead(luminance, width, height, threshold, grey, topLeft, topRight, bottomLeft)).IsTrue();

        await KeystoneFrameDecodeTest.AssertDecodes(version, pixelsPerModule, degrees, keystone, binarized: false);

        static (FinderPattern[] First, FinderPattern[] Second) FirstTwoAlternatives(FinderPattern[] candidates, int count, FinderPattern[] selected)
        {
            var alternatives = new FinderPatternFinder.AlternativeTriples(candidates.AsSpan(0, count), selected);
            Span<FinderPattern> triple = stackalloc FinderPattern[3];
            var first = alternatives.TryNext(triple) ? triple.ToArray() : [];
            var second = alternatives.TryNext(triple) ? triple.ToArray() : [];
            return (first, second);
        }
    }

    /// <summary>A symbol's own triple reads its timing patterns through its own frame past 40 % keystone, where a triangle's shape says little.</summary>
    [Test]
    [Arguments(40, 4.11f, 268.9f, 0.444f)]
    [Arguments(25, 3.56f, 247.4f, 0.436f)]
    [Arguments(1, 5.21f, 337.2f, 0.471f)]
    public async Task DrawnTriple_ReadsItsTimingPatterns(int version, float pixelsPerModule, float degrees, float keystone)
    {
        var (luminance, width, height, truth, size) = Render(version, pixelsPerModule, degrees, keystone);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var candidates = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var count = FinderPatternFinder.FindCandidatesFullSweep(luminance, width, height, threshold, candidates, grey);
        var drawn = new FinderPattern[3];
        var finders = DrawnFinders(size);
        for (var f = 0; f < 3; f++)
        {
            var best = float.MaxValue;
            for (var i = 0; i < count; i++)
            {
                var off = ModulesFrom(truth, candidates[i], finders[f].U, finders[f].V);
                if (off < best)
                {
                    best = off;
                    drawn[f] = candidates[i];
                }
            }
            await Assert.That(best).IsLessThan(1.5f);
        }

        var read = QRImageDecoder.TimingPatternsRead(luminance, width, height, threshold, grey, drawn[0], drawn[1], drawn[2]);

        await Assert.That(read).IsTrue();
    }

    private static (byte[] Luminance, int Width, int Height, PerspectiveTransform Truth, int Size) Render(int version, float pixelsPerModule, float degrees, float keystone)
    {
        var qr = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version) });
        var (luminance, width, height) = SupersampledRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, pixelsPerModule, degrees, keystone);
        return (luminance, width, height, SupersampledGeometry.GridToPixel(qr.Size, qr.Size, pixelsPerModule, degrees, keystone), qr.Size);
    }

    /// <summary>The three finder centres in the grid of a symbol with the generator's 4-module quiet zone.</summary>
    private static (float U, float V)[] DrawnFinders(int size) => [(7.5f, 7.5f), (size - 7.5f, 7.5f), (7.5f, size - 7.5f)];

    private static float NearestModules(in PerspectiveTransform truth, ReadOnlySpan<FinderPattern> candidates, float u, float v)
    {
        var best = float.MaxValue;
        foreach (var candidate in candidates)
            best = Math.Min(best, ModulesFrom(truth, candidate, u, v));
        return best;
    }

    private static float NearestDrawnFinder(in PerspectiveTransform truth, int size, in FinderPattern candidate)
    {
        var best = float.MaxValue;
        foreach (var (u, v) in DrawnFinders(size))
            best = Math.Min(best, ModulesFrom(truth, candidate, u, v));
        return best;
    }

    /// <summary>How far a candidate is from grid point (<paramref name="u"/>, <paramref name="v"/>), in modules there.</summary>
    private static float ModulesFrom(in PerspectiveTransform truth, in FinderPattern candidate, float u, float v)
    {
        truth.Transform(u, v, out var x, out var y);
        truth.Transform(u + 1f, v, out var nextX, out var nextY);
        var module = MathF.Sqrt((nextX - x) * (nextX - x) + (nextY - y) * (nextY - y));
        return MathF.Sqrt((candidate.X - x) * (candidate.X - x) + (candidate.Y - y) * (candidate.Y - y)) / module;
    }

    private static int Sum(ReadOnlySpan<int> runs) => runs[0] + runs[1] + runs[2] + runs[3] + runs[4];
}
