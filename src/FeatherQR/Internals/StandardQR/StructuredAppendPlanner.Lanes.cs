#if NET8_0_OR_GREATER
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.Wasm;
#endif

namespace FeatherQR.Internals.StandardQR;

/// <summary>
/// The planner's greedy walk at up to eight budgets at once: one budget per vector lane, one vector per state of the segmentation program.
/// </summary>
/// <remarks>
/// The probes of a budget search walk the same text from the same start and differ only in the budget they close their chunks at, so they are independent instances of one program and share its control: while every lane is at the same character, which is nearly always, a step is the scalar loop's class lookup and branches over vector adds and mins, at about the cost of one scalar step for the eight of them.
/// A lane whose chunk closes re-reads the character that did not fit, so it falls behind the others: by one step, by two when it closed on the second half of a pair. A cut kept off a run of U+FEFF moves the chunk's start back to the character ahead of the run. When marks lie between that character and the one that did not fit, the vector loop prices that character and the marks in closed form, one Byte run, and the lane re-reads only the character that did not fit; a cut at the run's first mark re-reads from the character ahead, two steps back, three when that character is a pair. While the lanes are apart they are stepped with the class and the byte cost taken per lane in scalar code, and the lanes ahead wait, keeping their states, until the one furthest behind is level with them; left apart, lanes whose closes cost them differently never share a character again. Closing a chunk is itself scalar code, a dozen times per lane in a walk of thousands of steps.
/// A lane that has failed keeps closing chunks at its own budget, unrecorded: one that stopped would run ahead for good and the lanes would never share a character again.
/// The walk prices exactly what <see cref="CountChunks(ReadOnlySpan{char}, EciMode, bool, QRSegmentation, int, int, int, Span{int}, bool)"/> prices (the single-mode shortcuts of <see cref="LongestChunkEnd"/> are the program's own answers on the content they apply to), which <c>StructuredAppendLaneWalkTest</c> holds lane by lane. The byte order mark <see cref="QRCodeGeneratorOptions.Utf8Bom"/> asks for is not handled here, since its chunk is priced by another rule; the caller keeps those walks scalar.
/// The eight 32-bit lanes use accelerated <c>Vector256</c>; ARM64 uses eight saturating 16-bit NEON lanes, and every other 128-bit target the same lanes on portable vectors. Without accelerated vectors, or when a chunk averages under the backend's minimum length (the lanes then spend too many steps apart), nothing here runs and the caller's scalar probes do.
/// </remarks>
internal static partial class StructuredAppendPlanner
{
    /// <summary>Shortest average chunk, in characters, the lanes are used for: below it chunks close so often, a step or two apart, that the lanes are rarely at one character.</summary>
    private const int MinLaneChunkChars = 128;

    // NEON's compact cost state pays on much shorter chunks. Twenty characters is a
    // conservative cutoff: setup and frequent divergent steps can outweigh batching below it.
    private const int MinNeonLaneChunkChars = 20;

    // The portable 16-bit lanes on x64 without AVX; below this they lost to the scalar probes.
    private const int MinVector128LaneChunkChars = 40;

    // On WebAssembly the interpreter needs longer chunks than AOT-compiled code, and one flag gates both.
    private const int MinPackedSimdLaneChunkChars = 80;

#if NET8_0_OR_GREATER
    /// <summary>The shortest average chunk the lanes of this machine are used for.</summary>
    private static int LaneChunkChars
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => AdvSimd.Arm64.IsSupported ? MinNeonLaneChunkChars
            : Vector256.IsHardwareAccelerated ? MinLaneChunkChars
            : PackedSimd.IsSupported ? MinPackedSimdLaneChunkChars
            : MinVector128LaneChunkChars;
    }
#endif

#if NET8_0_OR_GREATER
    private const int Lanes = 8;

    // Where the first batch puts its budgets above the floor. A balanced budget sits within 22 bits
    // of the floor on one-byte content; a three-byte character is 24 bits that cannot be cut and a
    // pair 32, so UTF-8 content reaches 46.
    private static ReadOnlySpan<int> OneByteLaneOffsets => [3, 7, 11, 15, 19, 23, 27, 31];
    private static ReadOnlySpan<int> Utf8LaneOffsets => [5, 11, 17, 23, 29, 35, 41, 47];

    private interface ICharWidth
    {
        static abstract bool Utf8 { get; }
    }

    private readonly struct OneByteChars : ICharWidth
    {
        public static bool Utf8 => false;
    }

    private readonly struct Utf8Chars : ICharWidth
    {
        public static bool Utf8 => true;
    }
#endif

    /// <summary>
    /// Narrows [<paramref name="low"/>, <paramref name="high"/>] by a batch of walks limited to <paramref name="limit"/> chunks, or two from the floor: the cheapest budget that held them becomes the ceiling and the settled walk, its neighbour below the floor and the failed walk.
    /// Budgets are taken strictly inside (<paramref name="floor"/>, <paramref name="ceiling"/>): at the charset's offsets above the floor when <paramref name="fromFloor"/> (and when every one of those fails, a second batch above the highest, spread across the most a cut kept off a run of U+FEFF leaves unused: whenever two of its budgets fit below a ceiling that <paramref name="ceilingHolds"/> the count, and below one that may not only when at least half its budgets do), else all of them when there are at most eight, else eight that cut the bracket into nine.
    /// False when no batch was walked (no acceleration, short chunks, fewer than two budgets, or a character that fits no chunk), and then nothing is changed.
    /// </summary>
    internal static bool TryNarrowWithLanes(ReadOnlySpan<char> text, EciMode charset, int version, int floor, int ceiling, int limit, ref int low, ref int high, Span<int> settledEnds, ref int settledBudget, ref int settledCount, Span<int> failedEnds, ref int failedBudget, bool fromFloor = false, bool ceilingHolds = false)
    {
#if NET8_0_OR_GREATER
        if (!Vector128.IsHardwareAccelerated || text.Length < limit * LaneChunkChars)
            return false;

        Span<int> budgets = stackalloc int[Lanes];
        Span<int> counts = stackalloc int[Lanes];
        Span<int> laneEnds = stackalloc int[Lanes * MaxSymbols];
        var walked = false;
        var spread = 0;
        while (true)
        {
            var used = 0;
            if (fromFloor)
            {
                var offsets = charset == EciMode.Utf8 ? Utf8LaneOffsets : OneByteLaneOffsets;
                while (used < Lanes && floor + offsets[used] + spread * (used + 1) / Lanes < ceiling)
                {
                    budgets[used] = floor + offsets[used] + spread * (used + 1) / Lanes;
                    used++;
                }
            }
            else
            {
                var width = ceiling - floor;
                used = Math.Min(Lanes, width);
                for (var lane = 0; lane < used; lane++)
                    budgets[lane] = width <= Lanes ? floor + lane : floor + (int)((long)(lane + 1) * width / (Lanes + 1));
            }
            if (used < 2 || (spread > 0 && !ceilingHolds && used < Lanes / 2))
                return walked;

            var shared = SharedChunks(settledEnds, settledBudget, settledCount, failedEnds, failedBudget, limit, budgets[0]);
            var start = shared > 0 ? settledEnds[shared - 1] : 0;
            if (!WalkLanes(text, charset, version, budgets.Slice(0, used), limit, shared, start, counts, laneEnds, out _))
                return walked;

            // Lanes are ordered by budget and holding is monotone: failures, then holds.
            // The shared chunks are already in both buffers, which is what shared means.
            var firstHeld = used;
            for (var lane = 0; lane < used; lane++)
            {
                if (counts[lane] <= limit)
                {
                    firstHeld = lane;
                    break;
                }
            }

            if (firstHeld < used)
            {
                high = budgets[firstHeld];
                settledBudget = high;
                settledCount = counts[firstHeld];
                laneEnds.Slice(firstHeld * MaxSymbols + shared, settledCount - shared).CopyTo(settledEnds.Slice(shared));
            }
            if (firstHeld > 0)
            {
                low = budgets[firstHeld - 1] + 1;
                failedBudget = budgets[firstHeld - 1];
                laneEnds.Slice((firstHeld - 1) * MaxSymbols + shared, limit - shared).CopyTo(failedEnds.Slice(shared));
            }
            walked = true;

            // Cuts kept off runs of U+FEFF can leave the answer above every budget near the floor, by up to the longest run; one more batch from the highest that failed reaches that far, and the texts whose answer is near the floor keep their first batch as it was.
            // Under a ceiling not known to hold the count the answer can be past it, and a batch the ceiling cuts to a few budgets is then a walk the caller's fallback repeats, so it is taken only when at least half of it fits.
            if (!fromFloor || firstHeld < used || spread > 0)
                return true;
            spread = KeptOffBits(text, charset);
            if (spread == 0)
                return true;
            floor = budgets[used - 1];
        }
#else
        return false;
#endif
    }

#if NET8_0_OR_GREATER
    /// <summary>
    /// Walks the text at up to eight budgets from <paramref name="start"/>, with <paramref name="placed"/> chunks already placed.
    /// counts[lane] receives the walk's chunk count, or limit + 1 when it needs more; laneEnds[lane * MaxSymbols + k] the end of chunk k for k at or past placed.
    /// False when some character fits no chunk at some budget, which the scalar walk reports. <paramref name="apartSteps"/> counts the steps taken with the lanes at different characters, the slow ones, which the waiting keeps to a few per chunk.
    /// </summary>
    internal static bool WalkLanes(ReadOnlySpan<char> text, EciMode charset, int version, ReadOnlySpan<int> budgets, int limit, int placed, int start, Span<int> counts, Span<int> laneEnds, out int apartSteps)
    {
        LaneBatches++;
        // The search supplies QR capacities. Keep the 32-bit reference for direct walks
        // outside the 16-bit cost domain, including budgets smaller than the set headers.
        if (AdvSimd.Arm64.IsSupported && BudgetsFit16(budgets, charset))
        {
            return charset == EciMode.Utf8
                ? WalkLanesNeon<Utf8Chars>(text, charset, version, budgets, limit, placed, start, counts, laneEnds, out apartSteps)
                : WalkLanesNeon<OneByteChars>(text, charset, version, budgets, limit, placed, start, counts, laneEnds, out apartSteps);
        }
        // Without 256-bit vectors the 32-bit walk hands budgets that fit 16 bits to the portable 16-bit lanes; this dispatch keeps its size, at which it is inlined.
        return charset == EciMode.Utf8
            ? WalkLanes<Utf8Chars>(text, charset, version, budgets, limit, placed, start, counts, laneEnds, out apartSteps)
            : WalkLanes<OneByteChars>(text, charset, version, budgets, limit, placed, start, counts, laneEnds, out apartSteps);
    }

    /// <summary><see cref="WalkLanes"/> in the portable 16-bit lanes, entered directly by its parity test: the dispatch takes it only without 256-bit vectors and NEON.</summary>
    internal static bool WalkLanesVector128(ReadOnlySpan<char> text, EciMode charset, int version, ReadOnlySpan<int> budgets, int limit, int placed, int start, Span<int> counts, Span<int> laneEnds, out int apartSteps)
        => charset == EciMode.Utf8
            ? WalkLanesVector128<Utf8Chars>(text, charset, version, budgets, limit, placed, start, counts, laneEnds, out apartSteps)
            : WalkLanesVector128<OneByteChars>(text, charset, version, budgets, limit, placed, start, counts, laneEnds, out apartSteps);

    private static bool BudgetsFit16(ReadOnlySpan<int> budgets, EciMode charset)
    {
        var headers = HeaderBits + charset.GetStandardQrHeaderBits();
        foreach (var budget in budgets)
        {
            if ((uint)(budget - headers) >= ushort.MaxValue)
                return false;
        }
        return true;
    }

    // A pair needs no rule of its own while stepping: a cut never splits one, so whichever half breaks a budget, the chunk ends before the pair.
    /// <summary>
    /// The end a chunk from <paramref name="chunkStart"/> takes when it closes before <paramref name="position"/>: before a pair whose second half is there, since a split never cuts one, and off a U+FEFF as the scalar walk keeps it (<see cref="BeforeMark"/>).
    /// <paramref name="chunkStart"/> when that leaves the chunk nothing.
    /// </summary>
    private static int EndBefore(ReadOnlySpan<char> text, EciMode charset, int chunkStart, int position)
    {
        var end = position > 0 && char.IsLowSurrogate(text[position]) && char.IsHighSurrogate(text[position - 1]) ? position - 1 : position;
        return Math.Max(BeforeMark(text, charset, chunkStart, end), chunkStart);
    }

    /// <summary>
    /// What the chunk from <paramref name="end"/> costs just before <paramref name="position"/> when a cut was kept off the run of U+FEFF there: the character at <paramref name="end"/>, a pair whole, and the marks after it, one Byte run, since none opens at a mark past a chunk's head.
    /// -1 when no mark lies between, and the lane re-reads from <paramref name="end"/>.
    /// </summary>
    private static int KeptOffRunBits(ReadOnlySpan<char> text, EciMode charset, int end, int position, int openByte)
    {
        var width = end + 1 < text.Length && char.IsHighSurrogate(text[end]) && char.IsLowSurrogate(text[end + 1]) ? 2 : 1;
        var marks = position - end - width;
        if (marks <= 0)
            return -1;
        Debug.Assert(text.Slice(end + width, marks + 1).IndexOfAnyExcept(ByteOrderMark) < 0, "a cut is moved back over marks only");
        // A mark is three bytes in UTF-8, the only charset whose cuts are kept off marks.
        return openByte + 8 * (ModeSegmenter.ByteCost(text, end, charset) + 3 * marks);
    }
#endif
}
