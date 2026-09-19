#if NET8_0_OR_GREATER
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace FeatherQR.Internals.StandardQR;

internal static partial class StructuredAppendPlanner
{
    /// <summary>The eight-budget walk on ARM64, with one unsigned 16-bit cost per NEON lane.</summary>
    /// <remarks>
    /// A Standard QR symbol holds at most 23,648 data bits. Costs below a real budget are
    /// therefore exact in 16 bits; saturating addition keeps every unreachable or overlarge
    /// cost above that budget instead of wrapping it into a cheaper plan. The all-ones value
    /// is unreachable. Text offsets stay 32-bit, since the whole set can exceed 65,535 characters.
    /// Keeping the native vectors in locals avoids materializing each state on the stack.
    /// </remarks>
    private static bool WalkLanesNeon<TWidth>(ReadOnlySpan<char> text, EciMode charset, int version, ReadOnlySpan<int> budgets, int limit, int placed, int start, Span<int> counts, Span<int> laneEnds, out int apartSteps)
        where TWidth : ICharWidth
    {
        Debug.Assert(AdvSimd.Arm64.IsSupported);
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

        var unreachable = Vector128.Create(ushort.MaxValue);
        var n0 = unreachable;
        var n1 = unreachable;
        var n2 = unreachable;
        var a0 = unreachable;
        var a1 = unreachable;
        var b = unreachable;
        var cheapest = Vector128<ushort>.Zero; // the start state, then the cheapest of the six
        var vOpenNumeric = NeonCosts(openNumeric);
        var vOpenAlnum = NeonCosts(openAlnum);
        var vOpenByte = NeonCosts(openByte);
        var vOpenByte8 = NeonCosts(openByte + 8);
        var eight = NeonCosts(8);
        var vBudget = NeonCosts(budget[0], budget[1], budget[2], budget[3], budget[4], budget[5], budget[6], budget[7]);
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
            Vector128<ushort> nn0, nn1, nn2, na0, na1, nb, next, over;
            if (together)
            {
                var position = s0 + step;
                var ch = Unsafe.Add(ref origin, position);
                var cls = ModeSegmenter.ClassOf(ch);
                if (cls == ModeSegmenter.ClassOther)
                {
                    // No Byte run opens at a U+FEFF past the head of a chunk (ModeSegmenter.ByteOrderMark), and only the first chunk can start at one.
                    nb = TWidth.Utf8
                        ? AdvSimd.AddSaturate(ch == ModeSegmenter.ByteOrderMark && position > start ? b : Vector128.Min(b, AdvSimd.AddSaturate(cheapest, vOpenByte)), NeonCosts(8 * LaneByteCost(text, position, charset)))
                        : Vector128.Min(AdvSimd.AddSaturate(b, eight), AdvSimd.AddSaturate(cheapest, vOpenByte8));
                    if (AdvSimd.Arm64.MaxAcross(Vector128.GreaterThan(nb, vBudget)).ToScalar() == 0)
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
                            nb = TWidth.Utf8 ? AdvSimd.AddSaturate(b, NeonCosts(8 * LaneByteCost(text, position, charset))) : AdvSimd.AddSaturate(b, eight);
                            if (AdvSimd.Arm64.MaxAcross(Vector128.GreaterThan(nb, vBudget)).ToScalar() != 0)
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
                    nb = Vector128.Min(AdvSimd.AddSaturate(b, eight), AdvSimd.AddSaturate(cheapest, vOpenByte8));
                    na1 = Vector128.Min(AdvSimd.AddSaturate(a0, NeonCosts(6)), AdvSimd.AddSaturate(cheapest, vOpenAlnum));
                    na0 = AdvSimd.AddSaturate(a1, NeonCosts(5));
                    if (cls == ModeSegmenter.ClassDigit)
                    {
                        nn1 = Vector128.Min(AdvSimd.AddSaturate(n0, NeonCosts(4)), AdvSimd.AddSaturate(cheapest, vOpenNumeric));
                        nn2 = AdvSimd.AddSaturate(n1, NeonCosts(3));
                        nn0 = AdvSimd.AddSaturate(n2, NeonCosts(3));
                        next = Vector128.Min(Vector128.Min(Vector128.Min(nn0, nn1), Vector128.Min(nn2, na0)), Vector128.Min(na1, nb));
                    }
                    else
                    {
                        nn0 = nn1 = nn2 = unreachable;
                        next = Vector128.Min(Vector128.Min(na0, na1), nb);
                    }
                }
                over = Vector128.GreaterThan(next, vBudget);
            }
            else
            {
                apartSteps++;
                int p0 = s0 + step, p1 = s1 + step, p2 = s2 + step, p3 = s3 + step, p4 = s4 + step, p5 = s5 + step, p6 = s6 + step, p7 = s7 + step;
                var classes = NeonCosts(
                    ModeSegmenter.ClassOf(Unsafe.Add(ref origin, p0)), ModeSegmenter.ClassOf(Unsafe.Add(ref origin, p1)), ModeSegmenter.ClassOf(Unsafe.Add(ref origin, p2)), ModeSegmenter.ClassOf(Unsafe.Add(ref origin, p3)),
                    ModeSegmenter.ClassOf(Unsafe.Add(ref origin, p4)), ModeSegmenter.ClassOf(Unsafe.Add(ref origin, p5)), ModeSegmenter.ClassOf(Unsafe.Add(ref origin, p6)), ModeSegmenter.ClassOf(Unsafe.Add(ref origin, p7)));
                var isDigit = Vector128.Equals(classes, NeonCosts(ModeSegmenter.ClassDigit));
                var isAlnum = Vector128.GreaterThan(classes, Vector128<ushort>.Zero);
                var byteBits = TWidth.Utf8
                    ? NeonCosts(
                        8 * LaneByteCost(text, p0, charset), 8 * LaneByteCost(text, p1, charset), 8 * LaneByteCost(text, p2, charset), 8 * LaneByteCost(text, p3, charset),
                        8 * LaneByteCost(text, p4, charset), 8 * LaneByteCost(text, p5, charset), 8 * LaneByteCost(text, p6, charset), 8 * LaneByteCost(text, p7, charset))
                    : eight;

                nn1 = Vector128.ConditionalSelect(isDigit, Vector128.Min(AdvSimd.AddSaturate(n0, NeonCosts(4)), AdvSimd.AddSaturate(cheapest, vOpenNumeric)), unreachable);
                nn2 = Vector128.ConditionalSelect(isDigit, AdvSimd.AddSaturate(n1, NeonCosts(3)), unreachable);
                nn0 = Vector128.ConditionalSelect(isDigit, AdvSimd.AddSaturate(n2, NeonCosts(3)), unreachable);
                na1 = Vector128.ConditionalSelect(isAlnum, Vector128.Min(AdvSimd.AddSaturate(a0, NeonCosts(6)), AdvSimd.AddSaturate(cheapest, vOpenAlnum)), unreachable);
                na0 = Vector128.ConditionalSelect(isAlnum, AdvSimd.AddSaturate(a1, NeonCosts(5)), unreachable);
                // No rule for a U+FEFF here: a lane that moves while the lanes are apart is at the head of its chunk, one
                // character and then the marks its cut was kept off, where continuing that character's run is never dearer than opening another.
                nb = AdvSimd.AddSaturate(Vector128.Min(b, AdvSimd.AddSaturate(cheapest, vOpenByte)), byteBits);
                next = Vector128.Min(Vector128.Min(Vector128.Min(nn0, nn1), Vector128.Min(nn2, na0)), Vector128.Min(na1, nb));

                // The lanes ahead of the one furthest behind wait for it: they keep their states and read this
                // character again, so the lanes are back at one character a few steps after a close, whatever
                // the close cost each of them (a pair is two characters back, a cut kept off a mark more).
                var lowest = Math.Min(Math.Min(Math.Min(s0, s1), Math.Min(s2, s3)), Math.Min(Math.Min(s4, s5), Math.Min(s6, s7)));
                var hold = NeonHoldOffsets(s0, s1, s2, s3, s4, s5, s6, s7, lowest);
                nn0 = Vector128.ConditionalSelect(hold, n0, nn0);
                nn1 = Vector128.ConditionalSelect(hold, n1, nn1);
                nn2 = Vector128.ConditionalSelect(hold, n2, nn2);
                na0 = Vector128.ConditionalSelect(hold, a0, na0);
                na1 = Vector128.ConditionalSelect(hold, a1, na1);
                nb = Vector128.ConditionalSelect(hold, b, nb);
                next = Vector128.ConditionalSelect(hold, cheapest, next);
                over = Vector128.GreaterThan(next, vBudget); // never a lane that waits: what it keeps was within its budget
                s0 -= hold.GetElement(0) >> 15;
                s1 -= hold.GetElement(1) >> 15;
                s2 -= hold.GetElement(2) >> 15;
                s3 -= hold.GetElement(3) >> 15;
                s4 -= hold.GetElement(4) >> 15;
                s5 -= hold.GetElement(5) >> 15;
                s6 -= hold.GetElement(6) >> 15;
                s7 -= hold.GetElement(7) >> 15;
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

            if (AdvSimd.Arm64.MaxAcross(over).ToScalar() == 0)
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

            var overBits = over.ExtractMostSignificantBits();
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
                    budget[lane] = ushort.MaxValue;
                }
                chunkStart[lane] = end;
                offset[lane] -= 1 + (position - end);
            }

            vBudget = NeonCosts(budget[0], budget[1], budget[2], budget[3], budget[4], budget[5], budget[6], budget[7]);
            n0 = Vector128.ConditionalSelect(over, unreachable, nn0);
            n1 = Vector128.ConditionalSelect(over, unreachable, nn1);
            n2 = Vector128.ConditionalSelect(over, unreachable, nn2);
            a0 = Vector128.ConditionalSelect(over, unreachable, na0);
            a1 = Vector128.ConditionalSelect(over, unreachable, na1);
            b = Vector128.ConditionalSelect(over, unreachable, nb);
            cheapest = Vector128.ConditionalSelect(over, Vector128<ushort>.Zero, next);
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
                var byteBits = 8 * LaneByteCost(text, position, charset);
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

    // Keep this copy of ModeSegmenter.ByteCost inline in the NEON walk: a call in the
    // UTF-8 loop makes the JIT spill live vector state around each character cost.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int LaneByteCost(ReadOnlySpan<char> text, int index, EciMode charset)
    {
        if (charset != EciMode.Utf8)
            return 1;

        var c = text[index];
        if (c < 0x80)
            return 1;
        if (c < 0x800)
            return 2;
        if (char.IsHighSurrogate(c))
            return index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 4 : 3;
        if (char.IsLowSurrogate(c))
            return index > 0 && char.IsHighSurrogate(text[index - 1]) ? 0 : 3;
        return 3;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> NeonCosts(int value) => Vector128.Create((ushort)value);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> NeonCosts(int a, int b, int c, int d, int e, int f, int g, int h)
        => Vector128.Create((ushort)a, (ushort)b, (ushort)c, (ushort)d, (ushort)e, (ushort)f, (ushort)g, (ushort)h);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> NeonHoldOffsets(int a, int b, int c, int d, int e, int f, int g, int h, int lowest)
        => Vector128.Create((ushort)(a > lowest ? ushort.MaxValue : 0), (ushort)(b > lowest ? ushort.MaxValue : 0), (ushort)(c > lowest ? ushort.MaxValue : 0), (ushort)(d > lowest ? ushort.MaxValue : 0), (ushort)(e > lowest ? ushort.MaxValue : 0), (ushort)(f > lowest ? ushort.MaxValue : 0), (ushort)(g > lowest ? ushort.MaxValue : 0), (ushort)(h > lowest ? ushort.MaxValue : 0));
}
#endif
