using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// Parity test for the alignment-pattern row-scan kernels: the SIMD mask walk
/// (net8.0+; AVX2, NEON, or any 128-bit acceleration) must agree with the scalar
/// walk across synthetic QR-like scenes (random data modules with isolated dark
/// modules as false candidates, pattern present/absent, offset sweep; center
/// within 0.01px) plus noise sweeps over window lengths crossing every
/// mask-build block boundary and threshold extremes (center bit-identical: both
/// kernels derive it from the same integer run boundaries via the same
/// cross-check, so exact equality is the property under test).
/// </summary>
public class AlignmentPatternFinderParityTest
{
    private const int Ppm = 8;
    private const byte Threshold = 128;

    [Test]
    public async Task SimdAndScalarRowScans_Agree()
    {
        foreach (var seed in new[] { 1, 7, 42, 1234, 20260712 })
        {
            for (var offset = -3; offset <= 4; offset++)
            {
                foreach (var include in new[] { true, false })
                {
                    var scene = BuildScene(Ppm, include, offset, seed, out var size, out var expectedX, out var expectedY);

                    var (simdFound, simdX, simdY) = Sweep(scene, size, expectedX, expectedY, forceScalar: false);
                    var (scalarFound, scalarX, scalarY) = Sweep(scene, size, expectedX, expectedY, forceScalar: true);

                    await Assert.That(simdFound == scalarFound).IsTrue().Because($"found mismatch (seed={seed}, offset={offset}, include={include}): simd={simdFound}, scalar={scalarFound}");
                    if (simdFound)
                    {
                        await Assert.That(Math.Abs(simdX - scalarX) <= 0.01f && Math.Abs(simdY - scalarY) <= 0.01f).IsTrue().Because($"center mismatch (seed={seed}, offset={offset}): simd=({simdX},{simdY}), scalar=({scalarX},{scalarY})");
                    }
                    if (include)
                    {
                        await Assert.That(scalarFound).IsTrue().Because($"pattern not found at all (seed={seed}, offset={offset}), scene generator or finder broken");
                    }
                }
            }
        }
    }

    [Test]
    public async Task SimdAndScalarRowScans_Agree_NoiseWindowsAndThresholds()
    {
        // Random noise with window lengths crossing every mask-build block
        // boundary (scalar-only < 16, exactly one 16-px step, one 64-px block,
        // 64+tail) and threshold extremes (0 = nothing dark, 255 = everything
        // below max). The scene width IS the window: a huge allowance clamps
        // [minX, maxX] to the full row. Any mask-build bug (bit order, tail
        // handling, compare semantics) shifts run boundaries and diverges the
        // kernels here.
        const float ModuleSize = 4f;
        const int Height = 40;

        foreach (var windowLength in new[] { 15, 16, 17, 33, 63, 64, 65, 79, 96, 127, 129 })
        {
            foreach (var threshold in new byte[] { 0, 1, 128, 255 })
            {
                var random = new Random(windowLength * 31 + threshold);
                var scene = new byte[windowLength * Height];
                random.NextBytes(scene);

                var expectedX = windowLength / 2f;
                var expectedY = Height / 2f;

                var simdFound = AlignmentPatternFinder.TryFind(scene, windowLength, Height, threshold, expectedX, expectedY, ModuleSize, (ModuleSize, 0f), (0f, ModuleSize), allowanceModules: 100f, out var simdX, out var simdY);
                var scalarFound = AlignmentPatternFinder.TryFindScalar(scene, windowLength, Height, threshold, expectedX, expectedY, ModuleSize, (ModuleSize, 0f), (0f, ModuleSize), allowanceModules: 100f, out var scalarX, out var scalarY);

                await Assert.That(simdFound == scalarFound).IsTrue().Because($"found mismatch (window={windowLength}, threshold={threshold}): simd={simdFound}, scalar={scalarFound}");
                if (simdFound)
                {
                    await Assert.That(simdX == scalarX && simdY == scalarY).IsTrue().Because($"center mismatch (window={windowLength}, threshold={threshold}): simd=({simdX},{simdY}), scalar=({scalarX},{scalarY})");
                }
            }
        }
    }

    private static (bool Found, float X, float Y) Sweep(byte[] scene, int size, float expectedX, float expectedY, bool forceScalar)
    {
        foreach (var allowance in (Span<float>)[4f, 8f, 16f])
        {
            var found = forceScalar
                ? AlignmentPatternFinder.TryFindScalar(scene, size, size, Threshold, expectedX, expectedY, Ppm, (Ppm, 0f), (0f, Ppm), allowance, out var x, out var y)
                : AlignmentPatternFinder.TryFind(scene, size, size, Threshold, expectedX, expectedY, Ppm, (Ppm, 0f), (0f, Ppm), allowance, out x, out y);
            if (found)
                return (true, x, y);
        }
        return (false, 0, 0);
    }

    /// <summary>
    /// QR-like scene: white background, ~45% random dark modules (isolated dark
    /// modules included, the false candidates), optional 5×5 alignment pattern
    /// at prediction + offset with a guard zone keeping exactly one true match.
    /// </summary>
    private static byte[] BuildScene(int ppm, bool includePattern, int offsetModules, int seed, out int size, out float expectedX, out float expectedY)
    {
        const int SceneModules = 48;
        size = SceneModules * ppm;
        var scene = new byte[size * size];
        scene.AsSpan().Fill(255);

        var centerModule = SceneModules / 2;
        expectedX = (centerModule + 0.5f) * ppm;
        expectedY = (centerModule + 0.5f) * ppm;
        var patternX = centerModule + offsetModules;
        var patternY = centerModule + offsetModules;

        var random = new Random(seed);
        for (var my = 0; my < SceneModules; my++)
        {
            for (var mx = 0; mx < SceneModules; mx++)
            {
                if (Math.Abs(mx - patternX) <= 3 && Math.Abs(my - patternY) <= 3)
                    continue;
                if (random.Next(100) < 45)
                    FillModule(scene, size, ppm, mx, my, 0);
            }
        }

        if (includePattern)
        {
            for (var dy = -2; dy <= 2; dy++)
            {
                for (var dx = -2; dx <= 2; dx++)
                {
                    var ring = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    FillModule(scene, size, ppm, patternX + dx, patternY + dy, ring == 1 ? (byte)255 : (byte)0);
                }
            }
        }

        return scene;
    }

    private static void FillModule(byte[] scene, int size, int ppm, int moduleX, int moduleY, byte value)
    {
        for (var y = moduleY * ppm; y < (moduleY + 1) * ppm; y++)
        {
            scene.AsSpan(y * size + moduleX * ppm, ppm).Fill(value);
        }
    }
}
