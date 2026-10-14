#if NET8_0_OR_GREATER
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace FeatherQR.Internals.ImageDecoders;

internal static partial class Binarizer
{
    /// <summary>The pixels from which the vector tier may count its dense blocks into four sub-histograms. Under it the rental, the clearing and the merge cost more than the lanes save.</summary>
    internal const int LanedPixels = 1 << 16;

    /// <summary>
    /// Vector tier: 32 pixels compared against 0 and against 255, the two masks counted in registers, and only the other pixels sent to the bins.
    /// A two-valued block costs the same wherever the module boundaries fall, which the scalar fold does not.
    /// A block that is mostly other values goes to the scalar groups, and a run of blocks with no counted value at all is taken without the test.
    /// </summary>
    /// <remarks>
    /// From its first stretch on, an image of <see cref="LanedPixels"/> or more counts those groups into four sub-histograms, pixel i of a group into lane i % 4: an increment to a bin waits on the last increment to it, and neighbouring pixels of a photograph are often the same bin.
    /// The lanes are rented, cleared, merged into the bins at the end and returned. A smaller image, and one with no stretch, counts into the bins alone and rents nothing.
    /// </remarks>
    internal static void FillHistogramVector256(ReadOnlySpan<byte> luminance, Span<int> histogram)
    {
        ref var h = ref ClearedBins(histogram);
        var counted = CountBlocks256(luminance, 0, ref h, ref h, ref h, ref h, stopAtStretch: luminance.Length >= LanedPixels);
        if (counted.Next < luminance.Length)
        {
            var rented = ArrayPool<int>.Shared.Rent(3 * HistogramBins);
            try
            {
                // A rental can hold the counts of the call before
                var lanes = rented.AsSpan(0, 3 * HistogramBins);
                lanes.Clear();
                ref var l1 = ref MemoryMarshal.GetReference(lanes);
                ref var l2 = ref Unsafe.Add(ref l1, HistogramBins);
                ref var l3 = ref Unsafe.Add(ref l1, 2 * HistogramBins);
                var rest = CountBlocks256(luminance, counted.Next, ref h, ref l1, ref l2, ref l3, stopAtStretch: false);
                counted = (counted.Min + rest.Min, counted.Max + rest.Max, rest.Next);
                for (nuint i = 0; i < HistogramBins; i += (nuint)Vector256<int>.Count)
                {
                    (Vector256.LoadUnsafe(ref h, i) + Vector256.LoadUnsafe(ref l1, i) + Vector256.LoadUnsafe(ref l2, i) + Vector256.LoadUnsafe(ref l3, i)).StoreUnsafe(ref h, i);
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(rented);
            }
        }
        Unsafe.Add(ref h, 0) += counted.Min;
        Unsafe.Add(ref h, 255) += counted.Max;
    }

    /// <summary>
    /// The walk itself from <paramref name="offset"/> on, kept out of the method that rents so that no try region reaches the block loop. The four lanes are the bins themselves while none is rented.
    /// With <paramref name="stopAtStretch"/> it hands back where its first stretch would start, for the caller to go on from there with lanes.
    /// </summary>
    /// <returns>The pixels equal to 0 and to 255 that were counted in registers, for the caller to add to their bins, and the first pixel not counted: the length, or where the first stretch starts.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (int Min, int Max, int Next) CountBlocks256(ReadOnlySpan<byte> luminance, int offset, ref int l0, ref int l1, ref int l2, ref int l3, bool stopAtStretch)
    {
        // Each 32-pixel block takes one of three paths:
        //   pure or sparse   12 or fewer other pixels: the extremes counted from the masks, the others walked one by one
        //   dense            13 or more: the scalar tier's groups of eight, since the compares would only add to their cost
        //   a stretch        two dense blocks in a row with no 0 and no 255 at all start 31 blocks taken without the test
        //
        //   block      0   0   0   0  255 255   0   0   0  200 255  ...    32 pixels
        //   isMin      1   1   1   1   0   0    1   1   1   0   0          popcount into minCount
        //   isMax      0   0   0   0   1   1    0   0   0   0   1          popcount into maxCount
        //   others     0   0   0   0   0   0    0   0   0   1   0          one bin increment a set bit
        ref var p = ref MemoryMarshal.GetReference(luminance);
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
                ref var b = ref Unsafe.Add(ref p, offset);
                CountGroupLaned(ref l0, ref l1, ref l2, ref l3, Unsafe.ReadUnaligned<ulong>(ref b));
                CountGroupLaned(ref l0, ref l1, ref l2, ref l3, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 8)));
                CountGroupLaned(ref l0, ref l1, ref l2, ref l3, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 16)));
                CountGroupLaned(ref l0, ref l1, ref l2, ref l3, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 24)));

                uncounted = others == uint.MaxValue ? uncounted + 1 : 0;
                if (uncounted >= 2)
                {
                    if (stopAtStretch)
                        return (minCount, maxCount, offset + Vector256<byte>.Count);
                    var end = Math.Min(offset + (1 + UntestedBlocks) * Vector256<byte>.Count, luminance.Length);
                    for (offset += Vector256<byte>.Count; offset + 8 <= end; offset += 8)
                    {
                        CountGroupLaned(ref l0, ref l1, ref l2, ref l3, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, offset)));
                    }
                    offset -= Vector256<byte>.Count; // the step of the loop lands on the next untested block
                }
                continue;
            }

            uncounted = 0;
            minCount += BitOperations.PopCount(isMin);
            maxCount += BitOperations.PopCount(isMax);
            while (others != 0)
            {
                Unsafe.Add(ref l0, Unsafe.Add(ref p, offset + BitOperations.TrailingZeroCount(others)))++;
                others &= others - 1;
            }
        }
        for (; offset < luminance.Length; offset++)
        {
            Unsafe.Add(ref l0, Unsafe.Add(ref p, offset))++;
        }
        return (minCount, maxCount, offset);
    }
}
#endif
