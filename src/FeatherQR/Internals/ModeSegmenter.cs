#if !NETSTANDARD2_1_OR_GREATER && !NET5_0_OR_GREATER
// ArrayPool is only reached from the netstandard2.0 branch of ByteUnitCount.
using System.Buffers;
#endif
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
/// With parents, a state is carried as a key: its cost shifted up three bits with the state's number below it. The minimum of keys is the minimum cost and, among equal costs, the lowest state, so the predecessor is the low bits of the minimum the cost-only loop takes anyway.
/// Only three predecessors vary (those of Numeric1, Alnum1 and Byte; the others are constants of the packing groups), so the table is two bytes a character, and the walk back steps from one place a run can begin to the one before it rather than from character to character.
/// </remarks>
internal static partial class ModeSegmenter
{
    /// <summary>Bytes of the predecessor table per character: the predecessor of Byte, then those of Numeric1 (low three bits) and Alnum1 in one byte.</summary>
    public const int ParentBytesPerChar = 2;

    /// <summary>Parent bytes that fit the stack budget (256 characters); longer content rents.</summary>
    public const int MaxStackParents = 512;

    /// <summary>The longest content the keyed loops take; a key is a cost times eight, and this leaves it far below the overflow.</summary>
    private const int MaxTrackedChars = 1 << 20;

    // An unreachable state as a key, below any number a few additions could overflow.
    private const int UnreachableKey = (int.MaxValue / 16) << 3;

    /// <summary>
    /// U+FEFF, which under UTF-8 no Byte run opens at past the first character: the byte-segment decoder takes a segment's leading EF BB BF for a byte order mark and drops it, so the run the character is in opens a character early instead and keeps it interior.
    /// At the first character the single-mode stream begins with the same bytes. Every text still has a plan, its single Byte run.
    /// </summary>
    internal const char ByteOrderMark = (char)0xFEFF;

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
    internal const int ClassOther = 0;
    internal const int ClassDigit = 2;

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
    internal static int ClassOf(char c) => c < characterClass.Length ? characterClass[c] : ClassOther;

    /// <summary>
    /// Minimal payload bits (excluding any ECI prefix) for the content at the given mode indicator and count indicator widths, or a value at or above <see cref="Unreachable"/> when a character has no allowed mode.
    /// When <paramref name="parents"/> is non-empty (<see cref="ParentBytesPerChar"/> bytes a character) it receives the predecessors <see cref="Reconstruct"/> walks back.
    /// </summary>
    public static int ComputeCosts(ReadOnlySpan<char> text, EciMode charset, int modeIndicatorBits, int cciNumeric, int cciAlnum, int cciByte, Span<byte> parents, out int finalState, bool allowAlnum = true, bool allowByte = true)
    {
        Debug.Assert(parents.IsEmpty || parents.Length == text.Length * ParentBytesPerChar);

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

        Debug.Assert(text.Length <= MaxTrackedChars);
        OpenFromStart(text, charset, openNumeric << 3, openAlnum << 3, openByte << 3, allowAlnum, allowByte, parents, out var n1, out var a1, out var b);
        var best = latin
            ? TrackedLatin(text, n1, a1, b, openNumeric << 3, openAlnum << 3, (openByte + 8) << 3, allowAlnum, parents, ParentBytesPerChar)
            : TrackedGeneral(text, charset, n1, a1, b, openNumeric << 3, openAlnum << 3, openByte << 3, allowAlnum, allowByte, parents, ParentBytesPerChar);
        return FromKey(best, out finalState);
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
                nb = c == ByteOrderMark && i > 0 ? b + byteBits : Math.Min(b + byteBits, cheapest + openByte + byteBits);

            n0 = nn0; n1 = nn1; n2 = nn2; a0 = na0; a1 = na1; b = nb;
            cheapest = Math.Min(Math.Min(Math.Min(n0, n1), Math.Min(n2, a0)), Math.Min(a1, b));
        }

        return Best(n0, n1, n2, a0, a1, b, out finalState);
    }

    /// <summary>The keys after the first character, which whatever encodes it opens from the start; its predecessors say so. Numeric0, Numeric2 and Alnum0 are never reachable there.</summary>
    private static void OpenFromStart(ReadOnlySpan<char> text, EciMode charset, int openNumeric, int openAlnum, int openByte, bool allowAlnum, bool allowByte, Span<byte> table, out int n1, out int a1, out int b)
    {
        var cls = ClassOf(text[0]);
        n1 = cls == ClassDigit ? openNumeric | StateNumeric1 : UnreachableKey | StateNumeric1;
        a1 = cls != ClassOther && allowAlnum ? openAlnum | StateAlnum1 : UnreachableKey | StateAlnum1;
        b = allowByte ? (openByte + (ByteCost(text, 0, charset) << 6)) | StateByte : UnreachableKey | StateByte;
        table[0] = StateStart;
        table[1] = StateStart | (StateStart << 3);
    }

    /// <summary>The cost and the state of the cheapest key; <see cref="Unreachable"/> and Byte when no allowed mode set encodes the content, as the cost-only loops answer.</summary>
    private static int FromKey(int key, out int state)
    {
        if (key >= UnreachableKey)
        {
            state = StateByte;
            return Unreachable;
        }

        state = key & 7;
        return key >> 3;
    }

    /// <summary>
    /// Keys with parents from the second character on, one byte per character, Byte allowed: the per-symbol plan of every Latin symbol. Returns the cheapest key.
    /// The widths are keys already (bits times eight), <paramref name="openByte"/> with its first character in; the table is read and written <paramref name="stride"/> bytes a character.
    /// </summary>
    private static int TrackedLatin(ReadOnlySpan<char> text, int n1, int a1, int b, int openNumeric, int openAlnum, int openByte, bool allowAlnum, Span<byte> table, int stride)
    {
        const int U = UnreachableKey;
        int n0 = U | StateNumeric0, n2 = U | StateNumeric2, a0 = U | StateAlnum0;
        var length = text.Length;
        for (var i = 1; i < length; i++)
        {
            var cls = ClassOf(text[i]);
            // The cheapest state of each denser mode; a key carries its state, so the lowest wins a tie.
            var numeric = Math.Min(Math.Min(n0, n1), n2);
            var alnum = Math.Min(a0, a1);
            var byteKey = Math.Min(Math.Min(numeric, alnum) + openByte, b + 64);
            table[i * stride] = (byte)(byteKey & 7);
            if (cls == ClassOther)
            {
                b = (byteKey & ~7) | StateByte;
                n0 = U | StateNumeric0; n1 = U | StateNumeric1; n2 = U | StateNumeric2; a0 = U | StateAlnum0; a1 = U | StateAlnum1;
                while (i + 1 < length && ClassOf(text[i + 1]) == ClassOther)
                {
                    i++;
                    b += 64;
                    table[i * stride] = StateByte;
                }
                continue;
            }

            int nn0 = U | StateNumeric0, nn1 = U | StateNumeric1, nn2 = U | StateNumeric2, na0 = U | StateAlnum0, na1 = U | StateAlnum1;
            int parentNumeric1 = 0, parentAlnum1 = 0;
            if (cls == ClassDigit)
            {
                // Continued from Numeric0 (4 bits), or opened from Alphanumeric or Byte.
                var key = Math.Min(n0 + 32, Math.Min(alnum, b) + openNumeric);
                parentNumeric1 = key & 7;
                nn1 = (key & ~7) | StateNumeric1;
                nn2 = n1 + 25; // 3 bits, and Numeric1 becomes Numeric2
                nn0 = n2 + 22; // 3 bits, and Numeric2 becomes Numeric0
            }
            if (allowAlnum)
            {
                // Opened from Numeric or Byte, or continued from Alnum0 (6 bits).
                var key = Math.Min(Math.Min(numeric, b) + openAlnum, a0 + 48);
                parentAlnum1 = key & 7;
                na1 = (key & ~7) | StateAlnum1;
                na0 = a1 + 39; // 5 bits, and Alnum1 becomes Alnum0
            }
            table[i * stride + 1] = (byte)(parentNumeric1 | (parentAlnum1 << 3));

            n0 = nn0; n1 = nn1; n2 = nn2; a0 = na0; a1 = na1; b = (byteKey & ~7) | StateByte;
        }

        return Math.Min(Math.Min(Math.Min(n0, n1), Math.Min(n2, a0)), Math.Min(a1, b));
    }

    /// <summary>Keys with parents for the remaining shapes: a UTF-8 charset, or a Micro QR version without Byte mode. <paramref name="openByte"/> is without its first character, whose bytes are its own.</summary>
    private static int TrackedGeneral(ReadOnlySpan<char> text, EciMode charset, int n1, int a1, int b, int openNumeric, int openAlnum, int openByte, bool allowAlnum, bool allowByte, Span<byte> table, int stride)
    {
        const int U = UnreachableKey;
        int n0 = U | StateNumeric0, n2 = U | StateNumeric2, a0 = U | StateAlnum0;
        var length = text.Length;
        for (var i = 1; i < length; i++)
        {
            var cls = ClassOf(text[i]);
            var numeric = Math.Min(Math.Min(n0, n1), n2);
            var alnum = Math.Min(a0, a1);
            if (cls == ClassOther)
            {
                n0 = U | StateNumeric0; n1 = U | StateNumeric1; n2 = U | StateNumeric2; a0 = U | StateAlnum0; a1 = U | StateAlnum1;
                if (!allowByte)
                {
                    b = U | StateByte;
                    continue; // nothing encodes it; every state stays unreachable from here on
                }

                var key = (text[i] == ByteOrderMark ? b : Math.Min(Math.Min(numeric, alnum) + openByte, b)) + (ByteCost(text, i, charset) << 6);
                table[i * stride] = (byte)(key & 7);
                b = (key & ~7) | StateByte;
                while (i + 1 < length && ClassOf(text[i + 1]) == ClassOther)
                {
                    i++;
                    b += ByteCost(text, i, charset) << 6;
                    table[i * stride] = StateByte;
                }
                continue;
            }

            // An alphanumeric character is ASCII: one byte in every charset.
            int nn0 = U | StateNumeric0, nn1 = U | StateNumeric1, nn2 = U | StateNumeric2, na0 = U | StateAlnum0, na1 = U | StateAlnum1, nb = U | StateByte;
            int parentNumeric1 = 0, parentAlnum1 = 0;
            if (cls == ClassDigit)
            {
                var key = Math.Min(n0 + 32, Math.Min(alnum, b) + openNumeric);
                parentNumeric1 = key & 7;
                nn1 = (key & ~7) | StateNumeric1;
                nn2 = n1 + 25;
                nn0 = n2 + 22;
            }
            if (allowAlnum)
            {
                var key = Math.Min(Math.Min(numeric, b) + openAlnum, a0 + 48);
                parentAlnum1 = key & 7;
                na1 = (key & ~7) | StateAlnum1;
                na0 = a1 + 39;
            }
            if (allowByte)
            {
                var key = Math.Min(Math.Min(numeric, alnum) + openByte, b) + 64;
                table[i * stride] = (byte)(key & 7);
                nb = (key & ~7) | StateByte;
            }
            table[i * stride + 1] = (byte)(parentNumeric1 | (parentAlnum1 << 3));

            n0 = nn0; n1 = nn1; n2 = nn2; a0 = na0; a1 = na1; b = nb;
        }

        return Math.Min(Math.Min(Math.Min(n0, n1), Math.Min(n2, a0)), Math.Min(a1, b));
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
            var nb = c == ByteOrderMark && i > 0 ? b + byteBits : Math.Min(b + byteBits, cheapest + openByte + byteBits);

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
        => WalkBack(text.Length, parents, ParentBytesPerChar, finalState, segments, materialise: true, out segmentCount, out _, out _, out _);

    /// <summary>Counts the runs of each mode on the minimal-cost path, without materialising it.</summary>
    public static void CountRuns(ReadOnlySpan<byte> parents, int finalState, int length, out int runsNumeric, out int runsAlnum, out int runsByte)
        => WalkBack(length, parents, ParentBytesPerChar, finalState, default, materialise: false, out _, out runsNumeric, out runsAlnum, out runsByte);

    /// <summary>
    /// The walk back, run by run. A run of Numeric can only begin where the state is Numeric1, every third character of the run, and a run of Alphanumeric where it is Alnum1, every second; a run of Byte begins at the first entry that does not say Byte.
    /// Each step reads whether the run continued at the place before, whose address is the position alone, so no load waits for the one before it.
    /// The first character's predecessor is the start, which no run continues from, so every search lands at or after it.
    /// </summary>
    private static bool WalkBack(int length, ReadOnlySpan<byte> table, int stride, int finalState, Span<ModeSegment> segments, bool materialise, out int segmentCount, out int runsNumeric, out int runsAlnum, out int runsByte)
    {
        segmentCount = 0;
        runsNumeric = 0;
        runsAlnum = 0;
        runsByte = 0;
        var state = finalState;
        var end = length;
        var count = 0;

        while (end > 0)
        {
            var i = end - 1;
            int mode, parent;
            if (state == StateByte)
            {
                mode = 2;
                runsByte++;
                while ((parent = table[i * stride]) == StateByte)
                    i--;
            }
            else if (state <= StateNumeric2)
            {
                mode = 0;
                runsNumeric++;
                // Back to where this group of three began.
                i -= state == StateNumeric1 ? 0 : state == StateNumeric2 ? 1 : 2;
                while ((parent = table[i * stride + 1] & 7) == StateNumeric0)
                    i -= 3;
            }
            else
            {
                mode = 1;
                runsAlnum++;
                i -= state == StateAlnum1 ? 0 : 1;
                while ((parent = table[i * stride + 1] >> 3) == StateAlnum0)
                    i -= 2;
            }

            if (materialise)
            {
                if (count >= segments.Length)
                    return false;
                segments[count++] = new ModeSegment(mode, i, end - i, 0);
            }
            end = i;
            state = parent;
        }

        Debug.Assert(state == StateStart || length == 0, "the walk must terminate at the virtual start state");
        segments.Slice(0, count).Reverse();
        segmentCount = count;
        return true;
    }

    /// <summary>
    /// Whether the plan opens a Byte run at a mid-content U+FEFF.
    /// The shared byte-segment decoder consumes a leading BOM of every segment without an explicit ISO-8859-1 declaration, so such a plan would decode with the character silently dropped.
    /// Under UTF-8 the program builds no such plan (<see cref="ByteOrderMark"/>), and the rMQR and Micro QR planners keep this as the refusal of a model that disagreed; neither plans a text holding the mark under any other charset.
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

    /// <summary>
    /// Encoded byte length of one character in Byte mode.
    /// Latin-1 charsets are one byte per character; UTF-8 mirrors what <see cref="Encoding.UTF8"/> emits, including its greedy surrogate pairing (a paired high surrogate carries all four bytes and its low surrogate none, an unpaired surrogate costs the three bytes of the replacement character).
    /// </summary>
    internal static int ByteCost(ReadOnlySpan<char> text, int index, EciMode charset)
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
