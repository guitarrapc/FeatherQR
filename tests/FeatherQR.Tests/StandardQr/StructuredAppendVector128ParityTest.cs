#if NET8_0_OR_GREATER
using FeatherQR.Internals;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// The portable 16-bit lane walk against the scalar walk, entered directly: the dispatch takes it only without 256-bit vectors and NEON,
/// so this is where an x64 machine with AVX2 runs it at all. Same inputs as the NEON walk's test: resets, saturation, the scalar finish.
/// </summary>
public class StructuredAppendVector128ParityTest
{
    public static IEnumerable<(string Text, EciMode Charset)> Inputs() => StructuredAppendNeonParityTest.Inputs();

    [Test]
    [MethodDataSource(nameof(Inputs))]
    public async Task Walk_ResetsAndSaturatesWithoutChangingChunkEnds(string text, EciMode charset)
    {
        if (!System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated)
        {
            Skip.Test("Vector128 not accelerated on this machine");
            return;
        }
        var analysis = TextAnalyzer.Analyze(text, charset);
        var expected = new int[StructuredAppendPlanner.MaxSymbols];
        var actual = new int[8 * StructuredAppendPlanner.MaxSymbols];
        var counts = new int[8];
        foreach (var version in new[] { 1, 9, 10, 26, 27, 40 })
            foreach (var used in new[] { 1, 2, 3, 4, 5, 7, 8 })
            {
                var capacity = StructuredAppendPlanner.Capacity(version, QREccLevel.L);
                var budgets = Enumerable.Range(0, used).Select(i => capacity - 3 * i).ToArray();
                var walked = StructuredAppendPlanner.WalkLanesVector128(text, analysis.EciMode, version, budgets,
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

    public static IEnumerable<string> MarkRuns() => StructuredAppendLaneWalkTest.MarkRuns();

    [Test]
    [MethodDataSource(nameof(MarkRuns))]
    public async Task Walk_ACutKeptOffARunOfMarks_LeavesTheLaneAStepBehind(string name)
    {
        if (!System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated)
        {
            Skip.Test("Vector128 not accelerated on this machine");
            return;
        }
        var text = StructuredAppendLaneWalkTest.MarkRun(name);
        var charset = TextAnalyzer.Analyze(text, EciMode.Default).EciMode;
        const int version = 40;
        var capacity = StructuredAppendPlanner.Capacity(version, QREccLevel.L);
        var budgets = Enumerable.Range(0, 8).Select(lane => capacity - 6 * (7 - lane)).ToArray();
        var limit = StructuredAppendPlanner.MaxSymbols;
        var counts = new int[8];
        var laneEnds = new int[8 * limit];
        var scalarEnds = new int[limit];

        await Assert.That(StructuredAppendPlanner.WalkLanesVector128(text, charset, version, budgets, limit, 0, 0, counts, laneEnds, out var apart)).IsTrue().Because(name);
        var closes = 0;
        for (var lane = 0; lane < 8; lane++)
        {
            var because = $"{name} budget {budgets[lane]}";
            var expected = StructuredAppendPlanner.CountChunks(text, charset, false, QRSegmentation.Optimal, version, budgets[lane], limit, scalarEnds);
            await Assert.That(counts[lane]).IsEqualTo(expected > limit ? limit + 1 : expected).Because(because);
            for (var k = 0; k < Math.Min(expected, limit); k++)
                await Assert.That(laneEnds[lane * limit + k]).IsEqualTo(scalarEnds[k]).Because($"{because}, chunk {k}");
            closes += StructuredAppendPlanner.CountChunks(text, charset, false, QRSegmentation.Optimal, version, budgets[lane], 64, scalarEnds) - 1;
        }

        // As in the 32-bit lanes: a close sets its lane a step back, two at a pair.
        await Assert.That(apart).IsGreaterThan(0).Because(name);
        await Assert.That(apart).IsLessThanOrEqualTo(2 * closes).Because(name);
    }

    [Test]
    [Arguments(32)]
    [Arguments(65_553)]
    [Arguments(65_554)]
    [Arguments(65_565)]
    [Arguments(65_566)]
    public async Task Walk_BudgetRepresentationBoundary_MatchesScalar(int budget)
    {
        if (!System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated)
        {
            Skip.Test("Vector128 not accelerated on this machine");
            return;
        }
        const string text = "order 1234567890 🐈 x﻿42";
        var expected = new int[16];
        var ends = new int[8 * 16];
        var counts = new int[8];
        foreach (var charset in new[] { EciMode.Default, EciMode.Utf8 })
        {
            // The dispatch hands this walk only budgets whose cost room fits 16 bits; the largest ones here sit at its edge
            if ((uint)(budget - StructuredAppendPlanner.HeaderBits - charset.GetStandardQrHeaderBits()) >= ushort.MaxValue)
                continue;
            var count = StructuredAppendPlanner.CountChunks(text, charset, false, QRSegmentation.Optimal, 40, budget, 16, expected);
            var walked = StructuredAppendPlanner.WalkLanesVector128(text, charset, 40, new[] { budget }, 16, 0, 0, counts, ends, out _);
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
