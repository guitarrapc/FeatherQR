#if NET8_0_OR_GREATER
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace FeatherQR.Internals.StandardQR;

internal static partial class StructuredAppendPlanner
{
    private static bool WalkLanes<TWidth>(ReadOnlySpan<char> text, EciMode charset, int version, ReadOnlySpan<int> budgets, int limit, int placed, int start, Span<int> counts, Span<int> laneEnds, out int apartSteps)
        where TWidth : ICharWidth
    {
        if (!AdvSimd.Arm64.IsSupported && !Vector256.IsHardwareAccelerated && BudgetsFit16(budgets, charset))
            return WalkLanesVector128<TWidth>(text, charset, version, budgets, limit, placed, start, counts, laneEnds, out apartSteps);

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

        // The vector loop runs until the lane furthest ahead reaches the end of the text, by an exit updated only at a close, so it can stop up to a close's set-back short; each lane finishes in scalar code from where it stands.
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
                // No rule for a U+FEFF here: a lane that moves while the lanes are apart is at the head of its chunk, one character and then the marks its cut was kept off, where continuing that character's run is never dearer than opening another.
                nb = Vector256.Min(b, cheapest + vOpenByte) + byteBits;
                next = Vector256.Min(Vector256.Min(Vector256.Min(nn0, nn1), Vector256.Min(nn2, na0)), Vector256.Min(na1, nb));

                // The lanes ahead of the one furthest behind wait for it: they keep their states and read this character again, so the lanes are back at one character a few steps after a close, whatever the close cost each of them (a pair is two characters back, a cut kept off a run at its first mark two or three).
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

            // Some lane's chunk closes before this character. The lane re-reads its next chunk from the cut; when marks lie between the cut and this character, the character ahead and the marks are priced here and the lane re-reads only this character.
            var kept = int.MinValue; // not priced yet this step
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
                    // It reports nothing any more and cannot place this character: let it through rather than close on it for ever.
                    budget[lane] = int.MaxValue;
                }
                chunkStart[lane] = end;
                if (TWidth.Utf8)
                {
                    // Re-reading from the cut would hold the other lanes a step for every character re-read. Every lane that closes in a step stands at this character with one cut, so all of them resume or none, at one cost.
                    var laneKept = position - end >= 2 ? KeptOffRunBits(text, charset, end, position, openByte) : -1;
                    Debug.Assert(kept == int.MinValue || laneKept == kept);
                    kept = laneKept;
                    if (kept >= 0)
                    {
                        // The closing chunk held these marks in one Byte run from the character ahead or an earlier one, within its budget.
                        Debug.Assert(kept <= budget[lane]);
                        offset[lane] -= 1;
                        continue;
                    }
                }
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
            if (TWidth.Utf8 && kept >= 0)
            {
                // Only Byte is reachable after a mark, so the run's cost is both states. Only UTF-8 cuts are kept off marks, and the gate keeps this out of the one-byte walk's code.
                var cost = Vector256.Create(kept);
                b = Vector256.ConditionalSelect(over, cost, b);
                cheapest = Vector256.ConditionalSelect(over, cost, cheapest);
            }
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
}
#endif
