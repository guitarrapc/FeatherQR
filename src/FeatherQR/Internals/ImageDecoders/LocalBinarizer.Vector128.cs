#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace FeatherQR.Internals.ImageDecoders;

internal static partial class LocalBinarizer
{
    // Loaded at the count of pixels left: the lanes the last, overlapping step of a row is the first to reach
    private static ReadOnlySpan<byte> TailLanes128 =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
    ];

    /// <summary>
    /// One block row's statistics, two blocks and sixteen pixels a step. A block a whole step does not reach is taken by a step that ends on it, and the last block, when it starts off its own column, by one that ends at the row's end.
    /// </summary>
    private static void MeasureBlocksVector128(ReadOnlySpan<byte> luminance, int width, int top, Span<int> packed)
    {
        // Five block columns at least, so sixteen pixels and two blocks on their own columns are there
        ref var origin = ref MemoryMarshal.GetReference(luminance.Slice(top * width, (BlockSize - 1) * width + width));
        var stride = (nuint)width;
        var aligned = width / BlockSize;
        var blockX = 0;
        for (; blockX + 2 <= aligned; blockX += 2)
            MeasureTwoBlocks(ref origin, (nuint)(blockX * BlockSize), stride, out packed[blockX], out packed[blockX + 1]);
        if (blockX != aligned)
            MeasureTwoBlocks(ref origin, (nuint)((aligned - 2) * BlockSize), stride, out _, out packed[aligned - 1]);
        if (aligned != packed.Length)
            MeasureTwoBlocks(ref origin, (nuint)(width - 2 * BlockSize), stride, out _, out packed[aligned]);
    }

    /// <summary>Two blocks side by side: lane-wise minimum, maximum and widened sums over the eight rows, then each half folded to one value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void MeasureTwoBlocks(ref byte origin, nuint offset, nuint stride, out int first, out int second)
    {
        var row = Vector128.LoadUnsafe(ref origin, offset);
        var min = row;
        var max = row;
        var sumLow = Vector128.WidenLower(row);
        var sumHigh = Vector128.WidenUpper(row);
        for (var y = 1; y < BlockSize; y++)
        {
            offset += stride;
            row = Vector128.LoadUnsafe(ref origin, offset);
            min = Vector128.Min(min, row);
            max = Vector128.Max(max, row);
            sumLow += Vector128.WidenLower(row);
            sumHigh += Vector128.WidenUpper(row);
        }
        // Each 8-byte half folded onto its lowest byte: the shifts stay inside a 64-bit lane
        var minFold = min.AsUInt64();
        var maxFold = max.AsUInt64();
        for (var shift = 32; shift >= 8; shift >>= 1)
        {
            minFold = Vector128.Min(minFold.AsByte(), Vector128.ShiftRightLogical(minFold, shift).AsByte()).AsUInt64();
            maxFold = Vector128.Max(maxFold.AsByte(), Vector128.ShiftRightLogical(maxFold, shift).AsByte()).AsUInt64();
        }
        first = Vector128.Sum(sumLow) | (int)(minFold.GetElement(0) & 0xFF) << MinShift | (int)(maxFold.GetElement(0) & 0xFF) << MaxShift;
        second = Vector128.Sum(sumHigh) | (int)(minFold.GetElement(1) & 0xFF) << MinShift | (int)(maxFold.GetElement(1) & 0xFF) << MaxShift;
    }

    /// <summary>
    /// <see cref="WriteRowsScalar"/> sixteen pixels a step. A row's last step ends at its last pixel, over the step before it, and counts only the pixels that one did not reach.
    /// </summary>
    private static void WriteRowsVector128(ReadOnlySpan<byte> luminance, Span<byte> binarized, int width, int top, int bottom, byte flip, byte globalThreshold, ReadOnlySpan<byte> thresholdRow, ref int light, ref bool differs)
    {
        // Five block columns at least, so a row holds a whole step
        ref var source = ref MemoryMarshal.GetReference(luminance.Slice(top * width, (bottom - top) * width));
        ref var target = ref MemoryMarshal.GetReference(binarized.Slice(top * width, (bottom - top) * width));
        ref var thresholds = ref MemoryMarshal.GetReference(thresholdRow.Slice(0, width));
        var flipLanes = Vector128.Create(flip);
        // luminance < g is min(luminance, g - 1) == luminance, with nothing dark at g = 0
        var globalMinus1 = Vector128.Create((byte)Math.Max(globalThreshold - 1, 0));
        var globalNone = globalThreshold == 0 ? Vector128<byte>.AllBitsSet : Vector128<byte>.Zero;
        var mismatch = Vector128<byte>.Zero;
        var darkCount = 0;
        var lastStep = width - Vector128<byte>.Count;
        var tail = width % Vector128<byte>.Count;
        var tailLanes = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(TailLanes128), (nuint)tail);

        nuint rowStart = 0;
        for (var y = top; y < bottom; y++, rowStart += (nuint)width)
        {
            var x = 0;
            while (x <= lastStep)
            {
                // Byte counters hold 255 steps before they are widened out
                var counts = Vector128<byte>.Zero;
                var stop = Math.Min(lastStep, x + 254 * Vector128<byte>.Count);
                for (; x <= stop; x += Vector128<byte>.Count)
                {
                    var pixels = Vector128.LoadUnsafe(ref source, rowStart + (nuint)x) ^ flipLanes;
                    // Dark is luminance <= threshold
                    var isDark = Vector128.Equals(Vector128.Min(pixels, Vector128.LoadUnsafe(ref thresholds, (nuint)x)), pixels);
                    Vector128.OnesComplement(isDark).StoreUnsafe(ref target, rowStart + (nuint)x);
                    counts -= isDark; // all bits set is -1
                    mismatch |= isDark ^ Vector128.AndNot(Vector128.Equals(Vector128.Min(pixels, globalMinus1), pixels), globalNone);
                }
                darkCount += Vector128.Sum(Vector128.WidenLower(counts)) + Vector128.Sum(Vector128.WidenUpper(counts));
            }

            if (tail != 0)
            {
                var pixels = Vector128.LoadUnsafe(ref source, rowStart + (nuint)lastStep) ^ flipLanes;
                var isDark = Vector128.Equals(Vector128.Min(pixels, Vector128.LoadUnsafe(ref thresholds, (nuint)lastStep)), pixels);
                Vector128.OnesComplement(isDark).StoreUnsafe(ref target, rowStart + (nuint)lastStep);
                var counts = Vector128<byte>.Zero - (isDark & tailLanes);
                mismatch |= isDark ^ Vector128.AndNot(Vector128.Equals(Vector128.Min(pixels, globalMinus1), pixels), globalNone);
                darkCount += Vector128.Sum(Vector128.WidenLower(counts)) + Vector128.Sum(Vector128.WidenUpper(counts));
            }
        }

        light += (bottom - top) * width - darkCount;
        differs |= mismatch != Vector128<byte>.Zero;
    }
}
#endif
