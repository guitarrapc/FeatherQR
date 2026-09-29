#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Internals.MicroQR;

internal static partial class MicroQRImageDecoder
{
    internal static void SampleGridVector128(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, float originX, float originY, float uX, float uY, float vX, float vY, int size, Span<byte> modules)
    {
        var laneCentres = Vector128.Create(0.5f, 1.5f, 2.5f, 3.5f);
        var columnX = Vector128.Create(uX);
        var columnY = Vector128.Create(uY);
        var maxPx = Vector128.Create(width - 1);
        var maxPy = Vector128.Create(height - 1);
        var stride = Vector128.Create(width);
        Span<int> indices = stackalloc int[4];
        for (var v = 0; v < size; v++)
        {
            var gridV = v + 0.5f;
            var rowX = originX + gridV * vX;
            var rowY = originY + gridV * vY;
            var rowXs = Vector128.Create(rowX);
            var rowYs = Vector128.Create(rowY);
            var rowBase = v * size;

            var u = 0;
            for (; u + 4 <= size; u += 4)
            {
                // u + lane + 0.5 is exact, as the scalar gridU is
                var gridU = laneCentres + Vector128.Create((float)u);
                var px = VectorCast.ToInt32(rowXs + gridU * columnX);
                var py = VectorCast.ToInt32(rowYs + gridU * columnY);
                px = Vector128.Max(Vector128.Min(px, maxPx), Vector128<int>.Zero);
                py = Vector128.Max(Vector128.Min(py, maxPy), Vector128<int>.Zero);
                (py * stride + px).CopyTo(indices);
                modules[rowBase + u] = luminance[indices[0]] < threshold ? (byte)1 : (byte)0;
                modules[rowBase + u + 1] = luminance[indices[1]] < threshold ? (byte)1 : (byte)0;
                modules[rowBase + u + 2] = luminance[indices[2]] < threshold ? (byte)1 : (byte)0;
                modules[rowBase + u + 3] = luminance[indices[3]] < threshold ? (byte)1 : (byte)0;
            }

            for (; u < size; u++)
            {
                var gridU = u + 0.5f;
                var px = PixelIndex.Clamp(rowX + gridU * uX, width);
                var py = PixelIndex.Clamp(rowY + gridU * uY, height);
                modules[rowBase + u] = luminance[py * width + px] < threshold ? (byte)1 : (byte)0;
            }
        }
    }
}
#endif
