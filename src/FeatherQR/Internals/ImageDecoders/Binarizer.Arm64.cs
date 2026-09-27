#if NET8_0_OR_GREATER
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace FeatherQR.Internals.ImageDecoders;

internal static partial class Binarizer
{
    // The ARM64 tier's zero counters are bytes and a sparse block adds at most two to a lane, so they are drained before 128 blocks.
    private const int ZeroCounterBlocks = 120;

    /// <summary>
    /// ARM64 tier: the vector tier's blocks on two 128-bit loads, with what ARM64 makes cheap and dear taken into account.
    /// There is no movemask, so the extremes of a block are counted by one add across its two compare masks, the zeros are summed in byte counters, and a mask of the other pixels is built, one bit a byte by a narrowing shift, only for a sparse block that has any.
    /// The dense blocks and the untested stretch count into four sub-histograms, pixel i of a group into lane i % 4: an increment to a bin waits on the last increment to it, and four lanes cut that chain to a quarter.
    /// The lanes are rented, cleared, merged into the bins at the end and returned.
    /// </summary>
    internal static void FillHistogramAdvSimd(ReadOnlySpan<byte> luminance, Span<int> histogram)
    {
        // Why: histogram[v]++ waits on the previous store to the same bin, and a rendered symbol is 0 and 255
        // in long runs; that chain costs this core twice what it costs x64. So 0 and 255 are counted in
        // registers, and every other pixel goes to one of four lanes, which cuts the chain to a quarter.
        //
        // Each 32-pixel block (two 128-bit loads) takes one of three paths:
        //   pure   (all 0 or 255)   : counted in registers only, nothing stored
        //   sparse (1-12 others)    : 0 and 255 counted in registers, the others walked one by one
        //   dense  (13 or more)     : eight pixels a load into four lanes; two dense blocks in a row
        //                             start a stretch of 31 blocks taken without the test
        //
        // What ARM64 makes cheap and dear:
        //   - No movemask, and a general-register popcount round-trips through the vector unit. A block's
        //     extremes are counted by one add across the compare masks instead (lanes are 0, -1 or -2).
        //   - Zeros accumulate in byte counters, drained every 120 blocks; the 255s are what is left.
        //   - The other-pixel mask (one bit a byte, from a narrowing shift) is built only when a sparse block has any.
        //   - Four lanes, pixel i into lane i % 4, fit this core's 128 KB L1 where they lost on x64. Each lane
        //     is its own reference: one base plus 256·k makes the JIT sign-extend on the way to every load.
        //   - The 4 KB of lanes is past the stack budget, so it is rented once a call, cleared (a rental can
        //     hold the previous call's counts), merged into the bins at the end and returned.
        histogram = histogram.Slice(0, HistogramBins);
        var rented = ArrayPool<int>.Shared.Rent(4 * HistogramBins);
        try
        {
            CountIntoLanes(luminance, histogram, rented.AsSpan(0, 4 * HistogramBins));
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// The walk itself, in its own method so the rental's try region does not reach the block loop: a loop inside one holds its vectors in registers less readily, and this tier was measured without it.
    /// Kept out of line: under dynamic PGO the Tier1 caller inlined it back into the try region, and measured alone the dense inputs lost 10 to 33 % to the scalar tier.
    /// </summary>
    /// <param name="luminance">Grayscale pixels.</param>
    /// <param name="histogram">Receives the 256 bins, overwritten.</param>
    /// <param name="lanes">The four sub-histograms, cleared here: a rental can hold the counts of the call before.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CountIntoLanes(ReadOnlySpan<byte> luminance, Span<int> histogram, Span<int> lanes)
    {
        lanes.Clear();
        ref var l0 = ref MemoryMarshal.GetReference(lanes);
        ref var l1 = ref Unsafe.Add(ref l0, HistogramBins);
        ref var l2 = ref Unsafe.Add(ref l0, 2 * HistogramBins);
        ref var l3 = ref Unsafe.Add(ref l0, 3 * HistogramBins);
        ref var p = ref MemoryMarshal.GetReference(luminance);
        var offset = 0;
        var minCount = 0;
        var extremes = 0; // zeros and 255s of the sparse blocks together
        var uncounted = 0; // blocks in a row with no 0 and no 255
        var allOnes = Vector128<byte>.AllBitsSet;
        var zeroCounters = Vector128<byte>.Zero;
        var pending = 0;
        for (; offset + 32 <= luminance.Length; offset += 32)
        {
            var lo = Vector128.LoadUnsafe(ref p, (nuint)offset);
            var hi = Vector128.LoadUnsafe(ref p, (nuint)offset + 16);
            var zerosLo = Vector128.Equals(lo, Vector128<byte>.Zero);
            var zerosHi = Vector128.Equals(hi, Vector128<byte>.Zero);
            var extremesLo = zerosLo | Vector128.Equals(lo, allOnes);
            var extremesHi = zerosHi | Vector128.Equals(hi, allOnes);
            // A lane of the sum is 0, -1 or -2, so the byte sum is minus the block's extremes, of which there are at most 32
            var others = 32 - (byte)-AdvSimd.Arm64.AddAcross(extremesLo + extremesHi).ToScalar();
            if (others > DenseBlock)
            {
                ref var b = ref Unsafe.Add(ref p, offset);
                CountGroupLaned(ref l0, ref l1, ref l2, ref l3, Unsafe.ReadUnaligned<ulong>(ref b));
                CountGroupLaned(ref l0, ref l1, ref l2, ref l3, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 8)));
                CountGroupLaned(ref l0, ref l1, ref l2, ref l3, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 16)));
                CountGroupLaned(ref l0, ref l1, ref l2, ref l3, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 24)));

                uncounted = others == 32 ? uncounted + 1 : 0;
                if (uncounted >= 2)
                {
                    var end = Math.Min(offset + (1 + UntestedBlocks) * 32, luminance.Length);
                    for (offset += 32; offset + 8 <= end; offset += 8)
                    {
                        CountGroupLaned(ref l0, ref l1, ref l2, ref l3, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, offset)));
                    }
                    offset -= 32; // the loop's own step lands on the next untested block
                }
                continue;
            }

            uncounted = 0;
            extremes += 32 - others;
            // A zero's lane is 0xFF, so subtracting it counts one
            zeroCounters = zeroCounters - zerosLo - zerosHi;
            if (++pending == ZeroCounterBlocks)
            {
                minCount += AdvSimd.Arm64.AddAcrossWidening(zeroCounters).ToScalar();
                zeroCounters = Vector128<byte>.Zero;
                pending = 0;
            }
            if (others == 0)
                continue;

            var othersLo = OneBitPerByte(~extremesLo);
            var othersHi = OneBitPerByte(~extremesHi);
            while (othersLo != 0)
            {
                Unsafe.Add(ref l0, Unsafe.Add(ref p, offset + (BitOperations.TrailingZeroCount(othersLo) >> 2)))++;
                othersLo &= othersLo - 1;
            }
            while (othersHi != 0)
            {
                Unsafe.Add(ref l0, Unsafe.Add(ref p, offset + 16 + (BitOperations.TrailingZeroCount(othersHi) >> 2)))++;
                othersHi &= othersHi - 1;
            }
        }
        minCount += AdvSimd.Arm64.AddAcrossWidening(zeroCounters).ToScalar();
        for (; offset < luminance.Length; offset++)
        {
            Unsafe.Add(ref l0, Unsafe.Add(ref p, offset))++;
        }

        ref var h = ref MemoryMarshal.GetReference(histogram);
        for (nuint i = 0; i < HistogramBins; i += 4)
        {
            (Vector128.LoadUnsafe(ref l0, i) + Vector128.LoadUnsafe(ref l1, i) + Vector128.LoadUnsafe(ref l2, i) + Vector128.LoadUnsafe(ref l3, i)).StoreUnsafe(ref h, i);
        }
        Unsafe.Add(ref h, 0) += minCount;
        Unsafe.Add(ref h, 255) += extremes - minCount;
    }

    /// <summary>Sixteen compare lanes (0 or 0xFF) to a 64-bit mask with lane j at bit 4j.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong OneBitPerByte(Vector128<byte> lanes)
    {
        // No movemask on ARM64. A narrowing shift does it for two lanes at once: read the lanes as eight 16-bit pairs, keep
        // bits 0 and 4 of each byte (the AND with 0x11), shift each pair right by 4 and keep its low byte. The low lane's bit 4
        // lands at bit 0, the high lane's bit 0, which sat at bit 8 of the pair, lands at bit 4, and the high lane's bit 4 is
        // shifted out by the narrowing. The eight bytes are the mask; the caller finds a lane with tzcnt and divides by 4.
        //
        //   pair (high, low)   FF FF     FF 00     00 FF     00 00
        //   & 0x11             11 11     11 00     00 11     00 00
        //   >> 4, low byte     0x11      0x10      0x01      0x00     both, high only, low only, neither
        return AdvSimd.ShiftRightLogicalNarrowingLower((lanes & Vector128.Create((byte)0x11)).AsUInt16(), 4).AsUInt64().ToScalar();
    }

    /// <summary>
    /// <see cref="CountGroup"/> over four sub-histograms: pixel i into lane i % 4, a uniform group into lane 0.
    /// Each lane through its own reference: an offset from one base makes the JIT sign-extend the sum on the way to every load.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CountGroupLaned(ref int l0, ref int l1, ref int l2, ref int l3, ulong v)
    {
        if (v == ((v >> 8) | (v << 56)))
        {
            Unsafe.Add(ref l0, (byte)v) += 8;
            return;
        }

        Unsafe.Add(ref l0, (byte)v)++;
        Unsafe.Add(ref l1, (byte)(v >> 8))++;
        Unsafe.Add(ref l2, (byte)(v >> 16))++;
        Unsafe.Add(ref l3, (byte)(v >> 24))++;
        Unsafe.Add(ref l0, (byte)(v >> 32))++;
        Unsafe.Add(ref l1, (byte)(v >> 40))++;
        Unsafe.Add(ref l2, (byte)(v >> 48))++;
        Unsafe.Add(ref l3, (byte)(v >> 56))++;
    }
}
#endif
