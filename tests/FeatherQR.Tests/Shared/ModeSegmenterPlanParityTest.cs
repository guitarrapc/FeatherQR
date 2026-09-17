using System.Text;
using FeatherQR.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// The register-held segmentation program against the all-states relaxation it replaced, on the
/// parts the cost alone does not pin: the predecessor chosen on a tie (which decides the plan the
/// writer emits), the prefix walk's stopping point at every budget it can turn on, and the
/// character class table behind every loop.
/// </summary>
public class ModeSegmenterPlanParityTest
{
    private const int ModeIndicatorBits = 4;

    private const int StateNumeric0 = 0;
    private const int StateNumeric1 = 1;
    private const int StateNumeric2 = 2;
    private const int StateAlnum0 = 3;
    private const int StateAlnum1 = 4;
    private const int StateByte = 5;
    private const int StateStart = 6;
    private const int States = 7;
    private const int Unreachable = int.MaxValue / 4;

    // U+FEFF, spelled out: as a literal it is invisible in the source.
    private static readonly string Bom = ((char)0xFEFF).ToString();

    public static IEnumerable<(int CciNumeric, int CciAlnum, int CciByte)> Widths() =>
    [
        (10, 9, 8),
        (12, 11, 16),
        (14, 13, 16),
        (3, 0, 0),   // Micro QR M1 shape
        (4, 3, 4),   // Micro QR M2 shape
    ];

    public static IEnumerable<(bool AllowAlnum, bool AllowByte)> Modes() =>
    [
        (true, true),
        (false, true),
        (true, false),
        (false, false),
    ];

    public static IEnumerable<(string Name, string Text)> Corpus()
    {
        var fixed_ = new (string, string)[]
        {
            ("empty", ""),
            ("digit", "7"),
            ("letter", "A"),
            ("byte", "z"),
            ("pair", "😀"),
            ("lone-high", "\ud83d"),
            ("lone-low", "\ude00"),
            ("bom", Bom),
            ("digits", "0123456789"),
            ("alnum", "HELLO WORLD $%*+-./:"),
            ("mixed-runs", "order 20260915 item 0000123456 qty 42 "),
            ("mixed-long", string.Concat(Enumerable.Repeat("order 20260915 item 0000123456 qty 42 ", 80))),
            ("prose", string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 12))),
            ("japanese", string.Concat(Enumerable.Repeat("こんにちは世界、QRコードの分割テストです。", 20))),
            ("ties-1A", "1A1A1A1A"),
            ("ties-A1", "A1A1A1A1"),
            ("ties-a1", "a1a1a1a1a1a1"),
            ("digits-then-byte", "1234567890123456" + new string('z', 40)),
            ("byte-then-digits", new string('z', 40) + "1234567890123456"),
            ("runs-with-pairs", "abc😀123\ud83dABC\ude00xyz"),
            ("bom-inside-runs", "x" + Bom + "y 123 " + Bom + "456"),
            ("pair-at-run-edges", "😀A😀1😀"),
            ("latin1", "Crème brûlée à la carte, jalapeño, naïve café."),
        };
        foreach (var entry in fixed_)
            yield return entry;

        // Seeded random mixes of every class, so the tie-break is exercised on shapes nobody wrote by hand.
        var alphabet = "0123456789012345ABCDEF abcdefgh.,-:éあ😀" + Bom;
        var random = new Random(20260917);
        for (var k = 0; k < 24; k++)
        {
            var length = random.Next(1, k < 12 ? 24 : 300);
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
            foreach (var (cciNumeric, cciAlnum, cciByte) in Widths())
            {
                foreach (var (allowAlnum, allowByte) in Modes())
                {
                    var because = $"{name} in {charset} at ({cciNumeric}, {cciAlnum}, {cciByte}) alnum={allowAlnum} byte={allowByte}";

                    var expectedParents = new byte[text.Length * States];
                    var expected = Reference(text, charset, cciNumeric, cciAlnum, cciByte, allowAlnum, allowByte, expectedParents, out var expectedState);

                    var parents = new byte[text.Length * ModeSegmenter.ParentBytesPerChar];
                    var actual = ModeSegmenter.ComputeCosts(text, charset, ModeIndicatorBits, cciNumeric, cciAlnum, cciByte, parents, out var actualState, allowAlnum, allowByte);
                    await Assert.That(actual).IsEqualTo(expected).Because(because);
                    await Assert.That(actualState).IsEqualTo(expectedState).Because(because);

                    // The cost-only loop is a different body; it must say the same thing.
                    var costOnly = ModeSegmenter.ComputeCosts(text, charset, ModeIndicatorBits, cciNumeric, cciAlnum, cciByte, default, out var costOnlyState, allowAlnum, allowByte);
                    await Assert.That(costOnly).IsEqualTo(expected).Because(because);
                    await Assert.That(costOnlyState).IsEqualTo(expectedState).Because(because);

                    if (expected >= Unreachable || text.Length == 0)
                        continue;

                    var expectedPlan = new ModeSegment[text.Length];
                    var actualPlan = new ModeSegment[text.Length];
                    var expectedCount = ReferenceReconstruct(text.Length, expectedParents, expectedState, expectedPlan);
                    await Assert.That(ModeSegmenter.Reconstruct(text, parents, actualState, actualPlan, out var actualCount)).IsTrue().Because(because);
                    await Assert.That(actualCount).IsEqualTo(expectedCount).Because(because);
                    for (var i = 0; i < expectedCount; i++)
                    {
                        await Assert.That(actualPlan[i].ModeIndex).IsEqualTo(expectedPlan[i].ModeIndex).Because($"{because}, run {i}");
                        await Assert.That(actualPlan[i].Start).IsEqualTo(expectedPlan[i].Start).Because($"{because}, run {i}");
                        await Assert.That(actualPlan[i].Length).IsEqualTo(expectedPlan[i].Length).Because($"{because}, run {i}");
                    }
                }
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task LongestPrefixWithinBudget_StopsWhereTheReferenceStops(string name, string text)
    {
        foreach (var charset in new[] { EciMode.Default, EciMode.Utf8 })
        {
            foreach (var (cciNumeric, cciAlnum, cciByte) in Widths().Take(3))
            {
                // Every budget the answer can turn on: each prefix's own cost and its neighbours.
                var prefixCosts = ReferencePrefixCosts(text, charset, cciNumeric, cciAlnum, cciByte);
                var budgets = new SortedSet<int> { 0, 1, 4, 5 };
                for (var length = 1; length <= text.Length; length++)
                {
                    budgets.Add(prefixCosts[length] - 1);
                    budgets.Add(prefixCosts[length]);
                    budgets.Add(prefixCosts[length] + 1);
                }

                foreach (var budget in budgets)
                {
                    var expected = ReferencePrefix(text, prefixCosts, budget);
                    var actual = ModeSegmenter.LongestPrefixWithinBudget(text, charset, ModeIndicatorBits, cciNumeric, cciAlnum, cciByte, budget);
                    await Assert.That(actual).IsEqualTo(expected).Because($"{name} in {charset} at ({cciNumeric}, {cciAlnum}, {cciByte}) budget {budget}");
                }
            }
        }
    }

    [Test]
    public async Task CharacterClass_AgreesWithCharacterSetsForEveryChar()
    {
        // One character at the narrowest widths costs 18 as a digit, 19 as an alphanumeric and 20 as a byte,
        // so the class the program gave it is readable from the cost.
        for (var code = 0; code <= 0xFFFF; code++)
        {
            var c = (char)code;
            if (char.IsSurrogate(c))
                continue;
            var expected = CharacterSets.IsNumeric(c) ? 18 : CharacterSets.IsAlphanumeric(c) ? 19 : 20;
            var actual = ModeSegmenter.ComputeCosts(c.ToString(), EciMode.Default, ModeIndicatorBits, 10, 9, 8, default, out _);
            await Assert.That(actual).IsEqualTo(expected).Because($"U+{code:X4}");
        }
    }

    /// <summary>The prefix walk as a definition: the longest prefix the reference prices within the budget, never inside a pair.</summary>
    private static int ReferencePrefix(string text, int[] prefixCosts, int budget)
    {
        var fitted = 0;
        for (var length = 1; length <= text.Length; length++)
        {
            if (length < text.Length && char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length]))
                continue;
            if (prefixCosts[length] > budget)
                break;
            fitted = length;
        }
        return fitted;
    }

    /// <summary>The reference's cost of every prefix, indexed by prefix length, from one pass: the optimum of each prefix is the state after its last character.</summary>
    private static int[] ReferencePrefixCosts(string text, EciMode charset, int cciNumeric, int cciAlnum, int cciByte)
    {
        var costs = new int[text.Length + 1];
        var prev = new int[States];
        for (var s = 0; s < States; s++)
            prev[s] = Unreachable;
        prev[StateStart] = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var cur = new int[States];
            for (var s = 0; s < States; s++)
                cur[s] = Unreachable;

            var c = text[i];
            var isNumeric = c is >= '0' and <= '9';
            var isAlnum = IsAlnum(c);
            var byteBits = 8 * ByteCost(text, i, charset);

            for (var from = 0; from < States; from++)
            {
                var basis = prev[from];
                if (basis >= Unreachable)
                    continue;

                if (isNumeric)
                {
                    var target = from <= StateNumeric2 ? (from == StateNumeric2 ? StateNumeric0 : from + 1) : StateNumeric1;
                    var cost = from <= StateNumeric2 ? basis + (from == StateNumeric0 ? 4 : 3) : basis + ModeIndicatorBits + cciNumeric + 4;
                    Relax(cur, [], i, target, cost, from, false);
                }

                if (isAlnum)
                {
                    var pair = from is StateAlnum0 or StateAlnum1;
                    var target = pair ? (from == StateAlnum0 ? StateAlnum1 : StateAlnum0) : StateAlnum1;
                    var cost = pair ? basis + (from == StateAlnum0 ? 6 : 5) : basis + ModeIndicatorBits + cciAlnum + 6;
                    Relax(cur, [], i, target, cost, from, false);
                }

                {
                    var cost = from == StateByte ? basis + byteBits : basis + ModeIndicatorBits + cciByte + byteBits;
                    Relax(cur, [], i, StateByte, cost, from, false);
                }
            }

            prev = cur;
            var best = Unreachable;
            for (var s = 0; s <= StateByte; s++)
                best = Math.Min(best, prev[s]);
            costs[i + 1] = best;
        }

        return costs;
    }

    /// <summary>
    /// The all-states relaxation with its predecessor rule: states are relaxed in order and a later one replaces an earlier one only when strictly cheaper.
    /// </summary>
    private static int Reference(string text, EciMode charset, int cciNumeric, int cciAlnum, int cciByte, bool allowAlnum, bool allowByte, byte[] parents, out int finalState)
    {
        var track = parents.Length > 0;
        var prev = new int[States];
        for (var s = 0; s < States; s++)
            prev[s] = Unreachable;
        prev[StateStart] = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var cur = new int[States];
            for (var s = 0; s < States; s++)
                cur[s] = Unreachable;

            var c = text[i];
            var isNumeric = c is >= '0' and <= '9';
            var isAlnum = IsAlnum(c);
            var byteBits = 8 * ByteCost(text, i, charset);

            for (var from = 0; from < States; from++)
            {
                var basis = prev[from];
                if (basis >= Unreachable)
                    continue;

                if (isNumeric)
                {
                    var target = from <= StateNumeric2 ? (from == StateNumeric2 ? StateNumeric0 : from + 1) : StateNumeric1;
                    var cost = from <= StateNumeric2 ? basis + (from == StateNumeric0 ? 4 : 3) : basis + ModeIndicatorBits + cciNumeric + 4;
                    Relax(cur, parents, i, target, cost, from, track);
                }

                if (isAlnum && allowAlnum)
                {
                    var pair = from is StateAlnum0 or StateAlnum1;
                    var target = pair ? (from == StateAlnum0 ? StateAlnum1 : StateAlnum0) : StateAlnum1;
                    var cost = pair ? basis + (from == StateAlnum0 ? 6 : 5) : basis + ModeIndicatorBits + cciAlnum + 6;
                    Relax(cur, parents, i, target, cost, from, track);
                }

                if (allowByte)
                {
                    var cost = from == StateByte ? basis + byteBits : basis + ModeIndicatorBits + cciByte + byteBits;
                    Relax(cur, parents, i, StateByte, cost, from, track);
                }
            }

            prev = cur;
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

    private static void Relax(int[] cur, byte[] parents, int i, int target, int cost, int from, bool track)
    {
        if (cost >= cur[target])
            return;
        cur[target] = cost;
        if (track)
            parents[i * States + target] = (byte)from;
    }

    /// <summary>The reference's own walk back over its table of one predecessor per character and state, character by character.</summary>
    private static int ReferenceReconstruct(int length, byte[] parents, int finalState, ModeSegment[] segments)
    {
        static int ModeOf(int state) => state <= StateNumeric2 ? 0 : state <= StateAlnum1 ? 1 : state == StateByte ? 2 : -1;

        var state = finalState;
        var end = length;
        var count = 0;
        for (var i = length - 1; i >= 0; i--)
        {
            var parent = parents[i * States + state];
            if (parent == StateStart || ModeOf(parent) != ModeOf(state))
            {
                segments[count++] = new ModeSegment(ModeOf(state), i, end - i, 0);
                end = i;
            }
            state = parent;
        }

        Array.Reverse(segments, 0, count);
        return count;
    }

    private static bool IsAlnum(char c)
        => (c is >= '0' and <= '9') || (c is >= 'A' and <= 'Z') || c is ' ' or '$' or '%' or '*' or '+' or '-' or '.' or '/' or ':';

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
