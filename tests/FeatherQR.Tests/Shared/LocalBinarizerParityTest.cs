using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// The regional binarization, in every form the machine runs, against a reference written here from ZXing's hybrid scheme.
/// Widths and heights off a multiple of 8 put the last block column and row over the one before. Widths off a multiple of 16 and of 32 give the vector forms a last step that overlaps the one before it.
/// The negative is read from the same pixels, each complemented as it is loaded, and has to equal the reference run on a negated copy.
/// </summary>
public class LocalBinarizerParityTest
{
    private static readonly string[] Contents = ["noise", "blurred noise", "ramp", "symbol under a shadow", "two levels", "flat grey", "flat black", "flat white", "flat block edges", "levels 24 apart", "levels 25 apart", "flat at its neighbours' level", "one pixel apart"];

    // Rows of 4 to 37 blocks on their own columns, so the four-block and two-block steps end on every remainder, and each width's last step overlaps by another count
    private static readonly (int Width, int Height)[] Sizes =
    [
        (33, 33), (40, 40), (41, 43), (47, 40), (48, 56), (63, 65), (64, 64), (72, 40), (79, 47), (80, 41), (95, 41), (96, 48), (97, 49), (100, 37),
        (104, 40), (111, 41), (127, 47), (129, 131), (136, 72), (160, 40), (256, 200), (300, 44),
    ];

    private static IEnumerable<LocalBinarizer.Kernel> Kernels()
    {
        yield return LocalBinarizer.Kernel.Auto;
        yield return LocalBinarizer.Kernel.Scalar;
        if (Vector128.IsHardwareAccelerated)
            yield return LocalBinarizer.Kernel.Vector128;
        if (Vector256.IsHardwareAccelerated)
            yield return LocalBinarizer.Kernel.Vector256;
    }

    public static IEnumerable<(int, int, string, byte, bool)> Cases()
    {
        foreach (var (width, height) in Sizes)
        {
            foreach (var content in Contents)
            {
                foreach (var globalThreshold in new byte[] { 0, 1, 128, 255 })
                {
                    yield return (width, height, content, globalThreshold, false);
                    yield return (width, height, content, globalThreshold, true);
                }
            }
        }
        // Rows of more steps than a byte counter holds, in both vector widths. A counter runs over only where its lane is the counted class in every step, so some of these rows are all light and some all dark.
        yield return (8200, 40, "noise", 128, false);
        yield return (8200, 40, "symbol under a shadow", 128, true);
        yield return (8231, 41, "blurred noise", 128, false);
        foreach (var negative in new[] { false, true })
        {
            yield return (8200, 40, "flat white", 128, negative);
            yield return (8200, 40, "flat black", 128, negative);
            yield return (16500, 41, "flat white", 128, negative);
        }
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task TryBinarize_EveryKernel_MatchesReference(int width, int height, string content, byte globalThreshold, bool negative)
    {
        var luminance = Render(content, width, height);
        var seen = negative ? Negated(luminance) : luminance;
        var expected = Reference(seen, seen, width, height, globalThreshold);

        foreach (var kernel in Kernels())
        {
            var scratch = new int[LocalBinarizer.ScratchLength(width, height)];
            var binarized = new byte[width * height];
            Array.Fill(binarized, (byte)77);
            var measured = false;
            var differs = LocalBinarizer.TryBinarizeWith(kernel, luminance, width, height, negative, globalThreshold, binarized, scratch, ref measured, out var dark);

            await Assert.That(FirstDifference(binarized, expected.Binarized)).IsEqualTo(-1).Because($"{kernel}: the first pixel that differs");
            await Assert.That(dark).IsEqualTo(expected.Dark).Because($"{kernel}");
            await Assert.That(differs).IsEqualTo(expected.Differs).Because($"{kernel}");
            await Assert.That(measured).IsTrue();
        }

        // The convenience entries must also recompute statistics when the caller reuses scratch.
        var entryScratch = new int[LocalBinarizer.ScratchLength(width, height)];
        var vector = new byte[width * height];
        var scalar = new byte[width * height];
        var vectorDiffers = LocalBinarizer.TryBinarize(luminance, width, height, negative, globalThreshold, vector, entryScratch, out var vectorDark);
        // The statistics of one call are not the next call's: each entry without the flag reads them again
        Array.Fill(entryScratch, -1);
        var scalarDiffers = LocalBinarizer.TryBinarizeScalar(luminance, width, height, negative, globalThreshold, scalar, entryScratch, out var scalarDark);

        await Assert.That(FirstDifference(scalar, expected.Binarized)).IsEqualTo(-1);
        await Assert.That(scalarDark).IsEqualTo(expected.Dark);
        await Assert.That(scalarDiffers).IsEqualTo(expected.Differs);
        await Assert.That(FirstDifference(vector, expected.Binarized)).IsEqualTo(-1);
        await Assert.That(vectorDark).IsEqualTo(expected.Dark);
        await Assert.That(vectorDiffers).IsEqualTo(expected.Differs);
    }

    public static IEnumerable<(int, int, string, bool)> KeptCases()
    {
        foreach (var (width, height) in new[] { (40, 40), (41, 43), (97, 49), (129, 131), (300, 44) })
        {
            foreach (var content in new[] { "noise", "blurred noise", "symbol under a shadow", "flat block edges", "levels 24 apart", "levels 25 apart", "flat at its neighbours' level" })
            {
                yield return (width, height, content, false);
                yield return (width, height, content, true);
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(KeptCases))]
    public async Task TryBinarize_StatisticsKept_GiveTheOtherPolarityWhatItGivesAlone(int width, int height, string content, bool negativeFirst)
    {
        var luminance = Render(content, width, height);
        var negated = Negated(luminance);

        foreach (var kernel in Kernels())
        {
            var scratch = new int[LocalBinarizer.ScratchLength(width, height)];
            var binarized = new byte[width * height];
            var measured = false;
            foreach (var negative in new[] { negativeFirst, !negativeFirst })
            {
                var seen = negative ? negated : luminance;
                var expected = Reference(seen, seen, width, height, 128);
                var differs = LocalBinarizer.TryBinarizeWith(kernel, luminance, width, height, negative, 128, binarized, scratch, ref measured, out var dark);

                await Assert.That(FirstDifference(binarized, expected.Binarized)).IsEqualTo(-1).Because($"{kernel}, negative {negative}: the first pixel that differs");
                await Assert.That(dark).IsEqualTo(expected.Dark).Because($"{kernel}, negative {negative}");
                await Assert.That(differs).IsEqualTo(expected.Differs).Because($"{kernel}, negative {negative}");
                await Assert.That(measured).IsTrue();
            }
        }
    }

    /// <summary>
    /// Proof that a call told the statistics are there reads none from the pixels: given another image's pixels, it thresholds them with the first image's blocks.
    /// </summary>
    [Test]
    [Arguments(41, 43, false)]
    [Arguments(41, 43, true)]
    [Arguments(129, 131, false)]
    [Arguments(129, 131, true)]
    public async Task TryBinarize_StatisticsKept_AreNotReadAgain(int width, int height, bool negative)
    {
        var measuredImage = Render("symbol under a shadow", width, height);
        var otherImage = Render("noise", width, height);
        var expected = negative
            ? Reference(Negated(measuredImage), Negated(otherImage), width, height, 128)
            : Reference(measuredImage, otherImage, width, height, 128);
        // A premise: the other image's own blocks give another output
        await Assert.That(FirstDifference(expected.Binarized, Reference(negative ? Negated(otherImage) : otherImage, negative ? Negated(otherImage) : otherImage, width, height, 128).Binarized)).IsNotEqualTo(-1);

        foreach (var kernel in Kernels())
        {
            var scratch = new int[LocalBinarizer.ScratchLength(width, height)];
            var binarized = new byte[width * height];
            var measured = false;
            LocalBinarizer.TryBinarizeWith(kernel, measuredImage, width, height, !negative, 128, binarized, scratch, ref measured, out _);
            var differs = LocalBinarizer.TryBinarizeWith(kernel, otherImage, width, height, negative, 128, binarized, scratch, ref measured, out var dark);

            await Assert.That(FirstDifference(binarized, expected.Binarized)).IsEqualTo(-1).Because($"{kernel}: the first pixel that differs");
            await Assert.That(dark).IsEqualTo(expected.Dark).Because($"{kernel}");
            await Assert.That(differs).IsEqualTo(expected.Differs).Because($"{kernel}");
        }
    }

    [Test]
    public async Task EveryKernel_ReachesNothingPastItsSpans()
    {
        if (!PageEndMemory.IsSupported)
            Skip.Test("no page protection on this platform");
        using var sourcePage = new PageEndMemory();
        using var targetPage = new PageEndMemory();
        using var scratchPage = new PageEndMemory();
        var runs = 0;
        foreach (var kernel in Kernels())
        {
            for (var width = 33; width <= 99; width++)
            {
                foreach (var height in new[] { 40, 41 })
                {
                    var source = sourcePage.AtPageEnd(Render("noise", width, height));
                    var target = targetPage.AtPageEnd(width * height);
                    var scratch = MemoryMarshal.Cast<byte, int>(scratchPage.AtPageEnd(LocalBinarizer.ScratchLength(width, height) * sizeof(int)));
                    var measured = false;
                    LocalBinarizer.TryBinarizeWith(kernel, source, width, height, negative: false, 128, target, scratch, ref measured, out _);
                    LocalBinarizer.TryBinarizeWith(kernel, source, width, height, negative: true, 128, target, scratch, ref measured, out _);
                    runs++;
                }
            }
        }
        await Assert.That(runs).IsGreaterThan(0);
    }

    [Test]
    public async Task NeighbourhoodMultiplier_DividesEverySumOfBlackPointsBy25()
    {
        var wrong = 0;
        for (var sum = 0; sum <= 25 * 255; sum++)
        {
            if (sum * LocalBinarizer.NeighbourhoodMultiplier >> LocalBinarizer.NeighbourhoodShift != sum / 25)
                wrong++;
        }
        await Assert.That(wrong).IsEqualTo(0);
    }

    [Test]
    [Arguments(32, 64)]
    [Arguments(64, 32)]
    [Arguments(8, 8)]
    public async Task TryBinarize_UnderFiveBlocksASide_RefusesWithoutWriting(int width, int height)
    {
        var luminance = Render("noise", width, height);
        var binarized = new byte[width * height];
        Array.Fill(binarized, (byte)77);

        await Assert.That(LocalBinarizer.ScratchLength(width, height)).IsEqualTo(0);
        var measured = false;
        await Assert.That(LocalBinarizer.TryBinarize(luminance, width, height, negative: false, 128, binarized, [], ref measured, out var dark)).IsFalse();
        await Assert.That(dark).IsEqualTo(0);
        await Assert.That(measured).IsFalse();
        await Assert.That(binarized.All(static b => b == 77)).IsTrue();
    }

    private static int FirstDifference(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected)
    {
        var same = actual.CommonPrefixLength(expected);
        return same == actual.Length && same == expected.Length ? -1 : same;
    }

    private static byte[] Negated(byte[] luminance) => luminance.Select(static v => (byte)(255 - v)).ToArray();

    private static byte[] Render(string content, int width, int height)
    {
        var random = new Random(width * 7919 + height * 31 + content.Length);
        var luminance = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                luminance[y * width + x] = content switch
                {
                    "noise" => (byte)random.Next(256),
                    "blurred noise" => (byte)(96 + random.Next(64)),
                    "ramp" => (byte)(20 + 200 * x / width),
                    "symbol under a shadow" => (byte)((((x / 3) ^ (y / 3)) & 1) == 0 ? (x < width / 2 ? 220 : 60) : (x < width / 2 ? 30 : 8)),
                    "two levels" => random.Next(2) == 0 ? (byte)0 : (byte)255,
                    "flat grey" => 150,
                    "flat black" => 0,
                    "flat white" => 255,
                    // Both sides of the flat bound: a block holding both levels has a range of exactly 24, or 25
                    "levels 24 apart" => random.Next(2) == 0 ? (byte)100 : (byte)124,
                    "levels 25 apart" => random.Next(2) == 0 ? (byte)100 : (byte)125,
                    // A block's minimum or maximum in one pixel, at every place in a block in turn: 140 with one pixel at 120 is flat with a minimum of 120, and with one at 170 it is not flat.
                    // A fold that passes a pixel over reads the first as 140 and the second as flat. The noise blocks between them hold the pixels those thresholds move.
                    "one pixel apart" => OnePixelApart(x, y, random),
                    "flat at its neighbours' level" => (x / 8) % 3 == 2 && (y / 8) % 3 == 2 ? (byte)100 : ((x + y) & 1) == 0 ? (byte)0 : (byte)200,
                    // Blocks flat inside, each its own level, so the flat rule reads its neighbours
                    _ => (byte)(((x / 8 + y / 8) % 3) * 90 + 10),
                };
            }
        }
        return luminance;
    }

    private static byte OnePixelApart(int x, int y, Random random)
    {
        int blockX = x / 8, blockY = y / 8;
        var kind = (blockX + blockY) % 3;
        if (kind == 0)
            return (byte)random.Next(256);
        var place = (blockX * 7 + blockY * 13) % 64;
        if (x % 8 != place % 8 || y % 8 != place / 8)
            return 140;
        return kind == 1 ? (byte)120 : (byte)170;
    }

    /// <summary>The scheme, block by block: the black points of <paramref name="measured"/>, and <paramref name="luminance"/> read against the thresholds they give. The two are one image but where a test hands a call another image's statistics.</summary>
    private static (byte[] Binarized, int Dark, bool Differs) Reference(byte[] measured, byte[] luminance, int width, int height, byte globalThreshold)
    {
        const int block = 8;
        var columns = (width + block - 1) / block;
        var rows = (height + block - 1) / block;
        var blackPoints = new int[rows, columns];
        for (var by = 0; by < rows; by++)
        {
            for (var bx = 0; bx < columns; bx++)
            {
                var top = Math.Min(by * block, height - block);
                var left = Math.Min(bx * block, width - block);
                int sum = 0, min = 255, max = 0;
                for (var y = top; y < top + block; y++)
                {
                    for (var x = left; x < left + block; x++)
                    {
                        var v = measured[y * width + x];
                        sum += v;
                        min = Math.Min(min, v);
                        max = Math.Max(max, v);
                    }
                }
                var blackPoint = sum / 64;
                if (max - min <= 24)
                {
                    blackPoint = min / 2;
                    if (by > 0 && bx > 0)
                    {
                        var neighbours = (blackPoints[by - 1, bx] + 2 * blackPoints[by, bx - 1] + blackPoints[by - 1, bx - 1]) / 4;
                        if (min < neighbours)
                            blackPoint = neighbours;
                    }
                }
                blackPoints[by, bx] = blackPoint;
            }
        }

        var binarized = new byte[width * height];
        for (var by = 0; by < rows; by++)
        {
            for (var bx = 0; bx < columns; bx++)
            {
                var cy = Math.Min(Math.Max(by, 2), rows - 3);
                var cx = Math.Min(Math.Max(bx, 2), columns - 3);
                var sum = 0;
                for (var dy = -2; dy <= 2; dy++)
                {
                    for (var dx = -2; dx <= 2; dx++)
                        sum += blackPoints[cy + dy, cx + dx];
                }
                var threshold = sum / 25;
                var top = Math.Min(by * block, height - block);
                var left = Math.Min(bx * block, width - block);
                for (var y = top; y < top + block; y++)
                {
                    for (var x = left; x < left + block; x++)
                        binarized[y * width + x] = luminance[y * width + x] <= threshold ? (byte)0 : (byte)255;
                }
            }
        }

        var dark = 0;
        var differs = false;
        for (var i = 0; i < binarized.Length; i++)
        {
            var isDark = binarized[i] == 0;
            dark += isDark ? 1 : 0;
            differs |= isDark != luminance[i] < globalThreshold;
        }
        return (binarized, dark, differs);
    }
}
