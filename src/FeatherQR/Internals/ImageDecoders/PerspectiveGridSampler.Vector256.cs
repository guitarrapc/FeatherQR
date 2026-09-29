#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace FeatherQR.Internals.ImageDecoders;

internal static partial class PerspectiveGridSampler
{
    internal static void SampleVector256(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in PerspectiveTransform transform, int dimension, Span<byte> modules)
    {
        var laneOffsets = Vector256.Create(0.5f, 1.5f, 2.5f, 3.5f, 4.5f, 5.5f, 6.5f, 7.5f);
        var a11 = Vector256.Create(transform.a11);
        var a12 = Vector256.Create(transform.a12);
        var a13 = Vector256.Create(transform.a13);
        var zero = Vector256<int>.Zero;
        var maxPx = Vector256.Create(width - 1);
        var maxPy = Vector256.Create(height - 1);
        var widthVector = Vector256.Create(width);

        Span<int> indices = stackalloc int[8];

        for (var v = 0; v < dimension; v++)
        {
            var rowBase = v * dimension;
            var gridY = v + 0.5f;
            var rowNumeratorX = Vector256.Create(transform.a21 * gridY + transform.a31);
            var rowNumeratorY = Vector256.Create(transform.a22 * gridY + transform.a32);
            var rowDenominator = Vector256.Create(transform.a23 * gridY + transform.a33);

            var u = 0;
            for (; u + 8 <= dimension; u += 8)
            {
                var gridX = laneOffsets + Vector256.Create((float)u);
                var reciprocal = Vector256<float>.One / (a13 * gridX + rowDenominator);
                var x = (a11 * gridX + rowNumeratorX) * reciprocal;
                var y = (a12 * gridX + rowNumeratorY) * reciprocal;

                // ConvertToInt32 truncates toward zero like the scalar cast, so both take the pixel containing the point (ConvertToInt32Native is the one that follows the platform's rounding); out-of-range lanes differ from scalar saturation but are clamped into bounds either way.
                var px = Vector256.ConvertToInt32(x);
                var py = Vector256.ConvertToInt32(y);
                px = Vector256.Max(Vector256.Min(px, maxPx), zero);
                py = Vector256.Max(Vector256.Min(py, maxPy), zero);

                var index = py * widthVector + px;
                index.CopyTo(indices);

                for (var lane = 0; lane < 8; lane++)
                {
                    modules[rowBase + u + lane] = luminance[indices[lane]] < threshold ? (byte)1 : (byte)0;
                }
            }

            // Scalar tail, same op sequence as SampleScalar
            var rowNX = transform.a21 * gridY + transform.a31;
            var rowNY = transform.a22 * gridY + transform.a32;
            var rowD = transform.a23 * gridY + transform.a33;
            for (; u < dimension; u++)
            {
                var gridXs = u + 0.5f;
                var reciprocal = 1f / (transform.a13 * gridXs + rowD);
                var x = (transform.a11 * gridXs + rowNX) * reciprocal;
                var y = (transform.a12 * gridXs + rowNY) * reciprocal;

                var px = (int)x;
                var py = (int)y;
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
#endif
