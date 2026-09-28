#if NET8_0_OR_GREATER
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace FeatherQR.Internals.ImageDecoders;

internal static partial class FinderPatternFinder
{
    /// <summary>
    /// The dark runs of a row: where each starts into <paramref name="starts"/>, the pixel after each into <paramref name="ends"/>. A run reaching the end of the row ends at the row's length.
    /// </summary>
    /// <returns>The number of dark runs.</returns>
    /// <remarks>
    /// Ends are exclusive: run k is ends[k] - starts[k] pixels long, and the light gap after it is starts[k + 1] - ends[k].
    /// Both spans need room for (length + 1) / 2 + 1 runs, and the row has to be shorter than <see cref="EdgeListWidthLimit"/>: positions are stored in sixteen bits.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ExtractRowEdges(ReadOnlySpan<byte> row, byte threshold, Span<short> starts, Span<short> ends)
    {
        // How the edges come out of the pixels.
        //
        // 1. A row is taken 64 pixels at a time. DarkWord makes the word for the machine (two 32-byte compares on x64, the
        //    NEON fold on ARM64), bit i set where pixel x + i is dark. The word is used at once and never stored, so there is
        //    no mask buffer to clear, write and read back.
        //
        // 2. Edges come from the word and the same word one pixel later:
        //      previous = (word << 1) | carry      bit i = pixel x + i - 1 is dark; the carry is the last pixel of the word before,
        //                                          so a run is followed across words. It begins at 0: a row that starts dark starts a run at 0.
        //      rising   = word & ~previous         dark, and the pixel before it light: a run starts here
        //      falling  = ~word & previous         light, and the pixel before it dark: a run ended just before here
        //
        //    pixels    0 0 1 1 1 0 0 0 1 1 1 1 1 1 1 1 1 0 0 0 1 1 1 0 0 1 1 0 0 0 0 0      (1 = dark)
        //    rising        ^           ^                       ^         ^                  starts 2, 8, 20, 25
        //    falling             ^                       ^           ^       ^              ends   5, 17, 23, 27
        //
        // 3. Each bit set goes into its own array, lowest bit first: the position of the lowest set bit is one instruction and
        //    bits &= bits - 1 clears it. Taking every edge from one set (word ^ previous) and sending them alternately to the two
        //    arrays measured no faster than a single array: every edge then pays for working out where it goes, which cost what
        //    the split saved the classification. The classification wants them apart because a window is three consecutive
        //    starts and ends, so six loads give a step of windows.
        //    Within a word the two sets alternate, start, end, start, end, so one loop takes one of each an iteration and the
        //    odd one out follows. Each edge is a two-cycle chain (clear the bit, find the next); in one loop the two chains
        //    overlap, and the loop branches half as often as two loops in a row would.
        //
        // 4. A run that reaches the end of the row still needs its end. In a last word that is not full the bits past the row are 0,
        //    so the end falls out as the falling edge at the row's length. When the length is a multiple of 64 no word holds that bit,
        //    and the carry left after the loop says the run is still open.
        //
        // The worst case for the two arrays is a row that alternates every pixel: (length + 1) / 2 runs.

        var width = row.Length;
        var capacity = (width + 1) / 2 + 1;
        if (width >= EdgeListWidthLimit || starts.Length < capacity || ends.Length < capacity)
            throw new ArgumentException($"A row of {width} needs {capacity} starts and ends and has to be shorter than {EdgeListWidthLimit}");

        ref var pixels = ref MemoryMarshal.GetReference(row);
        ref var start = ref MemoryMarshal.GetReference(starts);
        ref var end = ref MemoryMarshal.GetReference(ends);
        var startCount = 0;
        var endCount = 0;
        var carry = 0ul;

        for (var x = 0; x < width; x += 64)
        {
            var word = DarkWord(ref pixels, x, width, threshold);

            // previous: each pixel's left neighbour, the carry standing in for the last pixel of the word before
            var previous = (word << 1) | carry;
            carry = word >> 63;
            var rising = word & ~previous;   // dark after light: a run starts here
            var falling = ~word & previous;  // light after dark: a run ended just before here

            // Lowest set bit first, so positions come out in row order. Within a word the two alternate, so one loop takes one of
            // each and the odd one out follows: on ARM64 the two bit-clear chains overlap, and on x64 the loop branches half as often
            while (rising != 0 && falling != 0)
            {
                Unsafe.Add(ref start, startCount++) = (short)(x + BitOperations.TrailingZeroCount(rising));
                Unsafe.Add(ref end, endCount++) = (short)(x + BitOperations.TrailingZeroCount(falling));
                rising &= rising - 1;
                falling &= falling - 1;
            }
            if (rising != 0)
                Unsafe.Add(ref start, startCount++) = (short)(x + BitOperations.TrailingZeroCount(rising));
            if (falling != 0)
                Unsafe.Add(ref end, endCount++) = (short)(x + BitOperations.TrailingZeroCount(falling));
        }
        // A dark run reaching the end of a row whose length is a multiple of 64 ends at the length
        if (carry != 0)
            Unsafe.Add(ref end, endCount++) = (short)width;

        return endCount;
    }

    /// <summary>Bit i set where pixel x + i is dark, for the 64 pixels at <paramref name="x"/>; bits past the row stay 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong DarkWord(ref byte pixels, int x, int width, byte threshold)
    {
        // How 64 pixels become one word, bit i set where pixel x + i is dark, that is v < threshold as unsigned bytes.
        //
        // x64: there is no unsigned byte compare below AVX-512, and min(v, t - 1) == v says the same thing: v is below t exactly
        //      when clamping it to t - 1 changes nothing. Two 32-byte compares give the two halves of the word as their sign
        //      bits, one movemask each, and the high half is shifted up by 32.
        //
        // ARM64: byte-lane LessThan is the unsigned compare (cmhi), but there is no movemask, so the word is folded out of the
        //      compare masks. Each of the four 16-lane masks is ANDed with the weights 1, 2, 4, ..., 128, 1, 2, ..., 128, so a dark
        //      lane holds the bit its pixel has inside its byte of the word. Three pairwise adds then merge neighbouring lanes,
        //      64 to 32 to 16 to 8 bytes; a lane pair never shares a bit, so no add carries:
        //
        //        pixels    0 1 2 3 4 5 6 7 | 8 .. 15 | 16 .. 31 | 32 .. 63       four compares, a weight kept where dark
        //        add 1     (0+1) (2+3) (4+5) (6+7) (8+9) ...                       32 bytes, two pixels' bits each
        //        add 2     (0..3) (4..7) (8..11) ...                               16 bytes, four pixels' bits each
        //        add 3     (0..7) (8..15) ... (56..63)                              8 bytes: the word, low byte first
        //
        //      The third add pairs the vector with itself, so its low 64 bits are the word and the high 64 a copy.
        //
        // The last word of a row is partial: what is left is taken a vector at a time where a vector fits, then pixel by pixel,
        // and the bits past the row stay 0, which is what lets a run that reaches the end of the row end there.
        //
        // At a threshold of 0 nothing is dark. The x64 identity would say the opposite, t - 1 wraps to 255, so it is answered first.
        if (threshold == 0)
            return 0;

        if (Vector256.IsHardwareAccelerated)
        {
            // x64: the min identity, two halves
            var thresholdMinus1 = Vector256.Create((byte)(threshold - 1));
            if (x + 64 <= width)
            {
                // A full word: the sign bits of two 32-byte compares, low half and high half
                var low = Vector256.LoadUnsafe(ref pixels, (nuint)x);
                var high = Vector256.LoadUnsafe(ref pixels, (nuint)x + 32);
                return Vector256.Equals(Vector256.Min(low, thresholdMinus1), low).ExtractMostSignificantBits()
                    | (ulong)Vector256.Equals(Vector256.Min(high, thresholdMinus1), high).ExtractMostSignificantBits() << 32;
            }

            // The last, partial word: one more vector if 32 pixels are left, then pixel by pixel
            ulong word = 0;
            var i = x;
            if (i + 32 <= width)
            {
                var low = Vector256.LoadUnsafe(ref pixels, (nuint)i);
                word = Vector256.Equals(Vector256.Min(low, thresholdMinus1), low).ExtractMostSignificantBits();
                i += 32;
            }
            for (; i < width; i++)
            {
                if (Unsafe.Add(ref pixels, i) < threshold)
                    word |= 1ul << (i - x);
            }
            return word;
        }
        else
        {
            // ARM64: the fold
            var thr = Vector128.Create(threshold);
            if (x + 64 <= width)
            {
                var d0 = Vector128.LessThan(Vector128.LoadUnsafe(ref pixels, (nuint)x), thr) & NeonBitWeights;
                var d1 = Vector128.LessThan(Vector128.LoadUnsafe(ref pixels, (nuint)(x + 16)), thr) & NeonBitWeights;
                var d2 = Vector128.LessThan(Vector128.LoadUnsafe(ref pixels, (nuint)(x + 32)), thr) & NeonBitWeights;
                var d3 = Vector128.LessThan(Vector128.LoadUnsafe(ref pixels, (nuint)(x + 48)), thr) & NeonBitWeights;
                var s = AdvSimd.Arm64.AddPairwise(AdvSimd.Arm64.AddPairwise(d0, d1), AdvSimd.Arm64.AddPairwise(d2, d3));
                s = AdvSimd.Arm64.AddPairwise(s, s);
                return s.AsUInt64().ToScalar();
            }

            // The last, partial word: 16 pixels at a time, then pixel by pixel
            ulong word = 0;
            var i = x;
            for (; i + 16 <= width; i += 16)
                word |= (ulong)Vector128.LessThan(Vector128.LoadUnsafe(ref pixels, (nuint)i), thr).ExtractMostSignificantBits() << (i - x);
            for (; i < width; i++)
            {
                if (Unsafe.Add(ref pixels, i) < threshold)
                    word |= 1ul << (i - x);
            }
            return word;
        }
    }

    /// <summary>
    /// The three integer checks on the <see cref="ClassifyWindowLanes"/> windows that begin at dark runs <paramref name="k"/> on: one bit a window in each result.
    /// </summary>
    /// <remarks>
    /// Window j is runs starts[j]..ends[j], ends[j]..starts[j + 1], starts[j + 1]..ends[j + 1], ends[j + 1]..starts[j + 2], starts[j + 2]..ends[j + 2]; two more starts and ends than lanes are read from <paramref name="k"/> on, whatever they hold.
    /// <paramref name="strict"/> is <see cref="IsFinderRatio(int, int, int, int, int)"/>, <paramref name="near"/> is <see cref="IsNearFinderRatio"/>, <paramref name="crisp"/> is <see cref="IsSmallCrispFinderRuns"/>, for runs below <see cref="EdgeListWidthLimit"/> in total.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ClassifyWindows(ref short starts, ref short ends, nuint k, out uint strict, out uint near, out uint crisp)
    {
        if (!Vector256.IsHardwareAccelerated)
        {
            ClassifyWindows8(ref starts, ref ends, k, out var strictLanes, out var nearLanes, out var crispLanes);
            strict = LaneBits(strictLanes);
            near = LaneBits(nearLanes);
            crisp = LaneBits(crispLanes);
            return;
        }
        ClassifyWindows16(ref starts, ref ends, k, out strict, out near, out crisp);
    }

    private static readonly Vector64<byte> LaneWeights8 = Vector64.Create((byte)1, 2, 4, 8, 16, 32, 64, 128);

    /// <summary>Eight verdict lanes (0 or all bits) to eight bits, bit j for lane j.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint LaneBits(Vector128<short> lanes)
    {
        // ExtractMostSignificantBits is one instruction on x64 and a sequence on ARM64, where three instructions do the same
        // for lanes that are 0 or all bits: narrow each short to its low byte (0x00 or 0xFF), keep one bit a lane with the
        // weights 1, 2, 4, ..., 128, and add the eight bytes across. No two lanes keep the same bit, so the sum is the mask
        // and never carries.
        //
        //   lanes      -1    0    0   -1   -1    0    0    0
        //   narrowed   FF   00   00   FF   FF   00   00   00
        //   weighted    1    0    0    8   16    0    0    0      sum 25 = 0b00011001: lanes 0, 3 and 4
        if (AdvSimd.Arm64.IsSupported)
            return AdvSimd.Arm64.AddAcross(AdvSimd.ExtractNarrowingLower(lanes).AsByte() & LaneWeights8).ToScalar();
        return lanes.ExtractMostSignificantBits();
    }

    private static void ScanRowEdges(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, int y, Span<short> edges, Span<FinderPattern> candidates, ref int candidateCount)
    {
        var half = EdgeHalfLength(width);
        if (edges.Length < 2 * half || (uint)y >= (uint)height || (long)width * height > luminance.Length)
            throw new ArgumentException($"Edge buffer of {edges.Length} for a row of {width}, or row {y} outside the {width} x {height} image");

        // One row: its dark runs as two arrays, then the windows a step at a time.
        //
        //   starts   s0   s1   s2   s3 ...      window k is dark runs k, k + 1 and k + 2 and the two gaps between:
        //   ends     e0   e1   e2   e3 ...      e[k] - s[k], s[k+1] - e[k], e[k+1] - s[k+1], s[k+2] - e[k+1], e[k+2] - s[k+2]
        //
        // A step judges ClassifyWindowLanes windows at once, sixteen with 256-bit vectors and eight on ARM64, and gets one bit
        // a window for each of the three checks. The lanes past the last window of a row read whatever the buffer holds and
        // are masked off (live), and the near-miss bits are masked off while the grey levels are off, because the mask walk
        // skips that check then. Only flagged windows run scalar code, in row order, through the follow-ups the mask walk runs.
        //
        // On ARM64 the verdicts arrive as lanes and each becomes bits by a short sequence, so the OR of the three is turned
        // into bits first, and a step with nothing flagged, which on a symbol is nearly every step, pays only that one.
        var endCount = ExtractRowEdges(luminance.Slice(y * width, width), threshold, edges.Slice(0, half), edges.Slice(half, half));
        ref var starts = ref MemoryMarshal.GetReference(edges);
        ref var ends = ref Unsafe.Add(ref starts, half);

        // Window k is dark runs k, k + 1 and k + 2 with the two light runs between them, judged where the mask walk judges it: at the end of run k + 2
        var windows = endCount - 2;
        if (windows <= 0)
            return;

        var lanes = ClassifyWindowLanes;
        var allLanes = (1u << lanes) - 1;
        var greyOn = grey.IsEnabled ? allLanes : 0u;

        Span<int> runs = stackalloc int[5];
        for (var k = 0; k < windows; k += lanes)
        {
            // Lanes past the last window hold whatever the buffer held
            var live = k + lanes > windows ? (1u << (windows - k)) - 1 : allLanes;
            uint strictBits, nearBits, crispBits;
            if (Vector256.IsHardwareAccelerated)
            {
                ClassifyWindows16(ref starts, ref ends, (nuint)k, out strictBits, out nearBits, out crispBits);
            }
            else
            {
                // Nearly every step flags nothing, and on ARM64 lanes become bits by a sequence: one for the OR of the three first
                ClassifyWindows8(ref starts, ref ends, (nuint)k, out var strictLanes, out var nearLanes, out var crispLanes);
                if ((LaneBits(strictLanes | nearLanes | crispLanes) & live) == 0)
                    continue;
                strictBits = LaneBits(strictLanes);
                nearBits = LaneBits(nearLanes);
                crispBits = LaneBits(crispLanes);
            }
            strictBits &= live;
            nearBits &= live & greyOn;
            crispBits &= live;
            var flagged = strictBits | nearBits | crispBits;
            while (flagged != 0)
            {
                var lane = BitOperations.TrailingZeroCount(flagged);
                flagged &= flagged - 1;
                ref var windowStarts = ref Unsafe.Add(ref starts, k + lane);
                ref var windowEnds = ref Unsafe.Add(ref ends, k + lane);
                var q0 = windowEnds - windowStarts;
                var q1 = Unsafe.Add(ref windowStarts, 1) - windowEnds;
                var q2 = Unsafe.Add(ref windowEnds, 1) - Unsafe.Add(ref windowStarts, 1);
                var q3 = Unsafe.Add(ref windowStarts, 2) - Unsafe.Add(ref windowEnds, 1);
                var q4 = Unsafe.Add(ref windowEnds, 2) - Unsafe.Add(ref windowStarts, 2);
                int end = Unsafe.Add(ref windowEnds, 2);

                // The order of the mask walk's condition: strict, else the grey second look on a near miss, else small crisp runs a neighbouring row repeats
                var bit = 1u << lane;
                if ((strictBits & bit) != 0
                    || ((nearBits & bit) != 0 && IsFinderRatioByCoverage(luminance, width, height, grey, q0, q1, q2, q3, q4, end, y, 1, 0))
                    || ((crispBits & bit) != 0 && IsRepeatedNearFinderRatio(luminance, width, height, threshold, q0, q1, q2, q3, q4, end, y)))
                {
                    runs[0] = q0;
                    runs[1] = q1;
                    runs[2] = q2;
                    runs[3] = q3;
                    runs[4] = q4;
                    TryAddCandidate(luminance, width, height, threshold, grey, runs, end, y, referenceWalk: false, candidates, ref candidateCount);
                }
            }
        }
    }
}
#endif
