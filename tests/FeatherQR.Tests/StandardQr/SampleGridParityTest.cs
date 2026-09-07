using FeatherQR.Internals.StandardQR;
using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// Parity test for the grid sampling kernels: the SIMD path (net8.0+, AVX2) must
/// produce byte-identical module output to the scalar path for affine and
/// projective transforms across dimensions.
/// </summary>
public class SampleGridParityTest
{
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
            foreach (var dimension in new[] { 21, 33, 77, 177 })
            {
                foreach (var projective in new[] { true, false })
                {
                    var (luminance, width, transform) = BuildScene(dimension, projective, seed);

                    var scalar = new byte[dimension * dimension];
                    QRImageDecoder.SampleGridScalar(luminance, width, width, Threshold, transform, dimension, scalar);

                    var simd = new byte[dimension * dimension];
                    QRImageDecoder.SampleGridSimd(luminance, width, width, Threshold, transform, dimension, simd);

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
            foreach (var dimension in new[] { 21, 33, 77, 177 })
            {
                foreach (var projective in new[] { true, false })
                {
                    var (luminance, width, transform) = BuildScene(dimension, projective, seed);

                    var scalar = new byte[dimension * dimension];
                    QRImageDecoder.SampleGridScalar(luminance, width, width, Threshold, transform, dimension, scalar);

                    var simd = new byte[dimension * dimension];
                    QRImageDecoder.SampleGridSimd128(luminance, width, width, Threshold, transform, dimension, simd);

                    await Assert.That(simd.AsSpan().SequenceEqual(scalar)).IsTrue().Because($"SIMD128/scalar sampling mismatch (seed={seed}, dim={dimension}, projective={projective})");
                }
            }
        }
#endif
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

        float fourthGrid = projective ? dimension - 6.5f : dimension - 3.5f;
        var fourthX = projective ? margin + fourthGrid * Ppm : trX + blX - tlX;
        var fourthY = projective ? margin + fourthGrid * Ppm : trY + blY - tlY;

        var transform = PerspectiveTransform.QuadrilateralToQuadrilateral(
            3.5f, 3.5f, dimension - 3.5f, 3.5f, fourthGrid, fourthGrid, 3.5f, dimension - 3.5f,
            tlX, tlY, trX, trY, fourthX, fourthY, blX, blY);
        return (luminance, width, transform);
    }
}
