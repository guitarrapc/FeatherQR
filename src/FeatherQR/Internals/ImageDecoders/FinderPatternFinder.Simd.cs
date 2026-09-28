#if NET8_0_OR_GREATER
using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace FeatherQR.Internals.ImageDecoders;

internal static partial class FinderPatternFinder
{
    /// <summary>Per-byte bit weights [1,2,4,...,128] repeated: dark byte i contributes bit (i mod 8) of its half.</summary>
    private static readonly Vector128<byte> NeonBitWeights = Vector128.Create(
        (byte)1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128);

    /// <summary>
    /// Mask-based row scan: vector compares (32 px AVX2, 64 px NEON fold, 16 px otherwise) produce a dark bitmask; runs are walked via trailing-zero counts.
    /// The 1:1:3:1:1 window is evaluated at the end of every dark run from the third onward, exactly the positions and order the scalar walk evaluates, so the result is bit-identical.
    /// </summary>
    private static void ScanRowMask(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, int y, Span<FinderPattern> candidates, ref int candidateCount)
    {
        // The mask covers a full row; keep the common case on the stack (512 B covers rows up to ~4000 px) and rent for wider images.
        var maskLength = ((width + 63) >> 6) + 1;
        ulong[]? rented = maskLength > 64 ? ArrayPool<ulong>.Shared.Rent(maskLength) : null;
        Span<ulong> mask = rented is null ? stackalloc ulong[64] : rented;
        mask = mask.Slice(0, maskLength);
        mask.Clear();

        try
        {
            // Build the dark bitmask (bit i = pixel i of this row is dark);
            // threshold == 0 means nothing is dark, so the compare loops can skip.
            var row = luminance.Slice(y * width, width);
            var i = 0;
            if (threshold > 0)
            {
                ref var rowRef = ref MemoryMarshal.GetReference(row);
                if (Vector256.IsHardwareAccelerated && width >= 32)
                {
                    // x64 has no unsigned byte compare: unsigned v < t ⟺ min(v, t-1) == v
                    var thresholdMinus1 = Vector256.Create((byte)(threshold - 1));
                    for (; i + 32 <= width; i += 32)
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
                        // NEON has no movemask; fold 64 pixels straight into one mask word instead: 4 compares select per-byte bit weights, 3 pairwise adds reduce them (simdjson bulk-movemask shape).
                        // Measured ~8-11% over per-16 ExtractMostSignificantBits and 3.3-4.1x over the scalar walk on Apple M2
                        for (; i + 64 <= width; i += 64)
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
                    for (; i + 16 <= width; i += 16)
                    {
                        var dark = Vector128.LessThan(Vector128.LoadUnsafe(ref rowRef, (nuint)i), thr);
                        mask[i >> 6] |= (ulong)dark.ExtractMostSignificantBits() << (i & 63);
                    }
                }
            }
            for (; i < width; i++)
            {
                if (row[i] < threshold)
                    mask[i >> 6] |= 1ul << (i & 63);
            }

            // Walk dark runs. The scalar window is [dark, light, dark, light, dark], evaluated whenever its 5th run (a dark run) completes, at its dark→light transition or at the end of the row. That is: at the end of every dark run from the third onward, with the window being that run plus the two dark runs (and light gaps) before it.
            var darkStart = NextBit(mask, 0, width, set: true);
            var dPrev2 = 0; // dark run k-2
            var gPrev1 = 0; // light gap between k-2 and k-1
            var dPrev1 = 0; // dark run k-1
            var gCur = 0;   // light gap between k-1 and k
            var darkRuns = 0;

            Span<int> runs = stackalloc int[5];
            while (darkStart < width)
            {
                var darkEnd = NextBit(mask, darkStart, width, set: false);
                var dCur = darkEnd - darkStart;

                if (darkRuns >= 2)
                {
                    if (IsFinderRatio(dPrev2, gPrev1, dPrev1, gCur, dCur)
                        || (grey.IsEnabled && IsNearFinderRatio(dPrev2, gPrev1, dPrev1, gCur, dCur) && IsFinderRatioByCoverage(luminance, width, height, grey, dPrev2, gPrev1, dPrev1, gCur, dCur, darkEnd, y, 1, 0))
                        || IsRepeatedNearFinderRatio(luminance, width, height, threshold, dPrev2, gPrev1, dPrev1, gCur, dCur, darkEnd, y))
                    {
                        runs[0] = dPrev2;
                        runs[1] = gPrev1;
                        runs[2] = dPrev1;
                        runs[3] = gCur;
                        runs[4] = dCur;
                        TryAddCandidate(luminance, width, height, threshold, grey, runs, darkEnd, y, referenceWalk: false, candidates, ref candidateCount);
                    }
                }

                var nextDark = NextBit(mask, darkEnd, width, set: true);
                dPrev2 = dPrev1;
                gPrev1 = gCur;
                dPrev1 = dCur;
                gCur = nextDark - darkEnd;
                darkRuns++;
                darkStart = nextDark;
            }
        }
        finally
        {
            if (rented is not null)
                ArrayPool<ulong>.Shared.Return(rented);
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
