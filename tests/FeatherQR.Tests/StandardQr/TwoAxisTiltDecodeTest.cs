using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.StandardQR;
using FeatherQR.SkiaSharp.Internals;
using SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// Symbols on a plane tilted about both of their axes, where the keystone runs toward one of the symbol's corners and every finder is drawn stretched off its own axes.
/// Tilted toward the top-left finder, the triangle of the three centres stops being right-angled in the image: a leg outgrows the hypotenuse past about 43 % keystone, and the vertex opposite the longest side is another finder.
/// </summary>
public class TwoAxisTiltDecodeTest
{
    private const string Content = "FQR KEYSTONE 0123";

    /// <summary>The corner the timing patterns name decodes where the triangle's shape names another.</summary>
    [Test]
    [Arguments(9, 4.81f, 310.1f, 0.499f, 214.4f)]
    [Arguments(14, 5.05f, 259.1f, 0.492f, 224.8f)]
    [Arguments(21, 3.84f, 84.5f, 0.48f, 236.2f)]
    [Arguments(30, 5.96f, 289.8f, 0.488f, 243.7f)]
    [Arguments(37, 4.27f, 230.9f, 0.447f, 231.1f)]
    [Arguments(40, 4.25f, 341.3f, 0.485f, 207.9f)]
    public async Task TiltTowardTheTopLeftFinder_Decodes(int version, float pixelsPerModule, float degrees, float keystone, float tiltDegrees)
        => await KeystoneFrameDecodeTest.AssertDecodes(version, pixelsPerModule, degrees, keystone, binarized: false, tiltDegrees);

    /// <summary>A photograph tilted steeply toward one of its finders, from zxing-cpp's black-box samples (Apache-2.0), at the four right angles the sweep reads it at.</summary>
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task RealImageTiltedTowardAFinder_Decodes(int quarterTurns)
    {
        var path = Path.Combine(FixtureLoader.FixtureRoot, "RealImages", "zxing-cpp-samples", "qrcode-2", "fix-finderpattern-order.webp");
        var expected = File.ReadAllText(Path.ChangeExtension(path, ".txt"));
        using var bitmap = SKBitmap.Decode(path);
        var luminance = new byte[bitmap.Width * bitmap.Height];
        BitmapLuminanceConverter.Convert(bitmap, luminance);
        var (turned, width, height) = NearestNeighbourRenderer.Turn(luminance, bitmap.Width, bitmap.Height, quarterTurns, mirror: false);

        var success = QRCodeDecoder.TryDecodeImage(turned, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"turned {quarterTurns * 90}°: {info.Status}");
        await Assert.That(text).IsEqualTo(expected);
    }

    /// <summary>
    /// The premise of the decodes above: the drawn triple, a leg of whose triangle is longer than its hypotenuse, so that the shape names another corner, and whose timing patterns read only from the drawn corner.
    /// </summary>
    [Test]
    [Arguments(14, 5.05f, 259.1f, 0.492f, 224.8f)]
    [Arguments(40, 4.25f, 341.3f, 0.485f, 207.9f)]
    public async Task TiltTowardTheTopLeftFinder_OnlyTheDrawnCornersTimingPatternsRead(int version, float pixelsPerModule, float degrees, float keystone, float tiltDegrees)
    {
        var (luminance, width, height, truth, size) = Render(version, pixelsPerModule, degrees, keystone, tiltDegrees, damaged: false);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var drawn = await DrawnTriple(luminance, width, height, threshold, grey, truth, size);

        var hypotenuse = Distance(drawn[1], drawn[2]);
        await Assert.That(Math.Max(Distance(drawn[0], drawn[1]), Distance(drawn[0], drawn[2]))).IsGreaterThan(hypotenuse);
        QRImageDecoder.OrderFinderPatterns(drawn, out var shapeTopLeft, out var shapeTopRight, out var shapeBottomLeft);
        await Assert.That(shapeTopLeft.X == drawn[0].X && shapeTopLeft.Y == drawn[0].Y).IsFalse();
        await Assert.That(QRImageDecoder.TimingPatternsRead(luminance, width, height, threshold, grey, shapeTopLeft, shapeTopRight, shapeBottomLeft)).IsFalse();
        await Assert.That(QRImageDecoder.TimingPatternsRead(luminance, width, height, threshold, grey, drawn[0], drawn[1], drawn[2])).IsTrue();
    }

    /// <summary>The corner the timing patterns name, whichever order the triple comes in; a triple no corner's timing patterns read has none.</summary>
    [Test]
    [Arguments(9, 4.81f, 310.1f, 0.499f, 214.4f)]
    [Arguments(30, 5.96f, 289.8f, 0.488f, 243.7f)]
    public async Task CornerByTimingPatterns_IsTheDrawnCornerInAnyOrder(int version, float pixelsPerModule, float degrees, float keystone, float tiltDegrees)
    {
        var (luminance, width, height, truth, size) = Render(version, pixelsPerModule, degrees, keystone, tiltDegrees, damaged: false);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var drawn = await DrawnTriple(luminance, width, height, threshold, grey, truth, size);

        int[][] orders = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];
        foreach (var order in orders)
        {
            FinderPattern[] triple = [drawn[order[0]], drawn[order[1]], drawn[order[2]]];
            var found = QRImageDecoder.TryCornerByTimingPatterns(luminance, width, height, threshold, grey, triple, out var topLeft, out var topRight, out var bottomLeft);

            await Assert.That(found).IsTrue();
            await Assert.That(topLeft).IsEqualTo(drawn[0]);
            await Assert.That(topRight).IsEqualTo(drawn[1]);
            await Assert.That(bottomLeft).IsEqualTo(drawn[2]);
        }

        // Moved half the symbol along the image rows, the three centres frame no timing pattern from any corner
        var shift = Distance(drawn[0], drawn[1]) / 2f;
        FinderPattern[] moved = [.. drawn.Select(finder => finder with { X = finder.X + shift })];
        await Assert.That(QRImageDecoder.TryCornerByTimingPatterns(luminance, width, height, threshold, grey, moved, out _, out _, out _)).IsFalse();
    }

    /// <summary>
    /// The selected triple holds a false candidate, and the drawn triple, next by confirmation, is one whose shape names another corner: its corner comes from its timing patterns too.
    /// </summary>
    [Test]
    [Arguments(24, 5.5f, 220f, 0.486f, 205f)]
    public async Task FalseCandidateSelected_TheDrawnTriplesCornerFromItsTimingPatterns_Decodes(int version, float pixelsPerModule, float degrees, float keystone, float tiltDegrees)
    {
        var (luminance, width, height, truth, size) = Render(version, pixelsPerModule, degrees, keystone, tiltDegrees, damaged: false);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);

        // The premise: a false candidate in the selected triple, and a drawn triple whose leg is longer than its hypotenuse
        var selected = new FinderPattern[3];
        await Assert.That(FinderPatternFinder.TryFind(luminance, width, height, threshold, selected, grey)).IsTrue();
        await Assert.That(selected.Any(candidate => DrawnFinders(size).All(finder => ModulesFrom(truth, candidate, finder.U, finder.V) >= 1.5f))).IsTrue();
        var drawn = await DrawnTriple(luminance, width, height, threshold, grey, truth, size);
        await Assert.That(Math.Max(Distance(drawn[0], drawn[1]), Distance(drawn[0], drawn[2]))).IsGreaterThan(Distance(drawn[1], drawn[2]));

        await KeystoneFrameDecodeTest.AssertDecodes(version, pixelsPerModule, degrees, keystone, binarized: false, tiltDegrees);
    }

    /// <summary>On a flat symbol the timing patterns read from its corner and from neither other vertex, so a symbol whose shape names its corner never decodes from another.</summary>
    [Test]
    [Arguments(2, 4f, 17f)]
    [Arguments(25, 3.5f, 200f)]
    [Arguments(40, 3f, 311f)]
    public async Task FlatSymbol_TimingPatternsReadFromItsCornerOnly(int version, float pixelsPerModule, float degrees)
    {
        var (luminance, width, height, truth, size) = Render(version, pixelsPerModule, degrees, 0f, 0f, damaged: false);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var drawn = await DrawnTriple(luminance, width, height, threshold, grey, truth, size);

        await Assert.That(QRImageDecoder.TimingPatternsRead(luminance, width, height, threshold, grey, drawn[0], drawn[1], drawn[2])).IsTrue();
        // Each other vertex as the corner, the remaining two ordered as the cross product orders them
        await Assert.That(QRImageDecoder.TimingPatternsRead(luminance, width, height, threshold, grey, drawn[1], drawn[2], drawn[0])).IsFalse();
        await Assert.That(QRImageDecoder.TimingPatternsRead(luminance, width, height, threshold, grey, drawn[2], drawn[0], drawn[1])).IsFalse();
    }

    /// <summary>The corner the timing patterns name is one more grid, not a looser read: with its data destroyed the same render does not decode.</summary>
    [Test]
    [Arguments(14, 5.05f, 259.1f, 0.492f, 224.8f)]
    [Arguments(40, 4.25f, 341.3f, 0.485f, 207.9f)]
    public async Task TiltTowardTheTopLeftFinder_DataDestroyed_DoesNotDecode(int version, float pixelsPerModule, float degrees, float keystone, float tiltDegrees)
    {
        var (luminance, width, height, _, _) = Render(version, pixelsPerModule, degrees, keystone, tiltDegrees, damaged: true);

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsFalse().Because(info.Status.ToString());
        await Assert.That(text).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// A finder stretched off its own axes by a tilt about both, drawn over 2.2 times as tall as wide by whole pixels, at 3.1 and 3.8 px/module: a candidate, its column taken inside 5/12 to 12/5 of its row once its rising diagonal reads.
    /// A stretch along a finder's axes leaves the ratio at the stretch; turned off them it rises, and past a 50 % keystone's stretch of 2 the window's fifth for whole pixels is what keeps it.
    /// </summary>
    [Test]
    [Arguments(3.1f, 25f, 101f)]
    [Arguments(3.8f, 26f, 100f)]
    public async Task StretchedOffItsAxes_ColumnPastElevenFifthsOfTheRow_IsACandidate(float pixelsPerModule, float degrees, float tiltDegrees)
    {
        var (luminance, width, height, truth, size) = Render(40, pixelsPerModule, degrees, 0.5f, tiltDegrees, damaged: false);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);

        // The premise: at the top-right finder the column is past 11/5 of the row, each reading 1:1:3:1:1 on its own
        truth.Transform(size - 7.5f, 7.5f, out var x, out var y);
        var row = new int[5];
        var column = new int[5];
        await Assert.That(FinderPatternFinder.MeasureRuns(luminance, width, height, threshold, (int)x, (int)y, 1, 0, FinderPatternFinder.NoRunCap, row, out _)).IsTrue();
        await Assert.That(FinderPatternFinder.MeasureRuns(luminance, width, height, threshold, (int)x, (int)y, 0, 1, FinderPatternFinder.NoRunCap, column, out _)).IsTrue();
        await Assert.That(FinderPatternFinder.IsFinderRatio(row) && FinderPatternFinder.IsFinderRatio(column)).IsTrue();
        await Assert.That(5 * Sum(column)).IsGreaterThan(11 * Sum(row));

        var candidates = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var count = FinderPatternFinder.FindCandidatesFullSweep(luminance, width, height, threshold, candidates, grey);

        foreach (var (u, v) in DrawnFinders(size))
            await Assert.That(NearestModules(truth, candidates.AsSpan(0, count), u, v)).IsLessThan(1.5f).Because($"the drawn finder at grid ({u}, {v})");
    }

    /// <summary>The renderer's tilted plane, checked against its own pixels: every module centre through the geometry it reports lands on that module's colour.</summary>
    [Test]
    [Arguments(10, 3f, 0.5f, 45f)]
    [Arguments(25, 4f, 0.3f, 101f)]
    [Arguments(40, 3.5f, 0.45f, 225f)]
    [Arguments(1, 6f, 0.5f, 300f)]
    public async Task Renderer_TiltedPlane_MatchesItsGeometry(int version, float pixelsPerModule, float keystone, float tiltDegrees)
    {
        var qr = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version) });
        var (luminance, width, _) = SupersampledRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, pixelsPerModule, 17f, keystone, tiltDegrees);
        var truth = SupersampledGeometry.GridToPixel(qr.Size, qr.Size, pixelsPerModule, 17f, keystone, tiltDegrees);

        var wrong = 0;
        for (var row = 0; row < qr.Size; row++)
        {
            for (var column = 0; column < qr.Size; column++)
            {
                truth.Transform(column + 0.5f, row + 0.5f, out var x, out var y);
                if (luminance[(int)y * width + (int)x] < 128 != qr[row, column])
                    wrong++;
            }
        }

        await Assert.That(wrong).IsEqualTo(0);
    }

    private static (byte[] Luminance, int Width, int Height, PerspectiveTransform Truth, int Size) Render(int version, float pixelsPerModule, float degrees, float keystone, float tiltDegrees, bool damaged)
    {
        var qr = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version) });
        var size = qr.Size;
        // Every module right of column 8 and below row 8 of the symbol, clear of the finders, the timing patterns and both format copies
        bool IsDark(int row, int column) => damaged && row >= 13 && column >= 13 && row < size - 4 && column < size - 4 ? !qr[row, column] : qr[row, column];
        var (luminance, width, height) = SupersampledRenderer.Render(IsDark, size, size, pixelsPerModule, degrees, keystone, tiltDegrees);
        return (luminance, width, height, SupersampledGeometry.GridToPixel(size, size, pixelsPerModule, degrees, keystone, tiltDegrees), size);
    }

    /// <summary>The full sweep's candidate nearest each drawn finder, top-left, top-right, bottom-left; each within 1.5 modules.</summary>
    private static async Task<FinderPattern[]> DrawnTriple(byte[] luminance, int width, int height, byte threshold, GreyLevels grey, PerspectiveTransform truth, int size)
    {
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
        return drawn;
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

    /// <summary>How far a candidate is from grid point (<paramref name="u"/>, <paramref name="v"/>), in modules there.</summary>
    private static float ModulesFrom(in PerspectiveTransform truth, in FinderPattern candidate, float u, float v)
    {
        truth.Transform(u, v, out var x, out var y);
        truth.Transform(u + 1f, v, out var nextX, out var nextY);
        var module = MathF.Sqrt((nextX - x) * (nextX - x) + (nextY - y) * (nextY - y));
        return MathF.Sqrt((candidate.X - x) * (candidate.X - x) + (candidate.Y - y) * (candidate.Y - y)) / module;
    }

    private static float Distance(in FinderPattern a, in FinderPattern b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static int Sum(ReadOnlySpan<int> runs) => runs[0] + runs[1] + runs[2] + runs[3] + runs[4];
}
