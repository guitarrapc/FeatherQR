using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// Parity test for the bounded walk of the axis cross-checks.
/// <see cref="FinderPatternFinder.MeasureRunsBounded"/> measures the centre run first and gives the walk up the moment a run passes what any accepted cross section allows; the reference walks all five runs and lets the verdict refuse.
/// The two may differ only where the verdict is a refusal, so they are compared at the cross-check itself (the returned centre and total, bit for bit) and at the walk (equal runs whenever the bounded walk finishes, a refusal by the reference whenever it does not).
/// </summary>
public class FinderCrossCheckParityTest
{
    // Columns are independent run sequences, so the image is read along its columns; the row walk reads the same sequences through the transposed image.
    private static readonly (int Lines, int Length)[] sizes = [(41, 97), (97, 41), (8, 160)];

    [Test]
    public async Task CrossCheck_BoundedWalkMatchesReference_CrispCrossSections()
    {
        var compared = 0L;
        var accepted = 0L;
        var handedBack = 0L;
        foreach (var (lines, length) in sizes)
        {
            foreach (var module in new[] { 1, 2, 3, 4, 7 })
            {
                foreach (var seed in new[] { 3, 17 })
                {
                    var columns = BuildRunColumns(lines, length, module, seed);
                    var rows = Transpose(columns, lines, length);
                    foreach (var expected in ExpectedTotals(module))
                    {
                        var mismatch = CompareEveryCentre(columns, lines, length, 128, default, vertical: true, expected, ref compared, ref accepted, ref handedBack)
                            ?? CompareEveryCentre(rows, length, lines, 128, default, vertical: false, expected, ref compared, ref accepted, ref handedBack);
                        await Assert.That(mismatch).IsNull().Because($"{lines} lines of {length}, module={module}, seed={seed}, expected={expected}");
                    }
                }
            }
        }

        // Both verdicts have to occur in number, or the comparison held nothing
        await Assert.That(accepted).IsGreaterThan(5_000);
        await Assert.That(compared - accepted).IsGreaterThan(100_000);
        // The mode that hands its runs back accepts small crisp runs only, which the 1 px a module scenes are made of
        await Assert.That(handedBack).IsGreaterThan(500);
    }

    [Test]
    public async Task CrossCheck_BoundedWalkMatchesReference_GreyEdges()
    {
        // With the grey levels of the image, near misses are measured again from coverage, the one accept path the bounds were not derived from directly.
        // The box-filtered columns are what reach it: their whole-pixel runs miss the ratio by a rounding their edges keep. The blur along the line
        // spreads each edge evenly over the pixels beside it, so coverage measures the whole-pixel runs again; those columns hold the grey refusals.
        var compared = 0L;
        var handedBack = 0L;
        var acceptedGrey = new long[2]; // columns, rows
        var acceptedCrisp = new long[2];
        foreach (var (lines, length) in sizes)
        {
            foreach (var module in new[] { 2, 3, 4 })
            {
                // Only a window whose total is 20 px or less can be a near miss the strict ratio refuses, so the second look is reached
                // at the smallest pitches alone: the box-filtered scenes are built at several of them, the blurred ones hold the refusals.
                var blurred = BlurAlongColumns(BuildRunColumns(lines, length, module, seed: 5 + module), lines, length);
                var boxFiltered = BuildBoxFilteredColumns(lines, length, 1.4f + module * 0.2f, seed: 11 + module);
                var boxFilteredWider = BuildBoxFilteredColumns(lines, length, 1.8f + module * 0.2f, seed: 29 + module);
                foreach (var columns in new[] { blurred, boxFiltered, boxFilteredWider })
                {
                    var rows = Transpose(columns, lines, length);
                    var threshold = Binarizer.ComputeOtsuThreshold(columns, out var grey);
                    await Assert.That(grey.IsEnabled).IsTrue();

                    foreach (var expected in ExpectedTotals(module))
                    {
                        var mismatch = CompareEveryCentre(columns, lines, length, threshold, grey, vertical: true, expected, ref compared, ref acceptedGrey[0], ref handedBack)
                            ?? CompareEveryCentre(rows, length, lines, threshold, grey, vertical: false, expected, ref compared, ref acceptedGrey[1], ref handedBack);
                        await Assert.That(mismatch).IsNull().Because($"{lines} lines of {length}, module={module}, expected={expected}, grey");

                        // The same centres with the grey levels off, which is also where these scenes are held to the reference without them
                        var unused = 0L;
                        var crispMismatch = CompareEveryCentre(columns, lines, length, threshold, default, vertical: true, expected, ref unused, ref acceptedCrisp[0], ref unused)
                            ?? CompareEveryCentre(rows, length, lines, threshold, default, vertical: false, expected, ref unused, ref acceptedCrisp[1], ref unused);
                        await Assert.That(crispMismatch).IsNull().Because($"{lines} lines of {length}, module={module}, expected={expected}, grey off");
                    }
                }
            }
        }

        // The second look has to have turned refusals into acceptances on both axes, or it was never reached: the same centres
        // with and without the grey levels, which can only add acceptances. The rows read the transposed image, so the two
        // counts are the same number reached through the other walk, and each is held on its own.
        await Assert.That(acceptedGrey[0] - acceptedCrisp[0]).IsGreaterThan(500);
        await Assert.That(acceptedGrey[1] - acceptedCrisp[1]).IsGreaterThan(500);
        // Both verdicts have to occur in number here too, and the mode that hands its runs back has to be reached
        await Assert.That(compared - acceptedGrey[0] - acceptedGrey[1]).IsGreaterThan(100_000);
        await Assert.That(handedBack).IsGreaterThan(500);
    }

    [Test]
    public async Task MeasureRunsBounded_FinishesWithTheReferenceRuns_OrTheReferenceIsRefused()
    {
        var finished = 0L;
        var givenUp = 0L;
        string? first = null;
        Span<int> bounded = stackalloc int[5];
        Span<int> reference = stackalloc int[5];
        foreach (var (lines, length) in sizes)
        {
            foreach (var module in new[] { 1, 2, 3, 5 })
            {
                var columns = BuildRunColumns(lines, length, module, seed: 29 + module);
                var rows = Transpose(columns, lines, length);
                foreach (var expected in ExpectedTotals(module))
                {
                    foreach (var vertical in new[] { true, false })
                    {
                        var image = vertical ? columns : rows;
                        var width = vertical ? lines : length;
                        var height = vertical ? length : lines;
                        for (var y = 0; y < height; y++)
                        {
                            for (var x = 0; x < width; x++)
                            {
                                bounded.Fill(unchecked((int)0xA5A5A5A5));
                                var ok = FinderPatternFinder.MeasureRunsBounded(image, width, height, 128, x, y, vertical ? 0 : 1, vertical ? 1 : 0, expected, bounded, out var end);
                                var referenceOk = FinderPatternFinder.MeasureAxisRunsReference(image, width, height, 128, x, y, vertical, expected, reference, out var referenceEnd);
                                if (ok)
                                {
                                    finished++;
                                    if (!referenceOk || end != referenceEnd || !bounded.SequenceEqual(reference))
                                        first ??= $"module={module}, expected={expected}, vertical={vertical}, centre=({x},{y}): finished with [{string.Join(",", bounded.ToArray())}] end {end}, reference {referenceOk} [{string.Join(",", reference.ToArray())}] end {referenceEnd}";
                                }
                                else
                                {
                                    givenUp++;
                                    if (referenceOk && FinderRunBoundsTest.CouldBeAccepted(expected, reference))
                                        first ??= $"module={module}, expected={expected}, vertical={vertical}, centre=({x},{y}): gave up on [{string.Join(",", reference.ToArray())}], which the verdict could accept";
                                }
                            }
                        }
                    }
                }
            }
        }

        await Assert.That(first).IsNull();
        await Assert.That(finished).IsGreaterThan(10_000);
        await Assert.That(givenUp).IsGreaterThan(100_000);
    }

    /// <summary>
    /// The cross-check through both walks from every pixel, in its ordinary mode and in the small-crisp mode that hands its runs back.
    /// Returns the first disagreement, or null.
    /// </summary>
    private static string? CompareEveryCentre(byte[] image, int width, int height, byte threshold, GreyLevels grey, bool vertical, int expected, ref long compared, ref long accepted, ref long acceptedHandingRunsBack)
    {
        Span<int> boundedRuns = stackalloc int[5];
        Span<int> referenceRuns = stackalloc int[5];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                foreach (var crispMode in new[] { false, true })
                {
                    boundedRuns.Clear();
                    referenceRuns.Clear();
                    var bounded = FinderPatternFinder.CrossCheck(image, width, height, threshold, grey, x, y, vertical, expected, referenceWalk: false, out var boundedTotal, crispMode ? boundedRuns : default);
                    var reference = FinderPatternFinder.CrossCheck(image, width, height, threshold, grey, x, y, vertical, expected, referenceWalk: true, out var referenceTotal, crispMode ? referenceRuns : default);

                    compared++;
                    if (BitConverter.SingleToInt32Bits(bounded) != BitConverter.SingleToInt32Bits(reference))
                        return $"centre=({x},{y}), crispMode={crispMode}: bounded {bounded}, reference {reference}";
                    if (float.IsNaN(reference))
                        continue; // the total of a refused cross section is not read by any caller
                    accepted++;
                    if (crispMode)
                        acceptedHandingRunsBack++;
                    if (boundedTotal != referenceTotal || !boundedRuns.SequenceEqual(referenceRuns))
                        return $"centre=({x},{y}), crispMode={crispMode}: bounded total {boundedTotal}, reference {referenceTotal}";
                }
            }
        }
        return null;
    }

    /// <summary>Expected totals on both sides of the 40 % window around a finder of this module size, and its edges.</summary>
    private static int[] ExpectedTotals(int module)
    {
        var nominal = 7 * module;
        return [.. new[] { 1, 5, 6, 7, nominal * 5 / 7, nominal - 1, nominal, nominal + 1, nominal * 10 / 7, nominal * 2 }.Where(e => e >= 1).Distinct()];
    }

    /// <summary>
    /// <paramref name="lines"/> columns of <paramref name="length"/> pixels, each its own sequence of dark and light runs.
    /// A third of the sequences are 1:1:3:1:1 at the module size with every run moved by up to half a module and a pixel, so they sit on both sides of every tolerance; the rest are data-like runs of one to four modules.
    /// </summary>
    private static byte[] BuildRunColumns(int lines, int length, int module, int seed)
    {
        var random = new Random(seed);
        var image = new byte[lines * length];
        for (var x = 0; x < lines; x++)
        {
            var y = 0;
            var dark = random.Next(2) == 0;
            while (y < length)
            {
                if (dark && random.Next(3) == 0)
                {
                    // a finder-like cross section: dark, light, dark x3, light, dark
                    for (var part = 0; part < 5 && y < length; part++, dark = !dark)
                    {
                        var nominal = part == 2 ? 3 * module : module;
                        var run = Math.Max(1, nominal + random.Next(-(nominal / 2) - 1, nominal / 2 + 2));
                        var level = Level(random, dark);
                        for (var k = 0; k < run && y < length; k++, y++)
                            image[y * lines + x] = level;
                    }
                }
                else
                {
                    var run = module * random.Next(1, 5) + (module > 1 ? random.Next(-1, 2) : 0);
                    var level = Level(random, dark);
                    for (var k = 0; k < Math.Max(1, run) && y < length; k++, y++)
                        image[y * lines + x] = level;
                    dark = !dark;
                }
            }
        }
        return image;
    }

    // Levels on both sides of the threshold of 128 and next to it, so the dark and the light compare are both held at their edge
    private static byte Level(Random random, bool dark)
        => dark ? (random.Next(3) == 0 ? (byte)127 : (byte)0) : random.Next(3) switch { 0 => (byte)128, 1 => (byte)129, _ => (byte)255 };

    private static byte[] Transpose(byte[] image, int width, int height)
    {
        var transposed = new byte[image.Length];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                transposed[x * height + y] = image[y * width + x];
        return transposed;
    }

    /// <summary>
    /// <paramref name="lines"/> columns of <paramref name="length"/> pixels drawn by a box filter at a fractional module size: every pixel's level is the part of it a light run covers.
    /// The finder-like cross sections sit at any sub-pixel offset with each run moved by up to a third of a module, so their whole-pixel runs miss the ratio by a rounding where their edges, measured from coverage, keep it.
    /// </summary>
    private static byte[] BuildBoxFilteredColumns(int lines, int length, float module, int seed)
    {
        var random = new Random(seed);
        var image = new byte[lines * length];
        Span<float> edges = stackalloc float[length + 2];
        for (var x = 0; x < lines; x++)
        {
            // Edges of alternating runs along the column, the first run dark
            var count = 0;
            var position = (float)random.NextDouble() * module;
            edges[count++] = position;
            while (position < length && count < edges.Length)
            {
                // An odd count is inside a dark run, where a cross section can begin
                if (count % 2 == 1 && random.Next(3) == 0 && count + 6 <= edges.Length)
                {
                    // a finder-like cross section: dark, light, dark x3, light, dark, then the light run after it
                    for (var part = 0; part < 5; part++)
                    {
                        var nominal = part == 2 ? 3 * module : module;
                        position += nominal + ((float)random.NextDouble() * 2 - 1) * module / 3;
                        edges[count++] = position;
                    }
                    position += module * random.Next(1, 4);
                    edges[count++] = position;
                }
                else
                {
                    position += module * random.Next(1, 5);
                    edges[count++] = position;
                }
            }

            for (var y = 0; y < length; y++)
            {
                // Darkness of pixel [y, y + 1): its overlap with the dark runs [edges[2k], edges[2k + 1])
                var darkness = 0f;
                for (var k = 0; k + 1 < count; k += 2)
                    darkness += Math.Max(0f, Math.Min(y + 1, edges[k + 1]) - Math.Max(y, edges[k]));
                image[y * lines + x] = (byte)Math.Round(255 * (1 - Math.Min(1f, darkness)));
            }
        }
        return image;
    }

    private static byte[] BlurAlongColumns(byte[] image, int width, int height)
    {
        var soft = new byte[image.Length];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                soft[y * width + x] = (byte)((image[Math.Max(y - 1, 0) * width + x] + 2 * image[y * width + x] + image[Math.Min(y + 1, height - 1) * width + x]) / 4);
        return soft;
    }
}
