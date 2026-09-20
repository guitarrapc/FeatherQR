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
        // A mark a line, so that some cut of some walk lands just before one and is moved off it.
        yield return ("marks-after-line-feeds", Repeat("order 20260915 item 0000123456 qty 42" + (char)0x0A + (char)0xFEFF, 9_000));
        // A mark where the unconstrained plan would open a Byte run, which no plan does; and one behind a pair, which a cut clears whole.
        yield return ("marks-after-digits", Repeat("order 20260915 item 0000123456" + (char)0xFEFF + " qty 42 ", 9_000));
        yield return ("marks-after-pairs", Repeat("🎉" + (char)0xFEFF + "12345678 ", 5_000));
        // At the head of the text the mark is the first chunk's to open a run at, as it is a single symbol's.
        yield return ("mark-at-the-head", (char)0xFEFF + Repeat("order 20260915 item 0000123456 qty 42 ", 9_000));
        yield return ("random-runs", RandomRuns(9_000, 20260917));
        yield return ("random-runs-2", RandomRuns(6_000, 7));
    }

    /// <summary>
    /// The corpus and runs of marks long enough that the cuts kept off them put the answer above every budget of the first batch from the floor, which a second batch reaches for.
    /// Not walked lane by lane: every close sets a lane back through the whole run, so on the short chunks of small versions the lanes spend most steps apart, which the bound on those steps is not about.
    /// </summary>
    public static IEnumerable<(string Name, string Text)> PlanCorpus()
    {
        foreach (var row in Corpus())
            yield return row;
        yield return ("runs-of-six-marks", Repeat("0123456789012345678901234567890123456789" + new string((char)0xFEFF, 6), 15_000));
        yield return ("runs-of-twenty-marks", Repeat("0123456789012345678901234567890123456789" + new string((char)0xFEFF, 20), 15_000));
    }

    [Test]
    [Arguments(9_000, 40)]
    [Arguments(900, 5)]
    [Arguments(180, 2)]
    public async Task Plan_UsesParallelBudgetsOnArm64_AndMatchesScalarCuts(int length, int maxVersion)
    {
        if (!System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported)
            return;
        var text = Repeat("order 20260915 item 0000123456 qty 42 ", length);
        var analysis = TextAnalyzer.Analyze(text, EciMode.Default);
        var expected = new int[StructuredAppendPlanner.MaxSymbols];
        var actual = new int[StructuredAppendPlanner.MaxSymbols];
        var scalar = StructuredAppendPlanner.TryPlan(text, QREccLevel.L, analysis.EciMode, analysis.EncodingMode,
            false, QRSegmentation.Optimal, 1, maxVersion, expected, out var expectedCount, out var expectedVersion, out var expectedBudget, allowLanes: false);
        var before = StructuredAppendPlanner.LaneBatches;
        var parallel = StructuredAppendPlanner.TryPlan(text, QREccLevel.L, analysis.EciMode, analysis.EncodingMode,
            false, QRSegmentation.Optimal, 1, maxVersion, actual, out var count, out var version, out var budget, allowLanes: true);
        var batches = StructuredAppendPlanner.LaneBatches - before;
        await Assert.That(scalar && parallel).IsTrue();
        await Assert.That(count).IsEqualTo(expectedCount);
        await Assert.That(version).IsEqualTo(expectedVersion);
        await Assert.That(budget).IsEqualTo(expectedBudget);
        await Assert.That(actual.AsSpan(0, count).ToArray()).IsEquivalentTo(expected.AsSpan(0, count).ToArray());
        await Assert.That(batches).IsGreaterThan(0);
    }

    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task WalkLanes_EndsEveryChunkWhereTheScalarWalkDoes(string name, string text)
    {
        if (!Vector256.IsHardwareAccelerated && !System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported)
            return;
        var charset = TextAnalyzer.Analyze(text, EciMode.Default).EciMode;
        var scalarEnds = new int[StructuredAppendPlanner.MaxSymbols];
        var laneEnds = new int[8 * StructuredAppendPlanner.MaxSymbols];
        var counts = new int[8];
        var worstApart = 0;
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
                        var walked = StructuredAppendPlanner.WalkLanes(text, charset, version, budgets, limit, placed, start, counts, laneEnds, out var apart);
                        // The lanes give up only on a character that fits no chunk, and the corpus holds none at these versions.
                        await Assert.That(walked).IsTrue().Because($"{name} v{version} spacing {spacing} lanes {lanes} resumed {resumed}");
                        worstApart = Math.Max(worstApart, apart);
                        for (var lane = 0; lane < lanes; lane++)
                        {
                            var because = $"{name} v{version} budget {budgets[lane]} (lane {lane} of {lanes}, spacing {spacing}, resumed {resumed})";
                            var expected = StructuredAppendPlanner.CountChunks(text.AsSpan(start), charset, false, QRSegmentation.Optimal, version, budgets[lane], limit - placed, scalarEnds);

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

        // The lanes ahead wait for the one behind, so they are apart for a few steps a close, and a lane closes a few times in a
        // thousand characters (a failed lane keeps closing, unrecorded); left apart they would stay so for the rest of the text.
        await Assert.That(worstApart).IsGreaterThan(0).Because(name);
        await Assert.That(worstApart).IsLessThan(text.Length / 4).Because(name);
    }

    [Test]
    public async Task WalkLanes_PricesAMarkInALanesLastCharactersAsTheScalarWalkDoes()
    {
        // The lanes that fell behind finish the text one by one, from their own states. Runs of marks make a lane that
        // closes inside one fall far behind, and the text ends in digits and a mark, where a Byte run must not open:
        // the filler walks each lane's last chunk across its budget, and the shifts put every budget within the few bits that opening one would save.
        if (!Vector256.IsHardwareAccelerated && !System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported)
            return;
        var mark = (char)0xFEFF;
        var scalarEnds = new int[StructuredAppendPlanner.MaxSymbols];
        var laneEnds = new int[8 * StructuredAppendPlanner.MaxSymbols];
        var counts = new int[8];
        var limit = StructuredAppendPlanner.MaxSymbols;
        // Under a declared ISO-8859-1 the mark is a byte like any other and a run may open at it, in the lanes as in the scalar walk.
        foreach (var charset in new[] { EciMode.Utf8, EciMode.Iso8859_1 })
            foreach (var spacing in new[] { 13, 40 })
                foreach (var shift in new[] { 0, 1, 2, 3, 4, 5, 6, 7 })
                {
                    const int version = 9;
                    var capacity = StructuredAppendPlanner.Capacity(version, QREccLevel.L);
                    var budgets = new int[8];
                    for (var lane = 0; lane < 8; lane++)
                        budgets[lane] = capacity - shift - spacing * (7 - lane);

                    for (var filler = 0; filler < 250; filler++)
                    {
                        var text = Repeat("order 20260915 item 0000123456 qty 42 " + new string(mark, 6), 1_500) + new string('x', filler) + new string('7', 30) + mark;
                        await Assert.That(StructuredAppendPlanner.WalkLanes(text, charset, version, budgets, limit, 0, 0, counts, laneEnds, out _)).IsTrue();
                        for (var lane = 0; lane < 8; lane++)
                        {
                            var because = $"{charset} spacing {spacing} shift {shift} filler {filler} budget {budgets[lane]}";
                            var expected = StructuredAppendPlanner.CountChunks(text, charset, false, QRSegmentation.Optimal, version, budgets[lane], limit, scalarEnds);
                            await Assert.That(counts[lane]).IsEqualTo(expected > limit ? limit + 1 : expected).Because(because);
                            for (var k = 0; k < Math.Min(expected, limit); k++)
                                await Assert.That(laneEnds[lane * StructuredAppendPlanner.MaxSymbols + k]).IsEqualTo(scalarEnds[k]).Because($"{because}, chunk {k}");
                        }
                    }
                }
    }

    [Test]
    public async Task FromTheFloor_ASecondBatchReachesAcrossTheLongestRunOfMarks_OnlyWhenTheFirstFailsThroughout()
    {
        // The plan is the same with or without the second batch, so what is pinned here is where each batch lands against
        // the balanced budget B the scalar search finds: a budget holds exactly when it is at least B.
        if (!Vector256.IsHardwareAccelerated && !System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported)
            return;
        var text = Repeat("0123456789012345678901234567890123456789" + new string((char)0xFEFF, 6), 15_000);
        var analysis = TextAnalyzer.Analyze(text, EciMode.Default);
        var ends = new int[StructuredAppendPlanner.MaxSymbols];
        await Assert.That(StructuredAppendPlanner.TryPlan(text, QREccLevel.L, analysis.EciMode, analysis.EncodingMode, false, QRSegmentation.Optimal, 1, 40, ends, out var count, out var version, out var balanced, allowLanes: false)).IsTrue();
        var ceiling = StructuredAppendPlanner.Capacity(version, QREccLevel.L);
        // A digit and six marks: nineteen bytes a cut kept off the run can leave unused.
        await Assert.That(StructuredAppendPlanner.KeptOffBits(text, analysis.EciMode)).IsEqualTo(19 * 8);

        (int Settled, int Failed) Narrow(int floor, int? cut = null, bool ceilingHolds = true)
        {
            var top = cut ?? ceiling;
            int low = floor, high = top, settled = -1, settledCount = 0, failed = -1;
            var settledEnds = new int[StructuredAppendPlanner.MaxSymbols];
            var failedEnds = new int[StructuredAppendPlanner.MaxSymbols];
            // True whenever a batch was walked, the second skipped or not: the count-settling caller walks a scalar probe on false.
            var walked = StructuredAppendPlanner.TryNarrowWithLanes(text, analysis.EciMode, version, floor, top, count, ref low, ref high, settledEnds, ref settled, ref settledCount, failedEnds, ref failed, fromFloor: true, ceilingHolds);
            return walked ? (settled, failed) : (int.MinValue, int.MinValue);
        }

        // A ceiling that cuts the second batch to three budgets: below one known to hold the count they are walked (and
        // fail, being below the answer), below one that is not the answer may be past it and the batch is not taken.
        var proven = Narrow(balanced - 200, cut: balanced - 60);
        await Assert.That(proven.Settled).IsEqualTo(-1);
        await Assert.That(proven.Failed).IsGreaterThan(balanced - 140).And.IsLessThan(balanced - 60);
        var unproven = Narrow(balanced - 200, cut: balanced - 60, ceilingHolds: false);
        await Assert.That(unproven.Settled).IsEqualTo(-1);
        await Assert.That(unproven.Failed).IsLessThan(balanced - 140);
        // Four budgets are half the batch, which is enough below either ceiling.
        var half = Narrow(balanced - 200, cut: balanced - 50, ceilingHolds: false);
        await Assert.That(half.Settled).IsEqualTo(-1);
        await Assert.That(half.Failed).IsGreaterThan(balanced - 60).And.IsLessThan(balanced - 50);
        // A second batch that fails throughout is the last: 500 bits below, the run's reach falls short and the caller goes on.
        var twice = Narrow(balanced - 500);
        await Assert.That(twice.Settled).IsEqualTo(-1);
        await Assert.That(twice.Failed).IsLessThan(balanced - 240);

        // Near the floor the first batch settles it, a UTF-8 lane step (6 bits) apart, and nothing is walked after it.
        var near = Narrow(balanced - 10);
        await Assert.That(near.Settled).IsGreaterThanOrEqualTo(balanced).And.IsLessThan(balanced + 6);
        await Assert.That(near.Failed).IsLessThan(balanced).And.IsGreaterThanOrEqualTo(balanced - 6);

        // Every budget of the first batch fails 200 bits below it; the second, from the highest that failed and spread
        // across the run, settles it within a step of its own.
        var far = Narrow(balanced - 200);
        await Assert.That(far.Settled).IsGreaterThanOrEqualTo(balanced).And.IsLessThan(balanced + 32);
        await Assert.That(far.Failed).IsLessThan(balanced).And.IsGreaterThanOrEqualTo(balanced - 32);
    }

    [Test]
    public async Task ScalarWalks_CountsTheBisectionsResumedProbesToo()
    {
        // Without lanes the search is scalar walks alone, and each bisection probe resumes after the chunks it shares
        // with its neighbours: those are walks of the search too, and a counter that missed them would pin nothing there.
        var text = Repeat("0123456789012345678901234567890123456789" + new string((char)0xFEFF, 6), 15_000);
        var analysis = TextAnalyzer.Analyze(text, EciMode.Default);
        var ends = new int[StructuredAppendPlanner.MaxSymbols];

        int scalar = StructuredAppendPlanner.ScalarWalks, lanes = StructuredAppendPlanner.LaneBatches;
        var planned = StructuredAppendPlanner.TryPlan(text, QREccLevel.L, analysis.EciMode, analysis.EncodingMode, false, QRSegmentation.Optimal, 1, 40, ends, out _, out _, out _, allowLanes: false);
        scalar = StructuredAppendPlanner.ScalarWalks - scalar;
        lanes = StructuredAppendPlanner.LaneBatches - lanes;

        await Assert.That(planned).IsTrue();
        await Assert.That(scalar).IsEqualTo(12);
        await Assert.That(lanes).IsEqualTo(0);
    }

    [Test]
    [Arguments(20, 5_500, QREccLevel.H, 40, 2, 3)]
    [Arguments(8, 15_000, QREccLevel.Q, 36, 2, 4)]
    public async Task Plan_TakesTheSecondBatchWhereItsCeilingAllowsIt(int marks, int length, QREccLevel ecc, int maxVersion, int scalarWalks, int laneBatches)
    {
        // Which ceilings may take the second batch is invisible in the plan, so the walks are counted. In the first row the
        // count is asked below a capacity that does not hold it (the set needs one symbol more), and a second batch there
        // would be walked and repeated by the walk at the capacity; in the second the bracket's ceiling holds the count and
        // the batch saves scalar probes. A change of search that moves these must say why, with a measurement.
        if (!Vector256.IsHardwareAccelerated && !System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported)
            return;
        var digits = marks == 20 ? "0123456789012345678901234567890123456789" : "01234567890123456789";
        var text = Repeat(digits + new string((char)0xFEFF, marks), length);
        var analysis = TextAnalyzer.Analyze(text, EciMode.Default);
        var ends = new int[StructuredAppendPlanner.MaxSymbols];

        int scalar = StructuredAppendPlanner.ScalarWalks, lanes = StructuredAppendPlanner.LaneBatches;
        var planned = StructuredAppendPlanner.TryPlan(text, ecc, analysis.EciMode, analysis.EncodingMode, false, QRSegmentation.Optimal, 1, maxVersion, ends, out _, out _, out _, allowLanes: true);
        scalar = StructuredAppendPlanner.ScalarWalks - scalar;
        lanes = StructuredAppendPlanner.LaneBatches - lanes;

        await Assert.That(planned).IsTrue();
        await Assert.That(scalar).IsEqualTo(scalarWalks);
        await Assert.That(lanes).IsEqualTo(laneBatches);
    }

    [Test]
    [Arguments("a" + "~~~~~~", EciMode.Utf8, (1 + 18) * 8)]
    [Arguments("xy\U0001F600~~ z~", EciMode.Utf8, (4 + 6) * 8)]
    [Arguments("\U0001F600~", EciMode.Utf8, (4 + 3) * 8)]
    [Arguments("~~~", EciMode.Utf8, (3 + 6) * 8)]
    [Arguments("order 42\n~order 43", EciMode.Utf8, (1 + 3) * 8)]
    [Arguments("order 42\n~order 43", EciMode.Iso8859_1, 0)]
    [Arguments("~order 42", EciMode.Utf8, 0)]
    public async Task KeptOffBits_IsTheLongestRunAndTheCharacterAheadOfIt(string text, EciMode charset, int expected)
    {
        // '~' stands for U+FEFF, which the source keeps visible. The mark at the head of the text is the first chunk's,
        // which no cut is kept off.
        await Assert.That(StructuredAppendPlanner.KeptOffBits(text.Replace('~', (char)0xFEFF), charset)).IsEqualTo(expected);
    }

    [Test]
    [MethodDataSource(nameof(PlanCorpus))]
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
