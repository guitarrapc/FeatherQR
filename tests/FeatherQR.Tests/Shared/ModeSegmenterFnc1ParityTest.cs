using System.Text;
using FeatherQR.Internals;
using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// The FNC1 segmentation program against an all-states relaxation of its nine states (specs/standardqr-encoder.md,
/// FNC1): the cost, the state it ends in, and the plan the table walks back to, which the cost alone does not pin.
/// The rule of the reference is the program's: a separator is an Alphanumeric character that lands in a separator
/// state, a literal <c>%</c> two of them, and from a separator state a separator or a literal <c>%</c> can only open
/// a run. Among equal costs a continued run beats an opened one, and the lowest state wins among the rest.
/// </summary>
public class ModeSegmenterFnc1ParityTest
{
    private const int StateNumeric0 = 0;
    private const int StateNumeric1 = 1;
    private const int StateNumeric2 = 2;
    private const int StateAlnum0 = 3;
    private const int StateAlnum1 = 4;
    private const int StateSeparator0 = 5;
    private const int StateSeparator1 = 6;
    private const int StateByte = 7;
    private const int StateStart = 8;
    private const int States = 9;
    private const int Unreachable = int.MaxValue / 4;
    private const byte Opened = 0x80;
    private const char Separator = '\u001D';

    // U+FEFF, spelled out: as a literal it is invisible in the source.
    private static readonly string Bom = ((char)0xFEFF).ToString();

    /// <summary>Standard QR's three count indicator bands behind its 4-bit mode indicator, and two rMQR shapes behind its 3-bit one.</summary>
    public static IEnumerable<(int ModeIndicatorBits, int CciNumeric, int CciAlnum, int CciByte)> Widths() =>
    [
        (4, 10, 9, 8),
        (4, 12, 11, 16),
        (4, 14, 13, 16),
        (3, 4, 3, 3),
        (3, 9, 8, 8),
    ];

    public static IEnumerable<(string Name, string Text)> Corpus()
    {
        var fixed_ = new (string, string)[]
        {
            ("empty", ""),
            ("digit", "7"),
            ("letter", "A"),
            ("byte", "z"),
            ("separator", "\u001D"),
            ("percent", "%"),
            ("two-percents", "%%"),
            ("separator-pair", "\u001D\u001D"),
            ("separator-then-percent", "\u001D%"),
            ("percent-then-separator", "%\u001D"),
            ("bom", Bom),
            ("pair", "😀"),
            ("digits", "0123456789"),
            ("alnum", "HELLO WORLD $%*+-./:"),
            ("iso-first-position", "01049123451234591597033130128\u001D10ABC123"),
            ("iso-second-position", "AB%\u001DCD"),
            ("retail", "0109520123456788" + "17251231" + "10ABC123"),
            ("logistics", "00195201234567891232" + "0209520123456788" + "11250504" + "3725\u001D" + "10ABC123"),
            ("weighed", "0109520123456788" + "3103000195" + "39220299\u001D" + "17251231"),
            ("pharma", "0109520123456788" + "17251231" + "10ABC123\u001D" + "21SN123456789"),
            ("separators-between-digits", "12\u001D34\u001D56\u001D78"),
            ("separators-between-letters", "AB\u001DCD\u001DEF"),
            ("percents-in-digits", "12%34%56"),
            ("separator-pairs-in-alnum", "ABC\u001D\u001DDEF\u001D%GHI%\u001DJKL"),
            ("lone-high-at-end", "12\u001Dab\ud83d"),
            ("lone-low-at-start", "\ude00ab\u001D12"),
            ("bom-first-then-pair", Bom + "😀\u001D%9"),
            ("three-byte-runs", "日本語日本語日本語\u001D日本語%日本語123"),
            ("ties-1A", "1A1A1A1A"),
            ("ties-A1", "A1A1A1A1"),
            ("byte-digit-alternation", "a1a1a1a1a1a1"),
            ("ties-separator", "1\u001D1\u001D1\u001D1"),
            ("digits-then-byte", "1234567890123456" + new string('z', 40)),
            ("byte-then-digits", new string('z', 40) + "1234567890123456"),
            ("bytes-around-separator", "abc\u001Ddef"),
            ("bom-inside-runs", "x" + Bom + "y 123 " + Bom + "456"),
            ("bom-after-separator", "AB\u001D" + Bom + "CD"),
            ("runs-with-pairs", "abc😀123\ud83dABC\ude00xyz"),
            ("japanese-with-separators", "日本語\u001D123\u001Dカタカナ%45"),
            ("long-element-string", "0109520123456788" + string.Join("\u001D", Enumerable.Range(91, 9).Select(ai => ai + new string('A', 90)))),
        };
        foreach (var entry in fixed_)
            yield return entry;

        // Seeded random strings, GS1-shaped (digits, the alphanumeric alphabet, separators, percents, a few bytes)
        // and then with characters of two and three UTF-8 bytes, pairs and marks among them.
        var gs1 = "0123456789012345678ABCDEFGHIJKLMNOPQRSTUVWXYZ \u001D\u001D\u001D%%abcxyz-./+*$:";
        var utf8 = "0123456789ABC\u001D%abcあ日😀é" + Bom;
        var random = new Random(20261017);
        for (var k = 0; k < 48; k++)
        {
            var alphabet = k < 32 ? gs1 : utf8;
            var length = random.Next(1, k % 4 == 3 ? 400 : 60);
            var sb = new StringBuilder(length);
            for (var i = 0; i < length; i++)
                sb.Append(alphabet[random.Next(alphabet.Length)]);
            yield return ($"random-{k}", sb.ToString());
        }
    }

    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task ReconstructedPlan_MatchesTheAllStatesReference(string name, string text)
    {
        foreach (var charset in new[] { EciMode.Default, EciMode.Iso8859_1, EciMode.Utf8 })
        {
            foreach (var (modeIndicatorBits, cciNumeric, cciAlnum, cciByte) in Widths())
            {
                var because = $"{name} in {charset} at ({modeIndicatorBits}: {cciNumeric}, {cciAlnum}, {cciByte})";

                var expectedParents = new byte[text.Length * States];
                var expected = Reference(text, charset, modeIndicatorBits, cciNumeric, cciAlnum, cciByte, expectedParents, out var expectedState);

                var parents = new byte[text.Length * ModeSegmenter.Fnc1ParentBytesPerChar];
                var actual = ModeSegmenter.ComputeCostsFnc1(text, charset, modeIndicatorBits, cciNumeric, cciAlnum, cciByte, parents, out var actualState);
                await Assert.That(actual).IsEqualTo(expected).Because(because);
                await Assert.That(actualState).IsEqualTo(expectedState).Because(because);

                // The cost-only loop is a different body. It must say the same thing.
                var costOnly = ModeSegmenter.ComputeCostsFnc1(text, charset, modeIndicatorBits, cciNumeric, cciAlnum, cciByte, default, out var costOnlyState);
                await Assert.That(costOnly).IsEqualTo(expected).Because(because);
                await Assert.That(costOnlyState).IsEqualTo(expectedState).Because(because);

                if (text.Length == 0)
                    continue;

                var expectedPlan = new ModeSegment[text.Length];
                var actualPlan = new ModeSegment[text.Length];
                var expectedCount = ReferenceReconstruct(text, expectedParents, expectedState, expectedPlan);
                await Assert.That(ModeSegmenter.ReconstructFnc1(text, parents, actualState, actualPlan, out var actualCount)).IsTrue().Because(because);
                await Assert.That(actualCount).IsEqualTo(expectedCount).Because(because);
                await Assert.That(actualPlan.AsSpan(0, actualCount).ToArray()).IsEquivalentTo(expectedPlan.AsSpan(0, expectedCount).ToArray(), CollectionOrdering.Matching).Because(because);
            }
        }
    }

    /// <summary>
    /// The all-states relaxation. Every state of the character before is relaxed into every state the character can
    /// reach from it, continuing the run it is in or opening a new one, and a target keeps the first of its equal
    /// costs, a continued run before an opened one and the lower state among each kind.
    /// </summary>
    private static int Reference(string text, EciMode charset, int modeIndicatorBits, int cciNumeric, int cciAlnum, int cciByte, byte[] parents, out int finalState)
    {
        var prev = new int[States];
        for (var s = 0; s < States; s++)
            prev[s] = Unreachable;
        prev[StateStart] = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var cur = new int[States];
            var curOpened = new bool[States];
            for (var s = 0; s < States; s++)
                cur[s] = Unreachable;

            var c = text[i];
            var isNumeric = c is >= '0' and <= '9';
            var isSeparator = c == Separator;
            var isPercent = c == '%';
            var isAlnum = !isSeparator && !isPercent && IsAlnum(c);
            var byteBits = 8 * ByteCost(text, i, charset);

            for (var from = 0; from < States; from++)
            {
                var basis = prev[from];
                if (basis >= Unreachable)
                    continue;

                if (isNumeric)
                {
                    var continues = from <= StateNumeric2;
                    var target = continues ? (from == StateNumeric2 ? StateNumeric0 : from + 1) : StateNumeric1;
                    var cost = continues ? basis + (from == StateNumeric0 ? 4 : 3) : basis + modeIndicatorBits + cciNumeric + 4;
                    Relax(cur, curOpened, parents, i, target, cost, from, opened: !continues);
                }

                if (isSeparator)
                {
                    // Continued from a plain Alphanumeric state only. From a separator state it opens a run.
                    var continues = from is StateAlnum0 or StateAlnum1;
                    var target = from == StateAlnum1 ? StateSeparator0 : StateSeparator1;
                    var cost = continues ? basis + (from == StateAlnum0 ? 6 : 5) : basis + modeIndicatorBits + cciAlnum + 6;
                    Relax(cur, curOpened, parents, i, target, cost, from, opened: !continues);
                }
                else if (isPercent)
                {
                    // Two Alphanumeric characters, 11 bits whichever half of a pair they start at.
                    var continues = from is StateAlnum0 or StateAlnum1;
                    var target = continues ? from : StateAlnum0;
                    var cost = continues ? basis + 11 : basis + modeIndicatorBits + cciAlnum + 11;
                    Relax(cur, curOpened, parents, i, target, cost, from, opened: !continues);
                }
                else if (isAlnum)
                {
                    var continues = from is StateAlnum0 or StateAlnum1 or StateSeparator0 or StateSeparator1;
                    var target = from is StateAlnum1 or StateSeparator1 ? StateAlnum0 : StateAlnum1;
                    var cost = continues ? basis + (from is StateAlnum0 or StateSeparator0 ? 6 : 5) : basis + modeIndicatorBits + cciAlnum + 6;
                    Relax(cur, curOpened, parents, i, target, cost, from, opened: !continues);
                }

                {
                    // No Byte run opens at a U+FEFF past the first character: the decoder would take it for a byte order mark.
                    if (from != StateByte && i > 0 && c == (char)0xFEFF)
                        continue;
                    var cost = from == StateByte ? basis + byteBits : basis + modeIndicatorBits + cciByte + byteBits;
                    Relax(cur, curOpened, parents, i, StateByte, cost, from, opened: from != StateByte);
                }
            }

            prev = cur;
        }

        if (text.Length == 0)
        {
            finalState = StateStart;
            return 0;
        }

        var best = Unreachable;
        finalState = StateByte;
        for (var s = 0; s < StateStart; s++)
        {
            if (prev[s] < best)
            {
                best = prev[s];
                finalState = s;
            }
        }
        return best;
    }

    private static void Relax(int[] cur, bool[] curOpened, byte[] parents, int i, int target, int cost, int from, bool opened)
    {
        if (cost > cur[target])
            return;
        if (cost == cur[target] && (opened || !curOpened[target]))
            return;
        cur[target] = cost;
        curOpened[target] = opened;
        parents[i * States + target] = (byte)(from | (opened ? Opened : 0));
    }

    /// <summary>The runs of the reference's path, oldest first, with the unit count an Alphanumeric run carries under FNC1.</summary>
    private static int ReferenceReconstruct(string text, byte[] parents, int finalState, ModeSegment[] plan)
    {
        var count = 0;
        var state = finalState;
        var end = text.Length;
        for (var i = text.Length - 1; i >= 0; i--)
        {
            var entry = parents[i * States + state];
            var from = entry & ~Opened;
            if ((entry & Opened) != 0 || from == StateStart)
            {
                var modeIndex = state <= StateNumeric2 ? 0 : state <= StateSeparator1 ? 1 : 2;
                var units = end - i;
                if (modeIndex == 1)
                {
                    foreach (var c in text.AsSpan(i, end - i))
                    {
                        if (c == '%')
                            units++;
                    }
                }
                plan[count++] = new ModeSegment(modeIndex, i, end - i, units);
                end = i;
            }
            state = from;
        }
        Array.Reverse(plan, 0, count);
        return count;
    }

    private static bool IsAlnum(char c) => c is (>= '0' and <= '9') or (>= 'A' and <= 'Z') or ' ' or '$' or '%' or '*' or '+' or '-' or '.' or '/' or ':';

    /// <summary>What <see cref="Encoding.UTF8"/> would write for the character at <paramref name="index"/>, counted by the rules the program uses.</summary>
    private static int ByteCost(string text, int index, EciMode charset)
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
