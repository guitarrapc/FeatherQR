#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Internals.RmQR;

internal static partial class RmQRImageDecoder
{
    /// <summary><see cref="ClassifySubFinderLatticeVector128"/> eight points at a time.</summary>
    internal static void ClassifySubFinderLatticeVector256(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, float predictedX, float predictedY, float uX, float uY, float svX, float svY, int side, float marginX, float marginY, Span<ulong> mismatchIfDark, Span<ulong> mismatchIfLight)
    {
        var first = -(side - 1) / 2;
        var laneHalves = Vector256.Create(0f, 0.5f, 1f, 1.5f, 2f, 2.5f, 3f, 3.5f);
        var columnU = Vector256.Create(uX);
        var columnV = Vector256.Create(uY);
        var marginXs = Vector256.Create(marginX);
        var marginYs = Vector256.Create(marginY);
        var minusOne = Vector256.Create(-1f);
        var widths = Vector256.Create((float)width);
        var heights = Vector256.Create((float)height);
        var stride = Vector256.Create(width);
        var level = (int)threshold;
        var sideMask = side == 64 ? ulong.MaxValue : (1UL << side) - 1;
        for (var row = 0; row < side; row++)
        {
            var halfV = (first + row) * 0.5f;
            var rowX = Vector256.Create(predictedX + halfV * svX);
            var rowY = Vector256.Create(predictedY + halfV * svY);
            var ifDark = 0UL;
            var ifLight = 0UL;
            for (var column = 0; column < side; column += 8)
            {
                var halfU = Vector256.Create((first + column) * 0.5f) + laneHalves;
                var x = rowX + halfU * columnU;
                var y = rowY + halfU * columnV;
                var xLow = x - marginXs;
                var xHigh = x + marginXs;
                var yLow = y - marginYs;
                var yHigh = y + marginYs;

                var outside = Vector256.LessThanOrEqual(xHigh, minusOne) | Vector256.GreaterThanOrEqual(xLow, widths)
                    | Vector256.LessThanOrEqual(yHigh, minusOne) | Vector256.GreaterThanOrEqual(yLow, heights);
                var inside = Vector256.GreaterThan(xLow, minusOne) & Vector256.LessThan(xHigh, widths)
                    & Vector256.GreaterThan(yLow, minusOne) & Vector256.LessThan(yHigh, heights);
                var pxLow = VectorCast.ToInt32Native(xLow);
                var pyLow = VectorCast.ToInt32Native(yLow);
                var onePixel = Vector256.Equals(pxLow, VectorCast.ToInt32Native(xHigh)) & Vector256.Equals(pyLow, VectorCast.ToInt32Native(yHigh));
                var certain = onePixel & inside.AsInt32();

                var outsideBits = (ulong)outside.ExtractMostSignificantBits() << column;
                ifDark |= outsideBits;
                ifLight |= outsideBits;
                var certainBits = certain.ExtractMostSignificantBits();
                if (certainBits == 0)
                    continue;
                var index = Vector256.ConditionalSelect(certain, pyLow * stride + pxLow, Vector256<int>.Zero);
                // Each half taken once: lane by lane from the whole vector, the upper half is extracted again for each of its four lanes
                var low = index.GetLower();
                var high = index.GetUpper();
                var dark = (uint)(luminance[low.GetElement(0)] - level) >> 31
                    | (uint)(luminance[low.GetElement(1)] - level) >> 31 << 1
                    | (uint)(luminance[low.GetElement(2)] - level) >> 31 << 2
                    | (uint)(luminance[low.GetElement(3)] - level) >> 31 << 3
                    | (uint)(luminance[high.GetElement(0)] - level) >> 31 << 4
                    | (uint)(luminance[high.GetElement(1)] - level) >> 31 << 5
                    | (uint)(luminance[high.GetElement(2)] - level) >> 31 << 6
                    | (uint)(luminance[high.GetElement(3)] - level) >> 31 << 7;
                ifLight |= (ulong)(dark & certainBits) << column;
                ifDark |= (ulong)(~dark & certainBits) << column;
            }
            mismatchIfDark[row] = ifDark & sideMask;
            mismatchIfLight[row] = ifLight & sideMask;
        }
    }

    /// <summary><see cref="CountSubFinderSurvivorsScalar"/> four position rows at a time, while four are left.</summary>
    /// <returns>The first position row not counted.</returns>
    internal static int CountSubFinderSurvivorsVector256(ReadOnlySpan<ulong> mismatchIfDark, ReadOnlySpan<ulong> mismatchIfLight, int positions, Span<ulong> survivors)
    {
        var positionMask = Vector256.Create((1UL << positions) - 1);
        var p = 0;
        for (; p + 4 <= positions; p += 4)
        {
            var one = Vector256<ulong>.Zero;
            var two = Vector256<ulong>.Zero;
            var dark0 = Vector256.Create(mismatchIfDark.Slice(p, 4));
            var dark1 = Vector256.Create(mismatchIfDark.Slice(p + 2, 4));
            var light1 = Vector256.Create(mismatchIfLight.Slice(p + 2, 4));
            var dark2 = Vector256.Create(mismatchIfDark.Slice(p + 4, 4));
            var light2 = Vector256.Create(mismatchIfLight.Slice(p + 4, 4));
            var dark3 = Vector256.Create(mismatchIfDark.Slice(p + 6, 4));
            var light3 = Vector256.Create(mismatchIfLight.Slice(p + 6, 4));
            var dark4 = Vector256.Create(mismatchIfDark.Slice(p + 8, 4));
            CountTemplateRow(ref one, ref two, dark0, dark0, dark0);
            CountTemplateRow(ref one, ref two, dark1, light1, light1);
            CountTemplateRow(ref one, ref two, dark2, light2, dark2);
            CountTemplateRow(ref one, ref two, dark3, light3, light3);
            CountTemplateRow(ref one, ref two, dark4, dark4, dark4);
            Vector256.AndNot(positionMask, two).CopyTo(survivors.Slice(p, 4));
        }
        return p;
    }

    /// <inheritdoc cref="CountTemplateRow(ref ulong, ref ulong, ulong, ulong, ulong)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CountTemplateRow(ref Vector256<ulong> one, ref Vector256<ulong> two, Vector256<ulong> ends, Vector256<ulong> inner, Vector256<ulong> centre)
    {
        CountMismatch(ref one, ref two, ends);
        CountMismatch(ref one, ref two, Vector256.ShiftRightLogical(inner, 2));
        CountMismatch(ref one, ref two, Vector256.ShiftRightLogical(centre, 4));
        CountMismatch(ref one, ref two, Vector256.ShiftRightLogical(inner, 6));
        CountMismatch(ref one, ref two, Vector256.ShiftRightLogical(ends, 8));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CountMismatch(ref Vector256<ulong> one, ref Vector256<ulong> two, Vector256<ulong> mismatch)
    {
        two |= one & mismatch;
        one |= mismatch;
    }
}
#endif
