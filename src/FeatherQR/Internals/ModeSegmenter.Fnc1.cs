using System.Diagnostics;
using System.Runtime.CompilerServices;

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
/// A charset of one byte a character takes loops that keep the eight states in locals, as the program without FNC1
/// keeps its six (see <see cref="ModeSegmenter"/>): relaxing every state through arrays costs far more in memory
/// traffic than the arithmetic. UTF-8 takes copies of those loops with a byte count a character.
/// The loops open every mode from one cheapest state, as the plain program does, rather than from a minimum over the
/// other modes: an opening from a run's own mode never lies on a cheapest plan, since merging the two runs saves a
/// header and no mode's cost grows by the merge, and where the separator rule forbids the continuation (a separator
/// state before a separator or a literal percent) the opening is the allowed move. The eight states are updated in
/// place, which keeps the keys and minima a second set of states would hold off the stack, and a run of characters
/// only Byte encodes is one add each, as in the plain loops.
/// With parents, the loop carries a state as a key, its cost shifted up four bits with the state's number below
/// it, one bit more than the program without FNC1 needs for its eight. The minimum of keys is the minimum cost and,
/// among equal costs, the lowest state. A continued run beats an
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
    /// <remarks>
    /// The FNC1 prices differ from <see cref="CheapestSixths"/>'s in two characters, which no class pass of its own could price faster than the plain bound and two counts, both vectorised on .NET 8 and later.
    /// A literal percent is 48 where the plain alphabet gives 33, and the separator is 33 where a byte gives 48, both one byte under every charset.
    /// </remarks>
    public static int CheapestSixthsFnc1(ReadOnlySpan<char> text, EciMode charset)
        => CheapestSixths(text, charset) + (SixthsPerByte - SixthsPerAlnum) * (CountOf(text, '%') - CountOf(text, Fnc1FieldSeparator));

    private static int CountOf(ReadOnlySpan<char> text, char value)
    {
#if NET8_0_OR_GREATER
        return text.Count(value);
#else
        var count = 0;
        foreach (var c in text)
        {
            if (c == value)
                count++;
        }
        return count;
#endif
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

        return parents.IsEmpty
            ? CostsFnc1Utf8(text, charset, openNumeric, openAlnum, modeIndicatorBits + cciByte, out finalState)
            : TrackedFnc1Utf8(text, charset, openNumeric, openAlnum, modeIndicatorBits + cciByte, parents, out finalState);
    }

    /// <summary>A character only Byte encodes under FNC1: outside the Alphanumeric alphabet and not the separator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ByteOnlyFnc1(char c) => ClassOf(c) == ClassOther && c != Fnc1FieldSeparator;

    /// <summary>Costs only, one byte per character: the version scans.</summary>
    private static int CostsFnc1Latin(ReadOnlySpan<char> text, int openNumeric, int openAlnum, int openByte, out int finalState)
    {
        const int U = Unreachable;
        int n0 = U, n1 = U, n2 = U, a0 = U, a1 = U, s0 = U, s1 = U, b = U;
        var cheapest = 0; // the start state, then the cheapest of the eight
        var length = text.Length;
        for (var i = 0; i < length; i++)
        {
            var c = text[i];
            var cls = ClassOf(c);
            // Byte continues at 8 bits or opens from the cheapest state. A byte order mark inside the text never opens a run.
            b = c == ByteOrderMark && i > 0 ? b + 8 : Math.Min(b + 8, cheapest + openByte);
            if (cls == ClassOther && c != Fnc1FieldSeparator)
            {
                // Only Byte encodes this character, so it alone stays reachable and every
                // further such character extends its run: one add each.
                n0 = n1 = n2 = a0 = a1 = s0 = s1 = U;
                while (i + 1 < length && ByteOnlyFnc1(text[i + 1]))
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
                n0 = n1 = n2 = U;
            }

            if (c == Fnc1FieldSeparator)
            {
                // One Alphanumeric character that lands in a separator state, continued from a plain Alphanumeric
                // state only: after a separator, another one must open a new run.
                var first = Math.Min(a0 + 6, cheapest + openAlnum);
                s0 = a1 + 5;
                s1 = first;
                a0 = a1 = U;
            }
            else if (c == '%')
            {
                // Two Alphanumeric characters, 11 bits whichever half of a pair they start at, so the pairing state
                // stays. After a separator the two cannot continue the run.
                var first = Math.Min(a0 + 11, cheapest + openAlnum + 5);
                a1 += 11;
                a0 = first;
                s0 = s1 = U;
            }
            else
            {
                // A plain Alphanumeric character: 6 bits as the first of a pair, 5 as the second, continuing from
                // either kind of Alphanumeric state.
                var first = Math.Min(Math.Min(a0, s0) + 6, cheapest + openAlnum);
                a0 = Math.Min(a1, s1) + 5;
                a1 = first;
                s0 = s1 = U;
            }

            cheapest = Math.Min(Math.Min(Math.Min(n0, n1), Math.Min(n2, a0)), Math.Min(Math.Min(a1, s0), Math.Min(s1, b)));
        }

        if (length == 0)
        {
            finalState = Fnc1StateStart;
            return 0;
        }

        return BestFnc1(n0, n1, n2, a0, a1, s0, s1, b, out finalState);
    }

    /// <summary>The cheapest state to end in, the lowest winning a tie.</summary>
    private static int BestFnc1(int n0, int n1, int n2, int a0, int a1, int s0, int s1, int b, out int finalState)
    {
        var best = Unreachable;
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
        // The start state as a key: cost 0 with its number below, then the cheapest key of the eight.
        var cheapest = Fnc1StateStart;
        var length = text.Length;
        for (var i = 0; i < length; i++)
        {
            var c = text[i];
            var cls = ClassOf(c);
            var row = parents.Slice(i * Fnc1ParentBytesPerChar, Fnc1ParentBytesPerChar);

            b = c == ByteOrderMark && i > 0
                ? ContinueKey(row, Fnc1StateByte, b + (8 << 4))
                : ChooseKey(row, Fnc1StateByte, b + (8 << 4), cheapest + openByte);

            if (cls == ClassOther && c != Fnc1FieldSeparator)
            {
                // Only Byte encodes this character, so it alone stays reachable and every
                // further such character extends its run: one add and one byte each.
                n0 = UK | Fnc1StateNumeric0; n1 = UK | Fnc1StateNumeric1; n2 = UK | Fnc1StateNumeric2;
                a0 = UK | Fnc1StateAlnum0; a1 = UK | Fnc1StateAlnum1; s0 = UK | Fnc1StateSeparator0; s1 = UK | Fnc1StateSeparator1;
                while (i + 1 < length && ByteOnlyFnc1(text[i + 1]))
                {
                    i++;
                    b += 8 << 4;
                    parents[i * Fnc1ParentBytesPerChar + Fnc1StateByte] = Fnc1StateByte;
                }
                cheapest = b;
                continue;
            }

            if (cls == ClassDigit)
            {
                var wrapped = ContinueKey(row, Fnc1StateNumeric0, n2 + (3 << 4));
                n2 = ContinueKey(row, Fnc1StateNumeric2, n1 + (3 << 4));
                n1 = ChooseKey(row, Fnc1StateNumeric1, n0 + (4 << 4), cheapest + openNumeric);
                n0 = wrapped;
            }
            else
            {
                n0 = UK | Fnc1StateNumeric0; n1 = UK | Fnc1StateNumeric1; n2 = UK | Fnc1StateNumeric2;
            }

            if (c == Fnc1FieldSeparator)
            {
                var first = ChooseKey(row, Fnc1StateSeparator1, a0 + (6 << 4), cheapest + openAlnum);
                s0 = ContinueKey(row, Fnc1StateSeparator0, a1 + (5 << 4));
                s1 = first;
                a0 = UK | Fnc1StateAlnum0; a1 = UK | Fnc1StateAlnum1;
            }
            else if (c == '%')
            {
                var first = ChooseKey(row, Fnc1StateAlnum0, a0 + (11 << 4), cheapest + openAlnum + (5 << 4));
                a1 = ContinueKey(row, Fnc1StateAlnum1, a1 + (11 << 4));
                a0 = first;
                s0 = UK | Fnc1StateSeparator0; s1 = UK | Fnc1StateSeparator1;
            }
            else
            {
                var first = ChooseKey(row, Fnc1StateAlnum1, Math.Min(a0, s0) + (6 << 4), cheapest + openAlnum);
                a0 = ContinueKey(row, Fnc1StateAlnum0, Math.Min(a1, s1) + (5 << 4));
                a1 = first;
                s0 = UK | Fnc1StateSeparator0; s1 = UK | Fnc1StateSeparator1;
            }

            cheapest = Math.Min(Math.Min(Math.Min(n0, n1), Math.Min(n2, a0)), Math.Min(Math.Min(a1, s0), Math.Min(s1, b)));
        }

        if (length == 0)
        {
            finalState = Fnc1StateStart;
            return 0;
        }

        if (cheapest >= UK)
        {
            finalState = Fnc1StateByte;
            return Unreachable;
        }
        finalState = cheapest & 15;
        return cheapest >> 4;
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
    /// <see cref="CostsFnc1Latin"/> under UTF-8: the byte cost of each character is its own, a paired low surrogate's zero, and a
    /// byte order mark inside the text continues a Byte run only. <paramref name="openByte"/> is the header alone.
    /// </summary>
    private static int CostsFnc1Utf8(ReadOnlySpan<char> text, EciMode charset, int openNumeric, int openAlnum, int openByte, out int finalState)
    {
        const int U = Unreachable;
        int n0 = U, n1 = U, n2 = U, a0 = U, a1 = U, s0 = U, s1 = U, b = U;
        var cheapest = 0;
        var length = text.Length;
        for (var i = 0; i < length; i++)
        {
            var c = text[i];
            var cls = ClassOf(c);
            var byteBits = ByteCost(text, i, charset) << 3;
            b = c == ByteOrderMark && i > 0 ? b + byteBits : Math.Min(b + byteBits, cheapest + openByte + byteBits);
            if (cls == ClassOther && c != Fnc1FieldSeparator)
            {
                n0 = n1 = n2 = a0 = a1 = s0 = s1 = U;
                while (i + 1 < length && ByteOnlyFnc1(text[i + 1]))
                {
                    i++;
                    b += ByteCost(text, i, charset) << 3;
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
                n0 = n1 = n2 = U;
            }

            if (c == Fnc1FieldSeparator)
            {
                var first = Math.Min(a0 + 6, cheapest + openAlnum);
                s0 = a1 + 5;
                s1 = first;
                a0 = a1 = U;
            }
            else if (c == '%')
            {
                var first = Math.Min(a0 + 11, cheapest + openAlnum + 5);
                a1 += 11;
                a0 = first;
                s0 = s1 = U;
            }
            else
            {
                var first = Math.Min(Math.Min(a0, s0) + 6, cheapest + openAlnum);
                a0 = Math.Min(a1, s1) + 5;
                a1 = first;
                s0 = s1 = U;
            }

            cheapest = Math.Min(Math.Min(Math.Min(n0, n1), Math.Min(n2, a0)), Math.Min(Math.Min(a1, s0), Math.Min(s1, b)));
        }

        if (length == 0)
        {
            finalState = Fnc1StateStart;
            return 0;
        }

        return BestFnc1(n0, n1, n2, a0, a1, s0, s1, b, out finalState);
    }

    /// <summary><see cref="TrackedFnc1Latin"/> under UTF-8, the byte cost of each character its own as in <see cref="CostsFnc1Utf8"/>. <paramref name="openByte"/> is the header alone, in bits.</summary>
    private static int TrackedFnc1Utf8(ReadOnlySpan<char> text, EciMode charset, int openNumeric, int openAlnum, int openByte, Span<byte> parents, out int finalState)
    {
        Debug.Assert(text.Length <= MaxTrackedChars);
        const int UK = Fnc1UnreachableKey;
        openNumeric <<= 4;
        openAlnum <<= 4;
        openByte <<= 4;

        int n0 = UK | Fnc1StateNumeric0, n1 = UK | Fnc1StateNumeric1, n2 = UK | Fnc1StateNumeric2;
        int a0 = UK | Fnc1StateAlnum0, a1 = UK | Fnc1StateAlnum1, s0 = UK | Fnc1StateSeparator0, s1 = UK | Fnc1StateSeparator1, b = UK | Fnc1StateByte;
        var cheapest = Fnc1StateStart;
        var length = text.Length;
        for (var i = 0; i < length; i++)
        {
            var c = text[i];
            var cls = ClassOf(c);
            var row = parents.Slice(i * Fnc1ParentBytesPerChar, Fnc1ParentBytesPerChar);
            var byteKey = ByteCost(text, i, charset) << 7;

            b = c == ByteOrderMark && i > 0
                ? ContinueKey(row, Fnc1StateByte, b + byteKey)
                : ChooseKey(row, Fnc1StateByte, b + byteKey, cheapest + openByte + byteKey);

            if (cls == ClassOther && c != Fnc1FieldSeparator)
            {
                n0 = UK | Fnc1StateNumeric0; n1 = UK | Fnc1StateNumeric1; n2 = UK | Fnc1StateNumeric2;
                a0 = UK | Fnc1StateAlnum0; a1 = UK | Fnc1StateAlnum1; s0 = UK | Fnc1StateSeparator0; s1 = UK | Fnc1StateSeparator1;
                while (i + 1 < length && ByteOnlyFnc1(text[i + 1]))
                {
                    i++;
                    b += ByteCost(text, i, charset) << 7;
                    parents[i * Fnc1ParentBytesPerChar + Fnc1StateByte] = Fnc1StateByte;
                }
                cheapest = b;
                continue;
            }

            if (cls == ClassDigit)
            {
                var wrapped = ContinueKey(row, Fnc1StateNumeric0, n2 + (3 << 4));
                n2 = ContinueKey(row, Fnc1StateNumeric2, n1 + (3 << 4));
                n1 = ChooseKey(row, Fnc1StateNumeric1, n0 + (4 << 4), cheapest + openNumeric);
                n0 = wrapped;
            }
            else
            {
                n0 = UK | Fnc1StateNumeric0; n1 = UK | Fnc1StateNumeric1; n2 = UK | Fnc1StateNumeric2;
            }

            if (c == Fnc1FieldSeparator)
            {
                var first = ChooseKey(row, Fnc1StateSeparator1, a0 + (6 << 4), cheapest + openAlnum);
                s0 = ContinueKey(row, Fnc1StateSeparator0, a1 + (5 << 4));
                s1 = first;
                a0 = UK | Fnc1StateAlnum0; a1 = UK | Fnc1StateAlnum1;
            }
            else if (c == '%')
            {
                var first = ChooseKey(row, Fnc1StateAlnum0, a0 + (11 << 4), cheapest + openAlnum + (5 << 4));
                a1 = ContinueKey(row, Fnc1StateAlnum1, a1 + (11 << 4));
                a0 = first;
                s0 = UK | Fnc1StateSeparator0; s1 = UK | Fnc1StateSeparator1;
            }
            else
            {
                var first = ChooseKey(row, Fnc1StateAlnum1, Math.Min(a0, s0) + (6 << 4), cheapest + openAlnum);
                a0 = ContinueKey(row, Fnc1StateAlnum0, Math.Min(a1, s1) + (5 << 4));
                a1 = first;
                s0 = UK | Fnc1StateSeparator0; s1 = UK | Fnc1StateSeparator1;
            }

            cheapest = Math.Min(Math.Min(Math.Min(n0, n1), Math.Min(n2, a0)), Math.Min(Math.Min(a1, s0), Math.Min(s1, b)));
        }

        if (length == 0)
        {
            finalState = Fnc1StateStart;
            return 0;
        }

        if (cheapest >= UK)
        {
            finalState = Fnc1StateByte;
            return Unreachable;
        }
        finalState = cheapest & 15;
        return cheapest >> 4;
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
