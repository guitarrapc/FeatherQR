using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// Parity test for the tiers of <see cref="PerspectiveGridSampler"/>: each SIMD path (net8.0+, Vector256 and Vector128) must
/// produce byte-identical module output to the scalar path for affine and
/// projective transforms across dimensions.
/// </summary>
public class PerspectiveGridSamplerParityTest
{
    /// <summary>
    /// Micro QR's four sizes and Standard QR sizes: Micro QR samples its perspective search through the same tiers, and its
    /// sizes leave the scalar tails Standard QR's do not (after 8-wide steps, 3 at 11 and 7 at 15; after the 128-bit tier's
    /// steps, 3 at 11 and 15, where every Standard QR size leaves 1).
    /// </summary>
    private static readonly int[] Dimensions = [11, 13, 15, 17, 21, 33, 77, 177];

    private const byte Threshold = 128;

    [Test]
    public async Task SimdAndScalarSampling_AreByteIdentical()
    {
#if NET8_0_OR_GREATER
        if (!System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated)
        {
            Skip.Test("Vector256 not accelerated on this machine");
            return;
        }

        foreach (var seed in new[] { 1, 42, 1234 })
        {
            foreach (var dimension in Dimensions)
            {
                foreach (var projective in new[] { true, false })
                {
                    var (luminance, width, transform) = BuildScene(dimension, projective, seed);
                    await Assert.That(CentresInside(transform, dimension, width)).IsEqualTo(dimension * dimension).Because($"every module centre lands in the image (seed={seed}, dim={dimension}, projective={projective})");

                    var scalar = new byte[dimension * dimension];
                    PerspectiveGridSampler.SampleScalar(luminance, width, width, Threshold, transform, dimension, scalar);

                    var simd = new byte[dimension * dimension];
                    PerspectiveGridSampler.SampleVector256(luminance, width, width, Threshold, transform, dimension, simd);

                    await Assert.That(simd.AsSpan().SequenceEqual(scalar)).IsTrue().Because($"SIMD/scalar sampling mismatch (seed={seed}, dim={dimension}, projective={projective})");
                }
            }
        }
#endif
    }

    [Test]
    public async Task Simd128AndScalarSampling_AreByteIdentical()
    {
#if NET8_0_OR_GREATER
        if (!System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated)
        {
            Skip.Test("Vector128 not accelerated on this machine");
            return;
        }

        foreach (var seed in new[] { 1, 42, 1234 })
        {
            foreach (var dimension in Dimensions)
            {
                foreach (var projective in new[] { true, false })
                {
                    var (luminance, width, transform) = BuildScene(dimension, projective, seed);
                    await Assert.That(CentresInside(transform, dimension, width)).IsEqualTo(dimension * dimension).Because($"every module centre lands in the image (seed={seed}, dim={dimension}, projective={projective})");

                    var scalar = new byte[dimension * dimension];
                    PerspectiveGridSampler.SampleScalar(luminance, width, width, Threshold, transform, dimension, scalar);

                    var simd = new byte[dimension * dimension];
                    PerspectiveGridSampler.SampleVector128(luminance, width, width, Threshold, transform, dimension, simd);

                    await Assert.That(simd.AsSpan().SequenceEqual(scalar)).IsTrue().Because($"SIMD128/scalar sampling mismatch (seed={seed}, dim={dimension}, projective={projective})");
                }
            }
        }
#endif
    }

    /// <summary>The module centres the transform maps inside the image: on a grid that lands outside, both tiers compare the clamp alone.</summary>
    private static int CentresInside(in PerspectiveTransform transform, int dimension, int width)
    {
        var inside = 0;
        for (var v = 0; v < dimension; v++)
        {
            for (var u = 0; u < dimension; u++)
            {
                transform.Transform(u + 0.5f, v + 0.5f, out var x, out var y);
                if (x >= 0 && x < width && y >= 0 && y < width)
                    inside++;
            }
        }
        return inside;
    }

    private static (byte[] Luminance, int Width, PerspectiveTransform Transform) BuildScene(int dimension, bool projective, int seed)
    {
        const int Ppm = 8;
        var sceneModules = dimension + 8;
        var width = sceneModules * Ppm;
        var luminance = new byte[width * width];
        luminance.AsSpan().Fill(255);

        var random = new Random(seed);
        for (var my = 0; my < sceneModules; my++)
        {
            for (var mx = 0; mx < sceneModules; mx++)
            {
                if (random.Next(100) < 45)
                {
                    for (var y = my * Ppm; y < (my + 1) * Ppm; y++)
                    {
                        luminance.AsSpan(y * width + mx * Ppm, Ppm).Clear();
                    }
                }
            }
        }

        float margin = 4 * Ppm;
        var shrink = projective ? dimension * Ppm * 0.05f : 0f;
        var tlX = margin + 3.5f * Ppm + shrink * (3.5f / dimension);
        var tlY = margin + 3.5f * Ppm;
        var trX = margin + (dimension - 3.5f) * Ppm - shrink * (3.5f / dimension);
        var trY = margin + 3.5f * Ppm + (projective ? 0f : 6f);
        var blX = margin + 3.5f * Ppm;
        var blY = margin + (dimension - 3.5f) * Ppm;

        // Projective: the fourth corner pulled in from the parallelogram's by 3 modules, by less on a small grid, since it must stay
        // beyond the line through the top-right and bottom-left corners (x + y = dimension), which a pull of 3 reaches at 13
        var fourthGrid = projective ? dimension - 3.5f - Math.Min(3f, (dimension - 7) / 4f) : dimension - 3.5f;
        var fourthX = projective ? margin + fourthGrid * Ppm : trX + blX - tlX;
        var fourthY = projective ? margin + fourthGrid * Ppm : trY + blY - tlY;

        var transform = PerspectiveTransform.QuadrilateralToQuadrilateral(
            3.5f, 3.5f, dimension - 3.5f, 3.5f, fourthGrid, fourthGrid, 3.5f, dimension - 3.5f,
            tlX, tlY, trX, trY, fourthX, fourthY, blX, blY);
        return (luminance, width, transform);
    }
}
