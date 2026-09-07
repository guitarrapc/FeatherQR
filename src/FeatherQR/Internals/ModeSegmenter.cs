using System.Buffers;
using System.Diagnostics;
using System.Text;

namespace FeatherQR.Internals;

/// <summary>
/// Splits content into the minimal-bit Numeric / Alphanumeric / Byte runs and reconstructs them as a <see cref="ModeSegment"/> plan.
/// Shared by the Standard QR, Micro QR and rMQR planners, which differ in the header widths passed in and (Micro QR) mode availability; version scans and bounds stay in those planners.
/// </summary>
/// <remarks>
/// A run's cost is not a per-character constant, so the dynamic program carries the packing-group remainder in its state.
/// Micro QR restricts modes per version (M1 is Numeric-only, M2 has no Byte mode); its planner disables the missing transitions via <c>allowAlnum</c>/<c>allowByte</c>, and a character no allowed mode encodes leaves the cost at <see cref="Unreachable"/>.
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

    // Cheapest bits a single character can cost in any mode, in sixths so the numeric
    // and alphanumeric packing rates stay exact: 10 bits per 3 digits, 11 per 2
    // alphanumerics, 8 per byte. A partial group only ever costs more per character
    // (one digit is 4 bits against a rate of 3 1/3), so summing these rates can never
    // exceed what a real plan pays.
    private const int SixthsPerDigit = 20;
    private const int SixthsPerAlnum = 33;
    private const int SixthsPerByte = 48;

    /// <summary>
    /// Minimal payload bits (excluding any ECI prefix) for the content at the given mode indicator and count indicator widths, or a value at or above <see cref="Unreachable"/> when a character has no allowed mode.
    /// When <paramref name="parents"/> is non-empty it receives one predecessor state per (character, state) pair for reconstruction.
    /// </summary>
    public static int ComputeCosts(ReadOnlySpan<char> text, EciMode charset, int modeIndicatorBits, int cciNumeric, int cciAlnum, int cciByte, Span<byte> parents, out int finalState, bool allowAlnum = true, bool allowByte = true)
    {
        Debug.Assert(parents.IsEmpty || parents.Length == text.Length * StateCount);

        Span<int> prev = stackalloc int[StateCount];
        Span<int> cur = stackalloc int[StateCount];
        for (var s = 0; s < StateCount; s++)
            prev[s] = Unreachable;
        prev[StateStart] = 0;

        var track = !parents.IsEmpty;
        var openNumeric = modeIndicatorBits + cciNumeric;
        var openAlnum = modeIndicatorBits + cciAlnum;
        var openByte = modeIndicatorBits + cciByte;

        for (var i = 0; i < text.Length; i++)
        {
            for (var s = 0; s < StateCount; s++)
                cur[s] = Unreachable;

            var c = text[i];
            var isNumeric = CharacterSets.IsNumeric(c);
            var isAlnum = CharacterSets.IsAlphanumeric(c);
            var byteBits = 8 * ByteCost(text, i, charset);
            var parentBase = i * StateCount;

            for (var from = 0; from < StateCount; from++)
            {
                var basis = prev[from];
                if (basis >= Unreachable)
                    continue;

                if (isNumeric)
                {
                    int target, cost;
                    if (from <= StateNumeric2)
                    {
                        // Continue the run: the first digit of a group costs 4 bits, the next two 3 each.
                        cost = basis + (from == StateNumeric0 ? 4 : 3);
                        target = from == StateNumeric2 ? StateNumeric0 : from + 1;
                    }
                    else
                    {
                        cost = basis + openNumeric + 4;
                        target = StateNumeric1;
                    }
                    Relax(cur, parents, parentBase, target, cost, from, track);
                }

                if (isAlnum && allowAlnum)
                {
                    int target, cost;
                    if (from is StateAlnum0 or StateAlnum1)
                    {
                        // 11 bits per pair: 6 for the first character of a pair, 5 for the second.
                        cost = basis + (from == StateAlnum0 ? 6 : 5);
                        target = from == StateAlnum0 ? StateAlnum1 : StateAlnum0;
                    }
                    else
                    {
                        cost = basis + openAlnum + 6;
                        target = StateAlnum1;
                    }
                    Relax(cur, parents, parentBase, target, cost, from, track);
                }

                if (allowByte)
                {
                    // Byte mode encodes every character, so with Byte allowed a plan
                    // always exists; without it, a character outside the allowed
                    // alphabets leaves every state unreachable.
                    var cost = from == StateByte ? basis + byteBits : basis + openByte + byteBits;
                    Relax(cur, parents, parentBase, StateByte, cost, from, track);
                }
            }

            cur.CopyTo(prev);
        }

        var best = Unreachable;
        finalState = StateByte;
        for (var s = 0; s <= StateByte; s++)
        {
            if (prev[s] < best)
            {
                best = prev[s];
                finalState = s;
            }
        }
        return best;
    }

    private static void Relax(Span<int> cur, Span<byte> parents, int parentBase, int target, int cost, int from, bool track)
    {
        if (cost >= cur[target])
            return;
        cur[target] = cost;
        if (track)
            parents[parentBase + target] = (byte)from;
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
            if (segment.ModeIndex == 2 && segment.Start > 0 && text[segment.Start] == '\uFEFF')
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
