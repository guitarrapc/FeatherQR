#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
#endif

namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// A square module grid read through one projective transform: each module centre's pixel against the threshold.
/// Standard QR samples its four-point and parallelogram grids with it and Micro QR its perspective search.
/// </summary>
/// <remarks>
/// It was <c>QRImageDecoder.SampleGrid</c> until 2026-09-29, and Micro QR called it there, against the rule that symbology namespaces never reference each other.
/// rMQR's grid is not square and keeps its own sampler, with an affine tier.
/// </remarks>
internal static partial class PerspectiveGridSampler
{
    /// <summary>
    /// Samples every module center through the projective grid-to-pixel transform.
    /// Handles rotation, scale, shear and mild perspective.
    /// </summary>
    /// <remarks>
    /// The loop is bound by scalar conversion/clamp/branch overhead, not by the divisions (module computations are independent, so out-of-order execution hides division latency, halving the division count measured no gain).
    /// The SIMD paths process 8 module centers per iteration with the exact scalar op sequence (no FMA), so lane results are bit-identical to the scalar path: one Vector256 on AVX2 (measured 2.7x at version 40; PerspectiveSample findings log), two independent Vector128 chains plus a 4-lane cleanup on NEON/WASM (measured 1.9x at version 40 on Apple M2; PerspectiveSampleArm findings log).
    /// </remarks>
    internal static void Sample(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in PerspectiveTransform transform, int dimension, Span<byte> modules)
    {
#if NET8_0_OR_GREATER
        if (Vector256.IsHardwareAccelerated && dimension >= 8)
        {
            SampleVector256(luminance, width, height, threshold, transform, dimension, modules);
            return;
        }
        if (Vector128.IsHardwareAccelerated && dimension >= 4)
        {
            SampleVector128(luminance, width, height, threshold, transform, dimension, modules);
            return;
        }
#endif
        SampleScalar(luminance, width, height, threshold, transform, dimension, modules);
    }

    internal static void SampleScalar(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in PerspectiveTransform transform, int dimension, Span<byte> modules)
    {
        for (var v = 0; v < dimension; v++)
        {
            var rowBase = v * dimension;
            var gridY = v + 0.5f;
            var rowNumeratorX = transform.a21 * gridY + transform.a31;
            var rowNumeratorY = transform.a22 * gridY + transform.a32;
            var rowDenominator = transform.a23 * gridY + transform.a33;

            for (var u = 0; u < dimension; u++)
            {
                var gridX = u + 0.5f;
                var reciprocal = 1f / (transform.a13 * gridX + rowDenominator);
                var x = (transform.a11 * gridX + rowNumeratorX) * reciprocal;
                var y = (transform.a12 * gridX + rowNumeratorY) * reciprocal;

                // Pixel edges sit on integers, so the pixel containing a point is its floor
                var px = (int)x;
                var py = (int)y;

                // Clamp: mild inaccuracy at the outermost modules must not read OOB
                if (px < 0)
                    px = 0;
                else if (px >= width)
                    px = width - 1;
                if (py < 0)
                    py = 0;
                else if (py >= height)
                    py = height - 1;

                modules[rowBase + u] = luminance[py * width + px] < threshold ? (byte)1 : (byte)0;
            }
        }
    }
}
