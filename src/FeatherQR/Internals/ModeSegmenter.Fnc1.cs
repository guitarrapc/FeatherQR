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
        var track = !parents.IsEmpty;
        var openNumeric = modeIndicatorBits + cciNumeric + 4;
        var openAlnum = modeIndicatorBits + cciAlnum + 6;
        var openByte = modeIndicatorBits + cciByte;

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
