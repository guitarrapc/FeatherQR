using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// Parity test for the run measurement of the finder cross-checks.
/// <see cref="FinderPatternFinder.MeasureRuns"/> walks a stepped reference with the runs in locals; <see cref="FinderPatternFinder.MeasureAxisRunsReference"/> and <see cref="FinderPatternFinder.MeasureDiagonalRunsReference"/> are the loops it replaced, kept verbatim, and <see cref="FinderPatternFinder.MeasureRisingDiagonalRunsReference"/> is the falling one written for the other diagonal.
/// The verdict on the runs is shared code, and on most centres it is a refusal whatever the runs were, so the walkers are compared on the runs themselves: the five lengths and where the walk ended.
/// </summary>
public class FinderRunWalkParityTest
{
    private const int Dirty = unchecked((int)0xA5A5A5A5);

    // Taller than wide, wider than tall, single rows and columns: a walker that takes a limit from the wrong dimension agrees with the reference on a square image
    private static readonly (int Width, int Height)[] sizes = [(53, 37), (37, 53), (16, 16), (1, 40), (40, 1), (1, 1), (7, 90)];

    [Test]
    public async Task MeasureRuns_MatchesReference_EveryCentreAxisAndCap()
    {
        var compared = 0L;
        var accepted = 0L;
        foreach (var (width, height) in sizes)
        {
            foreach (var threshold in new byte[] { 0, 1, 128, 255 })
            {
                foreach (var seed in new[] { 1, 42, 20260922 })
                {
                    var scene = BuildBlockScene(width, height, threshold, seed);
                    var mismatch = CompareEveryCentre(scene, width, height, threshold, ref compared, ref accepted);
                    await Assert.That(mismatch).IsNull().Because($"{width}x{height}, threshold={threshold}, seed={seed}");
                }
            }
        }

        // The scenes have to reach both outcomes, or the comparison above held nothing
        await Assert.That(accepted).IsGreaterThan(compared / 10);
        await Assert.That(compared - accepted).IsGreaterThan(compared / 100);
    }

    [Test]
    public async Task MeasureRuns_MatchesReference_UniformImages()
    {
        // All dark: every centre run reaches a border. All light: the centre run is empty and the light runs reach the borders.
        var compared = 0L;
        var accepted = 0L;
        foreach (var (width, height) in sizes)
        {
            foreach (var value in new byte[] { 0, 255 })
            {
                var scene = new byte[width * height];
                scene.AsSpan().Fill(value);
                var mismatch = CompareEveryCentre(scene, width, height, 128, ref compared, ref accepted);
                await Assert.That(mismatch).IsNull().Because($"{width}x{height}, value={value}");
            }
        }
    }

    [Test]
    public async Task MeasureRuns_MatchesReference_FinderCrossSections()
    {
        // The input the walkers exist for: a finder at several module sizes, walked from every pixel of its centre square, where all five runs are non-zero and the caps are not reached.
        var compared = 0L;
        var accepted = 0L;
        foreach (var ppm in new[] { 1, 2, 3, 5, 8 })
        {
            foreach (var (extraWidth, extraHeight) in new[] { (0, 0), (37, 3), (2, 41) })
            {
                var width = 9 * ppm + extraWidth;
                var height = 9 * ppm + extraHeight;
                var scene = new byte[width * height];
                scene.AsSpan().Fill(255);
                for (var my = 0; my < 7; my++)
                {
                    for (var mx = 0; mx < 7; mx++)
                    {
                        if (Math.Max(Math.Abs(mx - 3), Math.Abs(my - 3)) == 2)
                            continue;
                        for (var y = (my + 1) * ppm; y < (my + 2) * ppm; y++)
                            scene.AsSpan(y * width + (mx + 1) * ppm, ppm).Fill(0);
                    }
                }

                var mismatch = CompareEveryCentre(scene, width, height, 128, ref compared, ref accepted);
                await Assert.That(mismatch).IsNull().Because($"ppm={ppm}, {width}x{height}");

                // From the centre pixel every axis reads 1:1:3:1:1 in whole modules
                var runs = new int[5];
                foreach (var (stepX, stepY) in new[] { (0, 1), (1, 0), (1, 1), (1, -1) })
                {
                    // On the rising diagonal the centre square is crossed symmetrically from one row up when the module is an even number of pixels
                    var centre = 4 * ppm + ppm / 2;
                    var centreY = stepY == -1 ? centre - (1 - ppm % 2) : centre;
                    var ok = FinderPatternFinder.MeasureRuns(scene, width, height, 128, centre, centreY, stepX, stepY, FinderPatternFinder.NoRunCap, runs, out _);
                    await Assert.That(ok).IsTrue();
                    await Assert.That(runs).IsEquivalentTo(new[] { ppm, ppm, 3 * ppm, ppm, ppm }).Because($"ppm={ppm}, step=({stepX},{stepY})");
                }
            }
        }
    }

    [Test]
    public async Task TryFind_MatchesReferenceWalkers_NonSquareImages()
    {
        // End to end: TryFindScalar runs the reference walkers and the scalar row walk, TryFind the bounded axis walk, the stepped diagonal one and the vector row kernels. Real symbols on canvases that are not square, crisp and blurred, so the grey second look is reached through both.
        var fast = new FinderPattern[3];
        var reference = new FinderPattern[3];
        foreach (var version in new[] { 1, 4, 7 })
        {
            var qr = QRCodeGenerator.Create($"walk v{version}".AsSpan(), QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version) });
            // 20 px a module on the smallest symbol only: a finder's diagonal side runs are then longer than any cap an axis check would use, which is what shows the diagonal walk has none
            foreach (var ppm in version == 1 ? new[] { 2, 3, 4, 7, 20 } : [2, 3, 4, 7])
            {
                foreach (var (padX, padY) in new[] { (0, 0), (41, 3), (5, 67) })
                {
                    foreach (var blurred in new[] { false, true })
                    {
                        var scene = Render(qr, ppm, padX, padY, blurred, out var width, out var height);
                        var threshold = Binarizer.ComputeOtsuThreshold(scene, out var grey);

                        var fastFound = FinderPatternFinder.TryFind(scene, width, height, threshold, fast, grey);
                        var referenceFound = FinderPatternFinder.TryFindScalar(scene, width, height, threshold, reference, grey);

                        var label = $"version={version}, ppm={ppm}, pad=({padX},{padY}), blurred={blurred}";
                        // A 3 x 3 blur at 2 px a module is below what the finder reads; the two still have to agree on that
                        if (!blurred || ppm >= 3)
                            await Assert.That(referenceFound).IsTrue().Because($"{label}: the scene is not a symbol the finder reads");
                        await Assert.That(fastFound).IsEqualTo(referenceFound).Because(label);
                        for (var i = 0; referenceFound && i < 3; i++)
                        {
                            await Assert.That(fast[i].X == reference[i].X && fast[i].Y == reference[i].Y && fast[i].ModuleSize == reference[i].ModuleSize && fast[i].Count == reference[i].Count).IsTrue().Because($"{label}, pattern {i}");
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Both walkers from every pixel, along every axis, under every cap, onto dirty buffers.
    /// Returns the first disagreement, or null.
    /// </summary>
    private static string? CompareEveryCentre(byte[] scene, int width, int height, byte threshold, ref long compared, ref long accepted)
    {
        Span<int> fast = stackalloc int[5];
        Span<int> reference = stackalloc int[5];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                foreach (var (stepX, stepY) in new[] { (0, 1), (1, 0), (1, 1), (1, -1) })
                {
                    // The diagonal checks have no cap; the axis checks cap each side run at the expected total, which is 7 or more in a decode and anything here
                    var caps = stepX != 0 && stepY != 0 ? new[] { FinderPatternFinder.NoRunCap } : new[] { 0, 1, 2, 5, 21, 1000 };
                    foreach (var cap in caps)
                    {
                        fast.Fill(Dirty);
                        reference.Fill(Dirty);
                        var fastOk = FinderPatternFinder.MeasureRuns(scene, width, height, threshold, x, y, stepX, stepY, cap, fast, out var fastEnd);
                        var referenceOk = stepY == -1
                            ? FinderPatternFinder.MeasureRisingDiagonalRunsReference(scene, width, height, threshold, x, y, reference, out var referenceEnd)
                            : stepX == stepY
                            ? FinderPatternFinder.MeasureDiagonalRunsReference(scene, width, height, threshold, x, y, reference, out referenceEnd)
                            : FinderPatternFinder.MeasureAxisRunsReference(scene, width, height, threshold, x, y, vertical: stepX == 0, cap, reference, out referenceEnd);

                        compared++;
                        if (referenceOk)
                            accepted++;
                        if (fastOk != referenceOk)
                            return $"centre=({x},{y}), step=({stepX},{stepY}), cap={cap}: measured {fastOk}, reference {referenceOk}";
                        if (!referenceOk)
                            continue; // a centre run that reaches the border is refused before the runs are looked at
                        if (fastEnd != referenceEnd || !fast.SequenceEqual(reference))
                            return $"centre=({x},{y}), step=({stepX},{stepY}), cap={cap}: measured [{string.Join(",", fast.ToArray())}] end {fastEnd}, reference [{string.Join(",", reference.ToArray())}] end {referenceEnd}";
                    }
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Blocks of 1 to 4 px, dark or light, with levels on both sides of the threshold and next to it, so runs of every length up to a dozen pixels occur and the compare is held at its edge.
    /// </summary>
    private static byte[] BuildBlockScene(int width, int height, byte threshold, int seed)
    {
        var random = new Random(seed);
        var scene = new byte[width * height];
        byte[] levels = [0, (byte)Math.Max(threshold - 1, 0), threshold, (byte)Math.Min(threshold + 1, 255), 255];
        for (var y = 0; y < height;)
        {
            var blockHeight = random.Next(1, 5);
            for (var x = 0; x < width;)
            {
                var blockWidth = random.Next(1, 5);
                var level = levels[random.Next(levels.Length)];
                for (var by = y; by < Math.Min(y + blockHeight, height); by++)
                    scene.AsSpan(by * width + x, Math.Min(blockWidth, width - x)).Fill(level);
                x += blockWidth;
            }
            y += blockHeight;
        }
        return scene;
    }

    private static byte[] Render(QRCodeData qr, int ppm, int padX, int padY, bool blurred, out int width, out int height)
    {
        width = qr.Size * ppm + padX;
        height = qr.Size * ppm + padY;
        var crisp = new byte[width * height];
        crisp.AsSpan().Fill(255);
        for (var row = 0; row < qr.Size; row++)
        {
            for (var col = 0; col < qr.Size; col++)
            {
                if (!qr[row, col])
                    continue;
                for (var y = row * ppm; y < (row + 1) * ppm; y++)
                    crisp.AsSpan((padY / 2 + y) * width + padX / 2 + col * ppm, ppm).Fill(0);
            }
        }
        if (!blurred)
            return crisp;

        // A 3 x 3 box: grey edges, levels left apart
        var soft = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sum = 0;
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                        sum += crisp[Math.Clamp(y + dy, 0, height - 1) * width + Math.Clamp(x + dx, 0, width - 1)];
                soft[y * width + x] = (byte)(sum / 9);
            }
        }
        return soft;
    }
}
