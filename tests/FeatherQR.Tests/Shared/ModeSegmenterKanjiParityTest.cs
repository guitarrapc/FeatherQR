using System.Text;
using FeatherQR.Internals;
using static FeatherQR.Tests.KanjiPlanReference;

namespace FeatherQR.Tests;

/// <summary>
/// The eligible-path program (the eighth state, Kanji) against <see cref="KanjiPlanReference"/>, an all-states relaxation that shares only the cost model's numbers with it:
/// the cost, the state it ends in, the plan the predecessor table walks back to (which decides the stream the writer emits), the runs counted per mode, and the lower bound the planners screen candidates with.
/// </summary>
/// <remarks>
/// The seven-state program is untouched by the eighth state and keeps its own suites (<c>ModeSegmenterPlanParityTest</c>, <c>ModeSegmenterByteRunParityTest</c>, <c>ModeSegmenterCostRunParityTest</c>, <c>ModeSegmenterLaneParityTest</c>).
/// </remarks>
public class ModeSegmenterKanjiParityTest
{
    /// <summary>Every width shape the three planners pass: Standard QR's three bands, Micro QR M3 and M4, and rMQR's narrowest, widest and one in between.</summary>
    private static IEnumerable<Widths> AllWidths() =>
    [
        new(4, 10, 9, 8, 8),
        new(4, 12, 11, 16, 10),
        new(4, 14, 13, 16, 12),
        new(2, 5, 4, 4, 3),
        new(3, 6, 5, 5, 4),
        new(3, 4, 3, 3, 2),
        new(3, 7, 7, 6, 5),
        new(3, 9, 8, 8, 7),
    ];

    public static IEnumerable<(string Name, string Text)> Corpus()
    {
        var fixed_ = new (string, string)[]
        {
            ("kana-ascii", "QRコード"),
            ("kanji-digits", "日本7777"),
            ("kanji-digits-x10", string.Concat(Enumerable.Repeat("日本7777", 10))),
            ("kanji-digits-x80", string.Concat(Enumerable.Repeat("日本7777", 80))),
            ("tower", "東京タワー333m"),
            ("space", "日本 語"),
            ("dosage", "1日2回3錠"),
            ("alternating-alnum", string.Concat(Enumerable.Repeat("A日B本C語", 5))),
            ("alternating-byte", string.Concat(Enumerable.Repeat("a日b本c", 5))),
            ("url", "https://例え.jp/パス?q=123"),
            ("ties-digit-kana", "1あ1あ1あ"),
            ("ties-kana-digit", "あ1あ1あ1"),
            ("ties-letter-kana", "Aあ Aあ Aあ"),
            ("cells-first", "日本語abc"),
            ("cells-last", "abc日本語"),
            ("digits-around", "12345日本67890"),
            ("alnum-around", "QR CODE 日本 QR CODE"),
            ("only-cells", "こんにちは"),
            ("one-cell", "日"),
            ("only-ascii", "abc 123 XYZ"),
            ("one-digit", "7"),
            ("cyrillic", "Привет123"),
            ("greek", "ΑΒΓ-42"),
            ("box", "─│┌┐ABC"),
            ("latin1-cells", "×÷§12"),
            ("fullwidth", "ＱＲ１２３QR123"),
            ("sentence", string.Concat(Enumerable.Repeat("こんにちは世界、QRコードの分割テストです。2026年9月30日 ", 6))),
        };
        foreach (var entry in fixed_)
            yield return entry;

        // Seeded mixes of every class, so ties are exercised on shapes nobody wrote by hand.
        const string alphabet = "0123456789012345ABCDEF abcdefgh.,-:あ日アЯΩ─×、";
        var random = new Random(20260930);
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
    public async Task Corpus_IsEligible()
    {
        foreach (var (name, text) in Corpus())
        {
            foreach (var c in text)
                await Assert.That(c < 0x80 || ShiftJisKanjiReverseTable.Lookup(c) >= 0).IsTrue().Because($"{name}: U+{(int)c:X4}");
        }
    }

    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task ComputeCostsKanji_MatchesTheEightStateReference(string name, string text)
    {
        foreach (var widths in AllWidths())
        {
            var because = $"{name} at {widths}";
            var expected = Cost(text, widths, null, out var expectedState);

            var parents = new byte[text.Length * ModeSegmenter.ParentBytesPerChar];
            var actual = ModeSegmenter.ComputeCostsKanji(text, widths.ModeIndicator, widths.Numeric, widths.Alnum, widths.Byte, widths.Kanji, parents, out var actualState);
            await Assert.That(actual).IsEqualTo(expected).Because(because);
            await Assert.That(actualState).IsEqualTo(expectedState).Because(because);

            // The cost-only loop is a different body; it must say the same thing.
            var costOnly = ModeSegmenter.ComputeCostsKanji(text, widths.ModeIndicator, widths.Numeric, widths.Alnum, widths.Byte, widths.Kanji, default, out var costOnlyState);
            await Assert.That(costOnly).IsEqualTo(expected).Because(because);
            await Assert.That(costOnlyState).IsEqualTo(expectedState).Because(because);

            var expectedPlan = Plan(text, widths);
            var actualPlan = new ModeSegment[text.Length];
            await Assert.That(ModeSegmenter.Reconstruct(text, parents, actualState, actualPlan, out var actualCount)).IsTrue().Because(because);
            await Assert.That(actualCount).IsEqualTo(expectedPlan.Length).Because(because);
            for (var i = 0; i < expectedPlan.Length; i++)
            {
                await Assert.That(actualPlan[i].ModeIndex).IsEqualTo(expectedPlan[i].ModeIndex).Because($"{because}, run {i}");
                await Assert.That(actualPlan[i].Start).IsEqualTo(expectedPlan[i].Start).Because($"{because}, run {i}");
                await Assert.That(actualPlan[i].Length).IsEqualTo(expectedPlan[i].Length).Because($"{because}, run {i}");
            }

            ModeSegmenter.CountRuns(parents, actualState, text.Length, out var runsNumeric, out var runsAlnum, out var runsByte, out var runsKanji);
            await Assert.That(runsNumeric).IsEqualTo(expectedPlan.Count(s => s.ModeIndex == 0)).Because(because);
            await Assert.That(runsAlnum).IsEqualTo(expectedPlan.Count(s => s.ModeIndex == 1)).Because(because);
            await Assert.That(runsByte).IsEqualTo(expectedPlan.Count(s => s.ModeIndex == 2)).Because(because);
            await Assert.That(runsKanji).IsEqualTo(expectedPlan.Count(s => s.ModeIndex == 3)).Because(because);
        }
    }

    /// <summary>
    /// The Structured Append walk's step for a Kanji set: the longest prefix whose minimal plan fits a budget, at every budget the answer can turn on (each prefix's own cost and its neighbours).
    /// The optimum of a prefix never falls as it grows, so the answer is the last prefix within budget.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task LongestPrefixWithinBudgetKanji_StopsWhereTheReferenceStops(string name, string text)
    {
        if (text.Length > 120)
            return; // the reference prices every prefix from scratch

        foreach (var widths in AllWidths())
        {
            var prefixCosts = PrefixCosts(text, widths);
            var budgets = new SortedSet<int> { 0, 1 };
            for (var length = 1; length <= text.Length; length++)
            {
                budgets.Add(prefixCosts[length] - 1);
                budgets.Add(prefixCosts[length]);
                budgets.Add(prefixCosts[length] + 1);
            }

            foreach (var budget in budgets)
            {
                var expected = 0;
                for (var length = 1; length <= text.Length && prefixCosts[length] <= budget; length++)
                    expected = length;
                var actual = ModeSegmenter.LongestPrefixWithinBudgetKanji(text, widths.ModeIndicator, widths.Numeric, widths.Alnum, widths.Byte, widths.Kanji, budget);
                await Assert.That(actual).IsEqualTo(expected).Because($"{name} at {widths}, budget {budget}");
            }
        }
    }

    /// <summary>
    /// The reconstructed plan is priced by the same model it came from: the sum of its headers and payloads is the program's cost.
    /// This is what the planners' re-measurement asserts, so a Kanji run priced at another width would fail there too.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task ReconstructedPlan_CostsWhatTheProgramSays(string name, string text)
    {
        foreach (var widths in AllWidths())
        {
            var parents = new byte[text.Length * ModeSegmenter.ParentBytesPerChar];
            var cost = ModeSegmenter.ComputeCostsKanji(text, widths.ModeIndicator, widths.Numeric, widths.Alnum, widths.Byte, widths.Kanji, parents, out var state);
            var plan = new ModeSegment[text.Length];
            ModeSegmenter.Reconstruct(text, parents, state, plan, out var count);
            ModeSegmenter.FillUnitCounts(text, EciMode.Default, plan.AsSpan(0, count));

            var measured = 0;
            foreach (var segment in plan.Take(count))
            {
                var width = segment.ModeIndex switch { 0 => widths.Numeric, 1 => widths.Alnum, 2 => widths.Byte, _ => widths.Kanji };
                measured += widths.ModeIndicator + width + ModeSegmenter.PayloadBits(segment.Mode, segment.UnitCount);
                await Assert.That(segment.UnitCount).IsEqualTo(segment.Length).Because($"{name}: every unit of an eligible plan is one character");
            }
            await Assert.That(measured).IsEqualTo(cost).Because($"{name} at {widths}");
        }
    }

    /// <summary>
    /// The planners' screen: each character at the cheapest rate any mode could give it, a character with a cell at Kanji's 13 bits (78 sixths), topped with the narrowest header.
    /// It may only reject, so it must never exceed the optimum at any width.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task CheapestSixthsKanji_IsALowerBoundAtEveryWidth(string name, string text)
    {
        var sixths = ModeSegmenter.CheapestSixthsKanji(text);
        foreach (var widths in AllWidths())
        {
            var bound = (sixths + 5) / 6 + widths.ModeIndicator + widths.Narrowest;
            await Assert.That(bound).IsLessThanOrEqualTo(Cost(text, widths, null, out _)).Because($"{name} at {widths}");
        }
    }

    [Test]
    public async Task CheapestSixthsKanji_PricesEachClassAtItsCheapestRate()
    {
        await Assert.That(ModeSegmenter.CheapestSixthsKanji("7")).IsEqualTo(20);
        await Assert.That(ModeSegmenter.CheapestSixthsKanji("Q")).IsEqualTo(33);
        await Assert.That(ModeSegmenter.CheapestSixthsKanji("q")).IsEqualTo(48);
        await Assert.That(ModeSegmenter.CheapestSixthsKanji("日")).IsEqualTo(78);
        await Assert.That(ModeSegmenter.CheapestSixthsKanji("×")).IsEqualTo(78);
        await Assert.That(ModeSegmenter.CheapestSixthsKanji("日本7777")).IsEqualTo(2 * 78 + 4 * 20);
    }

    /// <summary>One character at every width: its class and the one mode that can carry it are readable from the cost.</summary>
    [Test]
    public async Task EachAsciiCharacter_TakesItsCheapestMode_AndACellTakesKanji()
    {
        var widths = new Widths(4, 10, 9, 8, 8);
        for (var code = 0; code < 0x80; code++)
        {
            var c = (char)code;
            var expected = KanjiPlanReference.IsAlnum(c) ? (c is >= '0' and <= '9' ? 18 : 19) : 20;
            await Assert.That(ModeSegmenter.ComputeCostsKanji(c.ToString(), 4, 10, 9, 8, 8, default, out _)).IsEqualTo(expected).Because($"U+{code:X4}");
        }
        await Assert.That(ModeSegmenter.ComputeCostsKanji("日", 4, 10, 9, 8, 8, default, out var state)).IsEqualTo(4 + 8 + 13);
        await Assert.That(state).IsEqualTo(StateKanji);
        await Assert.That(Cost("日", widths, null, out _)).IsEqualTo(25);
    }
}
