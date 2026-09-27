#if NET8_0_OR_GREATER
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace FeatherQR.Internals.ImageDecoders;

internal static partial class Binarizer
{
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
}
#endif
