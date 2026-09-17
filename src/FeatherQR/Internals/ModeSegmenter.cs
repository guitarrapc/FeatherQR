#if !NETSTANDARD2_1_OR_GREATER && !NET5_0_OR_GREATER
// ArrayPool is only reached from the netstandard2.0 branch of ByteUnitCount.
using System.Buffers;
#endif
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace FeatherQR.Internals;

/// <summary>
/// Splits content into the minimal-bit Numeric / Alphanumeric / Byte runs and reconstructs them as a <see cref="ModeSegment"/> plan.
/// Shared by the Standard QR, Micro QR and rMQR planners, which differ in the header widths passed in and (Micro QR) mode availability; version scans and bounds stay in those planners.
/// </summary>
/// <remarks>
/// A run's cost is not a per-character constant, so the dynamic program carries the packing-group remainder in its state: Numeric with 0, 1 or 2 digits in the open group, Alphanumeric with 0 or 1 characters in the open pair, Byte, and a virtual start.
/// The six costs live in locals and each target is the cheaper of continuing its run or opening it from the cheapest state, so a character is a handful of adds and mins; the program used to relax every state from every state through two stack arrays, which cost six times as much per character as the arithmetic.
/// The common shapes get their own loop: a Latin charset (every character one byte) with Byte allowed, for costs, for costs with parents, and for the prefix walk; everything else (UTF-8, a Micro QR version without a mode) shares one general loop per entry point. A run outside the alphanumeric alphabet leaves only Byte reachable, so every loop steps such a run as one add per character.
/// Micro QR restricts modes per version (M1 is Numeric-only, M2 has no Byte mode); its planner disables the missing transitions via <c>allowAlnum</c>/<c>allowByte</c>, and a character no allowed mode encodes leaves the cost at <see cref="Unreachable"/>.
/// Ties are broken as the all-states relaxation did (the lowest predecessor state wins), so the reconstructed plan is the same plan; <c>ModeSegmenterByteRunParityTest</c> holds every loop to that reference.
/// </remarks>
internal static class ModeSegmenter
{
    /// <summary>Dynamic programming states per character; the parents table is <c>text.Length * StateCount</c> bytes.</summary>
    public const int StateCount = 7;

    /// <summary>Parent bytes that fit the stack budget (73 characters); longer content rents.</summary>
    public const int MaxStackParents = 512;

    /// <summary>
    /// Cost returned by <see cref="ComputeCosts"/> when no allowed mode set encodes the content; small enough that adding a transition cost cannot overflow.
    /// </summary>
    /// <remarks>An unreachable state keeps a value at or above this; the value may drift upward by a few bits per character, which the longest content any caller passes leaves far below the overflow.</remarks>
    public const int Unreachable = int.MaxValue / 4;

    // Numeric and Alphanumeric states carry the number of characters already
    // accumulated into the current packing group.
    private const int StateNumeric0 = 0;
    private const int StateNumeric1 = 1;
    private const int StateNumeric2 = 2;
    private const int StateAlnum0 = 3;
    private const int StateAlnum1 = 4;
    private const int StateByte = 5;
    private const int StateStart = 6;

    // Character classes of the table below; 1 is the alphanumeric alphabet outside the digits.
    private const int ClassOther = 0;
    private const int ClassDigit = 2;

    // Cheapest bits a single character can cost in any mode, in sixths so the numeric
    // and alphanumeric packing rates stay exact: 10 bits per 3 digits, 11 per 2
    // alphanumerics, 8 per byte. A partial group only ever costs more per character
    // (one digit is 4 bits against a rate of 3 1/3), so summing these rates can never
    // exceed what a real plan pays.
    private const int SixthsPerDigit = 20;
    private const int SixthsPerAlnum = 33;
    private const int SixthsPerByte = 48;

    /// <summary>
    /// The class of each ASCII character for the program, one load per character: outside the alphanumeric alphabet, inside it, or a digit.
    /// Pinned to <see cref="CharacterSets"/> by <c>ModeSegmenterClassTableTest</c>.
    /// </summary>
    private static ReadOnlySpan<byte> characterClass => [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 0, 0, 0, 1, 1, 0, 0, 0, 0, 1, 1, 0, 1, 1, 1, // space $ % * + - . /
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 0, 0, 0, 0, 0, // 0-9 :
        0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, // A-Z
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    ];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ClassOf(char c) => c < characterClass.Length ? characterClass[c] : ClassOther;

    /// <summary>
    /// Minimal payload bits (excluding any ECI prefix) for the content at the given mode indicator and count indicator widths, or a value at or above <see cref="Unreachable"/> when a character has no allowed mode.
    /// When <paramref name="parents"/> is non-empty it receives one predecessor state per (character, state) pair for reconstruction.
    /// </summary>
    public static int ComputeCosts(ReadOnlySpan<char> text, EciMode charset, int modeIndicatorBits, int cciNumeric, int cciAlnum, int cciByte, Span<byte> parents, out int finalState, bool allowAlnum = true, bool allowByte = true)
    {
        Debug.Assert(parents.IsEmpty || parents.Length == text.Length * StateCount);

        // Opening a run costs its headers plus its first character: 4 bits for a digit, 6 for
        // an alphanumeric, 8 per byte (added per character where the charset is UTF-8).
        var openNumeric = modeIndicatorBits + cciNumeric + 4;
        var openAlnum = modeIndicatorBits + cciAlnum + 6;
        var openByte = modeIndicatorBits + cciByte;
        var latin = charset != EciMode.Utf8 && allowByte;

        if (parents.IsEmpty)
        {
            return latin
                ? CostsLatin(text, openNumeric, openAlnum, openByte + 8, allowAlnum, out finalState)
                : CostsGeneral(text, charset, openNumeric, openAlnum, openByte, allowAlnum, allowByte, out finalState);
        }

        return latin
            ? CostsTrackedLatin(text, openNumeric, openAlnum, openByte + 8, allowAlnum, parents, out finalState)
            : CostsTrackedGeneral(text, charset, openNumeric, openAlnum, openByte, allowAlnum, allowByte, parents, out finalState);
    }

    /// <summary>
    /// The longest prefix of <paramref name="text"/> whose minimal plan costs at most <paramref name="budgetBits"/>, in characters; 0 when not even the first character fits.
    /// </summary>
    /// <remarks>
    /// One forward pass of the program <see cref="ComputeCosts"/> runs: the optimum of every prefix falls out of the sweep, and it is monotone in the prefix length (a plan for a longer prefix restricted to a shorter one is a plan for the shorter at no more cost), so the pass stops at the first prefix over budget.
    /// A prefix that cuts a surrogate pair is never a candidate; the pair is priced on its first half, so the end after its second half is the one compared.
    /// </remarks>
    public static int LongestPrefixWithinBudget(ReadOnlySpan<char> text, EciMode charset, int modeIndicatorBits, int cciNumeric, int cciAlnum, int cciByte, int budgetBits)
    {
        var openNumeric = modeIndicatorBits + cciNumeric + 4;
        var openAlnum = modeIndicatorBits + cciAlnum + 6;
        var openByte = modeIndicatorBits + cciByte;
        return charset != EciMode.Utf8
            ? PrefixLatin(text, openNumeric, openAlnum, openByte + 8, budgetBits)
            : PrefixGeneral(text, charset, openNumeric, openAlnum, openByte, budgetBits);
    }

    /// <summary>Costs only, one byte per character, Byte allowed: the Structured Append searches and the whole-text bounds.</summary>
    private static int CostsLatin(ReadOnlySpan<char> text, int openNumeric, int openAlnum, int openByte, bool allowAlnum, out int finalState)
    {
        int n0 = Unreachable, n1 = Unreachable, n2 = Unreachable, a0 = Unreachable, a1 = Unreachable, b = Unreachable;
        var cheapest = 0; // the start state, then the cheapest of the six
        var length = text.Length;
        for (var i = 0; i < length; i++)
        {
            var cls = ClassOf(text[i]);
            if (cls == ClassOther)
            {
                // Only Byte encodes this character, so it alone stays reachable and every
                // further such character extends its run: one add each.
                b = Math.Min(b + 8, cheapest + openByte);
                n0 = n1 = n2 = a0 = a1 = Unreachable;
                while (i + 1 < length && ClassOf(text[i + 1]) == ClassOther)
                {
                    b += 8;
                    i++;
                }
                cheapest = b;
                continue;
            }

            if (cls == ClassDigit)
            {
                // The first digit of a group costs 4 bits, the next two 3 each.
                var wrapped = n2 + 3;
                n2 = n1 + 3;
                n1 = Math.Min(n0 + 4, cheapest + openNumeric);
                n0 = wrapped;
            }
            else
            {
                n0 = n1 = n2 = Unreachable;
            }
            if (allowAlnum)
            {
                // 11 bits per pair: 6 for the first character of a pair, 5 for the second.
                var paired = a1 + 5;
                a1 = Math.Min(a0 + 6, cheapest + openAlnum);
                a0 = paired;
            }
            b = Math.Min(b + 8, cheapest + openByte);
            cheapest = Math.Min(Math.Min(Math.Min(n0, n1), Math.Min(n2, a0)), Math.Min(a1, b));
        }

        return Best(n0, n1, n2, a0, a1, b, out finalState);
    }

    /// <summary>Costs only for the remaining shapes: a UTF-8 charset, or a Micro QR version without Byte mode.</summary>
    private static int CostsGeneral(ReadOnlySpan<char> text, EciMode charset, int openNumeric, int openAlnum, int openByte, bool allowAlnum, bool allowByte, out int finalState)
    {
        int n0 = Unreachable, n1 = Unreachable, n2 = Unreachable, a0 = Unreachable, a1 = Unreachable, b = Unreachable;
        var cheapest = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var cls = ClassOf(c);
            var byteBits = 8 * ByteCost(text, i, charset);

            int nn0 = Unreachable, nn1 = Unreachable, nn2 = Unreachable, na0 = Unreachable, na1 = Unreachable, nb = Unreachable;
            if (cls == ClassDigit)
            {
                nn1 = Math.Min(n0 + 4, cheapest + openNumeric);
                nn2 = n1 + 3;
                nn0 = n2 + 3;
            }
            if (cls != ClassOther && allowAlnum)
            {
                na1 = Math.Min(a0 + 6, cheapest + openAlnum);
                na0 = a1 + 5;
            }
            if (allowByte)
                nb = Math.Min(b + byteBits, cheapest + openByte + byteBits);

            n0 = nn0; n1 = nn1; n2 = nn2; a0 = na0; a1 = na1; b = nb;
            cheapest = Math.Min(Math.Min(Math.Min(n0, n1), Math.Min(n2, a0)), Math.Min(a1, b));
        }

        return Best(n0, n1, n2, a0, a1, b, out finalState);
    }

    /// <summary>
    /// Costs with parents, one byte per character, Byte allowed: the per-symbol plan of every Latin symbol.
    /// The six parents of an alphanumeric character are one 8-byte store (byte 6 is the start state's slot, never read; byte 7 is the next character's first slot, which its own step overwrites), except for the last character, whose eighth byte would fall past the table.
    /// </summary>
    private static int CostsTrackedLatin(ReadOnlySpan<char> text, int openNumeric, int openAlnum, int openByte, bool allowAlnum, Span<byte> parents, out int finalState)
    {
        int n0 = Unreachable, n1 = Unreachable, n2 = Unreachable, a0 = Unreachable, a1 = Unreachable, b = Unreachable, start = 0;
        var length = text.Length;
        for (var i = 0; i < length; i++)
        {
            var cls = ClassOf(text[i]);
            var parentBase = i * StateCount;
            if (cls == ClassOther)
            {
                b = OpenByte(n0, n1, n2, a0, a1, b + 8, start, openByte, out var parentByte);
                parents[parentBase + StateByte] = (byte)parentByte;
                n0 = n1 = n2 = a0 = a1 = Unreachable;
                start = Unreachable;
                while (i + 1 < length && ClassOf(text[i + 1]) == ClassOther)
                {
                    i++;
                    b += 8;
                    parents[i * StateCount + StateByte] = StateByte;
                }
                continue;
            }

            int nn0 = Unreachable, nn1 = Unreachable, nn2 = Unreachable, na0 = Unreachable, na1 = Unreachable;
            int parentNumeric1 = 0, parentAlnum1 = 0;
            if (cls == ClassDigit)
            {
                nn1 = OpenNumeric(n0, a0, a1, b, start, openNumeric, out parentNumeric1);
                nn2 = n1 + 3;
                nn0 = n2 + 3;
            }
            if (allowAlnum)
            {
                na1 = OpenAlnum(n0, n1, n2, a0, b, start, openAlnum, out parentAlnum1);
                na0 = a1 + 5;
            }
            var nb = OpenByte(n0, n1, n2, a0, a1, b + 8, start, openByte, out var parentB);

            var packed = (long)StateNumeric2 | ((long)parentNumeric1 << 8) | ((long)StateNumeric1 << 16) | ((long)StateAlnum1 << 24) | ((long)parentAlnum1 << 32) | ((long)parentB << 40);
            if (parentBase + 8 <= parents.Length)
            {
                BinaryPrimitives.WriteInt64LittleEndian(parents.Slice(parentBase, 8), packed);
            }
            else
            {
                parents[parentBase + StateNumeric0] = StateNumeric2;
                parents[parentBase + StateNumeric1] = (byte)parentNumeric1;
                parents[parentBase + StateNumeric2] = StateNumeric1;
                parents[parentBase + StateAlnum0] = StateAlnum1;
                parents[parentBase + StateAlnum1] = (byte)parentAlnum1;
                parents[parentBase + StateByte] = (byte)parentB;
            }

            n0 = nn0; n1 = nn1; n2 = nn2; a0 = na0; a1 = na1; b = nb;
            start = Unreachable;
        }

        return Best(n0, n1, n2, a0, a1, b, out finalState);
    }

    /// <summary>Costs with parents for the remaining shapes: a UTF-8 charset, or a Micro QR version without Byte mode.</summary>
    private static int CostsTrackedGeneral(ReadOnlySpan<char> text, EciMode charset, int openNumeric, int openAlnum, int openByte, bool allowAlnum, bool allowByte, Span<byte> parents, out int finalState)
    {
        int n0 = Unreachable, n1 = Unreachable, n2 = Unreachable, a0 = Unreachable, a1 = Unreachable, b = Unreachable, start = 0;
        var length = text.Length;
        for (var i = 0; i < length; i++)
        {
            var cls = ClassOf(text[i]);
            var parentBase = i * StateCount;
            if (cls == ClassOther)
            {
                if (allowByte)
                {
                    var byteBits = 8 * ByteCost(text, i, charset);
                    b = OpenByte(n0, n1, n2, a0, a1, b + byteBits, start, openByte + byteBits, out var parentByte);
                    parents[parentBase + StateByte] = (byte)parentByte;
                }
                n0 = n1 = n2 = a0 = a1 = Unreachable;
                start = Unreachable;
                if (!allowByte)
                    continue; // nothing encodes it; every state stays unreachable from here on

                while (i + 1 < length && ClassOf(text[i + 1]) == ClassOther)
                {
                    i++;
                    b += 8 * ByteCost(text, i, charset);
                    parents[i * StateCount + StateByte] = StateByte;
                }
                continue;
            }

            // An alphanumeric character is ASCII: one byte in every charset.
            int nn0 = Unreachable, nn1 = Unreachable, nn2 = Unreachable, na0 = Unreachable, na1 = Unreachable, nb = Unreachable;
            int parentNumeric1 = 0, parentAlnum1 = 0, parentB = 0;
            if (cls == ClassDigit)
            {
                nn1 = OpenNumeric(n0, a0, a1, b, start, openNumeric, out parentNumeric1);
                nn2 = n1 + 3;
                nn0 = n2 + 3;
            }
            if (allowAlnum)
            {
                na1 = OpenAlnum(n0, n1, n2, a0, b, start, openAlnum, out parentAlnum1);
                na0 = a1 + 5;
            }
            if (allowByte)
                nb = OpenByte(n0, n1, n2, a0, a1, b + 8, start, openByte + 8, out parentB);

            parents[parentBase + StateNumeric0] = StateNumeric2;
            parents[parentBase + StateNumeric1] = (byte)parentNumeric1;
            parents[parentBase + StateNumeric2] = StateNumeric1;
            parents[parentBase + StateAlnum0] = StateAlnum1;
            parents[parentBase + StateAlnum1] = (byte)parentAlnum1;
            parents[parentBase + StateByte] = (byte)parentB;

            n0 = nn0; n1 = nn1; n2 = nn2; a0 = na0; a1 = na1; b = nb;
            start = Unreachable;
        }

        return Best(n0, n1, n2, a0, a1, b, out finalState);
    }

    /// <summary>The prefix walk, one byte per character.</summary>
    private static int PrefixLatin(ReadOnlySpan<char> text, int openNumeric, int openAlnum, int openByte, int budgetBits)
    {
        int n0 = Unreachable, n1 = Unreachable, n2 = Unreachable, a0 = Unreachable, a1 = Unreachable, b = Unreachable;
        var cheapest = 0;
        var length = text.Length;
        for (var i = 0; i < length; i++)
        {
            var c = text[i];
            var cls = ClassOf(c);
            if (cls == ClassOther)
            {
                b = Math.Min(b + 8, cheapest + openByte);
                n0 = n1 = n2 = a0 = a1 = Unreachable;
                while (true)
                {
                    // A pair is priced on its first half and judged after its second.
                    if (b > budgetBits && !(char.IsHighSurrogate(c) && i + 1 < length && char.IsLowSurrogate(text[i + 1])))
                        return StoppedBefore(text, i);
                    if (i + 1 >= length || ClassOf(text[i + 1]) != ClassOther)
                        break;
                    i++;
                    c = text[i];
                    b += 8;
                }
                cheapest = b;
                continue;
            }

            if (cls == ClassDigit)
            {
                var wrapped = n2 + 3;
                n2 = n1 + 3;
                n1 = Math.Min(n0 + 4, cheapest + openNumeric);
                n0 = wrapped;
            }
            else
            {
                n0 = n1 = n2 = Unreachable;
            }
            {
                var paired = a1 + 5;
                a1 = Math.Min(a0 + 6, cheapest + openAlnum);
                a0 = paired;
            }
            b = Math.Min(b + 8, cheapest + openByte);
            cheapest = Math.Min(Math.Min(Math.Min(n0, n1), Math.Min(n2, a0)), Math.Min(a1, b));

            if (cheapest > budgetBits)
                return StoppedBefore(text, i);
        }

        return length;
    }

    /// <summary>The prefix walk under a UTF-8 charset.</summary>
    private static int PrefixGeneral(ReadOnlySpan<char> text, EciMode charset, int openNumeric, int openAlnum, int openByte, int budgetBits)
    {
        int n0 = Unreachable, n1 = Unreachable, n2 = Unreachable, a0 = Unreachable, a1 = Unreachable, b = Unreachable;
        var cheapest = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var cls = ClassOf(c);
            var byteBits = 8 * ByteCost(text, i, charset);

            int nn0 = Unreachable, nn1 = Unreachable, nn2 = Unreachable, na0 = Unreachable, na1 = Unreachable;
            if (cls == ClassDigit)
            {
                nn1 = Math.Min(n0 + 4, cheapest + openNumeric);
                nn2 = n1 + 3;
                nn0 = n2 + 3;
            }
            if (cls != ClassOther)
            {
                na1 = Math.Min(a0 + 6, cheapest + openAlnum);
                na0 = a1 + 5;
            }
            var nb = Math.Min(b + byteBits, cheapest + openByte + byteBits);

            n0 = nn0; n1 = nn1; n2 = nn2; a0 = na0; a1 = na1; b = nb;
            cheapest = Math.Min(Math.Min(Math.Min(n0, n1), Math.Min(n2, a0)), Math.Min(a1, b));

            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                continue;

            if (cheapest > budgetBits)
                return StoppedBefore(text, i);
        }

        return text.Length;
    }

    /// <summary>The end a walk had accepted when the prefix through <paramref name="i"/> went over budget: before a pair whose second half is at <paramref name="i"/>, since its first half was never judged; else <paramref name="i"/>.</summary>
    private static int StoppedBefore(ReadOnlySpan<char> text, int i)
        => i > 0 && char.IsLowSurrogate(text[i]) && char.IsHighSurrogate(text[i - 1]) ? i - 1 : i;

    // The three "open or continue" steps with the predecessor recorded. Candidates are tried in
    // state order and a later one replaces an earlier one only when strictly cheaper, which is
    // the tie-break the all-states relaxation had, so the plans reconstruct identically.

    /// <summary>Numeric with one digit in the group: continued from Numeric0, or opened from Alnum0, Alnum1, Byte or the start.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int OpenNumeric(int n0, int a0, int a1, int b, int start, int openNumeric, out int parent)
    {
        var best = n0 + 4;
        parent = StateNumeric0;
        var candidate = a0 + openNumeric;
        if (candidate < best) { best = candidate; parent = StateAlnum0; }
        candidate = a1 + openNumeric;
        if (candidate < best) { best = candidate; parent = StateAlnum1; }
        candidate = b + openNumeric;
        if (candidate < best) { best = candidate; parent = StateByte; }
        candidate = start + openNumeric;
        if (candidate < best) { best = candidate; parent = StateStart; }
        return best;
    }

    /// <summary>Alphanumeric with one character in the pair: opened from the Numeric states, continued from Alnum0, or opened from Byte or the start.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int OpenAlnum(int n0, int n1, int n2, int a0, int b, int start, int openAlnum, out int parent)
    {
        var best = n0 + openAlnum;
        parent = StateNumeric0;
        var candidate = n1 + openAlnum;
        if (candidate < best) { best = candidate; parent = StateNumeric1; }
        candidate = n2 + openAlnum;
        if (candidate < best) { best = candidate; parent = StateNumeric2; }
        candidate = a0 + 6;
        if (candidate < best) { best = candidate; parent = StateAlnum0; }
        candidate = b + openAlnum;
        if (candidate < best) { best = candidate; parent = StateByte; }
        candidate = start + openAlnum;
        if (candidate < best) { best = candidate; parent = StateStart; }
        return best;
    }

    /// <summary>Byte: opened from every other state, or continued (<paramref name="continued"/> is the Byte state plus this character's bits).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int OpenByte(int n0, int n1, int n2, int a0, int a1, int continued, int start, int openByte, out int parent)
    {
        var best = n0 + openByte;
        parent = StateNumeric0;
        var candidate = n1 + openByte;
        if (candidate < best) { best = candidate; parent = StateNumeric1; }
        candidate = n2 + openByte;
        if (candidate < best) { best = candidate; parent = StateNumeric2; }
        candidate = a0 + openByte;
        if (candidate < best) { best = candidate; parent = StateAlnum0; }
        candidate = a1 + openByte;
        if (candidate < best) { best = candidate; parent = StateAlnum1; }
        if (continued < best) { best = continued; parent = StateByte; }
        candidate = start + openByte;
        if (candidate < best) { best = candidate; parent = StateStart; }
        return best;
    }

    /// <summary>The cheapest state to end in, and its cost; <see cref="Unreachable"/> exactly when no allowed mode set encodes the content. The lowest state wins a tie.</summary>
    private static int Best(int n0, int n1, int n2, int a0, int a1, int b, out int state)
    {
        var best = Unreachable;
        state = StateByte;
        if (n0 < best) { best = n0; state = StateNumeric0; }
        if (n1 < best) { best = n1; state = StateNumeric1; }
        if (n2 < best) { best = n2; state = StateNumeric2; }
        if (a0 < best) { best = a0; state = StateAlnum0; }
        if (a1 < best) { best = a1; state = StateAlnum1; }
        if (b < best) { best = b; state = StateByte; }
        return best;
    }

    /// <summary>
    /// Walks the predecessor table back into runs, oldest first.
    /// Returns false when the plan needs more runs than the caller lent room for.
    /// </summary>
    public static bool Reconstruct(ReadOnlySpan<char> text, ReadOnlySpan<byte> parents, int finalState, Span<ModeSegment> segments, out int segmentCount)
    {
        segmentCount = 0;
        var state = finalState;
        var end = text.Length;
        var count = 0;

        for (var i = text.Length - 1; i >= 0; i--)
        {
            var parent = parents[i * StateCount + state];
            if (parent == StateStart || ModeIndexOf(parent) != ModeIndexOf(state))
            {
                if (count >= segments.Length)
                    return false;
                segments[count++] = new ModeSegment(ModeIndexOf(state), i, end - i, 0);
                end = i;
            }
            state = parent;
        }

        Debug.Assert(state == StateStart, "the walk must terminate at the virtual start state");
        segments.Slice(0, count).Reverse();
        segmentCount = count;
        return true;
    }

    /// <summary>Counts the runs of each mode on the minimal-cost path, without materialising it.</summary>
    public static void CountRuns(ReadOnlySpan<byte> parents, int finalState, int length, out int runsNumeric, out int runsAlnum, out int runsByte)
    {
        runsNumeric = 0;
        runsAlnum = 0;
        runsByte = 0;

        var state = finalState;
        for (var i = length - 1; i >= 0; i--)
        {
            var parent = parents[i * StateCount + state];
            if (parent == StateStart || ModeIndexOf(parent) != ModeIndexOf(state))
            {
                switch (ModeIndexOf(state))
                {
                    case 0: runsNumeric++; break;
                    case 1: runsAlnum++; break;
                    default: runsByte++; break;
                }
            }
            state = parent;
        }
    }

    /// <summary>
    /// Whether the plan relocates a mid-content U+FEFF to the start of a Byte run.
    /// The shared byte-segment decoder consumes a leading BOM of every segment without an explicit ISO-8859-1 declaration, so such a plan would decode with the character silently dropped — where the single-mode stream, which keeps it interior, round-trips.
    /// Planners reject these plans and fall back to Single.
    /// (A run at offset 0 is exempt: there the single-mode stream starts with the same bytes and behaves identically.)
    /// </summary>
    public static bool HasBomRelocatedToARunStart(ReadOnlySpan<char> text, ReadOnlySpan<ModeSegment> segments)
    {
        foreach (var segment in segments)
        {
            if (segment.ModeIndex == 2 && segment.Start > 0 && text[segment.Start] == '﻿')
                return true;
        }
        return false;
    }

    /// <summary>Fills each run with the value its count indicator carries.</summary>
    public static void FillUnitCounts(ReadOnlySpan<char> text, EciMode charset, Span<ModeSegment> segments)
    {
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            var units = segment.ModeIndex == 2
                ? ByteUnitCount(text.Slice(segment.Start, segment.Length), charset)
                : segment.Length;
            segments[i] = new ModeSegment(segment.ModeIndex, segment.Start, segment.Length, units);
        }
    }

    /// <summary>Payload bits of <paramref name="unitCount"/> units in <paramref name="mode"/> (ISO/IEC 18004 7.4 / ISO/IEC 23941 7.4).</summary>
    public static int PayloadBits(EncodingMode mode, int unitCount) => mode switch
    {
        EncodingMode.Numeric => unitCount / 3 * 10 + (unitCount % 3) switch { 2 => 7, 1 => 4, _ => 0 },
        EncodingMode.Alphanumeric => unitCount / 2 * 11 + unitCount % 2 * 6,
        EncodingMode.Byte => unitCount * 8,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), $"Encoding mode {mode} has no payload cost model."),
    };

    /// <summary>Encoded byte count of a Byte-mode run, i.e. the value its count indicator carries.</summary>
    /// <remarks>
    /// Deliberately asks <see cref="Encoding.UTF8"/> rather than reusing the dynamic program's own per-character model: comparing the two is what would catch an error in that model, so they have to stay independent computations.
    /// </remarks>
    public static int ByteUnitCount(ReadOnlySpan<char> text, EciMode charset)
    {
        if (charset != EciMode.Utf8)
            return text.Length;
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
        return Encoding.UTF8.GetByteCount(text);
#else
        // netstandard2.0 has no span overload, and the pointer overload would mean
        // enabling unsafe across the library, which this project has declined. Rent
        // rather than ToString(), so planning stays allocation-free on every target.
        var rented = ArrayPool<char>.Shared.Rent(Math.Max(text.Length, 1));
        try
        {
            text.CopyTo(rented);
            return Encoding.UTF8.GetByteCount(rented, 0, text.Length);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented, clearArray: false);
        }
#endif
    }

    /// <summary>
    /// The longest run of digits and the longest run of characters in the alphanumeric alphabet, in one pass.
    /// </summary>
    /// <remarks>
    /// What a plan can gain over one run comes from a stretch of a denser mode, and how much it can gain is bounded by how long that stretch is, so these two numbers answer whether planning could pay before any cost run does the work of finding out.
    /// Digits are alphanumeric, so the numeric run is never the longer of the two.
    /// </remarks>
    public static void LongestDenseRuns(ReadOnlySpan<char> text, out int numericRun, out int alnumRun)
    {
        int longestNumeric = 0, longestAlnum = 0, currentNumeric = 0, currentAlnum = 0;
        foreach (var c in text)
        {
            if (CharacterSets.IsNumeric(c))
            {
                currentNumeric++;
                if (currentNumeric > longestNumeric)
                    longestNumeric = currentNumeric;
            }
            else
            {
                currentNumeric = 0;
            }

            if (CharacterSets.IsAlphanumeric(c))
            {
                currentAlnum++;
                if (currentAlnum > longestAlnum)
                    longestAlnum = currentAlnum;
            }
            else
            {
                currentAlnum = 0;
            }
        }

        numericRun = longestNumeric;
        alnumRun = longestAlnum;
    }

    /// <summary>
    /// The sixths sum of pricing each character at the cheapest rate any mode could give it: one O(n) pass, no table.
    /// Rounded up and topped with the cheapest possible header by the caller, it is a lower bound on any plan at any version, so it may only reject.
    /// </summary>
    public static int CheapestSixths(ReadOnlySpan<char> text, EciMode charset)
    {
        var sixths = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (CharacterSets.IsNumeric(c))
                sixths += SixthsPerDigit;
            else if (CharacterSets.IsAlphanumeric(c))
                sixths += SixthsPerAlnum;
            else
                sixths += SixthsPerByte * ByteCost(text, i, charset);
        }
        return sixths;
    }

    /// <summary>Dense mode index of a state, or -1 for the virtual start state.</summary>
    private static int ModeIndexOf(int state) => state switch
    {
        <= StateNumeric2 => 0,
        StateAlnum0 or StateAlnum1 => 1,
        StateByte => 2,
        _ => -1,
    };

    /// <summary>
    /// Encoded byte length of one character in Byte mode.
    /// Latin-1 charsets are one byte per character; UTF-8 mirrors what <see cref="Encoding.UTF8"/> emits, including its greedy surrogate pairing (a paired high surrogate carries all four bytes and its low surrogate none, an unpaired surrogate costs the three bytes of the replacement character).
    /// </summary>
    private static int ByteCost(ReadOnlySpan<char> text, int index, EciMode charset)
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
}
