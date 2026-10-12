using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// A symbol with one finder gone, under glare, damage or an odd drawing: the two finders found imply where the third is, and the symbol reads from the three.
/// The third is the perpendicular of the pair's edge from one of them, or half of it from the pair's midpoint when the pair is the diagonal. Under perspective that puts it a module or two off, and a search of the timing patterns around it settles the centre.
/// </summary>
public class InferredFinderDecodeTest
{
    internal const string Content = "FQR INFERRED FINDER 0123";

    public enum Finder
    {
        TopLeft,
        TopRight,
        BottomLeft,
    }

    /// <summary>Each finder in turn blanked to the ground, upright and turned: the two others are candidates and the blanked one is not, no triple is selected, and the symbol reads through the inferred third.</summary>
    [Test]
    [Arguments(Finder.TopLeft, 0f)]
    [Arguments(Finder.TopRight, 0f)]
    [Arguments(Finder.BottomLeft, 0f)]
    [Arguments(Finder.TopLeft, 200f)]
    [Arguments(Finder.TopRight, 335f)]
    [Arguments(Finder.BottomLeft, 110f)]
    public async Task BlankedFinder_ReadsFromTheOtherTwo(Finder blanked, float degrees)
    {
        var (luminance, width, height, truth, size) = Render(blanked, degrees, keystone: 0f);
        await AssertPremises(luminance, width, height, truth, size, blanked);
        await AssertReads(luminance, width, height, truth, size, blanked, inferredTolerance: 0.5f);
    }

    /// <summary>
    /// Under keystone the perpendicular construction lands further from the drawn centre than the centre refinement moves a finder (three quarters of a module), and the symbol still reads: the search of the timing patterns around it.
    /// The frame is flat along the lines to the inferred finder, so it follows the symbol less as the keystone grows. For that reason the corner reported at the inferred finder is held to three modules here, not the half module of the other three.
    /// </summary>
    [Test]
    [Arguments(Finder.TopLeft, 15f, 0.18f)]
    [Arguments(Finder.BottomLeft, 200f, 0.18f)]
    [Arguments(Finder.TopRight, 300f, 0.18f)]
    public async Task BlankedFinder_UnderKeystone_ReadsThroughTheSearch(Finder blanked, float degrees, float keystone)
    {
        var (luminance, width, height, truth, size) = Render(blanked, degrees, keystone);
        await AssertPremises(luminance, width, height, truth, size, blanked);

        // The premise the search exists for: the construction from the drawn centres is out by more than the refinement moves a centre
        var drawn = DrawnFinders(size).Select(f => Pixel(truth, f.U, f.V)).ToArray();
        var module = ModulePixels(truth, size);
        var constructed = Construct(drawn, blanked);
        var off = MathF.Sqrt((constructed.X - drawn[(int)blanked].X) * (constructed.X - drawn[(int)blanked].X) + (constructed.Y - drawn[(int)blanked].Y) * (constructed.Y - drawn[(int)blanked].Y)) / module;
        await Assert.That(off).IsGreaterThan(0.75f).Because($"the constructed centre is {off:F2} modules from the drawn one");

        await AssertReads(luminance, width, height, truth, size, blanked, inferredTolerance: 3f);
    }

    /// <summary>
    /// Renders the quarter-module search reads and the module-step search alone does not.
    /// </summary>
    [Test]
    [Arguments(Finder.TopRight, 60f, 0.18f)]
    [Arguments(Finder.BottomLeft, 180f, 0.20f)]
    [Arguments(Finder.TopLeft, 135f, 0.16f)]
    public async Task BlankedFinder_UnderKeystone_ReadsThroughTheFineSearch(Finder blanked, float degrees, float keystone)
    {
        var (luminance, width, height, truth, size) = Render(blanked, degrees, keystone);
        await AssertPremises(luminance, width, height, truth, size, blanked);
        await AssertReads(luminance, width, height, truth, size, blanked, inferredTolerance: 3f);
    }

    /// <summary>The negative the inference must not admit: the same symbol with its data destroyed reads nothing.</summary>
    [Test]
    [Arguments(Finder.TopLeft, 0f, 0f)]
    [Arguments(Finder.BottomLeft, 200f, 0.18f)]
    public async Task BlankedFinderAndDestroyedData_ReadsNothing(Finder blanked, float degrees, float keystone)
    {
        var (luminance, width, height, _, _) = Render(blanked, degrees, keystone, destroyData: true);

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out _, out var info);

        await Assert.That(success).IsFalse().Because(info.Status.ToString());
    }

    private static async Task AssertPremises(byte[] luminance, int width, int height, PerspectiveTransform truth, int size, Finder blanked)
    {
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var candidates = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var count = FinderPatternFinder.FindCandidatesFullSweep(luminance, width, height, threshold, candidates, grey);
        var module = ModulePixels(truth, size);
        foreach (var (finder, (u, v)) in DrawnFinders(size).Select((f, i) => ((Finder)i, f)))
        {
            var nearest = Nearest(candidates.AsSpan(0, count), Pixel(truth, u, v)) / module;
            if (finder == blanked)
                await Assert.That(nearest).IsGreaterThan(2.5f).Because($"the blanked {finder} finder is not a candidate");
            else
                await Assert.That(nearest).IsLessThan(1.5f).Because($"the drawn {finder} finder is a candidate");
        }

        var patterns = new FinderPattern[3];
        await Assert.That(FinderPatternFinder.TryFind(luminance, width, height, threshold, patterns, grey)).IsFalse().Because("two finders select no triple");
    }

    /// <summary>The symbol reads, with the corners within half a module of the drawn ones but the corner at the blanked finder, held to <paramref name="inferredTolerance"/>.</summary>
    private static async Task AssertReads(byte[] luminance, int width, int height, PerspectiveTransform truth, int size, Finder blanked, float inferredTolerance)
    {
        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because(info.Status.ToString());
        await Assert.That(text).IsEqualTo(Content);

        // The symbol's module area sits inside the generator's quiet zone
        var module = ModulePixels(truth, size);
        var near = (float)QuietZone;
        var far = size - QuietZone;
        await AssertNear(truth, info.Corners.TopLeft, near, near, module, blanked == Finder.TopLeft ? inferredTolerance : 0.5f);
        await AssertNear(truth, info.Corners.TopRight, far, near, module, blanked == Finder.TopRight ? inferredTolerance : 0.5f);
        await AssertNear(truth, info.Corners.BottomRight, far, far, module, 0.5f);
        await AssertNear(truth, info.Corners.BottomLeft, near, far, module, blanked == Finder.BottomLeft ? inferredTolerance : 0.5f);
    }

    private static async Task AssertNear(PerspectiveTransform truth, ImagePoint actual, float u, float v, float module, float tolerance)
    {
        var (x, y) = Pixel(truth, u, v);
        var off = MathF.Sqrt((actual.X - x) * (actual.X - x) + (actual.Y - y) * (actual.Y - y)) / module;
        await Assert.That(off).IsLessThan(tolerance).Because($"corner at grid ({u}, {v}) reported ({actual.X}, {actual.Y}), drawn ({x}, {y})");
    }

    /// <summary>The generator's quiet zone, so the symbol's module area starts at module 4 of its grid.</summary>
    private const int QuietZone = 4;

    /// <summary>
    /// The version 3 symbol at 6 px/module with the finder and its separator blanked to the ground. With <paramref name="destroyData"/>, every module past the finders' rows and columns but the alignment pattern is drawn at random too.
    /// </summary>
    internal static (byte[] Luminance, int Width, int Height, PerspectiveTransform Truth, int Size) Render(Finder blanked, float degrees, float keystone, bool destroyData = false)
    {
        var qr = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(3) });
        var size = qr.Size;
        var random = new Random((int)blanked * 7919 + (int)(degrees * 10f));
        var destroyed = new bool[size * size];
        for (var i = 0; i < destroyed.Length; i++)
            destroyed[i] = random.Next(2) == 0;
        bool IsDark(int row, int column)
        {
            if (InFinder(blanked, row, column, size))
                return false;
            // Version 3's alignment pattern is centred on module 22
            var alignment = QuietZone + 22;
            if (destroyData && row >= QuietZone + 9 && column >= QuietZone + 9 && (Math.Abs(row - alignment) > 2 || Math.Abs(column - alignment) > 2))
                return destroyed[row * size + column];
            return qr[row, column];
        }
        var (luminance, width, height) = SupersampledRenderer.Render(IsDark, size, size, 6f, degrees, keystone);
        return (luminance, width, height, SupersampledGeometry.GridToPixel(size, size, 6f, degrees, keystone), size);
    }

    /// <summary>The finder's 7 by 7 modules and the separator round them, past the quiet zone.</summary>
    private static bool InFinder(Finder finder, int row, int column, int size) => finder switch
    {
        Finder.TopLeft => row < QuietZone + 8 && column < QuietZone + 8,
        Finder.TopRight => row < QuietZone + 8 && column >= size - QuietZone - 8,
        _ => row >= size - QuietZone - 8 && column < QuietZone + 8,
    };

    /// <summary>The three finder centres in the grid of the symbol with its quiet zone: top-left, top-right, bottom-left.</summary>
    private static (float U, float V)[] DrawnFinders(int size) => [(QuietZone + 3.5f, QuietZone + 3.5f), (size - QuietZone - 3.5f, QuietZone + 3.5f), (QuietZone + 3.5f, size - QuietZone - 3.5f)];

    /// <summary>The missing centre as the inference constructs it before any search: the perpendicular of the pair's edge from the top-left finder, or half of it from the diagonal pair's midpoint.</summary>
    private static (float X, float Y) Construct((float X, float Y)[] drawn, Finder blanked)
    {
        var (tl, tr, bl) = (drawn[0], drawn[1], drawn[2]);
        switch (blanked)
        {
            case Finder.TopLeft:
                {
                    var (dx, dy) = (bl.X - tr.X, bl.Y - tr.Y);
                    var (mx, my) = ((tr.X + bl.X) / 2f, (tr.Y + bl.Y) / 2f);
                    return Nearer(drawn[0], (mx - dy / 2f, my + dx / 2f), (mx + dy / 2f, my - dx / 2f));
                }
            case Finder.TopRight:
                {
                    var (dx, dy) = (bl.X - tl.X, bl.Y - tl.Y);
                    return Nearer(drawn[1], (tl.X - dy, tl.Y + dx), (tl.X + dy, tl.Y - dx));
                }
            default:
                {
                    var (dx, dy) = (tr.X - tl.X, tr.Y - tl.Y);
                    return Nearer(drawn[2], (tl.X - dy, tl.Y + dx), (tl.X + dy, tl.Y - dx));
                }
        }

        static (float X, float Y) Nearer((float X, float Y) to, (float X, float Y) a, (float X, float Y) b)
            => (a.X - to.X) * (a.X - to.X) + (a.Y - to.Y) * (a.Y - to.Y) <= (b.X - to.X) * (b.X - to.X) + (b.Y - to.Y) * (b.Y - to.Y) ? a : b;
    }

    private static (float X, float Y) Pixel(in PerspectiveTransform truth, float u, float v)
    {
        truth.Transform(u, v, out var x, out var y);
        return (x, y);
    }

    /// <summary>The module's length in pixels at the symbol's centre.</summary>
    private static float ModulePixels(in PerspectiveTransform truth, int size)
    {
        var centre = size / 2f;
        var (x, y) = Pixel(truth, centre, centre);
        var (nextX, nextY) = Pixel(truth, centre + 1f, centre);
        return MathF.Sqrt((nextX - x) * (nextX - x) + (nextY - y) * (nextY - y));
    }

    private static float Nearest(ReadOnlySpan<FinderPattern> candidates, (float X, float Y) point)
    {
        var best = float.MaxValue;
        foreach (var candidate in candidates)
            best = Math.Min(best, MathF.Sqrt((candidate.X - point.X) * (candidate.X - point.X) + (candidate.Y - point.Y) * (candidate.Y - point.Y)));
        return best;
    }
}
