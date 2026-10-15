#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace FeatherQR.Internals.ImageDecoders;

internal static partial class LocalBinarizer
{
    /// <summary>What a sum of 25 black points is multiplied by, and how far the product is shifted, to divide it by 25. Exact up to 25 times 255.</summary>
    internal const int NeighbourhoodMultiplier = 5243;
    internal const int NeighbourhoodShift = 17;

    // Loaded at the count of pixels left: the lanes the last, overlapping step of a row is the first to reach
    private static ReadOnlySpan<byte> TailLanes256 =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
    ];

    /// <summary>
    /// One block row's statistics, four blocks and 32 pixels a step. The blocks a whole step does not reach are taken by a step that ends on the last of them, and the last block, when it starts off its own column, by one that ends at the row's end.
    /// </summary>
    private static void MeasureBlocksVector256(ReadOnlySpan<byte> luminance, int width, int top, Span<int> packed)
    {
        // Five block columns at least, so 32 pixels and four blocks on their own columns are there
        ref var origin = ref MemoryMarshal.GetReference(luminance.Slice(top * width, (BlockSize - 1) * width + width));
        ref var target = ref MemoryMarshal.GetReference(packed);
        var stride = (nuint)width;
        var aligned = width / BlockSize;
        var blockX = 0;
        for (; blockX + 4 <= aligned; blockX += 4)
            MeasureFourBlocks(ref origin, (nuint)(blockX * BlockSize), stride).StoreUnsafe(ref target, (nuint)blockX);
        if (blockX != aligned)
            MeasureFourBlocks(ref origin, (nuint)((aligned - 4) * BlockSize), stride).StoreUnsafe(ref target, (nuint)(aligned - 4));
        if (aligned != packed.Length)
            packed[aligned] = MeasureFourBlocks(ref origin, (nuint)(width - 4 * BlockSize), stride).GetElement(3);
    }

    /// <summary>Four blocks side by side: lane-wise minimum and maximum over the eight rows, the sums in 16-bit pairs, then each block's eight bytes folded to one value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> MeasureFourBlocks(ref byte origin, nuint offset, nuint stride)
    {
        var low = Vector256.Create((ushort)0x00FF);
        var row = Vector256.LoadUnsafe(ref origin, offset);
        var min = row;
        var max = row;
        // Two neighbouring pixels a 16-bit lane: 8 rows of 510 at most
        var sums = (row.AsUInt16() & low) + Vector256.ShiftRightLogical(row.AsUInt16(), 8);
        for (var y = 1; y < BlockSize; y++)
        {
            offset += stride;
            row = Vector256.LoadUnsafe(ref origin, offset);
            min = Vector256.Min(min, row);
            max = Vector256.Max(max, row);
            sums += (row.AsUInt16() & low) + Vector256.ShiftRightLogical(row.AsUInt16(), 8);
        }

        var sums32 = (sums.AsUInt32() & Vector256.Create(0x0000FFFFu)) + Vector256.ShiftRightLogical(sums.AsUInt32(), 16);
        var sums64 = (sums32.AsUInt64() & Vector256.Create(0x00000000FFFFFFFFul)) + Vector256.ShiftRightLogical(sums32.AsUInt64(), 32);

        // Each 8-byte block folded onto its lowest byte: the shifts stay inside a 64-bit lane
        var minFold = Vector256.Min(min, Vector256.ShiftRightLogical(min.AsUInt64(), 32).AsByte());
        var maxFold = Vector256.Max(max, Vector256.ShiftRightLogical(max.AsUInt64(), 32).AsByte());
        minFold = Vector256.Min(minFold, Vector256.ShiftRightLogical(minFold.AsUInt64(), 16).AsByte());
        maxFold = Vector256.Max(maxFold, Vector256.ShiftRightLogical(maxFold.AsUInt64(), 16).AsByte());
        minFold = Vector256.Min(minFold, Vector256.ShiftRightLogical(minFold.AsUInt64(), 8).AsByte());
        maxFold = Vector256.Max(maxFold, Vector256.ShiftRightLogical(maxFold.AsUInt64(), 8).AsByte());

        var lowestByte = Vector256.Create(0xFFul);
        var blocks = sums64
            | Vector256.ShiftLeft(minFold.AsUInt64() & lowestByte, MinShift)
            | Vector256.ShiftLeft(maxFold.AsUInt64() & lowestByte, MaxShift);
        // Each 64-bit lane holds 30 bits, so its low half is the block's int
        return Vector128.Narrow(blocks.GetLower(), blocks.GetUpper()).AsInt32();
    }

    /// <summary>
    /// <see cref="ComputeThresholdsScalar"/> eight blocks a step: the five rows added lane-wise, then five neighbouring sums, and the division by 25 as a multiplication.
    /// Each loop's last step ends on its last block, over the step before it.
    /// </summary>
    private static void ComputeThresholdsVector256(ReadOnlySpan<int> blackPoints, int columns, int centreY, Span<int> columnSums, Span<int> thresholds)
    {
        // The second loop reads two sums each side of its eight
        if (columns < Vector256<int>.Count + 2 * Radius)
        {
            ComputeThresholdsScalar(blackPoints, columns, centreY, columnSums, thresholds);
            return;
        }

        ref var rows = ref MemoryMarshal.GetReference(blackPoints.Slice((centreY - Radius) * columns, (2 * Radius + 1) * columns));
        ref var sums = ref MemoryMarshal.GetReference(columnSums.Slice(0, columns));
        ref var target = ref MemoryMarshal.GetReference(thresholds.Slice(0, columns));
        var stride = (nuint)columns;

        var last = columns - Vector256<int>.Count;
        for (var blockX = 0; blockX < columns; blockX += Vector256<int>.Count)
        {
            var at = (nuint)Math.Min(blockX, last);
            (Vector256.LoadUnsafe(ref rows, at) + Vector256.LoadUnsafe(ref rows, at + stride) + Vector256.LoadUnsafe(ref rows, at + 2 * stride)
                + Vector256.LoadUnsafe(ref rows, at + 3 * stride) + Vector256.LoadUnsafe(ref rows, at + 4 * stride)).StoreUnsafe(ref sums, at);
        }

        var multiplier = Vector256.Create(NeighbourhoodMultiplier);
        last = columns - Radius - Vector256<int>.Count;
        for (var blockX = Radius; blockX < columns - Radius; blockX += Vector256<int>.Count)
        {
            var at = (nuint)Math.Min(blockX, last);
            var sum = Vector256.LoadUnsafe(ref sums, at - 2) + Vector256.LoadUnsafe(ref sums, at - 1) + Vector256.LoadUnsafe(ref sums, at)
                + Vector256.LoadUnsafe(ref sums, at + 1) + Vector256.LoadUnsafe(ref sums, at + 2);
            Vector256.ShiftRightLogical(sum * multiplier, NeighbourhoodShift).StoreUnsafe(ref target, at);
        }
        SpreadEdgeThresholds(thresholds);
    }

    /// <summary>
    /// <see cref="WriteRowsScalar"/> 32 pixels a step. A row's last step ends at its last pixel, over the step before it, and counts only the pixels that one did not reach.
    /// </summary>
    private static void WriteRowsVector256(ReadOnlySpan<byte> luminance, Span<byte> binarized, int width, int top, int bottom, byte flip, byte globalThreshold, ReadOnlySpan<byte> thresholdRow, ref int light, ref bool differs)
    {
        // Five block columns at least, so a row holds a whole step
        ref var source = ref MemoryMarshal.GetReference(luminance.Slice(top * width, (bottom - top) * width));
        ref var target = ref MemoryMarshal.GetReference(binarized.Slice(top * width, (bottom - top) * width));
        ref var thresholds = ref MemoryMarshal.GetReference(thresholdRow.Slice(0, width));
        var flipLanes = Vector256.Create(flip);
        // Dark for the global threshold is below it, so light is above the value before it. At 0 nothing is dark, and the count of dark pixels answers instead
        var globalLanes = Vector256.Create((byte)Math.Max(globalThreshold - 1, 0));
        var mismatch = Vector256<byte>.Zero;
        var lightCount = 0;
        var lastStep = width - Vector256<byte>.Count;
        var tail = width % Vector256<byte>.Count;
        var tailLanes = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(TailLanes256), (nuint)tail);

        nuint rowStart = 0;
        for (var y = top; y < bottom; y++, rowStart += (nuint)width)
        {
            var x = 0;
            while (x <= lastStep)
            {
                // Byte counters hold 255 steps before they are widened out
                var counts = Vector256<byte>.Zero;
                var stop = Math.Min(lastStep, x + 254 * Vector256<byte>.Count);
                for (; x <= stop; x += Vector256<byte>.Count)
                {
                    var pixels = Vector256.LoadUnsafe(ref source, rowStart + (nuint)x) ^ flipLanes;
                    var isLight = Vector256.GreaterThan(pixels, Vector256.LoadUnsafe(ref thresholds, (nuint)x));
                    isLight.StoreUnsafe(ref target, rowStart + (nuint)x);
                    counts -= isLight; // all bits set is -1
                    mismatch |= isLight ^ Vector256.GreaterThan(pixels, globalLanes);
                }
                lightCount += Vector256.Sum(Vector256.WidenLower(counts)) + Vector256.Sum(Vector256.WidenUpper(counts));
            }

            if (tail != 0)
            {
                var pixels = Vector256.LoadUnsafe(ref source, rowStart + (nuint)lastStep) ^ flipLanes;
                var isLight = Vector256.GreaterThan(pixels, Vector256.LoadUnsafe(ref thresholds, (nuint)lastStep));
                isLight.StoreUnsafe(ref target, rowStart + (nuint)lastStep);
                var counts = Vector256<byte>.Zero - (isLight & tailLanes);
                mismatch |= isLight ^ Vector256.GreaterThan(pixels, globalLanes);
                lightCount += Vector256.Sum(Vector256.WidenLower(counts)) + Vector256.Sum(Vector256.WidenUpper(counts));
            }
        }

        light += lightCount;
        differs |= globalThreshold == 0 ? lightCount != (bottom - top) * width : mismatch != Vector256<byte>.Zero;
    }
}
#endif
