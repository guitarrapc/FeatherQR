#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics.Arm;
using FeatherQR.Internals;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>ARM64 budget costs stay exact at resets, saturation, and scalar fallback boundaries.</summary>
public class StructuredAppendNeonParityTest
{
    public static IEnumerable<(string Text, EciMode Charset)> Inputs()
    {
        yield return (new string('0', 100_000), EciMode.Default);
        yield return (new string('A', 40_000), EciMode.Default);
        yield return (new string('x', 40_000), EciMode.Default);
        foreach (var seed in new[] { 7, 20260919 })
        {
            var random = new Random(seed);
            var text = new System.Text.StringBuilder();
            var runs = new[] { "0123456789", "ABC $%*+-./:", "order", "é", "🎉", "\uD83C", "\uDE00", "x\uFEFF\uFEFF" };
            for (var i = 0; i < 700; i++)
                text.Append(runs[random.Next(runs.Length)]);
            foreach (var charset in new[] { EciMode.Default, EciMode.Iso8859_1, EciMode.Utf8 })
                yield return (text.ToString(), charset);
        }
    }

    [Test]
    [MethodDataSource(nameof(Inputs))]
    public async Task Walk_ResetsAndSaturatesWithoutChangingChunkEnds(string text, EciMode charset)
    {
        if (!AdvSimd.Arm64.IsSupported)
            return;
        var analysis = TextAnalyzer.Analyze(text, charset);
        var expected = new int[StructuredAppendPlanner.MaxSymbols];
        var actual = new int[8 * StructuredAppendPlanner.MaxSymbols];
        var counts = new int[8];
        foreach (var version in new[] { 1, 9, 10, 26, 27, 40 })
        foreach (var used in new[] { 1, 2, 3, 4, 5, 7, 8 })
        {
            var capacity = StructuredAppendPlanner.Capacity(version, QREccLevel.L);
            var budgets = Enumerable.Range(0, used).Select(i => capacity - 3 * i).ToArray();
            // Unsorted budgets also exercise partially active lanes independently of the search's order.
            var walked = StructuredAppendPlanner.WalkLanes(text, analysis.EciMode, version, budgets,
                StructuredAppendPlanner.MaxSymbols, 0, 0, counts, actual, out _);
            if (!walked)
            {
                var impossible = false;
                foreach (var budget in budgets)
                    impossible |= StructuredAppendPlanner.CountChunks(text, analysis.EciMode, false, QRSegmentation.Optimal,
                        version, budget, StructuredAppendPlanner.MaxSymbols, expected) == int.MaxValue;
                await Assert.That(impossible).IsTrue();
                continue;
            }
            for (var lane = 0; lane < used; lane++)
            {
                var count = StructuredAppendPlanner.CountChunks(text, analysis.EciMode, false, QRSegmentation.Optimal,
                    version, budgets[lane], StructuredAppendPlanner.MaxSymbols, expected);
                var because = $"v{version}, {analysis.EciMode}, n={text.Length}, used={used}, lane={lane}";
                await Assert.That(counts[lane]).IsEqualTo(count).Because(because);
                for (var k = 0; k < Math.Min(count, StructuredAppendPlanner.MaxSymbols); k++)
                    await Assert.That(actual[lane * StructuredAppendPlanner.MaxSymbols + k]).IsEqualTo(expected[k]).Because(because);
            }
        }
    }

    [Test]
    [Arguments(19, false)]
    [Arguments(20, true)]
    [Arguments(21, true)]
    public async Task Narrow_ShortChunkBoundary_UsesTheMeasuredBackendThreshold(int averageLength, bool expectedWalk)
    {
        if (!AdvSimd.Arm64.IsSupported)
            return;
        var text = string.Concat(Enumerable.Repeat("order 1234567890 item 42 ", 3)).AsSpan(0, averageLength * 2).ToString();
        int low = 100, high = 120, settled = -1, count = 0, failed = -1;
        var settledEnds = new int[16];
        var failedEnds = new int[16];
        var before = StructuredAppendPlanner.LaneBatches;
        var walked = StructuredAppendPlanner.TryNarrowWithLanes(text, EciMode.Default, 1, low, high, 2,
            ref low, ref high, settledEnds, ref settled, ref count, failedEnds, ref failed);
        var batches = StructuredAppendPlanner.LaneBatches - before;
        await Assert.That(walked).IsEqualTo(expectedWalk);
        await Assert.That(batches).IsEqualTo(expectedWalk ? 1 : 0);
        if (!expectedWalk)
        {
            await Assert.That(low).IsEqualTo(100);
            await Assert.That(high).IsEqualTo(120);
            await Assert.That(settled).IsEqualTo(-1);
            await Assert.That(failed).IsEqualTo(-1);
        }
    }

    [Test]
    [Arguments(0)]
    [Arguments(19)]
    [Arguments(20)]
    [Arguments(32)]
    [Arguments(65_553)]
    [Arguments(65_554)]
    [Arguments(65_555)]
    [Arguments(65_566)]
    [Arguments(65_567)]
    [Arguments(int.MaxValue)]
    public async Task Walk_BudgetRepresentationBoundary_MatchesScalar(int budget)
    {
        if (!AdvSimd.Arm64.IsSupported)
            return;
        const string text = "order 1234567890 🐈 x\uFEFF42";
        var expected = new int[16];
        var ends = new int[8 * 16];
        var counts = new int[8];
        foreach (var charset in new[] { EciMode.Default, EciMode.Utf8 })
        {
            var count = StructuredAppendPlanner.CountChunks(text, charset, false, QRSegmentation.Optimal, 40, budget, 16, expected);
            var walked = StructuredAppendPlanner.WalkLanes(text, charset, 40, new[] { budget }, 16, 0, 0, counts, ends, out _);
            await Assert.That(walked).IsEqualTo(count != int.MaxValue);
            if (!walked)
                continue;
            await Assert.That(counts[0]).IsEqualTo(count);
            for (var k = 0; k < Math.Min(count, 16); k++)
                await Assert.That(ends[k]).IsEqualTo(expected[k]);
        }
    }
}
#endif
