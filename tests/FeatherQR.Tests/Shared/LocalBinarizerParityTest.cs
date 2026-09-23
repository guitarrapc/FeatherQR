using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// The regional binarization, scalar and vector, against a reference written here from the scheme itself: 8 × 8 blocks, a black point each from its mean or, when flat, from half its minimum or its top and left neighbours, and every pixel read against the mean of the 5 × 5 black points around its block.
/// Widths and heights off a multiple of 8 put the last block column and row over the one before, which the vector form leaves to its scalar tail.
/// </summary>
public class LocalBinarizerParityTest
{
    public static IEnumerable<(int, int, string, byte)> Cases()
    {
        (int, int)[] sizes = [(40, 40), (41, 43), (47, 40), (48, 56), (63, 65), (64, 64), (100, 37), (129, 131), (256, 200)];
        string[] contents = ["noise", "blurred noise", "ramp", "symbol under a shadow", "two levels", "flat grey", "flat black", "flat white", "flat block edges"];
        foreach (var (width, height) in sizes)
        {
            foreach (var content in contents)
            {
                foreach (var globalThreshold in new byte[] { 0, 1, 128, 255 })
                    yield return (width, height, content, globalThreshold);
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task TryBinarize_VectorAndScalar_MatchReference(int width, int height, string content, byte globalThreshold)
    {
        var luminance = Render(content, width, height);
        var (expected, expectedDark, expectedDiffers) = Reference(luminance, width, height, globalThreshold);

        var scratch = new int[LocalBinarizer.ScratchLength(width, height)];
        var vector = new byte[width * height];
        var scalar = new byte[width * height];
        var vectorDiffers = LocalBinarizer.TryBinarize(luminance, width, height, globalThreshold, vector, scratch, out var vectorDark);
        Array.Clear(scratch);
        var scalarDiffers = LocalBinarizer.TryBinarizeScalar(luminance, width, height, globalThreshold, scalar, scratch, out var scalarDark);

        await Assert.That(scalar).IsEquivalentTo(expected);
        await Assert.That(scalarDark).IsEqualTo(expectedDark);
        await Assert.That(scalarDiffers).IsEqualTo(expectedDiffers);
        await Assert.That(vector).IsEquivalentTo(expected);
        await Assert.That(vectorDark).IsEqualTo(expectedDark);
        await Assert.That(vectorDiffers).IsEqualTo(expectedDiffers);
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
        await Assert.That(LocalBinarizer.TryBinarize(luminance, width, height, 128, binarized, [], out var dark)).IsFalse();
        await Assert.That(dark).IsEqualTo(0);
        await Assert.That(binarized.All(static b => b == 77)).IsTrue();
    }

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
                    // Blocks flat inside, each its own level, so the flat rule reads its neighbours
                    _ => (byte)(((x / 8 + y / 8) % 3) * 90 + 10),
                };
            }
        }
        return luminance;
    }

    private static (byte[] Binarized, int Dark, bool Differs) Reference(byte[] luminance, int width, int height, byte globalThreshold)
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
                        var v = luminance[y * width + x];
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
