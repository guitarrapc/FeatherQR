#if NET8_0_OR_GREATER
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Internals.RmQR;

internal static partial class RmQRImageDecoder
{
    /// <summary>The scalar classification four points at a time: the same expressions and comparisons per lane, the pixel read only where the lane is certain.</summary>
    internal static void ClassifySubFinderLatticeVector128(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, float predictedX, float predictedY, float uX, float uY, float svX, float svY, int side, float marginX, float marginY, Span<ulong> mismatchIfDark, Span<ulong> mismatchIfLight)
    {
        var first = -(side - 1) / 2;
        var laneHalves = Vector128.Create(0f, 0.5f, 1f, 1.5f);
        var columnU = Vector128.Create(uX);
        var columnV = Vector128.Create(uY);
        var marginXs = Vector128.Create(marginX);
        var marginYs = Vector128.Create(marginY);
        var minusOne = Vector128.Create(-1f);
        var widths = Vector128.Create((float)width);
        var heights = Vector128.Create((float)height);
        var stride = Vector128.Create(width);
        var sideMask = side == 64 ? ulong.MaxValue : (1UL << side) - 1;
        Span<int> indices = stackalloc int[4];
        for (var row = 0; row < side; row++)
        {
            var halfV = (first + row) * 0.5f;
            var rowX = Vector128.Create(predictedX + halfV * svX);
            var rowY = Vector128.Create(predictedY + halfV * svY);
            var ifDark = 0UL;
            var ifLight = 0UL;
            // Lanes past the side are classified and masked off below; a certain lane is inside the image whatever its column
            for (var column = 0; column < side; column += 4)
            {
                // (first + column) / 2 plus a lane's half is exact, as the scalar half is
                var halfU = Vector128.Create((first + column) * 0.5f) + laneHalves;
                var x = rowX + halfU * columnU;
                var y = rowY + halfU * columnV;
                var xLow = x - marginXs;
                var xHigh = x + marginXs;
                var yLow = y - marginYs;
                var yHigh = y + marginYs;

                var outside = Vector128.LessThanOrEqual(xHigh, minusOne) | Vector128.GreaterThanOrEqual(xLow, widths)
                    | Vector128.LessThanOrEqual(yHigh, minusOne) | Vector128.GreaterThanOrEqual(yLow, heights);
                var nearBorder = Vector128.LessThanOrEqual(xLow, minusOne) | Vector128.GreaterThanOrEqual(xHigh, widths)
                    | Vector128.LessThanOrEqual(yLow, minusOne) | Vector128.GreaterThanOrEqual(yHigh, heights);
                // Truncating, as the scalar cast does; a lane out of range is near the border and not read
                var pxLow = Vector128.ConvertToInt32(xLow);
                var pyLow = Vector128.ConvertToInt32(yLow);
                var onePixel = Vector128.Equals(pxLow, Vector128.ConvertToInt32(xHigh)) & Vector128.Equals(pyLow, Vector128.ConvertToInt32(yHigh));
                var certain = Vector128.AndNot(onePixel, nearBorder.AsInt32());

                var outsideBits = (ulong)outside.ExtractMostSignificantBits() << column;
                ifDark |= outsideBits;
                ifLight |= outsideBits;
                var certainBits = certain.ExtractMostSignificantBits();
                if (certainBits == 0)
                    continue;
                (pyLow * stride + pxLow).CopyTo(indices);
                while (certainBits != 0)
                {
                    var lane = BitOperations.TrailingZeroCount(certainBits);
                    certainBits &= certainBits - 1;
                    // Branch-free: on texture the pixel's class is a coin toss
                    var dark = (ulong)(luminance[indices[lane]] - threshold) >> 63;
                    ifLight |= dark << (column + lane);
                    ifDark |= (dark ^ 1) << (column + lane);
                }
            }
            mismatchIfDark[row] = ifDark & sideMask;
            mismatchIfLight[row] = ifLight & sideMask;
        }
    }

    /// <summary>
    /// Vector128 grid sampler: 8 module centres per step, with a projective and an affine variant.
    /// Byte-identical to <see cref="SampleGridScalar"/>.
    /// </summary>
    /// <remarks>
    /// Exactness is by construction, and each step is chosen to preserve it: lanes keep the scalar's own association <c>((a1x*gridX) + a2x*gridY) + a3x</c> (folding the last two into a row constant would re-associate), there is no FMA contraction and no reciprocal multiply, and the affine variant only skips a division by exactly <c>1f</c>.
    /// That is also why this is a separate implementation from the Standard QR row kernel, which computes <c>1/d</c> once and multiplies twice: it rounds differently.
    /// The overlapping tail block re-samples up to three already-written modules; it reads the same inputs and writes the same values.
    /// <para>
    /// The clamp is what makes the unchecked gather safe: px and py are pinned to [0, width-1] and [0, height-1].
    /// That relies on the caller having sliced <paramref name="luminance"/> to exactly width * height, as DecodeLuminance does — this method skips the bounds checks the scalar loop keeps, so a short span reads out of bounds here where it would throw there.
    /// </para>
    /// </remarks>
    internal static void SampleGridSimd128(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in PerspectiveTransform transform, int columns, int rows, Span<byte> modules)
    {
        if (!Vector128.IsHardwareAccelerated || columns < Simd128MinColumns)
        {
            SampleGridScalar(luminance, width, height, threshold, transform, columns, rows, modules);
            return;
        }

        // Affine frames have an exactly unit denominator, so both divisions drop out.
        // This is not a heuristic about typical input: TryDecodeFrame builds the isotropic and anisotropic frames with perspectiveX = perspectiveY = 0, so every attempt before the perspective search lands here.
        if (transform.a13 == 0f && transform.a23 == 0f && transform.a33 == 1f)
        {
            SampleGridSimd128Affine(luminance, width, height, threshold, transform, columns, rows, modules);
            return;
        }

        var laneOffsetsLo = Vector128.Create(0.5f, 1.5f, 2.5f, 3.5f);
        var laneOffsetsHi = Vector128.Create(4.5f, 5.5f, 6.5f, 7.5f);
        var a11 = Vector128.Create(transform.a11);
        var a12 = Vector128.Create(transform.a12);
        var a13 = Vector128.Create(transform.a13);
        var a31 = Vector128.Create(transform.a31);
        var a32 = Vector128.Create(transform.a32);
        var a33 = Vector128.Create(transform.a33);
        var zero = Vector128<int>.Zero;
        var maxPx = Vector128.Create(width - 1);
        var maxPy = Vector128.Create(height - 1);
        var widthVector = Vector128.Create(width);

        ref var luminanceRef = ref MemoryMarshal.GetReference(luminance);
        ref var moduleRef = ref MemoryMarshal.GetReference(modules);

        for (var row = 0; row < rows; row++)
        {
            var gridY = row + 0.5f;
            var rowBase = row * columns;
            var rowX = Vector128.Create(transform.a21 * gridY);
            var rowY = Vector128.Create(transform.a22 * gridY);
            var rowDenominator = Vector128.Create(transform.a23 * gridY);

            var column = 0;
            for (; column + 8 <= columns; column += 8)
            {
                var columnVector = Vector128.Create((float)column);
                var gridXLo = laneOffsetsLo + columnVector;
                var gridXHi = laneOffsetsHi + columnVector;
                var denominatorLo = a13 * gridXLo + rowDenominator + a33;
                var denominatorHi = a13 * gridXHi + rowDenominator + a33;
                var xLo = (a11 * gridXLo + rowX + a31) / denominatorLo;
                var yLo = (a12 * gridXLo + rowY + a32) / denominatorLo;
                var xHi = (a11 * gridXHi + rowX + a31) / denominatorHi;
                var yHi = (a12 * gridXHi + rowY + a32) / denominatorHi;

                // ConvertToInt32 truncates toward zero like the scalar cast: the pixel containing the point, not the nearest one.
                var indexLo = Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(yLo), maxPy), zero) * widthVector
                    + Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(xLo), maxPx), zero);
                var indexHi = Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(yHi), maxPy), zero) * widthVector
                    + Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(xHi), maxPx), zero);

                // Lane extraction beats spilling the index vector to the stack: the reload was measured on the critical path of every gather.
                ref var destination = ref Unsafe.Add(ref moduleRef, rowBase + column);
                Unsafe.Add(ref destination, 0) = Unsafe.Add(ref luminanceRef, indexLo.GetElement(0)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 1) = Unsafe.Add(ref luminanceRef, indexLo.GetElement(1)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 2) = Unsafe.Add(ref luminanceRef, indexLo.GetElement(2)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 3) = Unsafe.Add(ref luminanceRef, indexLo.GetElement(3)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 4) = Unsafe.Add(ref luminanceRef, indexHi.GetElement(0)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 5) = Unsafe.Add(ref luminanceRef, indexHi.GetElement(1)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 6) = Unsafe.Add(ref luminanceRef, indexHi.GetElement(2)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 7) = Unsafe.Add(ref luminanceRef, indexHi.GetElement(3)) < threshold ? (byte)1 : (byte)0;
            }

            while (column < columns)
            {
                var start = Math.Min(column, columns - 4);
                var gridX = laneOffsetsLo + Vector128.Create((float)start);
                var denominator = a13 * gridX + rowDenominator + a33;
                var x = (a11 * gridX + rowX + a31) / denominator;
                var y = (a12 * gridX + rowY + a32) / denominator;

                var index = Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(y), maxPy), zero) * widthVector
                    + Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(x), maxPx), zero);

                ref var destination = ref Unsafe.Add(ref moduleRef, rowBase + start);
                Unsafe.Add(ref destination, 0) = Unsafe.Add(ref luminanceRef, index.GetElement(0)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 1) = Unsafe.Add(ref luminanceRef, index.GetElement(1)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 2) = Unsafe.Add(ref luminanceRef, index.GetElement(2)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 3) = Unsafe.Add(ref luminanceRef, index.GetElement(3)) < threshold ? (byte)1 : (byte)0;
                column = start + 4;
            }
        }
    }

    /// <summary>
    /// Affine tier of <see cref="SampleGridSimd128"/>: the denominator is exactly 1f, so x / 1f == x and both divisions are dropped.
    /// Everything else — the hoisted products, the clamp, the overlapping tail — matches the projective kernel, and the sampled bytes match <see cref="SampleGridScalar"/>.
    /// </summary>
    /// <remarks>
    /// A separate method rather than a branch inside the shared loop: this is the shape that was measured, and it keeps the projective kernel's constants out of the affine loop's register budget.
    /// </remarks>
    private static void SampleGridSimd128Affine(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in PerspectiveTransform transform, int columns, int rows, Span<byte> modules)
    {
        var laneOffsetsLo = Vector128.Create(0.5f, 1.5f, 2.5f, 3.5f);
        var laneOffsetsHi = Vector128.Create(4.5f, 5.5f, 6.5f, 7.5f);
        var a11 = Vector128.Create(transform.a11);
        var a12 = Vector128.Create(transform.a12);
        var a31 = Vector128.Create(transform.a31);
        var a32 = Vector128.Create(transform.a32);
        var zero = Vector128<int>.Zero;
        var maxPx = Vector128.Create(width - 1);
        var maxPy = Vector128.Create(height - 1);
        var widthVector = Vector128.Create(width);

        ref var luminanceRef = ref MemoryMarshal.GetReference(luminance);
        ref var moduleRef = ref MemoryMarshal.GetReference(modules);

        for (var row = 0; row < rows; row++)
        {
            var gridY = row + 0.5f;
            var rowBase = row * columns;
            var rowX = Vector128.Create(transform.a21 * gridY);
            var rowY = Vector128.Create(transform.a22 * gridY);

            var column = 0;
            for (; column + 8 <= columns; column += 8)
            {
                var columnVector = Vector128.Create((float)column);
                var gridXLo = laneOffsetsLo + columnVector;
                var gridXHi = laneOffsetsHi + columnVector;
                var xLo = a11 * gridXLo + rowX + a31;
                var yLo = a12 * gridXLo + rowY + a32;
                var xHi = a11 * gridXHi + rowX + a31;
                var yHi = a12 * gridXHi + rowY + a32;

                // ConvertToInt32 truncates toward zero like the scalar cast: the pixel containing the point, not the nearest one.
                var indexLo = Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(yLo), maxPy), zero) * widthVector
                    + Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(xLo), maxPx), zero);
                var indexHi = Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(yHi), maxPy), zero) * widthVector
                    + Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(xHi), maxPx), zero);

                ref var destination = ref Unsafe.Add(ref moduleRef, rowBase + column);
                Unsafe.Add(ref destination, 0) = Unsafe.Add(ref luminanceRef, indexLo.GetElement(0)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 1) = Unsafe.Add(ref luminanceRef, indexLo.GetElement(1)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 2) = Unsafe.Add(ref luminanceRef, indexLo.GetElement(2)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 3) = Unsafe.Add(ref luminanceRef, indexLo.GetElement(3)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 4) = Unsafe.Add(ref luminanceRef, indexHi.GetElement(0)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 5) = Unsafe.Add(ref luminanceRef, indexHi.GetElement(1)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 6) = Unsafe.Add(ref luminanceRef, indexHi.GetElement(2)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 7) = Unsafe.Add(ref luminanceRef, indexHi.GetElement(3)) < threshold ? (byte)1 : (byte)0;
            }

            while (column < columns)
            {
                var start = Math.Min(column, columns - 4);
                var gridX = laneOffsetsLo + Vector128.Create((float)start);
                var x = a11 * gridX + rowX + a31;
                var y = a12 * gridX + rowY + a32;

                var index = Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(y), maxPy), zero) * widthVector
                    + Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(x), maxPx), zero);

                ref var destination = ref Unsafe.Add(ref moduleRef, rowBase + start);
                Unsafe.Add(ref destination, 0) = Unsafe.Add(ref luminanceRef, index.GetElement(0)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 1) = Unsafe.Add(ref luminanceRef, index.GetElement(1)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 2) = Unsafe.Add(ref luminanceRef, index.GetElement(2)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 3) = Unsafe.Add(ref luminanceRef, index.GetElement(3)) < threshold ? (byte)1 : (byte)0;
                column = start + 4;
            }
        }
    }
}
#endif
