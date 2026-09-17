#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Text;
using FeatherQR.Internals;
using FeatherQR.Internals.StandardQR;
namespace FeatherQR.Tests;

/// <summary>
/// The planner's walk at eight budgets at once against the scalar walk it stands in for: lane by
/// lane the same chunk ends and the same count, from the start of the text and resumed after a
/// shared chunk; and the plan as a whole, which has to be the same plan whether the lanes are used
/// or not. Content is long enough for the lanes to be taken, in every charset, with pairs and lone
/// surrogates, and at budgets a few bits apart, which is where lanes close their chunks a character
/// or two from each other.
/// </summary>
public class StructuredAppendLaneWalkTest
{
    public static IEnumerable<(string Name, string Text)> Corpus()
    {
        yield return ("order-lines", Repeat("order 20260915 item 0000123456 qty 42 ", 9_000));
        yield return ("digits-then-order-lines", Repeat("0123456789", 4_000) + Repeat("order 20260915 item 0000123456 qty 42 ", 5_000));
        yield return ("prose-then-digits", Repeat("The quick brown fox jumps over the lazy dog. ", 4_000) + Repeat("0123456789", 6_000));
        yield return ("alphanumeric", Repeat("HELLO WORLD 12345 $%*+-./: ABC 987654321 ", 8_000));
        yield return ("latin1", Repeat("Crème brûlée 1234567890 à la carte ", 7_000));
        yield return ("japanese-with-numbers", Repeat("ご注文番号 20260915-0000123456 の商品を 42 個、本日発送いたしました。", 6_000));
        yield return ("pairs-with-digits", Repeat("🎉🎊 12345678 🎈 ABCDEFGH ", 5_000));
        yield return ("lone-surrogates", Repeat("ab\uD83Ccd 1234567 \uDE00xyz ", 5_000));
        yield return ("bom-inside", Repeat("x" + (char)0xFEFF + "y 12345678 ", 4_000));
        yield return ("random-runs", RandomRuns(9_000, 20260917));
        yield return ("random-runs-2", RandomRuns(6_000, 7));
    }

    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task WalkLanes_EndsEveryChunkWhereTheScalarWalkDoes(string name, string text)
    {
        if (!Vector256.IsHardwareAccelerated)
            return;

        var charset = TextAnalyzer.Analyze(text, EciMode.Default).EciMode;
        var scalarEnds = new int[StructuredAppendPlanner.MaxSymbols];
        var laneEnds = new int[8 * StructuredAppendPlanner.MaxSymbols];
        var counts = new int[8];
        foreach (var version in new[] { 9, 12, 26, 27, 40 })
        {
            var capacity = StructuredAppendPlanner.Capacity(version, QREccLevel.L);
            foreach (var spacing in new[] { 1, 4, 13 })
                foreach (var lanes in new[] { 8, 5, 2 })
                {
                    var budgets = new int[lanes];
                    for (var lane = 0; lane < lanes; lane++)
                        budgets[lane] = capacity - spacing * (lanes - 1 - lane);

                    // From the start of the text, and resumed after the first chunk of the lowest budget,
                    // which every lane shares only when it ends where theirs does; the walk is asked to
                    // resume there regardless, as the planner does only when it is shared, so the scalar
                    // walk is resumed at the same place to compare like with like.
                    foreach (var resumed in new[] { false, true })
                    {
                        var placed = 0;
                        var start = 0;
                        if (resumed)
                        {
                            StructuredAppendPlanner.CountChunks(text, charset, false, QRSegmentation.Optimal, version, budgets[0], 1, scalarEnds);
                            placed = 1;
                            start = scalarEnds[0];
                            if (start >= text.Length)
                                continue;
                        }

                        var limit = StructuredAppendPlanner.MaxSymbols;
                        var walked = StructuredAppendPlanner.WalkLanes(text, charset, version, budgets, limit, placed, start, counts, laneEnds);
                        for (var lane = 0; lane < lanes; lane++)
                        {
                            var because = $"{name} v{version} budget {budgets[lane]} (lane {lane} of {lanes}, spacing {spacing}, resumed {resumed})";
                            var expected = StructuredAppendPlanner.CountChunks(text.AsSpan(start), charset, false, QRSegmentation.Optimal, version, budgets[lane], limit - placed, scalarEnds);
                            if (!walked)
                            {
                                // The lanes give up only on a character that fits no chunk, which the scalar walk reports.
                                continue;
                            }

                            await Assert.That(expected).IsNotEqualTo(int.MaxValue).Because(because);
                            var expectedCount = expected > limit - placed ? limit + 1 : expected + placed;
                            await Assert.That(counts[lane]).IsEqualTo(expectedCount).Because(because);
                            var chunks = Math.Min(expected, limit - placed);
                            for (var k = 0; k < chunks; k++)
                                await Assert.That(laneEnds[lane * StructuredAppendPlanner.MaxSymbols + placed + k]).IsEqualTo(start + scalarEnds[k]).Because($"{because}, chunk {placed + k}");
                        }
                    }
                }
        }
    }

    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task Plan_IsTheSamePlanWithAndWithoutTheLanes(string name, string text)
    {
        var analysis = TextAnalyzer.Analyze(text, EciMode.Default);
        var withLanes = new int[StructuredAppendPlanner.MaxSymbols];
        var withoutLanes = new int[StructuredAppendPlanner.MaxSymbols];
        foreach (var (minVersion, maxVersion) in new[] { (1, 40), (40, 40), (10, 26), (9, 12), (26, 28), (20, 33) })
            foreach (var ecc in new[] { QREccLevel.L, QREccLevel.M, QREccLevel.Q, QREccLevel.H })
                foreach (var bom in analysis.EciMode == EciMode.Utf8 ? new[] { false, true } : new[] { false })
                {
                    Array.Clear(withLanes);
                    Array.Clear(withoutLanes);
                    var planned = StructuredAppendPlanner.TryPlan(text, ecc, analysis.EciMode, analysis.EncodingMode, bom, QRSegmentation.Optimal, minVersion, maxVersion, withLanes, out var count, out var version, out var budget, allowLanes: true);
                    var expected = StructuredAppendPlanner.TryPlan(text, ecc, analysis.EciMode, analysis.EncodingMode, bom, QRSegmentation.Optimal, minVersion, maxVersion, withoutLanes, out var expectedCount, out var expectedVersion, out var expectedBudget, allowLanes: false);

                    var because = $"{name} range ({minVersion},{maxVersion}) level {ecc} bom {bom}";
                    await Assert.That(planned).IsEqualTo(expected).Because(because);
                    if (!expected)
                        continue;
                    await Assert.That(count).IsEqualTo(expectedCount).Because(because);
                    await Assert.That(version).IsEqualTo(expectedVersion).Because(because);
                    await Assert.That(budget).IsEqualTo(expectedBudget).Because(because);
                    for (var i = 0; i < Math.Min(count, StructuredAppendPlanner.MaxSymbols); i++)
                        await Assert.That(withLanes[i]).IsEqualTo(withoutLanes[i]).Because($"{because}, chunk {i}");
                }
    }

    private static string Repeat(string unit, int length)
    {
        var sb = new StringBuilder(length + unit.Length);
        while (sb.Length < length)
            sb.Append(unit);
        var text = sb.ToString(0, length);
        // Never end on a high surrogate: the encoder would write U+FFFD for it.
        return char.IsHighSurrogate(text[^1]) ? text[..^1] : text;
    }

    /// <summary>Runs of one class each (digits, upper-case alphanumerics, lower-case and punctuation), 1 to 39 long.</summary>
    private static string RandomRuns(int length, int seed)
    {
        const string alphabet = "0123456789012345678901234567ABCDEF abcdefghijklmnop.,-:";
        var random = new Random(seed);
        var sb = new StringBuilder(length + 40);
        while (sb.Length < length)
        {
            var run = random.Next(1, 40);
            var from = random.Next(3) switch { 0 => 0, 1 => 28, _ => 35 };
            var span = from == 0 ? 28 : from == 28 ? 7 : alphabet.Length - 35;
            for (var i = 0; i < run; i++)
                sb.Append(alphabet[from + random.Next(span)]);
        }
        return sb.ToString(0, length);
    }
}
#endif
