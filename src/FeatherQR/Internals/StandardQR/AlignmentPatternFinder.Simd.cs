#if NET8_0_OR_GREATER
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace FeatherQR.Internals.StandardQR;

internal static partial class AlignmentPatternFinder
{
    /// <summary>Per-byte bit weights [1,2,4,...,128] repeated: dark byte i contributes bit (i mod 8) of its half.</summary>
    private static readonly Vector128<byte> NeonBitWeights = Vector128.Create(
        (byte)1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128);

    /// <summary>
    /// Mask-based row scan: vector compares (32 px AVX2, 64 px NEON fold, 16 px otherwise) produce a dark bitmask; runs are walked via trailing-zero counts, evaluating the same (light, dark, light) triple at every light→dark transition as the scalar walk.
    /// </summary>
    private static void ScanRowMask(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, int y, int minX, int maxX, ExpectedRuns expected, (float X, float Y) axisX, (float X, float Y) axisY, ref NearestHit best)
    {
        var length = maxX - minX + 1;
        Span<ulong> mask = stackalloc ulong[((length + 63) >> 6) + 1];
        mask.Clear();

        // Build the dark bitmask (bit i = pixel minX+i is dark);
        // threshold == 0 means nothing is dark, so the compare loops can skip.
        var row = luminance.Slice(y * width + minX, length);
        var i = 0;
        if (threshold > 0)
        {
            ref var rowRef = ref MemoryMarshal.GetReference(row);
            if (Vector256.IsHardwareAccelerated && length >= 32)
            {
                // x64 has no unsigned byte compare: unsigned v < t ⟺ min(v, t-1) == v
                var thresholdMinus1 = Vector256.Create((byte)(threshold - 1));
                for (; i + 32 <= length; i += 32)
                {
                    var v = Vector256.LoadUnsafe(ref rowRef, (nuint)i);
                    var dark = Vector256.Equals(Vector256.Min(v, thresholdMinus1), v);
                    mask[i >> 6] |= (ulong)dark.ExtractMostSignificantBits() << (i & 63);
                }
            }
            else
            {
                // 128-bit lanes: LessThan on byte lanes is an unsigned compare (cmhi on NEON), so no min-trick is needed.
                var thr = Vector128.Create(threshold);
                if (AdvSimd.Arm64.IsSupported)
                {
                    // NEON has no movemask; fold 64 pixels straight into one mask word instead: 4 compares select per-byte bit weights, then a chain of pairwise adds reduces them (simdjson bulk-movemask shape).
                    for (; i + 64 <= length; i += 64)
                    {
                        var d0 = Vector128.LessThan(Vector128.LoadUnsafe(ref rowRef, (nuint)i), thr) & NeonBitWeights;
                        var d1 = Vector128.LessThan(Vector128.LoadUnsafe(ref rowRef, (nuint)(i + 16)), thr) & NeonBitWeights;
                        var d2 = Vector128.LessThan(Vector128.LoadUnsafe(ref rowRef, (nuint)(i + 32)), thr) & NeonBitWeights;
                        var d3 = Vector128.LessThan(Vector128.LoadUnsafe(ref rowRef, (nuint)(i + 48)), thr) & NeonBitWeights;
                        var s = AdvSimd.Arm64.AddPairwise(AdvSimd.Arm64.AddPairwise(d0, d1), AdvSimd.Arm64.AddPairwise(d2, d3));
                        s = AdvSimd.Arm64.AddPairwise(s, s);
                        // i is a multiple of 64 here, so this writes the whole word
                        mask[i >> 6] = s.AsUInt64().ToScalar();
                    }
                }
                for (; i + 16 <= length; i += 16)
                {
                    var dark = Vector128.LessThan(Vector128.LoadUnsafe(ref rowRef, (nuint)i), thr);
                    mask[i >> 6] |= (ulong)dark.ExtractMostSignificantBits() << (i & 63);
                }
            }
        }
        for (; i < length; i++)
        {
            if (row[i] < threshold)
                mask[i >> 6] |= 1ul << (i & 63);
        }

        // Walk dark runs; a leading dark run is skipped (the scalar walk waits for the first light pixel before opening a window).
        var pos = NextBit(mask, 0, length, set: false);
        var lightStart = pos;
        var previousDarkLength = 0;
        var previousGap = 0;

        while (true)
        {
            var darkStart = NextBit(mask, pos, length, set: true);
            if (darkStart >= length)
                break;
            var darkEnd = NextBit(mask, darkStart, length, set: false);

            var gap = darkStart - lightStart; // light run before this dark run
            if (previousDarkLength > 0
                && IsAlignmentRatio(previousGap, previousDarkLength, gap, expected.Row))
            {
                var x = minX + darkStart;
                var candidateX = x - gap - previousDarkLength / 2f;
                if (TryCrossCheck(luminance, width, height, threshold, candidateX, y, expected.Column, axisX, axisY, out var centerX, out var centerY))
                    best.Offer(centerX, centerY);
            }

            previousGap = gap;
            previousDarkLength = darkEnd - darkStart;
            lightStart = darkEnd;
            pos = darkEnd;
        }
    }

    /// <summary>Index of the next set (or clear) bit at or after <paramref name="from"/>, or <paramref name="length"/>.</summary>
    private static int NextBit(ReadOnlySpan<ulong> mask, int from, int length, bool set)
    {
        while (from < length)
        {
            var word = mask[from >> 6];
            if (!set)
                word = ~word;
            word &= ulong.MaxValue << (from & 63);
            if (word != 0)
            {
                var index = (from & ~63) + BitOperations.TrailingZeroCount(word);
                return Math.Min(index, length);
            }
            from = (from & ~63) + 64;
        }
        return length;
    }
}
#endif
