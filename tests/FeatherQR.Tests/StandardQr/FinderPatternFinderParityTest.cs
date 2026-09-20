using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// Parity test for the finder-pattern row-scan kernels: the SIMD mask walk
/// (net8.0+, AVX2) evaluates the 1:1:3:1:1 window at exactly the positions the
/// scalar walk does, so <see cref="FinderPatternFinder.TryFind"/> and
/// <see cref="FinderPatternFinder.TryFindScalar"/> must agree bit-for-bit 遯ｶ繝ｻ/// found flag, centers and module sizes 遯ｶ繝ｻacross synthetic QR-like scenes
/// (finder patterns + timing lines + random data modules, 3/2/0 patterns).
/// </summary>
public class FinderPatternFinderParityTest
{
    private const byte Threshold = 128;

    [Test]
    public async Task SimdAndScalarKernels_AgreeBitForBit()
    {
        // (dimension, ppm, finderCount): both benchmark shapes, a 2-finder scene
        // (found only if noise fakes a third candidate 遯ｶ繝ｻkernels must agree either
        // way), and a no-pattern scene.
        var configs = new (int Dimension, int Ppm, int FinderCount)[]
        {
            (37, 8, 3),
            (97, 5, 3),
            (37, 8, 2),
            (56, 8, 0),
        };

        var simd = new FinderPattern[3];
        var scalar = new FinderPattern[3];

        foreach (var seed in new[] { 1, 7, 42, 1234, 20260712 })
        {
            foreach (var (dimension, ppm, finderCount) in configs)
            {
                var scene = BuildQrScene(dimension, ppm, finderCount, seed, out var width, out var height);
                var simdFound = FinderPatternFinder.TryFind(scene, width, height, Threshold, simd, default);
                var scalarFound = FinderPatternFinder.TryFindScalar(scene, width, height, Threshold, scalar, default);

                await Assert.That(simdFound == scalarFound).IsTrue().Because($"found mismatch (seed={seed}, dim={dimension}, finders={finderCount}): simd={simdFound}, scalar={scalarFound}");
                if (simdFound)
                {
                    for (var i = 0; i < 3; i++)
                    {
                        await Assert.That(simd[i].X == scalar[i].X && simd[i].Y == scalar[i].Y && simd[i].ModuleSize == scalar[i].ModuleSize).IsTrue().Because($"pattern {i} mismatch (seed={seed}, dim={dimension}): simd=({simd[i].X},{simd[i].Y},{simd[i].ModuleSize}), scalar=({scalar[i].X},{scalar[i].Y},{scalar[i].ModuleSize})");
                    }
                }

                if (finderCount == 3)
                {
                    await Assert.That(scalarFound).IsTrue().Because($"3-finder scene not detected (seed={seed}, dim={dimension}), scene generator or finder broken");
                }
            }
        }
    }

    [Test]
    public async Task SimdAndScalarKernels_AgreeBitForBit_EdgeWidthsAndPpm()
    {
        // Odd pixel-per-module scenes whose widths land between the SIMD block
        // sizes (64/32/16 px) so the vector loops, the 16-px steps and the
        // scalar tail all execute: 135 px = 2x64 + 7, 145 px = 2x64 + 16 + 1.
        var configs = new (int Dimension, int Ppm, int FinderCount)[]
        {
            (37, 3, 3),
            (21, 5, 3),
        };

        var simd = new FinderPattern[3];
        var scalar = new FinderPattern[3];

        foreach (var seed in new[] { 1, 7, 42, 1234, 20260712 })
        {
            foreach (var (dimension, ppm, finderCount) in configs)
            {
                var scene = BuildQrScene(dimension, ppm, finderCount, seed, out var width, out var height);
                var simdFound = FinderPatternFinder.TryFind(scene, width, height, Threshold, simd, default);
                var scalarFound = FinderPatternFinder.TryFindScalar(scene, width, height, Threshold, scalar, default);

                await Assert.That(simdFound == scalarFound).IsTrue().Because($"found mismatch (seed={seed}, dim={dimension}, ppm={ppm}): simd={simdFound}, scalar={scalarFound}");
                if (simdFound)
                {
                    for (var i = 0; i < 3; i++)
                    {
                        await Assert.That(simd[i].X == scalar[i].X && simd[i].Y == scalar[i].Y && simd[i].ModuleSize == scalar[i].ModuleSize).IsTrue().Because($"pattern {i} mismatch (seed={seed}, dim={dimension}, ppm={ppm})");
                    }
                }

                await Assert.That(scalarFound).IsTrue().Because($"3-finder scene not detected (seed={seed}, dim={dimension}, ppm={ppm}), scene generator or finder broken");
            }
        }
    }

    [Test]
    public async Task SimdAndScalarKernels_AgreeBitForBit_NoiseWidthsAndThresholds()
    {
        // Random noise at widths crossing every mask-build block boundary
        // (scalar-only < 16, exactly one 16-px step, one 64-px block, 64+tail)
        // and threshold extremes (0 = nothing dark, 255 = everything below max).
        // Any mask-build bug (bit order, tail handling, compare semantics)
        // shifts run boundaries and diverges the kernels here.
        var simd = new FinderPattern[3];
        var scalar = new FinderPattern[3];

        foreach (var width in new[] { 15, 16, 17, 33, 63, 64, 65, 79, 96, 127, 129 })
        {
            foreach (var threshold in new byte[] { 0, 1, 128, 255 })
            {
                var random = new Random(width * 31 + threshold);
                var scene = new byte[width * 40];
                random.NextBytes(scene);

                // Full-range noise is grey everywhere, so the second look runs on almost every window here.
                // The two kernels gate it differently (the scalar walk through the span overload, the mask walk inline), so both settings have to agree.
                var histogram = new int[256];
                foreach (var value in scene)
                    histogram[value]++;
                var noiseGrey = GreyLevels.FromHistogram(histogram, threshold);

                // Nothing is dark below threshold 0, so there is no dark class to take a level from and the second look is off whatever the noise looks like
                await Assert.That(noiseGrey.IsEnabled).IsEqualTo(threshold != 0).Because($"width={width}, threshold={threshold}: this scene is not putting the kernels through the case it is here for");

                foreach (var grey in new[] { default, noiseGrey })
                {
                    var simdFound = FinderPatternFinder.TryFind(scene, width, 40, threshold, simd, grey);
                    var scalarFound = FinderPatternFinder.TryFindScalar(scene, width, 40, threshold, scalar, grey);

                    await Assert.That(simdFound == scalarFound).IsTrue().Because($"found mismatch (width={width}, threshold={threshold}, grey={grey.IsEnabled}): simd={simdFound}, scalar={scalarFound}");
                    if (simdFound)
                    {
                        for (var i = 0; i < 3; i++)
                        {
                            await Assert.That(simd[i].X == scalar[i].X && simd[i].Y == scalar[i].Y && simd[i].ModuleSize == scalar[i].ModuleSize).IsTrue().Because($"pattern {i} mismatch (width={width}, threshold={threshold}, grey={grey.IsEnabled})");
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// QR-like scene: quiet zone, up to three 7・・・ finder patterns (dark border,
    /// light ring, dark center) with light separators, timing lines, and ~45%
    /// random dark data modules elsewhere.
    /// </summary>
    private static byte[] BuildQrScene(int dimension, int ppm, int finderCount, int seed, out int width, out int height)
    {
        const int Quiet = 4;
        var totalModules = dimension + 2 * Quiet;
        width = totalModules * ppm;
        height = totalModules * ppm;
        var scene = new byte[width * height];
        scene.AsSpan().Fill(255);

        // Finder pattern top-left corners in symbol module coordinates
        var corners = new (int X, int Y)[] { (0, 0), (dimension - 7, 0), (0, dimension - 7) };

        var random = new Random(seed);
        for (var my = 0; my < dimension; my++)
        {
            for (var mx = 0; mx < dimension; mx++)
            {
                // Reserved: finder zones + their 1-module separators (light)
                var reserved = false;
                for (var f = 0; f < finderCount; f++)
                {
                    if (mx >= corners[f].X - 1 && mx <= corners[f].X + 7 && my >= corners[f].Y - 1 && my <= corners[f].Y + 7)
                    {
                        reserved = true;
                        break;
                    }
                }
                if (reserved)
                    continue;

                if (finderCount > 0 && (my == 6 || mx == 6))
                {
                    // Timing lines: alternating, dark on even module index
                    if ((mx + my) % 2 == 0)
                        FillModule(scene, width, ppm, Quiet + mx, Quiet + my, 0);
                }
                else if (random.Next(100) < 45)
                {
                    FillModule(scene, width, ppm, Quiet + mx, Quiet + my, 0);
                }
            }
        }

        for (var f = 0; f < finderCount; f++)
        {
            for (var dy = 0; dy < 7; dy++)
            {
                for (var dx = 0; dx < 7; dx++)
                {
                    // dark border (ring 3), light ring (ring 2), dark 3・・・ center
                    var ring = Math.Max(Math.Abs(dx - 3), Math.Abs(dy - 3));
                    if (ring != 2)
                    {
                        FillModule(scene, width, ppm, Quiet + corners[f].X + dx, Quiet + corners[f].Y + dy, 0);
                    }
                }
            }
        }

        return scene;
    }

    private static async Task FillModule(byte[] scene, int width, int ppm, int moduleX, int moduleY, byte value)
    {
        for (var y = moduleY * ppm; y < (moduleY + 1) * ppm; y++)
        {
            scene.AsSpan(y * width + moduleX * ppm, ppm).Fill(value);
        }
    }
}
