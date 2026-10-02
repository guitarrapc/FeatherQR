#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;

namespace FeatherQR.Internals.ImageDecoders;

internal static partial class PerspectiveGridSampler
{
    internal static void SampleVector128(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in PerspectiveTransform transform, int dimension, Span<byte> modules)
    {
        var laneOffsetsLo = Vector128.Create(0.5f, 1.5f, 2.5f, 3.5f);
        var laneOffsetsHi = Vector128.Create(4.5f, 5.5f, 6.5f, 7.5f);
        var a11 = Vector128.Create(transform.a11);
        var a12 = Vector128.Create(transform.a12);
        var a13 = Vector128.Create(transform.a13);
        var lastX = Vector128.Create((float)(width - 1));
        var lastY = Vector128.Create((float)(height - 1));
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

                // The pixel the scalar tier's PixelIndex.Clamp takes
                var pxLo = VectorCast.ToPixel(xLo, lastX);
                var pyLo = VectorCast.ToPixel(yLo, lastY);
                var pxHi = VectorCast.ToPixel(xHi, lastX);
                var pyHi = VectorCast.ToPixel(yHi, lastY);

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

                var px = VectorCast.ToPixel(x, lastX);
                var py = VectorCast.ToPixel(y, lastY);

                (py * widthVector + px).CopyTo(indices);

                for (var lane = 0; lane < 4; lane++)
                {
                    modules[rowBase + u + lane] = luminance[indices[lane]] < threshold ? (byte)1 : (byte)0;
                }
                u += 4;
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

                var px = PixelIndex.Clamp(x, width);
                var py = PixelIndex.Clamp(y, height);

                modules[rowBase + u] = luminance[py * width + px] < threshold ? (byte)1 : (byte)0;
            }
        }
    }
}
#endif
