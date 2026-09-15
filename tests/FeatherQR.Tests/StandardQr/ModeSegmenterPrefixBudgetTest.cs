using System.Text;
using FeatherQR.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// The forward walk that answers "the longest prefix whose minimal plan fits a budget" is held to
/// the full cost run on every prefix: the reference prices each prefix with <see cref="ModeSegmenter.ComputeCosts"/>
/// and takes the last one within the budget, sharing nothing with the walk but the cost model.
/// </summary>
public class ModeSegmenterPrefixBudgetTest
{
    private const int ModeIndicatorBits = 4;

    public static IEnumerable<(int CciNumeric, int CciAlnum, int CciByte)> BandWidths() =>
    [
        (10, 9, 8),
        (12, 11, 16),
        (14, 13, 16),
    ];

    [Test]
    [Arguments("digits", EciMode.Default)]
    [Arguments("upper", EciMode.Default)]
    [Arguments("prose", EciMode.Default)]
    [Arguments("transitions", EciMode.Default)]
    [Arguments("alternating", EciMode.Default)]
    [Arguments("mixed", EciMode.Default)]
    [Arguments("latin1", EciMode.Iso8859_1)]
    [Arguments("japanese", EciMode.Utf8)]
    [Arguments("emoji", EciMode.Utf8)]
    [Arguments("digitsThenEmoji", EciMode.Utf8)]
    public async Task LongestPrefixWithinBudget_MatchesTheCostRunOnEveryPrefix(string kind, EciMode charset)
    {
        var text = Text(kind);
        foreach (var (cciNumeric, cciAlnum, cciByte) in BandWidths())
        {
            // The cost of every candidate prefix, in order; a candidate never cuts a surrogate pair.
            var candidates = new List<(int Length, int Cost)>();
            for (var length = 1; length <= text.Length; length++)
            {
                if (char.IsHighSurrogate(text[length - 1]) && length < text.Length && char.IsLowSurrogate(text[length]))
                    continue;
                candidates.Add((length, ModeSegmenter.ComputeCosts(text.AsSpan(0, length), charset, ModeIndicatorBits, cciNumeric, cciAlnum, cciByte, default, out _)));
            }

            // Every boundary the answer can turn on: each prefix's cost, and one bit below it.
            var budgets = candidates.SelectMany(c => new[] { c.Cost, c.Cost - 1 }).Concat([0, 1, int.MaxValue / 8]).Distinct();
            foreach (var budget in budgets)
            {
                var expected = candidates.Where(c => c.Cost <= budget).Select(c => c.Length).DefaultIfEmpty(0).Max();

                await Assert.That(ModeSegmenter.LongestPrefixWithinBudget(text, charset, ModeIndicatorBits, cciNumeric, cciAlnum, cciByte, budget)).IsEqualTo(expected)
                    .Because($"{kind} at widths ({cciNumeric}, {cciAlnum}, {cciByte}) with budget {budget}");
            }
        }
    }

    [Test]
    public async Task LongestPrefixWithinBudget_EmptyText_IsZero()
    {
        await Assert.That(ModeSegmenter.LongestPrefixWithinBudget("", EciMode.Default, ModeIndicatorBits, 10, 9, 8, 1000)).IsEqualTo(0);
    }

    private static string Text(string kind)
    {
        var unit = kind switch
        {
            "digits" => "0123456789",
            "upper" => "HELLO WORLD ",
            "prose" => "The quick brown fox jumps over the lazy dog. ",
            "transitions" => "12345ABCDE abcde 67890 FGHIJ fghij ",
            "alternating" => "a7",
            "mixed" => "order 20260915 item 0000123456 qty 42 ",
            "latin1" => "Crème brûlée à la carte, jalapeño, naïve café. ",
            "japanese" => "こんにちは世界、QRコードの分割テストです。",
            "emoji" => "🎉🎊🎈",
            "digitsThenEmoji" => "1234567🎉🎊abc",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var sb = new StringBuilder();
        while (sb.Length < 90)
            sb.Append(unit);
        var text = sb.ToString(0, 90);
        return char.IsHighSurrogate(text[^1]) ? text[..^1] : text;
    }
}
