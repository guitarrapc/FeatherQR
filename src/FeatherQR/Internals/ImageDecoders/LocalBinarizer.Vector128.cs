#if NET8_0_OR_GREATER
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace FeatherQR.Internals.ImageDecoders;

internal static partial class LocalBinarizer
{
    /// <summary>Two blocks a step, 16 pixels wide: lane-wise min, max and widened sums over the eight rows, then each half folded to one value. Returns the first block left to the scalar loop.</summary>
    private static int BlockStatsVector128(ReadOnlySpan<byte> luminance, int width, byte flip, int top, int alignedColumns, Span<int> packed)
    {
        if (!Vector128.IsHardwareAccelerated)
            return 0;
        ref var origin = ref MemoryMarshal.GetReference(luminance);
        var flipLanes = Vector128.Create(flip);
        var blockX = 0;
        for (; blockX + 2 <= alignedColumns; blockX += 2)
        {
            var offset = (nuint)(top * width + blockX * BlockSize);
            var first = Vector128.LoadUnsafe(ref origin, offset) ^ flipLanes;
            var min = first;
            var max = first;
            var sumLow = Vector128.WidenLower(first);
            var sumHigh = Vector128.WidenUpper(first);
            for (var y = 1; y < BlockSize; y++)
            {
                var row = Vector128.LoadUnsafe(ref origin, offset + (nuint)(y * width)) ^ flipLanes;
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
            packed[blockX] = Pack((int)(minFold.GetElement(0) & 0xFF), (int)(maxFold.GetElement(0) & 0xFF), Vector128.Sum(sumLow));
            packed[blockX + 1] = Pack((int)(minFold.GetElement(1) & 0xFF), (int)(maxFold.GetElement(1) & 0xFF), Vector128.Sum(sumHigh));
        }
        return blockX;
    }

    /// <summary>Two blocks a step: each half compared with its own block's threshold. Returns the first block left to the scalar loop.</summary>
    private static int WriteBlocksVector128(ReadOnlySpan<byte> luminance, Span<byte> binarized, int width, byte flip, int top, int alignedColumns, ReadOnlySpan<int> thresholds)
    {
        if (!Vector128.IsHardwareAccelerated)
            return 0;
        ref var source = ref MemoryMarshal.GetReference(luminance);
        ref var target = ref MemoryMarshal.GetReference(binarized);
        var flipLanes = Vector128.Create(flip);
        var blockX = 0;
        for (; blockX + 2 <= alignedColumns; blockX += 2)
        {
            // Thresholds are means of bytes, so they fit a byte; dark is luminance <= threshold
            var threshold = Vector128.Create(Vector64.Create((byte)thresholds[blockX]), Vector64.Create((byte)thresholds[blockX + 1]));
            var offset = (nuint)(top * width + blockX * BlockSize);
            for (var y = 0; y < BlockSize; y++, offset += (nuint)width)
            {
                var pixels = Vector128.LoadUnsafe(ref source, offset) ^ flipLanes;
                Vector128.OnesComplement(Vector128.Equals(Vector128.Min(pixels, threshold), pixels)).StoreUnsafe(ref target, offset);
            }
        }
        return blockX;
    }

    /// <summary>Dark pixels, and whether any lands in the other class than the global threshold puts it, 16 pixels a step. Returns the first pixel left to the scalar loop.</summary>
    private static int CountVector128(ReadOnlySpan<byte> luminance, ReadOnlySpan<byte> binarized, byte flip, byte globalThreshold, ref int dark, ref bool differs)
    {
        if (!Vector128.IsHardwareAccelerated)
            return 0;
        ref var source = ref MemoryMarshal.GetReference(luminance);
        ref var target = ref MemoryMarshal.GetReference(binarized);
        // luminance < g is min(luminance, g - 1) == luminance, with nothing dark at g = 0
        var globalMinus1 = Vector128.Create((byte)(globalThreshold == 0 ? 0 : globalThreshold - 1));
        var globalNone = globalThreshold == 0 ? Vector128<byte>.AllBitsSet : Vector128<byte>.Zero;
        var flipLanes = Vector128.Create(flip);
        var mismatch = Vector128<byte>.Zero;
        var i = 0;
        var last = binarized.Length - Vector128<byte>.Count;
        while (i <= last)
        {
            // Byte counters hold up to 255 steps before they are widened out
            var counts = Vector128<byte>.Zero;
            var stop = Math.Min(last, i + 254 * Vector128<byte>.Count);
            for (; i <= stop; i += Vector128<byte>.Count)
            {
                var pixels = Vector128.LoadUnsafe(ref source, (nuint)i) ^ flipLanes;
                var isDark = Vector128.Equals(Vector128.LoadUnsafe(ref target, (nuint)i), Vector128<byte>.Zero);
                var globalDark = Vector128.AndNot(Vector128.Equals(Vector128.Min(pixels, globalMinus1), pixels), globalNone);
                mismatch |= isDark ^ globalDark;
                counts -= isDark; // all bits set is -1
            }
            dark += (int)(Vector128.Sum(Vector128.WidenLower(counts)) + Vector128.Sum(Vector128.WidenUpper(counts)));
        }
        differs |= mismatch != Vector128<byte>.Zero;
        return i;
    }
}
#endif
