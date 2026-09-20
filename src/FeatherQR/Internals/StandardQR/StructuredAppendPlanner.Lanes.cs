#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
#endif

namespace FeatherQR.Internals.StandardQR;

/// <summary>
/// The planner's greedy walk at up to eight budgets at once: one budget per vector lane, one vector per state of the segmentation program.
/// </summary>
/// <remarks>
/// The probes of a budget search walk the same text from the same start and differ only in the budget they close their chunks at, so they are independent instances of one program and share its control: while every lane is at the same character, which is nearly always, a step is the scalar loop's class lookup and branches over vector adds and mins, at about the cost of one scalar step for the eight of them.
/// A lane whose chunk closes re-reads the character that did not fit as the first of its next chunk, so it falls behind the others: by one step, by two when it closed on the second half of a pair, by more when the cut is kept off a U+FEFF. While the lanes are apart they are stepped with the class and the byte cost taken per lane in scalar code, and the lanes ahead wait, keeping their states, until the one furthest behind is level with them; left apart, lanes whose closes cost them differently never share a character again. Closing a chunk is itself scalar code, a dozen times per lane in a walk of thousands of steps.
/// A lane that has failed keeps closing chunks at its own budget, unrecorded: one that stopped would run ahead for good and the lanes would never share a character again.
/// The walk prices exactly what <see cref="CountChunks(ReadOnlySpan{char}, EciMode, bool, QRSegmentation, int, int, int, Span{int})"/> prices (the single-mode shortcuts of <see cref="LongestChunkEnd"/> are the program's own answers on the content they apply to), which <c>StructuredAppendLaneWalkTest</c> holds lane by lane. The byte order mark <see cref="QRCodeGeneratorOptions.Utf8Bom"/> asks for is not handled here, since its chunk is priced by another rule; the caller keeps those walks scalar.
/// The eight 32-bit lanes use accelerated <c>Vector256</c>; ARM64 uses eight saturating 16-bit NEON lanes. Without either capability, or when a chunk averages under the backend's minimum length (the lanes then spend too many steps apart), nothing here runs and the caller's scalar probes do.
/// </remarks>
internal static partial class StructuredAppendPlanner
{
    /// <summary>Shortest average chunk, in characters, the lanes are used for: below it chunks close so often, a step or two apart, that the lanes are rarely at one character.</summary>
    private const int MinLaneChunkChars = 128;

    // NEON's compact cost state pays on much shorter chunks. Twenty characters is a
    // conservative cutoff: setup and frequent divergent steps can outweigh batching below it.
    private const int MinNeonLaneChunkChars = 20;

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
        if ((!Vector256.IsHardwareAccelerated && !AdvSimd.Arm64.IsSupported)
            || text.Length < limit * (AdvSimd.Arm64.IsSupported ? MinNeonLaneChunkChars : MinLaneChunkChars))
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

            // Lanes are ordered by budget and holding is monotone: failures, then holds. The shared
            // chunks are already in both buffers, which is what shared means.
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

            // Cuts kept off runs of U+FEFF can leave the answer above every budget near the floor, by
            // up to the longest run; one more batch from the highest that failed reaches that far, and
            // the texts whose answer is near the floor keep their first batch as it was.
            // Under a ceiling not known to hold the count the answer can be past it, and a batch the ceiling cuts to a
            // few budgets is then a walk the caller's fallback repeats, so it is taken only when at least half of it fits.
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
        if (AdvSimd.Arm64.IsSupported && NeonBudgetsFit(budgets, charset))
        {
            return charset == EciMode.Utf8
                ? WalkLanesNeon<Utf8Chars>(text, charset, version, budgets, limit, placed, start, counts, laneEnds, out apartSteps)
                : WalkLanesNeon<OneByteChars>(text, charset, version, budgets, limit, placed, start, counts, laneEnds, out apartSteps);
        }
        return charset == EciMode.Utf8
            ? WalkLanes<Utf8Chars>(text, charset, version, budgets, limit, placed, start, counts, laneEnds, out apartSteps)
            : WalkLanes<OneByteChars>(text, charset, version, budgets, limit, placed, start, counts, laneEnds, out apartSteps);
    }

    private static bool NeonBudgetsFit(ReadOnlySpan<int> budgets, EciMode charset)
    {
        var headers = HeaderBits + charset.GetStandardQrHeaderBits();
        foreach (var budget in budgets)
        {
            if ((uint)(budget - headers) >= ushort.MaxValue)
                return false;
        }
        return true;
    }

    // A pair needs no rule of its own while stepping: it is priced whole on its first half, so a
    // budget it breaks is broken there and on its second half alike, and either way the chunk ends
    // before the pair.
    /// <summary>
    /// The end a chunk from <paramref name="chunkStart"/> takes when it closes before <paramref name="position"/>: before a pair whose second half is there, since a split never cuts one, and off a U+FEFF as the scalar walk keeps it (<see cref="BeforeMark"/>).
    /// <paramref name="chunkStart"/> when that leaves the chunk nothing.
    /// </summary>
    private static int EndBefore(ReadOnlySpan<char> text, EciMode charset, int chunkStart, int position)
    {
        var end = position > 0 && char.IsLowSurrogate(text[position]) && char.IsHighSurrogate(text[position - 1]) ? position - 1 : position;
        return Math.Max(BeforeMark(text, charset, chunkStart, end), chunkStart);
    }

    private static bool WalkLanes<TWidth>(ReadOnlySpan<char> text, EciMode charset, int version, ReadOnlySpan<int> budgets, int limit, int placed, int start, Span<int> counts, Span<int> laneEnds, out int apartSteps)
        where TWidth : ICharWidth
    {
        const int unreachableCost = ModeSegmenter.Unreachable;
        var used = budgets.Length;
        var length = text.Length;
        var openNumeric = ModeIndicatorBits + EncodingMode.Numeric.GetCountIndicatorLength(version) + 4;
        var openAlnum = ModeIndicatorBits + EncodingMode.Alphanumeric.GetCountIndicatorLength(version) + 6;
        var openByte = ModeIndicatorBits + EncodingMode.Byte.GetCountIndicatorLength(version);

        // A walk compares the program's cost with its budget less the headers every symbol pays.
        // Unused lanes repeat the last budget, so they stay in step with it and report nothing.
        Span<int> budget = stackalloc int[Lanes];
        Span<int> offset = stackalloc int[Lanes]; // a lane's position is its offset plus the step
        Span<int> chunkStart = stackalloc int[Lanes];
        Span<int> chunks = stackalloc int[Lanes];
        Span<bool> failed = stackalloc bool[Lanes];
        for (var lane = 0; lane < Lanes; lane++)
        {
            budget[lane] = budgets[lane < used ? lane : used - 1] - HeaderBits - charset.GetStandardQrHeaderBits();
            offset[lane] = start;
            chunkStart[lane] = start;
            chunks[lane] = placed;
            failed[lane] = lane >= used;
        }

        var unreachable = Vector256.Create(unreachableCost);
        var n0 = unreachable;
        var n1 = unreachable;
        var n2 = unreachable;
        var a0 = unreachable;
        var a1 = unreachable;
        var b = unreachable;
        var cheapest = Vector256<int>.Zero; // the start state, then the cheapest of the six
        var vOpenNumeric = Vector256.Create(openNumeric);
        var vOpenAlnum = Vector256.Create(openAlnum);
        var vOpenByte = Vector256.Create(openByte);
        var vOpenByte8 = Vector256.Create(openByte + 8);
        var eight = Vector256.Create(8);
        var vBudget = Vector256.Create(budget[0], budget[1], budget[2], budget[3], budget[4], budget[5], budget[6], budget[7]);
        ref var origin = ref MemoryMarshal.GetReference(text);
        int s0 = start, s1 = start, s2 = start, s3 = start, s4 = start, s5 = start, s6 = start, s7 = start;

        // The vector loop runs until the lane furthest ahead reaches the end of the text; the others
        // are a few characters behind and finish in scalar code.
        var step = 0;
        apartSteps = 0;
        var stop = length - start;
        var together = true;
        while (step < stop)
        {
            Vector256<int> nn0, nn1, nn2, na0, na1, nb, next, over;
            if (together)
            {
                var position = s0 + step;
                var ch = Unsafe.Add(ref origin, position);
                var cls = ModeSegmenter.ClassOf(ch);
                if (cls == ModeSegmenter.ClassOther)
                {
                    // No Byte run opens at a U+FEFF past the head of a chunk (ModeSegmenter.ByteOrderMark), and only the first chunk can start at one.
                    nb = TWidth.Utf8
                        ? (ch == ModeSegmenter.ByteOrderMark && position > start ? b : Vector256.Min(b, cheapest + vOpenByte)) + Vector256.Create(8 * ModeSegmenter.ByteCost(text, position, charset))
                        : Vector256.Min(b + eight, cheapest + vOpenByte8);
                    if (Vector256.GreaterThan(nb, vBudget).ExtractMostSignificantBits() == 0)
                    {
                        // Only Byte is reachable now, and every further character outside the
                        // alphabet extends its run: one add and the budget test.
                        n0 = n1 = n2 = a0 = a1 = unreachable;
                        b = nb;
                        step++;
                        while (step < stop)
                        {
                            position = s0 + step;
                            ch = Unsafe.Add(ref origin, position);
                            if (ModeSegmenter.ClassOf(ch) != ModeSegmenter.ClassOther)
                                break;
                            nb = TWidth.Utf8 ? b + Vector256.Create(8 * ModeSegmenter.ByteCost(text, position, charset)) : b + eight;
                            if (Vector256.GreaterThan(nb, vBudget).ExtractMostSignificantBits() != 0)
                                break; // some lane closes here: the step above takes it
                            b = nb;
                            step++;
                        }
                        cheapest = b;
                        continue;
                    }

                    nn0 = nn1 = nn2 = na0 = na1 = unreachable;
                    next = nb;
                }
                else
                {
                    nb = Vector256.Min(b + eight, cheapest + vOpenByte8);
                    na1 = Vector256.Min(a0 + Vector256.Create(6), cheapest + vOpenAlnum);
                    na0 = a1 + Vector256.Create(5);
                    if (cls == ModeSegmenter.ClassDigit)
                    {
                        nn1 = Vector256.Min(n0 + Vector256.Create(4), cheapest + vOpenNumeric);
                        nn2 = n1 + Vector256.Create(3);
                        nn0 = n2 + Vector256.Create(3);
                        next = Vector256.Min(Vector256.Min(Vector256.Min(nn0, nn1), Vector256.Min(nn2, na0)), Vector256.Min(na1, nb));
                    }
                    else
                    {
                        nn0 = nn1 = nn2 = unreachable;
                        next = Vector256.Min(Vector256.Min(na0, na1), nb);
                    }
                }
                over = Vector256.GreaterThan(next, vBudget);
            }
            else
            {
                apartSteps++;
                int p0 = s0 + step, p1 = s1 + step, p2 = s2 + step, p3 = s3 + step, p4 = s4 + step, p5 = s5 + step, p6 = s6 + step, p7 = s7 + step;
                var classes = Vector256.Create(
                    ModeSegmenter.ClassOf(Unsafe.Add(ref origin, p0)), ModeSegmenter.ClassOf(Unsafe.Add(ref origin, p1)), ModeSegmenter.ClassOf(Unsafe.Add(ref origin, p2)), ModeSegmenter.ClassOf(Unsafe.Add(ref origin, p3)),
                    ModeSegmenter.ClassOf(Unsafe.Add(ref origin, p4)), ModeSegmenter.ClassOf(Unsafe.Add(ref origin, p5)), ModeSegmenter.ClassOf(Unsafe.Add(ref origin, p6)), ModeSegmenter.ClassOf(Unsafe.Add(ref origin, p7)));
                var isDigit = Vector256.Equals(classes, Vector256.Create(ModeSegmenter.ClassDigit));
                var isAlnum = Vector256.GreaterThan(classes, Vector256<int>.Zero);
                var byteBits = TWidth.Utf8
                    ? Vector256.Create(
                        8 * ModeSegmenter.ByteCost(text, p0, charset), 8 * ModeSegmenter.ByteCost(text, p1, charset), 8 * ModeSegmenter.ByteCost(text, p2, charset), 8 * ModeSegmenter.ByteCost(text, p3, charset),
                        8 * ModeSegmenter.ByteCost(text, p4, charset), 8 * ModeSegmenter.ByteCost(text, p5, charset), 8 * ModeSegmenter.ByteCost(text, p6, charset), 8 * ModeSegmenter.ByteCost(text, p7, charset))
                    : eight;

                nn1 = Vector256.ConditionalSelect(isDigit, Vector256.Min(n0 + Vector256.Create(4), cheapest + vOpenNumeric), unreachable);
                nn2 = Vector256.ConditionalSelect(isDigit, n1 + Vector256.Create(3), unreachable);
                nn0 = Vector256.ConditionalSelect(isDigit, n2 + Vector256.Create(3), unreachable);
                na1 = Vector256.ConditionalSelect(isAlnum, Vector256.Min(a0 + Vector256.Create(6), cheapest + vOpenAlnum), unreachable);
                na0 = Vector256.ConditionalSelect(isAlnum, a1 + Vector256.Create(5), unreachable);
                // No rule for a U+FEFF here: a lane that moves while the lanes are apart is at the head of its chunk, one
                // character and then the marks its cut was kept off, where continuing that character's run is never dearer than opening another.
                nb = Vector256.Min(b, cheapest + vOpenByte) + byteBits;
                next = Vector256.Min(Vector256.Min(Vector256.Min(nn0, nn1), Vector256.Min(nn2, na0)), Vector256.Min(na1, nb));

                // The lanes ahead of the one furthest behind wait for it: they keep their states and read this
                // character again, so the lanes are back at one character a few steps after a close, whatever
                // the close cost each of them (a pair is two characters back, a cut kept off a mark more).
                var lowest = Math.Min(Math.Min(Math.Min(s0, s1), Math.Min(s2, s3)), Math.Min(Math.Min(s4, s5), Math.Min(s6, s7)));
                var hold = Vector256.GreaterThan(Vector256.Create(s0, s1, s2, s3, s4, s5, s6, s7), Vector256.Create(lowest));
                nn0 = Vector256.ConditionalSelect(hold, n0, nn0);
                nn1 = Vector256.ConditionalSelect(hold, n1, nn1);
                nn2 = Vector256.ConditionalSelect(hold, n2, nn2);
                na0 = Vector256.ConditionalSelect(hold, a0, na0);
                na1 = Vector256.ConditionalSelect(hold, a1, na1);
                nb = Vector256.ConditionalSelect(hold, b, nb);
                next = Vector256.ConditionalSelect(hold, cheapest, next);
                over = Vector256.GreaterThan(next, vBudget); // never a lane that waits: what it keeps was within its budget
                s0 += hold.GetElement(0);
                s1 += hold.GetElement(1);
                s2 += hold.GetElement(2);
                s3 += hold.GetElement(3);
                s4 += hold.GetElement(4);
                s5 += hold.GetElement(5);
                s6 += hold.GetElement(6);
                s7 += hold.GetElement(7);
                offset[0] = s0;
                offset[1] = s1;
                offset[2] = s2;
                offset[3] = s3;
                offset[4] = s4;
                offset[5] = s5;
                offset[6] = s6;
                offset[7] = s7;
                together = s0 == s1 && s1 == s2 && s2 == s3 && s3 == s4 && s4 == s5 && s5 == s6 && s6 == s7;
            }

            var overBits = over.ExtractMostSignificantBits();
            if (overBits == 0)
            {
                n0 = nn0;
                n1 = nn1;
                n2 = nn2;
                a0 = na0;
                a1 = na1;
                b = nb;
                cheapest = next;
                step++;
                continue;
            }

            // Some lane's chunk closes before this character, which starts its next chunk: the lane
            // re-reads it on the next step, with fresh states.
            for (var lane = 0; lane < Lanes; lane++)
            {
                if ((overBits & (1u << lane)) == 0)
                    continue;
                var position = offset[lane] + step;
                var end = EndBefore(text, charset, chunkStart[lane], position);
                if (!failed[lane])
                {
                    if (end == chunkStart[lane])
                        return false;
                    laneEnds[lane * MaxSymbols + chunks[lane]] = end;
                    chunks[lane]++;
                    if (chunks[lane] == limit)
                    {
                        // Text remains and the limit is spent: this budget does not hold the count.
                        failed[lane] = true;
                        counts[lane] = limit + 1;
                    }
                }
                else if (end == chunkStart[lane])
                {
                    // It reports nothing any more and cannot place this character: let it through
                    // rather than close on it for ever.
                    budget[lane] = int.MaxValue;
                }
                chunkStart[lane] = end;
                offset[lane] -= 1 + (position - end);
            }

            vBudget = Vector256.Create(budget[0], budget[1], budget[2], budget[3], budget[4], budget[5], budget[6], budget[7]);
            n0 = Vector256.ConditionalSelect(over, unreachable, nn0);
            n1 = Vector256.ConditionalSelect(over, unreachable, nn1);
            n2 = Vector256.ConditionalSelect(over, unreachable, nn2);
            a0 = Vector256.ConditionalSelect(over, unreachable, na0);
            a1 = Vector256.ConditionalSelect(over, unreachable, na1);
            b = Vector256.ConditionalSelect(over, unreachable, nb);
            cheapest = Vector256.ConditionalSelect(over, Vector256<int>.Zero, next);
            s0 = offset[0];
            s1 = offset[1];
            s2 = offset[2];
            s3 = offset[3];
            s4 = offset[4];
            s5 = offset[5];
            s6 = offset[6];
            s7 = offset[7];
            together = s0 == s1 && s1 == s2 && s2 == s3 && s3 == s4 && s4 == s5 && s5 == s6 && s6 == s7;
            step++;
            stop = length - Math.Max(Math.Max(Math.Max(s0, s1), Math.Max(s2, s3)), Math.Max(Math.Max(s4, s5), Math.Max(s6, s7)));
        }

        // Each lane finishes its last few characters in scalar code, from its own states.
        for (var lane = 0; lane < used; lane++)
        {
            if (failed[lane])
                continue;
            var position = offset[lane] + step;
            int l0 = n0.GetElement(lane), l1 = n1.GetElement(lane), l2 = n2.GetElement(lane), m0 = a0.GetElement(lane), m1 = a1.GetElement(lane), lb = b.GetElement(lane), lc = cheapest.GetElement(lane);
            var laneChunks = chunks[lane];
            var laneStart = chunkStart[lane];
            var held = true;
            while (position < length)
            {
                var cls = ModeSegmenter.ClassOf(text[position]);
                var byteBits = 8 * ModeSegmenter.ByteCost(text, position, charset);
                int t0 = unreachableCost, t1 = unreachableCost, t2 = unreachableCost, u0 = unreachableCost, u1 = unreachableCost;
                if (cls == ModeSegmenter.ClassDigit)
                {
                    t1 = Math.Min(l0 + 4, lc + openNumeric);
                    t2 = l1 + 3;
                    t0 = l2 + 3;
                }
                if (cls != ModeSegmenter.ClassOther)
                {
                    u1 = Math.Min(m0 + 6, lc + openAlnum);
                    u0 = m1 + 5;
                }
                var tb = (TWidth.Utf8 && text[position] == ModeSegmenter.ByteOrderMark ? lb : Math.Min(lb, lc + openByte)) + byteBits;
                var cost = Math.Min(Math.Min(Math.Min(t0, t1), Math.Min(t2, u0)), Math.Min(u1, tb));
                if (cost > budget[lane])
                {
                    var end = EndBefore(text, charset, laneStart, position);
                    if (end == laneStart)
                        return false;
                    laneEnds[lane * MaxSymbols + laneChunks] = end;
                    laneChunks++;
                    if (laneChunks == limit)
                    {
                        held = false;
                        break;
                    }
                    laneStart = end;
                    position = end;
                    l0 = l1 = l2 = m0 = m1 = lb = unreachableCost;
                    lc = 0;
                    continue;
                }

                l0 = t0;
                l1 = t1;
                l2 = t2;
                m0 = u0;
                m1 = u1;
                lb = tb;
                lc = cost;
                position++;
            }

            if (held)
            {
                laneEnds[lane * MaxSymbols + laneChunks] = length;
                counts[lane] = laneChunks + 1;
            }
            else
            {
                counts[lane] = limit + 1;
            }
        }

        return true;
    }
#endif
}
