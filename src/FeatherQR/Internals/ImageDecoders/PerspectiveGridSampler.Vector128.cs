#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Internals.StandardQR;

internal static partial class QRImageDecoder
{
    internal static void SampleGridSimd128(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in PerspectiveTransform transform, int dimension, Span<byte> modules)
    {
        var laneOffsetsLo = Vector128.Create(0.5f, 1.5f, 2.5f, 3.5f);
        var laneOffsetsHi = Vector128.Create(4.5f, 5.5f, 6.5f, 7.5f);
        var a11 = Vector128.Create(transform.a11);
        var a12 = Vector128.Create(transform.a12);
        var a13 = Vector128.Create(transform.a13);
        var zero = Vector128<int>.Zero;
        var maxPx = Vector128.Create(width - 1);
        var maxPy = Vector128.Create(height - 1);
        var widthVector = Vector128.Create(width);

        Span<int> indices = stackalloc int[8];

        for (var v = 0; v < dimension; v++)
        {
            var rowBase = v * dimension;
            var gridY = v + 0.5f;
            var rowNumeratorX = Vector128.Create(transform.a21 * gridY + transform.a31);
            var rowNumeratorY = Vector128.Create(transform.a22 * gridY + transform.a32);
            var rowDenominator = Vector128.Create(transform.a23 * gridY + transform.a33);

            // Two independent 4-lane chains per iteration: the second fdiv overlaps the first (fdiv 4S latency would otherwise stall the 4-lane loop) and per-iteration loop overhead is halved.
            var u = 0;
            for (; u + 8 <= dimension; u += 8)
            {
                var uVector = Vector128.Create((float)u);
                var gridXLo = laneOffsetsLo + uVector;
                var gridXHi = laneOffsetsHi + uVector;
                var reciprocalLo = Vector128<float>.One / (a13 * gridXLo + rowDenominator);
                var reciprocalHi = Vector128<float>.One / (a13 * gridXHi + rowDenominator);
                var xLo = (a11 * gridXLo + rowNumeratorX) * reciprocalLo;
                var yLo = (a12 * gridXLo + rowNumeratorY) * reciprocalLo;
                var xHi = (a11 * gridXHi + rowNumeratorX) * reciprocalHi;
                var yHi = (a12 * gridXHi + rowNumeratorY) * reciprocalHi;

                // ConvertToInt32 truncates toward zero like the scalar cast, so both take the pixel containing the point (ConvertToInt32Native is the one that follows the platform's rounding); out-of-range lanes differ from scalar saturation but are clamped into bounds either way.
                var pxLo = Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(xLo), maxPx), zero);
                var pyLo = Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(yLo), maxPy), zero);
                var pxHi = Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(xHi), maxPx), zero);
                var pyHi = Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(yHi), maxPy), zero);

                (pyLo * widthVector + pxLo).CopyTo(indices);
                (pyHi * widthVector + pxHi).CopyTo(indices.Slice(4));

                for (var lane = 0; lane < 8; lane++)
                {
                    modules[rowBase + u + lane] = luminance[indices[lane]] < threshold ? (byte)1 : (byte)0;
                }
            }

            // 4-lane cleanup keeps the per-row scalar tail under 4 modules (dimension mod 8 can be 4-7, e.g. 77 leaves 5 without this block).
            if (u + 4 <= dimension)
            {
                var gridX = laneOffsetsLo + Vector128.Create((float)u);
                var reciprocal = Vector128<float>.One / (a13 * gridX + rowDenominator);
                var x = (a11 * gridX + rowNumeratorX) * reciprocal;
                var y = (a12 * gridX + rowNumeratorY) * reciprocal;

                var px = Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(x), maxPx), zero);
                var py = Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(y), maxPy), zero);

                (py * widthVector + px).CopyTo(indices);

                for (var lane = 0; lane < 4; lane++)
                {
                    modules[rowBase + u + lane] = luminance[indices[lane]] < threshold ? (byte)1 : (byte)0;
                }
                u += 4;
            }

            // Scalar tail, same op sequence as SampleGridScalar
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
