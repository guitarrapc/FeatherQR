using System.Diagnostics;

namespace FeatherQR.Internals;

/// <summary>
/// The scalar cost walk under FNC1 (specs/standardqr-encoder.md, FNC1). The character classes change: the
/// separator U+001D is an Alphanumeric character (written <c>%</c>), a literal <c>%</c> costs two Alphanumeric
/// characters (written <c>%%</c>), and the Alphanumeric states split into "the last character was a separator"
/// and "it was not", because a separator followed by a separator or a literal <c>%</c> cannot share a run (a
/// reader takes <c>%%</c> first): from the separator states those two characters can only open a new run or go
/// to Byte, which is how the program prices a break against a Byte run. The parent table keeps one byte per
/// character and state, the predecessor state with a flag for a run opened there, so it does not share the
/// packed table of the program without FNC1, whose eight states fill its three bits. Kanji is not planned
/// under FNC1.
/// </summary>
/// <remarks>
/// A charset of one byte a character takes loops that keep the nine states in locals, as the program without FNC1
/// keeps its six (see <see cref="ModeSegmenter"/>): relaxing every state through arrays costs far more in memory
/// traffic than the arithmetic. UTF-8 keeps the array loop: GS1 data is ASCII, so the shape is rare under FNC1, and
/// its byte count is per character.
/// With parents, the loop carries a state as a key, its cost shifted up four bits with the state's number below
/// it, one bit more than the program without FNC1 needs for its eight. The minimum of keys is the minimum cost and,
/// among equal costs, the lowest state, which is the rule the array loop's search has. A continued run beats an
/// opened one at equal cost, which no key order can say, so each target compares its two keys by cost before it
/// keeps one.
/// </remarks>
internal static partial class ModeSegmenter
{
    private const int Fnc1StateNumeric0 = 0;
    private const int Fnc1StateNumeric1 = 1;
    private const int Fnc1StateNumeric2 = 2;
    private const int Fnc1StateAlnum0 = 3;
    private const int Fnc1StateAlnum1 = 4;
    private const int Fnc1StateSeparator0 = 5;
    private const int Fnc1StateSeparator1 = 6;
    private const int Fnc1StateByte = 7;
    private const int Fnc1StateStart = 8;
    public const int Fnc1StateCount = 9;
    private const byte Fnc1Opened = 0x80;
    private const char Fnc1FieldSeparator = '\u001D';

    public const int Fnc1ParentBytesPerChar = Fnc1StateCount;

    /// <summary>The stack budget of the FNC1 parent table, 112 characters (1008 bytes): the two-byte table's budget would send a 57-character text to the pool.</summary>
    public const int Fnc1MaxStackParents = Fnc1ParentBytesPerChar * 112;

    // An unreachable state as a key, below any number a few additions could overflow.
    private const int Fnc1UnreachableKey = (int.MaxValue / 32) << 4;

    /// <summary>The Alphanumeric unit count of a run under FNC1: its characters plus its literal percents, each written twice.</summary>
    public static int AlphanumericUnitsFnc1(ReadOnlySpan<char> chars)
    {
        var units = chars.Length;
        foreach (var c in chars)
        {
            if (c == '%')
                units++;
        }
        return units;
    }

    /// <summary>The cheapest sixths of a bit any mode gives each character under FNC1: a separator costs an Alphanumeric character, a literal percent the lesser of two of them and one byte.</summary>
    public static int CheapestSixthsFnc1(ReadOnlySpan<char> text, EciMode charset)
    {
        var sixths = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (CharacterSets.IsNumeric(c))
                sixths += SixthsPerDigit;
            else if (c == Fnc1FieldSeparator)
                sixths += SixthsPerAlnum;
            else if (c == '%')
                sixths += SixthsPerByte;
            else if (CharacterSets.IsAlphanumeric(c))
                sixths += SixthsPerAlnum;
            else
                sixths += SixthsPerByte * ByteCost(text, i, charset);
        }
        return sixths;
    }

    /// <summary>
    /// The cost in bits of the cheapest plan under FNC1 at the given header widths, and the parent table that
    /// <see cref="ReconstructFnc1"/> reads when <paramref name="parents"/> is given (<see cref="Fnc1ParentBytesPerChar"/>
    /// bytes a character). The headers of the stream (ECI, FNC1) are the caller's.
    /// </summary>
    public static int ComputeCostsFnc1(ReadOnlySpan<char> text, EciMode charset, int modeIndicatorBits, int cciNumeric, int cciAlnum, int cciByte, Span<byte> parents, out int finalState)
    {
        Debug.Assert(parents.IsEmpty || parents.Length == text.Length * Fnc1ParentBytesPerChar);

        // Opening a run costs its headers plus its first character: 4 bits for a digit, 6 for an alphanumeric, 8 for
        // a byte where the charset has one byte a character.
        var openNumeric = modeIndicatorBits + cciNumeric + 4;
        var openAlnum = modeIndicatorBits + cciAlnum + 6;
        if (charset != EciMode.Utf8)
        {
            return parents.IsEmpty
                ? CostsFnc1Latin(text, openNumeric, openAlnum, modeIndicatorBits + cciByte + 8, out finalState)
                : TrackedFnc1Latin(text, openNumeric, openAlnum, modeIndicatorBits + cciByte + 8, parents, out finalState);
        }

        return CostsFnc1General(text, charset, openNumeric, openAlnum, modeIndicatorBits + cciByte, parents, out finalState);
    }

    /// <summary>Costs only, one byte per character: the version scans.</summary>
    private static int CostsFnc1Latin(ReadOnlySpan<char> text, int openNumeric, int openAlnum, int openByte, out int finalState)
    {
        const int U = Unreachable;
        int n0 = U, n1 = U, n2 = U, a0 = U, a1 = U, s0 = U, s1 = U, b = U;
        // The start state: a run opens from it before the first character and from nothing else after.
        var start = 0;
        var length = text.Length;
        for (var i = 0; i < length; i++)
        {
            var c = text[i];
            var cls = ClassOf(c);

            var numeric = Math.Min(Math.Min(n0, n1), n2);
            var alnum = Math.Min(a0, a1);
            var separator = Math.Min(s0, s1);
            var alnumAll = Math.Min(alnum, separator);

            // Byte: continue, or open from the cheapest other state. A byte order mark inside the text never opens a run.
            var nb = c == ByteOrderMark && i > 0 ? b + 8 : Math.Min(b + 8, Math.Min(Math.Min(numeric, alnumAll), start) + openByte);

            int nn0 = U, nn1 = U, nn2 = U, na0 = U, na1 = U, ns0 = U, ns1 = U;
            if (cls == ClassDigit)
            {
                // The first digit of a group costs 4 bits, the next two 3 each.
                var open = Math.Min(Math.Min(alnumAll, b), start);
                nn1 = Math.Min(n0 + 4, open + openNumeric);
                nn2 = n1 + 3;
                nn0 = n2 + 3;
            }

            if (c == Fnc1FieldSeparator)
            {
                // One Alphanumeric character that lands in a separator state. A run continues from a plain
                // Alphanumeric state only: after a separator, another one must open a new run.
                var open = Math.Min(Math.Min(Math.Min(numeric, separator), b), start);
                ns1 = Math.Min(a0 + 6, open + openAlnum);
                ns0 = a1 + 5;
            }
            else if (c == '%')
            {
                // Two Alphanumeric characters, 11 bits whichever half of a pair they start at, so the pairing state
                // stays. After a separator the two cannot continue the run.
                var open = Math.Min(Math.Min(Math.Min(numeric, separator), b), start);
                na0 = Math.Min(a0 + 11, open + openAlnum + 5);
                na1 = a1 + 11;
            }
            else if (cls != ClassOther)
            {
                // A plain Alphanumeric character: 6 bits as the first of a pair, 5 as the second, continuing from
                // either kind of Alphanumeric state.
                var open = Math.Min(Math.Min(numeric, b), start);
                na1 = Math.Min(Math.Min(a0, s0) + 6, open + openAlnum);
                na0 = Math.Min(a1, s1) + 5;
            }

            n0 = nn0; n1 = nn1; n2 = nn2; a0 = na0; a1 = na1; s0 = ns0; s1 = ns1; b = nb;
            start = U;
        }

        if (length == 0)
        {
            finalState = Fnc1StateStart;
            return 0;
        }

        // The cheapest state to end in, the lowest winning a tie.
        var best = U;
        finalState = Fnc1StateByte;
        if (n0 < best) { best = n0; finalState = Fnc1StateNumeric0; }
        if (n1 < best) { best = n1; finalState = Fnc1StateNumeric1; }
        if (n2 < best) { best = n2; finalState = Fnc1StateNumeric2; }
        if (a0 < best) { best = a0; finalState = Fnc1StateAlnum0; }
        if (a1 < best) { best = a1; finalState = Fnc1StateAlnum1; }
        if (s0 < best) { best = s0; finalState = Fnc1StateSeparator0; }
        if (s1 < best) { best = s1; finalState = Fnc1StateSeparator1; }
        if (b < best) { best = b; finalState = Fnc1StateByte; }
        return best;
    }

    /// <summary>
    /// Keys with parents, one byte per character: the plan of a symbol under FNC1. The widths arrive in bits,
    /// <paramref name="openByte"/> with its first character in, and become keys here (bits times sixteen). Returns
    /// the cost and the state of the cheapest key.
    /// </summary>
    private static int TrackedFnc1Latin(ReadOnlySpan<char> text, int openNumeric, int openAlnum, int openByte, Span<byte> parents, out int finalState)
    {
        Debug.Assert(text.Length <= MaxTrackedChars);
        const int UK = Fnc1UnreachableKey;
        openNumeric <<= 4;
        openAlnum <<= 4;
        openByte <<= 4;

        int n0 = UK | Fnc1StateNumeric0, n1 = UK | Fnc1StateNumeric1, n2 = UK | Fnc1StateNumeric2;
        int a0 = UK | Fnc1StateAlnum0, a1 = UK | Fnc1StateAlnum1, s0 = UK | Fnc1StateSeparator0, s1 = UK | Fnc1StateSeparator1, b = UK | Fnc1StateByte;
        // The start state as a key: cost 0 before the first character, unreachable after it.
        var start = Fnc1StateStart;
        var length = text.Length;
        for (var i = 0; i < length; i++)
        {
            var c = text[i];
            var cls = ClassOf(c);
            var row = parents.Slice(i * Fnc1ParentBytesPerChar, Fnc1ParentBytesPerChar);

            var numeric = Math.Min(Math.Min(n0, n1), n2);
            var alnum = Math.Min(a0, a1);
            var separator = Math.Min(s0, s1);
            var alnumAll = Math.Min(alnum, separator);

            int nn0 = UK | Fnc1StateNumeric0, nn1 = UK | Fnc1StateNumeric1, nn2 = UK | Fnc1StateNumeric2;
            int na0 = UK | Fnc1StateAlnum0, na1 = UK | Fnc1StateAlnum1, ns0 = UK | Fnc1StateSeparator0, ns1 = UK | Fnc1StateSeparator1;

            var nb = c == ByteOrderMark && i > 0
                ? ContinueKey(row, Fnc1StateByte, b + (8 << 4))
                : ChooseKey(row, Fnc1StateByte, b + (8 << 4), Math.Min(Math.Min(numeric, alnumAll), start) + openByte);

            if (cls == ClassDigit)
            {
                var open = Math.Min(Math.Min(alnumAll, b), start);
                nn1 = ChooseKey(row, Fnc1StateNumeric1, n0 + (4 << 4), open + openNumeric);
                nn2 = ContinueKey(row, Fnc1StateNumeric2, n1 + (3 << 4));
                nn0 = ContinueKey(row, Fnc1StateNumeric0, n2 + (3 << 4));
            }

            if (c == Fnc1FieldSeparator)
            {
                var open = Math.Min(Math.Min(Math.Min(numeric, separator), b), start);
                ns1 = ChooseKey(row, Fnc1StateSeparator1, a0 + (6 << 4), open + openAlnum);
                ns0 = ContinueKey(row, Fnc1StateSeparator0, a1 + (5 << 4));
            }
            else if (c == '%')
            {
                var open = Math.Min(Math.Min(Math.Min(numeric, separator), b), start);
                na0 = ChooseKey(row, Fnc1StateAlnum0, a0 + (11 << 4), open + openAlnum + (5 << 4));
                na1 = ContinueKey(row, Fnc1StateAlnum1, a1 + (11 << 4));
            }
            else if (cls != ClassOther)
            {
                var open = Math.Min(Math.Min(numeric, b), start);
                na1 = ChooseKey(row, Fnc1StateAlnum1, Math.Min(a0, s0) + (6 << 4), open + openAlnum);
                na0 = ContinueKey(row, Fnc1StateAlnum0, Math.Min(a1, s1) + (5 << 4));
            }

            n0 = nn0; n1 = nn1; n2 = nn2; a0 = na0; a1 = na1; s0 = ns0; s1 = ns1; b = nb;
            start = UK | Fnc1StateStart;
        }

        if (length == 0)
        {
            finalState = Fnc1StateStart;
            return 0;
        }

        var best = Math.Min(Math.Min(Math.Min(n0, n1), Math.Min(n2, a0)), Math.Min(Math.Min(a1, s0), Math.Min(s1, b)));
        if (best >= UK)
        {
            finalState = Fnc1StateByte;
            return Unreachable;
        }
        finalState = best & 15;
        return best >> 4;
    }

    /// <summary>Records the continued key or the opened key for <paramref name="state"/>, the continuation winning a tie on cost, and returns the one kept with <paramref name="state"/> below it.</summary>
    private static int ChooseKey(Span<byte> row, int state, int continuedKey, int openedKey)
    {
        if ((continuedKey >> 4) <= (openedKey >> 4))
            return ContinueKey(row, state, continuedKey);
        if (openedKey >= Fnc1UnreachableKey)
            return Fnc1UnreachableKey | state;
        row[state] = (byte)((openedKey & 15) | Fnc1Opened);
        return (openedKey & ~15) | state;
    }

    /// <summary>Records the continued key for <paramref name="state"/>, or leaves an unreachable one at its floor so no sum of them can reach a reachable cost.</summary>
    private static int ContinueKey(Span<byte> row, int state, int continuedKey)
    {
        if (continuedKey >= Fnc1UnreachableKey)
            return Fnc1UnreachableKey | state;
        row[state] = (byte)(continuedKey & 15);
        return (continuedKey & ~15) | state;
    }

    /// <summary>
    /// The program for the remaining shape, a UTF-8 charset, where a character's byte count is its own and a byte
    /// order mark can stand anywhere: the states in two arrays, relaxed from one row into the next.
    /// </summary>
    private static int CostsFnc1General(ReadOnlySpan<char> text, EciMode charset, int openNumeric, int openAlnum, int openByte, Span<byte> parents, out int finalState)
    {
        var track = !parents.IsEmpty;

        // The cost of each state after the character before, Unreachable where no run ends there.
        Span<int> cost = stackalloc int[Fnc1StateCount];
        Span<int> next = stackalloc int[Fnc1StateCount];
        for (var s = 0; s < Fnc1StateStart; s++)
            cost[s] = Unreachable;
        cost[Fnc1StateStart] = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var cls = ClassOf(c);
            var isSeparator = c == Fnc1FieldSeparator;
            var isPercent = c == '%';
            var byteBits = 8 * ByteCost(text, i, charset);

            for (var s = 0; s < Fnc1StateCount; s++)
                next[s] = Unreachable;
            var row = track ? parents.Slice(i * Fnc1ParentBytesPerChar, Fnc1ParentBytesPerChar) : default;

            // Byte: continue, or open from the cheapest state. A byte order mark inside the text never opens a run.
            {
                var from = Fnc1StateStart;
                var open = c == ByteOrderMark && i > 0 ? Unreachable : CheapestExcept(cost, Fnc1StateByte, out from);
                var opened = open == Unreachable ? Unreachable : open + openByte + byteBits;
                var continued = cost[Fnc1StateByte] == Unreachable ? Unreachable : cost[Fnc1StateByte] + byteBits;
                Choose(next, row, Fnc1StateByte, continued, Fnc1StateByte, opened, from);
            }

            if (cls == ClassDigit)
            {
                // The first digit of a group costs 4 bits, the next two 3 each.
                var open = CheapestExcept(cost, Fnc1StateNumeric0, Fnc1StateNumeric1, Fnc1StateNumeric2, out var from);
                Choose(next, row, Fnc1StateNumeric1, Add(cost[Fnc1StateNumeric0], 4), Fnc1StateNumeric0, Add(open, openNumeric), from);
                Choose(next, row, Fnc1StateNumeric2, Add(cost[Fnc1StateNumeric1], 3), Fnc1StateNumeric1, Unreachable, 0);
                Choose(next, row, Fnc1StateNumeric0, Add(cost[Fnc1StateNumeric2], 3), Fnc1StateNumeric2, Unreachable, 0);
            }

            if (isSeparator)
            {
                // One Alphanumeric character that lands in a separator state. A run continues from a plain Alphanumeric
                // state only: after a separator, another one must open a new run.
                var open = CheapestExcept(cost, Fnc1StateAlnum0, Fnc1StateAlnum1, out var from);
                Choose(next, row, Fnc1StateSeparator1, Add(cost[Fnc1StateAlnum0], 6), Fnc1StateAlnum0, Add(open, openAlnum), from);
                Choose(next, row, Fnc1StateSeparator0, Add(cost[Fnc1StateAlnum1], 5), Fnc1StateAlnum1, Unreachable, 0);
            }
            else if (isPercent)
            {
                // Two Alphanumeric characters, 11 bits whichever half of a pair they start at, so the pairing state
                // stays. After a separator the two cannot continue the run.
                var open = CheapestExcept(cost, Fnc1StateAlnum0, Fnc1StateAlnum1, out var from);
                Choose(next, row, Fnc1StateAlnum0, Add(cost[Fnc1StateAlnum0], 11), Fnc1StateAlnum0, Add(open, openAlnum + 5), from);
                Choose(next, row, Fnc1StateAlnum1, Add(cost[Fnc1StateAlnum1], 11), Fnc1StateAlnum1, Unreachable, 0);
            }
            else if (cls != ClassOther)
            {
                // A plain Alphanumeric character: 6 bits as the first of a pair, 5 as the second, continuing from
                // either kind of Alphanumeric state.
                var open = CheapestExcept(cost, Fnc1StateAlnum0, Fnc1StateAlnum1, Fnc1StateSeparator0, Fnc1StateSeparator1, out var from);
                var continued1 = Lesser(Add(cost[Fnc1StateAlnum0], 6), Fnc1StateAlnum0, Add(cost[Fnc1StateSeparator0], 6), Fnc1StateSeparator0, out var parent1);
                Choose(next, row, Fnc1StateAlnum1, continued1, parent1, Add(open, openAlnum), from);
                var continued0 = Lesser(Add(cost[Fnc1StateAlnum1], 5), Fnc1StateAlnum1, Add(cost[Fnc1StateSeparator1], 5), Fnc1StateSeparator1, out var parent0);
                Choose(next, row, Fnc1StateAlnum0, continued0, parent0, Unreachable, 0);
            }

            next.CopyTo(cost);
            cost[Fnc1StateStart] = Unreachable;
        }

        var best = Unreachable;
        finalState = Fnc1StateByte;
        for (var s = 0; s < Fnc1StateStart; s++)
        {
            if (cost[s] < best)
            {
                best = cost[s];
                finalState = s;
            }
        }
        if (text.Length == 0)
        {
            finalState = Fnc1StateStart;
            return 0;
        }
        return best;
    }

    private static int Add(int cost, int bits) => cost == Unreachable ? Unreachable : cost + bits;

    private static int Lesser(int a, int stateA, int b, int stateB, out int state)
    {
        if (b < a)
        {
            state = stateB;
            return b;
        }
        state = stateA;
        return a;
    }

    /// <summary>Records the cheaper of continuing a run and opening one, the continuation winning a tie.</summary>
    private static void Choose(Span<int> next, Span<byte> row, int state, int continued, int continuedFrom, int opened, int openedFrom)
    {
        if (continued <= opened)
        {
            if (continued == Unreachable)
                return;
            next[state] = continued;
            if (!row.IsEmpty)
                row[state] = (byte)continuedFrom;
        }
        else
        {
            next[state] = opened;
            if (!row.IsEmpty)
                row[state] = (byte)(openedFrom | Fnc1Opened);
        }
    }

    private static int CheapestExcept(ReadOnlySpan<int> cost, int skip, out int state)
        => CheapestExcept(cost, skip, skip, skip, skip, out state);

    private static int CheapestExcept(ReadOnlySpan<int> cost, int skip0, int skip1, out int state)
        => CheapestExcept(cost, skip0, skip1, skip1, skip1, out state);

    private static int CheapestExcept(ReadOnlySpan<int> cost, int skip0, int skip1, int skip2, out int state)
        => CheapestExcept(cost, skip0, skip1, skip2, skip2, out state);

    /// <summary>The cheapest state a new run can open from, leaving out the states of the run being continued (the lowest state wins a tie).</summary>
    private static int CheapestExcept(ReadOnlySpan<int> cost, int skip0, int skip1, int skip2, int skip3, out int state)
    {
        var best = Unreachable;
        state = Fnc1StateStart;
        for (var s = 0; s < Fnc1StateCount; s++)
        {
            if (s == skip0 || s == skip1 || s == skip2 || s == skip3)
                continue;
            if (cost[s] < best)
            {
                best = cost[s];
                state = s;
            }
        }
        return best;
    }

    /// <summary>The plan of <see cref="ComputeCostsFnc1"/>'s table as runs, with the unit counts the FNC1 writer needs.</summary>
    public static bool ReconstructFnc1(ReadOnlySpan<char> text, ReadOnlySpan<byte> parents, int finalState, Span<ModeSegment> segments, out int segmentCount)
    {
        segmentCount = 0;
        var length = text.Length;
        if (length == 0)
            return true;

        var count = 0;
        var state = finalState;
        var end = length;
        for (var i = length - 1; i >= 0; i--)
        {
            var entry = parents[i * Fnc1ParentBytesPerChar + state];
            var parent = entry & ~Fnc1Opened;
            if ((entry & Fnc1Opened) != 0 || parent == Fnc1StateStart)
            {
                if (count >= segments.Length)
                    return false;
                var modeIndex = Fnc1ModeOf(state);
                var chars = text.Slice(i, end - i);
                var units = modeIndex == 1 ? AlphanumericUnitsFnc1(chars) : chars.Length;
                segments[count++] = new ModeSegment(modeIndex, i, end - i, units);
                end = i;
            }
            state = parent;
        }

        Debug.Assert(end == 0, "the walk must reach the start of the text");
        segments.Slice(0, count).Reverse();
        segmentCount = count;
        return true;
    }

    private static int Fnc1ModeOf(int state) => state <= Fnc1StateNumeric2 ? 0 : state <= Fnc1StateSeparator1 ? 1 : 2;
}
