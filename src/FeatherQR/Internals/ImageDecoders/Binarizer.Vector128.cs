#if NET8_0_OR_GREATER
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace FeatherQR.Internals.ImageDecoders;

internal static partial class Binarizer
{
    /// <summary>
    /// 128-bit tier: <see cref="FillHistogramVector256"/>'s 32-pixel blocks on two 128-bit loads, each half's compares a 16-bit movemask.
    /// </summary>
    /// <remarks>
    /// The block stays 32 pixels, not one vector: a 16-pixel step ran gradients slower than the scalar groups (see the decoder spec).
    /// </remarks>
    internal static void FillHistogramVector128(ReadOnlySpan<byte> luminance, Span<int> histogram)
    {
        ref var h = ref ClearedBins(histogram);
        ref var p = ref MemoryMarshal.GetReference(luminance);
        var offset = 0;
        var minCount = 0;
        var maxCount = 0;
        var uncounted = 0; // blocks in a row with no 0 and no 255
        var allOnes = Vector128<byte>.AllBitsSet;
        for (; offset + 32 <= luminance.Length; offset += 32)
        {
            var lo = Vector128.LoadUnsafe(ref p, (nuint)offset);
            var hi = Vector128.LoadUnsafe(ref p, (nuint)offset + 16);
            var isMin = Vector128.Equals(lo, Vector128<byte>.Zero).ExtractMostSignificantBits()
                | Vector128.Equals(hi, Vector128<byte>.Zero).ExtractMostSignificantBits() << 16;
            var isMax = Vector128.Equals(lo, allOnes).ExtractMostSignificantBits()
                | Vector128.Equals(hi, allOnes).ExtractMostSignificantBits() << 16;
            var others = ~(isMin | isMax);
            if (BitOperations.PopCount(others) > DenseBlock)
            {
                ref var b = ref Unsafe.Add(ref p, offset);
                CountGroup(ref h, Unsafe.ReadUnaligned<ulong>(ref b));
                CountGroup(ref h, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 8)));
                CountGroup(ref h, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 16)));
                CountGroup(ref h, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 24)));

                uncounted = others == uint.MaxValue ? uncounted + 1 : 0;
                if (uncounted >= 2)
                {
                    var end = Math.Min(offset + (1 + UntestedBlocks) * 32, luminance.Length);
                    for (offset += 32; offset + 8 <= end; offset += 8)
                    {
                        CountGroup(ref h, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, offset)));
                    }
                    offset -= 32; // the loop's own step lands on the next untested block
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
