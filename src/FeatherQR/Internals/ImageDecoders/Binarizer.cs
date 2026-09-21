#if NET8_0_OR_GREATER
using System.Numerics;
#endif
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
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
    /// Suits Tier-1 inputs with clear bimodal contrast.
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
    /// Every tier produces the same 256 bins, and the threshold and the grey levels are functions of the bins alone.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="histogram"/> holds fewer than 256 bins.</exception>
    internal static void FillHistogram(ReadOnlySpan<byte> luminance, Span<int> histogram)
    {
#if NET8_0_OR_GREATER
        // Wider and narrower vector tiers were measured and left out; see the decoder spec.
        if (Vector256.IsHardwareAccelerated)
        {
            FillHistogramVector256(luminance, histogram);
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
