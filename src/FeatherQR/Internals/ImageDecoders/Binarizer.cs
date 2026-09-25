#if NET8_0_OR_GREATER
using System.Buffers;
using System.Numerics;
#endif
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
#endif

namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// Global image binarization, shared by all three image decoders.
/// </summary>
internal static class Binarizer
{
    /// <summary>
    /// <see cref="ComputeOtsuThreshold(ReadOnlySpan{byte}, out GreyLevels)"/> for callers with no use for the grey levels.
    /// </summary>
    internal static byte ComputeOtsuThreshold(ReadOnlySpan<byte> luminance)
        => ComputeOtsuThreshold(luminance, out _);

    /// <summary>
    /// Otsu's method: picks the threshold that maximizes between-class variance of the luminance histogram, and the grey levels of the two classes it separates, from the same histogram.
    /// Suits clean inputs with clear bimodal contrast.
    /// </summary>
    /// <param name="luminance">Grayscale pixels.</param>
    /// <param name="grey">The levels a pixel between the two classes is read against; disabled when the image holds no such pixel.</param>
    /// <returns>The threshold: a pixel is dark when its luminance is below it.</returns>
    internal static byte ComputeOtsuThreshold(ReadOnlySpan<byte> luminance, out GreyLevels grey)
    {
        Span<int> histogram = stackalloc int[HistogramBins];
        FillHistogram(luminance, histogram);
        return ComputeOtsuThresholdFromHistogram(histogram, out grey);
    }

    /// <summary>
    /// The threshold and the grey levels of <see cref="ComputeOtsuThreshold(ReadOnlySpan{byte}, out GreyLevels)"/> from a histogram already filled: both are functions of the bins alone.
    /// </summary>
    /// <param name="histogram">Counts per luminance, as <see cref="FillHistogram"/> leaves them.</param>
    /// <param name="grey">The levels a pixel between the two classes is read against; disabled when the image holds no such pixel.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="histogram"/> holds fewer than 256 bins.</exception>
    internal static byte ComputeOtsuThresholdFromHistogram(ReadOnlySpan<int> histogram, out GreyLevels grey)
    {
        histogram = histogram.Slice(0, HistogramBins);

        long total = 0;
        long sumAll = 0;
        for (var i = 0; i < 256; i++)
        {
            total += histogram[i];
            sumAll += (long)i * histogram[i];
        }

        long sumBackground = 0;
        long weightBackground = 0;
        var bestVariance = -1.0;
        var bestThreshold = 128;

        for (var t = 0; t < 256; t++)
        {
            weightBackground += histogram[t];
            if (weightBackground == 0)
                continue;
            var weightForeground = total - weightBackground;
            if (weightForeground == 0)
                break;

            sumBackground += (long)t * histogram[t];
            var meanBackground = (double)sumBackground / weightBackground;
            var meanForeground = (double)(sumAll - sumBackground) / weightForeground;
            var diff = meanBackground - meanForeground;
            var variance = weightBackground * (double)weightForeground * diff * diff;

            if (variance > bestVariance)
            {
                bestVariance = variance;
                bestThreshold = t + 1; // dark: luminance < threshold
            }
        }

        var threshold = Math.Min(bestThreshold, 255);
        grey = GreyLevels.FromHistogram(histogram, threshold);
        return (byte)threshold;
    }

    /// <summary>Bins in a luminance histogram; what a caller that keeps one between two calls allocates.</summary>
    internal const int HistogramBins = 256;

    /// <summary>
    /// Turns an image's histogram into its negative's, in place: a pixel of value v is 255 − v there, so bin i moves to 255 − i.
    /// The inverted retry gets its thresholds from this instead of counting the negative's pixels.
    /// </summary>
    /// <remarks>
    /// The threshold is searched again on the result, not mirrored: splits that tie keep the first one found, and the first from the other end is a different split.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="histogram"/> holds fewer than 256 bins.</exception>
    internal static void InvertHistogram(Span<int> histogram)
        => histogram.Slice(0, HistogramBins).Reverse();

    /// <summary>
    /// Counts the pixels per luminance into <paramref name="histogram"/>, overwriting its first 256 bins.
    /// </summary>
    /// <remarks>
    /// A per-pixel <c>histogram[value]++</c> serializes on store-forwarding whenever consecutive pixels hit the same bin, and a rendered symbol is two values in long runs.
    /// The vector tier never sends those two values through memory; the scalar tier folds a uniform group of eight into one addition, which pays only where runs happen to start on a multiple of eight pixels.
    /// ARM64 has its own tier: the same blocks without a movemask, and the dense blocks spread over four sub-histograms, which its larger L1 holds where x64's lost to them.
    /// Every tier produces the same 256 bins, and the threshold and the grey levels are functions of the bins alone.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="histogram"/> holds fewer than 256 bins.</exception>
    internal static void FillHistogram(ReadOnlySpan<byte> luminance, Span<int> histogram)
    {
#if NET8_0_OR_GREATER
        // A 512-bit tier and a portable 128-bit tier were measured and left out; ARM64 has its own tier. See the decoder spec.
        if (Vector256.IsHardwareAccelerated)
        {
            FillHistogramVector256(luminance, histogram);
            return;
        }
        if (AdvSimd.Arm64.IsSupported)
        {
            FillHistogramAdvSimd(luminance, histogram);
            return;
        }
#endif
        FillHistogramScalar(luminance, histogram);
    }

    /// <summary>
    /// Scalar tier: eight pixels off one load, a uniform group folded into one <c>+= 8</c>.
    /// </summary>
    internal static void FillHistogramScalar(ReadOnlySpan<byte> luminance, Span<int> histogram)
    {
        ref var h = ref ClearedBins(histogram);
        ref var p = ref MemoryMarshal.GetReference(luminance);
        var offset = 0;
        for (; offset + 8 <= luminance.Length; offset += 8)
        {
            CountGroup(ref h, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, offset)));
        }
        for (; offset < luminance.Length; offset++)
        {
            Unsafe.Add(ref h, Unsafe.Add(ref p, offset))++;
        }
    }

#if NET8_0_OR_GREATER
    // A block with more pixels than this that are neither 0 nor 255 goes to the scalar groups: walking a full
    // mask costs more a pixel than the increment it replaces. 12 and 20 of 32 measured the same; 6 sent
    // blocks rich in the two counted values back through memory and lost a third on resampled images.
    private const int DenseBlock = 12;

    // After two blocks in a row holding neither counted value, this many blocks are taken untested: a photo-like
    // image is such blocks end to end, and the test in front of every one made its threshold 8 to 11 % slower than
    // the scalar walk alone. A rendered or resampled symbol almost never has such a block, so a stretch does not
    // start on one. 7 left 2 to 3 %; 31 measured level with the scalar walk end to end.
    private const int UntestedBlocks = 31;

    /// <summary>
    /// Vector tier: 32 pixels compared against 0 and against 255, the two masks counted in registers, and only the other pixels sent to the bins.
    /// A two-valued block costs the same wherever the module boundaries fall, which the scalar fold does not.
    /// A block that is mostly other values goes to the scalar groups, and a run of blocks with no counted value at all is taken without the test.
    /// </summary>
    internal static void FillHistogramVector256(ReadOnlySpan<byte> luminance, Span<int> histogram)
    {
        // Why: histogram[v]++ waits on the previous store to the same bin (store-to-load forwarding), and a rendered symbol
        // is 0 and 255 in long runs, so the scalar walk spends most of its time in that chain. Here the two extremes never go
        // through memory: each block is compared against 0 and against 255, the two masks are counted with a popcount, and
        // the two totals go into their bins once, at the end.
        //
        // Each 32-pixel block takes one of three paths:
        //   pure or sparse   12 or fewer other pixels: the extremes counted from the masks, the others walked one by one
        //                    through the third mask (the lowest set bit is the next, clear it, increment its bin)
        //   dense            13 or more: the scalar tier's groups of eight, so a photo-like image costs what it costs there,
        //                    the compares would only be added to it
        //   a stretch        two dense blocks in a row with no 0 and no 255 at all start 31 blocks taken without the test.
        //                    A photo-like image is such blocks end to end, and the test in front of each one is what made it
        //                    slower than the scalar tier; a rendered or resampled symbol almost never has one
        //
        //   block      0   0   0   0  255 255   0   0   0  200 255  ...    32 pixels
        //   isMin      1   1   1   1   0   0    1   1   1   0   0          popcount into minCount
        //   isMax      0   0   0   0   1   1    0   0   0   0   1          popcount into maxCount
        //   others     0   0   0   0   0   0    0   0   0   1   0          one bin increment a set bit
        //
        // The cut-over and the stretch length are the two constants above, with what was measured for them.
        ref var h = ref ClearedBins(histogram);
        ref var p = ref MemoryMarshal.GetReference(luminance);
        var offset = 0;
        var minCount = 0;
        var maxCount = 0;
        var uncounted = 0; // blocks in a row with no 0 and no 255
        var allOnes = Vector256<byte>.AllBitsSet;
        for (; offset + Vector256<byte>.Count <= luminance.Length; offset += Vector256<byte>.Count)
        {
            var block = Vector256.LoadUnsafe(ref p, (nuint)offset);
            var isMin = Vector256.Equals(block, Vector256<byte>.Zero).ExtractMostSignificantBits();
            var isMax = Vector256.Equals(block, allOnes).ExtractMostSignificantBits();
            var others = ~(isMin | isMax);
            if (BitOperations.PopCount(others) > DenseBlock)
            {
                // The shipped scalar groups, so a photo-like image costs what it costs on the scalar tier
                ref var b = ref Unsafe.Add(ref p, offset);
                CountGroup(ref h, Unsafe.ReadUnaligned<ulong>(ref b));
                CountGroup(ref h, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 8)));
                CountGroup(ref h, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 16)));
                CountGroup(ref h, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 24)));

                uncounted = others == uint.MaxValue ? uncounted + 1 : 0;
                if (uncounted >= 2)
                {
                    var end = Math.Min(offset + (1 + UntestedBlocks) * Vector256<byte>.Count, luminance.Length);
                    for (offset += Vector256<byte>.Count; offset + 8 <= end; offset += 8)
                    {
                        CountGroup(ref h, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, offset)));
                    }
                    offset -= Vector256<byte>.Count; // the loop's own step lands on the next untested block
                }
                continue;
            }

            uncounted = 0;
            minCount += BitOperations.PopCount(isMin);
            maxCount += BitOperations.PopCount(isMax);
            while (others != 0)
            {
                Unsafe.Add(ref h, Unsafe.Add(ref p, offset + BitOperations.TrailingZeroCount(others)))++;
                others &= others - 1;
            }
        }
        for (; offset < luminance.Length; offset++)
        {
            Unsafe.Add(ref h, Unsafe.Add(ref p, offset))++;
        }
        Unsafe.Add(ref h, 0) += minCount;
        Unsafe.Add(ref h, 255) += maxCount;
    }

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
#endif

    /// <summary>
    /// One group of eight pixels. Bin order is irrelevant to a histogram, so the load is endian-safe.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CountGroup(ref int h, ulong v)
    {
        // All 8 bytes equal ⟺ rotating by one byte is a fixed point
        // (netstandard has no BitOperations.RotateRight; the JIT emits ror)
        if (v == ((v >> 8) | (v << 56)))
        {
            Unsafe.Add(ref h, (byte)v) += 8;
            return;
        }

        Unsafe.Add(ref h, (byte)v)++;
        Unsafe.Add(ref h, (byte)(v >> 8))++;
        Unsafe.Add(ref h, (byte)(v >> 16))++;
        Unsafe.Add(ref h, (byte)(v >> 24))++;
        Unsafe.Add(ref h, (byte)(v >> 32))++;
        Unsafe.Add(ref h, (byte)(v >> 40))++;
        Unsafe.Add(ref h, (byte)(v >> 48))++;
        Unsafe.Add(ref h, (byte)(v >> 56))++;
    }

    /// <summary>The bins are indexed by a byte through an unchecked reference, which 256 of them make safe; the slice is what refuses fewer.</summary>
    private static ref int ClearedBins(Span<int> histogram)
    {
        histogram = histogram.Slice(0, HistogramBins);
        histogram.Clear();
        return ref MemoryMarshal.GetReference(histogram);
    }
}
