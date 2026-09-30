using FeatherQR.Internals;
using static FeatherQR.Tests.KanjiStreamReference;

namespace FeatherQR.Tests;

/// <summary>
/// The segmentation program for a Kanji-eligible text as a definition: an all-states relaxation over eight states (Numeric with 0, 1 or 2 digits in the open group, Alphanumeric with 0 or 1 in the open pair, Byte, the start, Kanji), for a text whose characters are ASCII or have an encoder cell.
/// </summary>
/// <remarks>
/// Nothing here comes from the library. An ASCII character is classed from the alphanumeric alphabet as ISO/IEC 18004 lists it and reaches Numeric, Alphanumeric or Byte at one byte; a character outside ASCII reaches only Kanji, since Byte could carry it only under an ECI header, which a Kanji plan never has.
/// Every width is passed in. States are relaxed in order and a later predecessor replaces an earlier one only when strictly cheaper, the tie rule the library documents, so the plan reconstructed here is the plan the library has to write.
/// </remarks>
internal static class KanjiPlanReference
{
    public const int StateNumeric0 = 0;
    public const int StateNumeric1 = 1;
    public const int StateNumeric2 = 2;
    public const int StateAlnum0 = 3;
    public const int StateAlnum1 = 4;
    public const int StateByte = 5;
    public const int StateStart = 6;
    public const int StateKanji = 7;
    public const int States = 8;
    public const int Unreachable = int.MaxValue / 4;

    /// <summary>Count indicator widths and the mode indicator width of one version.</summary>
    public readonly record struct Widths(int ModeIndicator, int Numeric, int Alnum, int Byte, int Kanji)
    {
        public int Narrowest => Math.Min(Math.Min(Numeric, Alnum), Math.Min(Byte, Kanji));
        public override string ToString() => $"mi {ModeIndicator} ({Numeric}, {Alnum}, {Byte}, {Kanji})";
    }

    public static Widths StandardQr(int version) => new(4, StandardQrCountBits('N', version), StandardQrCountBits('A', version), StandardQrCountBits('B', version), StandardQrCountBits('K', version));

    public static Widths MicroQr(int version) => new(version - 1, MicroQrCountBits('N', version), MicroQrCountBits('A', version), MicroQrCountBits('B', version), MicroQrCountBits('K', version));

    public static Widths RmQr(int versionIndex) => new(3, RmQrCountBits('N', versionIndex), RmQrCountBits('A', versionIndex), RmQrCountBits('B', versionIndex), RmQrCountBits('K', versionIndex));

    /// <summary>The minimal plan's bits, and its last state; <paramref name="parents"/>, when given, receives one predecessor per character and state.</summary>
    public static int Cost(string text, Widths widths, byte[]? parents, out int finalState)
    {
        var prev = new int[States];
        Array.Fill(prev, Unreachable);
        prev[StateStart] = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var cur = new int[States];
            Array.Fill(cur, Unreachable);

            var c = text[i];
            var ascii = c < 0x80;
            var digit = c is >= '0' and <= '9';
            var alnum = IsAlnum(c);

            for (var from = 0; from < States; from++)
            {
                var basis = prev[from];
                if (basis >= Unreachable)
                    continue;

                if (digit)
                {
                    var inGroup = from <= StateNumeric2;
                    var target = inGroup ? (from == StateNumeric2 ? StateNumeric0 : from + 1) : StateNumeric1;
                    var cost = inGroup ? basis + (from == StateNumeric0 ? 4 : 3) : basis + widths.ModeIndicator + widths.Numeric + 4;
                    Relax(cur, parents, i, target, cost, from);
                }

                if (alnum)
                {
                    var inPair = from is StateAlnum0 or StateAlnum1;
                    var target = inPair ? (from == StateAlnum0 ? StateAlnum1 : StateAlnum0) : StateAlnum1;
                    var cost = inPair ? basis + (from == StateAlnum0 ? 6 : 5) : basis + widths.ModeIndicator + widths.Alnum + 6;
                    Relax(cur, parents, i, target, cost, from);
                }

                if (ascii)
                {
                    var cost = from == StateByte ? basis + 8 : basis + widths.ModeIndicator + widths.Byte + 8;
                    Relax(cur, parents, i, StateByte, cost, from);
                }
                else
                {
                    var cost = from == StateKanji ? basis + 13 : basis + widths.ModeIndicator + widths.Kanji + 13;
                    Relax(cur, parents, i, StateKanji, cost, from);
                }
            }

            prev = cur;
        }

        var best = Unreachable;
        finalState = StateByte;
        for (var s = 0; s < States; s++)
        {
            if (s != StateStart && prev[s] < best)
            {
                best = prev[s];
                finalState = s;
            }
        }
        return best;
    }

    private static void Relax(int[] cur, byte[]? parents, int i, int target, int cost, int from)
    {
        if (cost >= cur[target])
            return;
        cur[target] = cost;
        if (parents is not null)
            parents[i * States + target] = (byte)from;
    }

    public static int ModeOf(int state) => state switch
    {
        <= StateNumeric2 => 0,
        <= StateAlnum1 => 1,
        StateByte => 2,
        StateKanji => 3,
        _ => -1,
    };

    /// <summary>The minimal plan, walked back character by character over the reference's own table.</summary>
    public static ModeSegment[] Plan(string text, Widths widths)
    {
        var parents = new byte[text.Length * States];
        Cost(text, widths, parents, out var state);

        var segments = new List<ModeSegment>();
        var end = text.Length;
        for (var i = text.Length - 1; i >= 0; i--)
        {
            var parent = parents[i * States + state];
            if (parent == StateStart || ModeOf(parent) != ModeOf(state))
            {
                segments.Add(new ModeSegment(ModeOf(state), i, end - i, 0));
                end = i;
            }
            state = parent;
        }

        segments.Reverse();
        return segments.ToArray();
    }

    /// <summary>The minimal plan as runs <see cref="KanjiStreamReference"/> writes.</summary>
    public static Run[] Runs(string text, Widths widths)
        => Plan(text, widths).Select(s => new Run("NABK"[s.ModeIndex], text.Substring(s.Start, s.Length))).ToArray();

    public static bool IsAlnum(char c)
        => (c is >= '0' and <= '9') || (c is >= 'A' and <= 'Z') || c is ' ' or '$' or '%' or '*' or '+' or '-' or '.' or '/' or ':';
}
