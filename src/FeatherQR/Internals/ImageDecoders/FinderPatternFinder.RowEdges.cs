#if NET8_0_OR_GREATER
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
#endif

namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// The edge-list row kernel of the finder search (net8.0+, 256-bit vectors or AdvSimd).
/// </summary>
/// <remarks>
/// The mask walk goes through a row run by run and judges a window at the end of every dark run with up to fifteen compares that may each leave early. On a large symbol that is ten thousand windows a search and on an image without a symbol forty-five thousand, nearly all of them refused, and the branches are what it costs.
/// Here the row's dark bitmask is never stored: 64 pixels become one word, and the word's rising and falling edges (the starts and the ends of dark runs) go straight into two arrays. A window is then three consecutive starts and ends, and sixteen windows are classified a step without a branch: the strict ratio, the near miss and the small crisp runs, the same three integer checks the other kernels make. Only the windows a check flags run scalar code, in row order, through the same follow-ups and the same cross-checks.
/// The strict ratio is checked in integers, which gives the verdict of the float form on every input: the two can only differ where total / 7f is inexact, and equality in the check needs a total divisible by 14, where it is exact. <c>FinderRowKernelParityTest</c> holds the candidate list to the scalar kernel's, bit for bit.
/// Positions are kept in sixteen bits, which is what bounds the row width: below 4,096 pixels every quantity of the checks fits a signed lane once 2·|d| &lt; t is written |d| &lt; (t + 1) / 2.
/// On ARM64 the same kernel runs on 128-bit vectors, eight windows a step, with the two things that machine does differently: the row's word comes from the NEON fold the mask walk already used (there is no movemask), and a step's verdicts become bits only once one add across the OR of the three says a lane is flagged, because turning eight lanes into bits is a sequence there and nearly every step has nothing flagged.
/// </remarks>
internal static partial class FinderPatternFinder
{
    /// <summary>Rows at least this wide go to the mask walk: positions are kept in sixteen bits, and 7 · run and 3 · total have to fit a signed lane.</summary>
    internal const int EdgeListWidthLimit = 4096;

    /// <summary>Rows narrower than one vector of pixels go to the mask walk.</summary>
    private const int EdgeListMinWidth = 32;

    /// <summary>Whether this runtime and machine have the edge-list kernel at all.</summary>
    internal static bool IsEdgeListKernelSupported
#if NET8_0_OR_GREATER
        => Vector256.IsHardwareAccelerated || AdvSimd.Arm64.IsSupported;
#else
        => false;
#endif

    /// <summary>Windows <c>ClassifyWindows</c> judges in one call: sixteen with 256-bit vectors, eight on ARM64.</summary>
    internal static int ClassifyWindowLanes
#if NET8_0_OR_GREATER
        => Vector256.IsHardwareAccelerated ? 16 : 8;
#else
        => 0;
#endif

    /// <summary>The edge buffer for a search over rows of this width, or null when the search runs another kernel.</summary>
    private static short[]? RentEdgeBuffer(FinderRowKernel kernel, int width)
    {
#if NET8_0_OR_GREATER
        if ((kernel == FinderRowKernel.Auto || kernel == FinderRowKernel.EdgeList) && IsEdgeListKernelSupported && width >= EdgeListMinWidth && width < EdgeListWidthLimit)
            return ArrayPool<short>.Shared.Rent(2 * EdgeHalfLength(width));
#endif
        return null;
    }

    private static void ReturnEdgeBuffer(short[]? rented)
    {
#if NET8_0_OR_GREATER
        if (rented is not null)
            ArrayPool<short>.Shared.Return(rented);
#endif
    }

#if NET8_0_OR_GREATER
    /// <summary>Slots past the last edge that a sixteen-lane load starting at any window may read.</summary>
    private const int EdgeLoadSlack = 32;

    /// <summary>Half of the edge buffer, one half for the starts and one for the ends: a row of w pixels has at most (w + 1) / 2 dark runs.</summary>
    private static int EdgeHalfLength(int width) => (width + 1) / 2 + 1 + EdgeLoadSlack;

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
        // 1. A row is taken 64 pixels at a time. Two 32-pixel compares give one word with bit i set where pixel x + i is dark.
        //    The word is used at once and never stored, so there is no mask buffer to clear, write and read back.
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
        // 3. Each bit set is taken apart on its own, into its own array, lowest bit first: the position of the lowest set bit is one
        //    instruction and bits &= bits - 1 clears it. Taking every edge from one set (word ^ previous) and sending them alternately
        //    to the two arrays measured no faster than a single array: every edge then pays for working out where it goes, which cost
        //    what the split saved the classification. The classification wants them apart because a window is three consecutive
        //    starts and ends, so six loads give sixteen windows.
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

            // Lowest set bit first, so positions come out in row order
            if (AdvSimd.Arm64.IsSupported)
            {
                // Within a word the two alternate, so one loop takes one of each and the odd one out follows: each edge is a
                // two-cycle chain (clear the bit, find the next), and two chains in one loop overlap where two loops in a row
                // did not. Measured 0.78 to 0.92 of the two loops on rendered symbols on Apple M2; level on noise.
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
                continue;
            }

            while (rising != 0)
            {
                Unsafe.Add(ref start, startCount++) = (short)(x + BitOperations.TrailingZeroCount(rising));
                rising &= rising - 1;
            }
            while (falling != 0)
            {
                Unsafe.Add(ref end, endCount++) = (short)(x + BitOperations.TrailingZeroCount(falling));
                falling &= falling - 1;
            }
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
        // At a threshold of 0 nothing is dark; the x64 identity below would call every pixel dark, because t - 1 wraps to 255
        if (threshold == 0)
            return 0;

        if (Vector256.IsHardwareAccelerated)
        {
            // Dark is v < threshold, unsigned, and x64 has no unsigned byte compare below AVX-512: min(v, t - 1) == v says the same
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
            // NEON: byte-lane LessThan is the unsigned compare (cmhi), and there is no movemask. A full word is the mask walk's fold:
            // four compares select per-byte bit weights and three pairwise adds reduce them to one word (the simdjson shape).
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

    /// <summary>Sixteen windows with 256-bit vectors.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ClassifyWindows16(ref short starts, ref short ends, nuint k, out uint strict, out uint near, out uint crisp)
    {
        // How the strict ratio becomes integer arithmetic. t is the total of the five runs, r one run.
        //
        // 1. The float check takes one module as m = t / 7 and allows half a module either way:
        //      side run   |m - r|  < m / 2
        //      centre     |3m - r| < 3m / 2
        //    Multiplied by 14 the fractions go:
        //      side run   |2t - 14r| < t
        //      centre     |6t - 14r| < 3t
        //    For t = 21 that accepts side runs of 2 to 4 pixels (1.5 < r < 4.5) and a centre of 5 to 13 (4.5 < r < 13.5).
        //
        // 2. The verdict is the float form's on every input, not nearly always. A run sits exactly on an end of its band only when
        //    14r = t or 3t (3t or 9t for the centre), which makes t a multiple of 14, and then t / 7f is an integer and exact: both
        //    forms refuse the equality. Everywhere else an integer run is at least 1/14 pixel from the end of its band, far more
        //    than the rounding of t / 7f.
        //
        // 3. Both ratio checks are functions of one difference a run, d = t - 7r (3t - 7r for the centre):
        //      strict      2·|d| < t        (3t for the centre)
        //      near miss   |d| <= 10        the scalar code's (uint)(t - 7r + 10) <= 20
        //
        // 4. 2·|d| can leave a signed sixteen-bit lane, so strict is written |d| < (t + 1) >> 1, the same condition on integers
        //    (for t = 5 both say |d| <= 2). With that, 7r and 3t are the largest values a lane holds, and they fit for rows shorter
        //    than EdgeListWidthLimit: that is where the limit comes from.
        //
        //    runs 3, 3, 9, 3, 3    t = 21, every d is 0                                          accepted
        //    runs 9, 3, 3, 2, 2    t = 19, first run d = 19 - 63 = -44, 44 >= (19 + 1) >> 1 = 10   refused
        //
        // In scalar code this form is no faster than the float one, which leaves on its first failed compare. It is here because it
        // is what sixteen lanes can do at once without a branch, a division or a conversion.

        var s0 = Vector256.LoadUnsafe(ref starts, k);
        var s1 = Vector256.LoadUnsafe(ref starts, k + 1);
        var s2 = Vector256.LoadUnsafe(ref starts, k + 2);
        var e0 = Vector256.LoadUnsafe(ref ends, k);
        var e1 = Vector256.LoadUnsafe(ref ends, k + 1);
        var e2 = Vector256.LoadUnsafe(ref ends, k + 2);
        // The five runs of each window: dark, light, dark, light, dark
        var r0 = e0 - s0;
        var r1 = s1 - e0;
        var r2 = e1 - s1;
        var r3 = s2 - e1;
        var r4 = e2 - s2;
        var total = e2 - s0;
        var total3 = total + total + total;

        // |d| a run: d = t - 7r on the sides, 3t - 7r on the centre, which is expected to be three modules
        var seven = Vector256.Create((short)7);
        var one = Vector256.Create((short)1);
        var d0 = Vector256.Abs(total - r0 * seven);
        var d1 = Vector256.Abs(total - r1 * seven);
        var d2 = Vector256.Abs(total3 - r2 * seven);
        var d3 = Vector256.Abs(total - r3 * seven);
        var d4 = Vector256.Abs(total - r4 * seven);
        // Seven modules need seven pixels at least; both scalar checks refuse a shorter window first
        var enough = Vector256.GreaterThanOrEqual(total, seven);
        var halfTotal = (total + one) >> 1;
        var halfTotal3 = (total3 + one) >> 1;

        // Strict: 2·|d| < t, as |d| < (t + 1) >> 1 so the doubling cannot overflow a lane
        strict = (enough
            & Vector256.LessThan(d0, halfTotal)
            & Vector256.LessThan(d1, halfTotal)
            & Vector256.LessThan(d2, halfTotal3)
            & Vector256.LessThan(d3, halfTotal)
            & Vector256.LessThan(d4, halfTotal)).ExtractMostSignificantBits();

        // Near miss: |d| <= 10 sevenths of a pixel, a signed compare because |d| is never negative
        var nearLimit = Vector256.Create((short)(NearMissSevenths + 1));
        near = (enough
            & Vector256.LessThan(d0, nearLimit)
            & Vector256.LessThan(d1, nearLimit)
            & Vector256.LessThan(d2, nearLimit)
            & Vector256.LessThan(d3, nearLimit)
            & Vector256.LessThan(d4, nearLimit)).ExtractMostSignificantBits();

        // Small crisp runs: sides of 1 or 2 pixels, a centre of 3 to 5. x - low <= span as unsigned is low <= x <= low + span in one compare
        var oneUnsigned = Vector256.Create((ushort)1);
        crisp = (Vector256.LessThanOrEqual((r2 - Vector256.Create((short)3)).AsUInt16(), Vector256.Create((ushort)2))
            & Vector256.LessThanOrEqual((r0 - one).AsUInt16(), oneUnsigned)
            & Vector256.LessThanOrEqual((r1 - one).AsUInt16(), oneUnsigned)
            & Vector256.LessThanOrEqual((r3 - one).AsUInt16(), oneUnsigned)
            & Vector256.LessThanOrEqual((r4 - one).AsUInt16(), oneUnsigned)).ExtractMostSignificantBits();
    }

    /// <summary>
    /// Eight windows with 128-bit vectors: the same arithmetic as <see cref="ClassifyWindows16"/>, the verdicts left as lanes (0 or all bits) so the caller can test the OR of the three before any becomes bits.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ClassifyWindows8(ref short starts, ref short ends, nuint k, out Vector128<short> strict, out Vector128<short> near, out Vector128<short> crisp)
    {
        var s0 = Vector128.LoadUnsafe(ref starts, k);
        var s1 = Vector128.LoadUnsafe(ref starts, k + 1);
        var s2 = Vector128.LoadUnsafe(ref starts, k + 2);
        var e0 = Vector128.LoadUnsafe(ref ends, k);
        var e1 = Vector128.LoadUnsafe(ref ends, k + 1);
        var e2 = Vector128.LoadUnsafe(ref ends, k + 2);
        var r0 = e0 - s0;
        var r1 = s1 - e0;
        var r2 = e1 - s1;
        var r3 = s2 - e1;
        var r4 = e2 - s2;
        var total = e2 - s0;
        var total3 = total + total + total;

        var seven = Vector128.Create((short)7);
        var one = Vector128.Create((short)1);
        var d0 = Vector128.Abs(total - r0 * seven);
        var d1 = Vector128.Abs(total - r1 * seven);
        var d2 = Vector128.Abs(total3 - r2 * seven);
        var d3 = Vector128.Abs(total - r3 * seven);
        var d4 = Vector128.Abs(total - r4 * seven);
        var enough = Vector128.GreaterThanOrEqual(total, seven);
        var halfTotal = (total + one) >> 1;
        var halfTotal3 = (total3 + one) >> 1;

        strict = enough
            & Vector128.LessThan(d0, halfTotal)
            & Vector128.LessThan(d1, halfTotal)
            & Vector128.LessThan(d2, halfTotal3)
            & Vector128.LessThan(d3, halfTotal)
            & Vector128.LessThan(d4, halfTotal);

        var nearLimit = Vector128.Create((short)(NearMissSevenths + 1));
        near = enough
            & Vector128.LessThan(d0, nearLimit)
            & Vector128.LessThan(d1, nearLimit)
            & Vector128.LessThan(d2, nearLimit)
            & Vector128.LessThan(d3, nearLimit)
            & Vector128.LessThan(d4, nearLimit);

        var oneUnsigned = Vector128.Create((ushort)1);
        crisp = (Vector128.LessThanOrEqual((r2 - Vector128.Create((short)3)).AsUInt16(), Vector128.Create((ushort)2))
            & Vector128.LessThanOrEqual((r0 - one).AsUInt16(), oneUnsigned)
            & Vector128.LessThanOrEqual((r1 - one).AsUInt16(), oneUnsigned)
            & Vector128.LessThanOrEqual((r3 - one).AsUInt16(), oneUnsigned)
            & Vector128.LessThanOrEqual((r4 - one).AsUInt16(), oneUnsigned)).AsInt16();
    }

    private static readonly Vector64<byte> LaneWeights8 = Vector64.Create((byte)1, 2, 4, 8, 16, 32, 64, 128);

    /// <summary>Eight verdict lanes (0 or all bits) to eight bits: narrowed to bytes, one weight kept a lane, added across. Three instructions where <c>ExtractMostSignificantBits</c> is a sequence on ARM64.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint LaneBits(Vector128<short> lanes)
        => AdvSimd.Arm64.IsSupported
            ? AdvSimd.Arm64.AddAcross(AdvSimd.ExtractNarrowingLower(lanes).AsByte() & LaneWeights8).ToScalar()
            : lanes.ExtractMostSignificantBits();

    private static void ScanRowEdges(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, int y, Span<short> edges, Span<FinderPattern> candidates, ref int candidateCount)
    {
        var half = EdgeHalfLength(width);
        if (edges.Length < 2 * half || (uint)y >= (uint)height || (long)width * height > luminance.Length)
            throw new ArgumentException($"Edge buffer of {edges.Length} for a row of {width}, or row {y} outside the {width} x {height} image");

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
#endif
}
