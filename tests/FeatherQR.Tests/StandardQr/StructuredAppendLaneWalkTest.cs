using TUnit.Assertions.Enums;
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
    /// <summary>Every text fits sixteen symbols at level H, which <c>StructuredAppendWriterPlanTest</c>, sharing this corpus, needs.</summary>
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
        // Runs a cut lands inside, after a digit, a pair and a space: the next chunk opens with that character and the run, short enough for a version 9 symbol.
        yield return ("runs-of-marks-after-digits", Repeat("0123456789012345678901234567890123456789" + new string((char)0xFEFF, 40), 9_000));
        yield return ("runs-of-marks-after-pairs", Repeat("🎉" + new string((char)0xFEFF, 30) + "12345678 ", 6_000));
        yield return ("runs-of-marks-after-spaces", Repeat("order 20260915 item 0000123456 qty 42 " + new string((char)0xFEFF, 24), 9_000));
        yield return ("random-runs", RandomRuns(9_000, 20260917));
        yield return ("random-runs-2", RandomRuns(6_000, 7));
    }

    /// <summary>
    /// The corpus and runs of marks long enough that the cuts kept off them put the answer above every budget of the first batch from the floor, which a second batch reaches for, and far above it.
    /// </summary>
    public static IEnumerable<(string Name, string Text)> PlanCorpus()
    {
        foreach (var row in Corpus())
            yield return row;
        yield return ("runs-of-six-marks", Repeat("0123456789012345678901234567890123456789" + new string((char)0xFEFF, 6), 15_000));
        yield return ("runs-of-twenty-marks", Repeat("0123456789012345678901234567890123456789" + new string((char)0xFEFF, 20), 15_000));
        yield return ("runs-of-two-hundred-marks", Repeat("0123456789012345678901234567890123456789" + new string((char)0xFEFF, 200), 15_000));
    }

    /// <summary>
    /// Runs of 24 marks after each kind of character a cut kept off one leaves at the head of the next chunk, walked lane by lane: the runs are short enough that a run priced a byte off moves a later cut, which the chunk ends show.
    /// </summary>
    public static IEnumerable<(string Name, string Text)> KeptOffCorpus()
    {
        // The space's run is the corpus's runs-of-marks-after-spaces.
        foreach (var ahead in KeptOffAhead().Where(ahead => ahead != "a-space"))
            yield return ($"runs-of-24-marks-after-{ahead}", KeptOffRun(ahead, 24, 9_000));
    }

    public static IEnumerable<string> KeptOffAhead() => ["a-digit", "a-space", "a-letter", "a-two-byte-character", "a-three-byte-character", "a-pair", "a-lone-high-surrogate", "a-lone-low-surrogate"];

    internal static string KeptOffRun(string ahead, int marks, int length)
    {
        var character = ahead switch
        {
            "a-digit" => "7",
            "a-space" => " ",
            "a-letter" => "x",
            "a-two-byte-character" => "é",
            "a-three-byte-character" => ((char)0x3042).ToString(),
            "a-pair" => char.ConvertFromUtf32(0x1F389),
            "a-lone-high-surrogate" => ((char)0xD83C).ToString(),
            _ => ((char)0xDE00).ToString(),
        };
        return Repeat("order 20260915 item 0000123456 qty 42" + character + new string((char)0xFEFF, marks), length);
    }

    /// <summary>Runs of 200 marks after five kinds of character a cut kept off one leaves at the head of the next chunk. Every budget runs out deep inside a run, so every close resumes; <see cref="KeptOffCorpus"/> pins the price in the 32-bit lanes.</summary>
    public static IEnumerable<string> MarkRuns() => ["after-digits", "after-a-space", "after-a-letter", "after-a-three-byte-character", "after-a-pair"];

    internal static string MarkRun(string name)
    {
        var ahead = name switch
        {
            "after-digits" => "0123456789012345678901234567890123456789",
            "after-a-space" => "order 20260915 item 0000123456 qty 42 ",
            "after-a-letter" => "order 20260915 item 0000123456 qty 42x",
            "after-a-three-byte-character" => "order 20260915 item 0000123456 qty 42" + (char)0x3042,
            _ => "order 20260915 item 0000123456 qty 42" + char.ConvertFromUtf32(0x1F389),
        };
        return Repeat(ahead + new string((char)0xFEFF, 200), 15_000);
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
        await Assert.That(actual.AsSpan(0, count).ToArray()).IsEquivalentTo(expected.AsSpan(0, count).ToArray(), CollectionOrdering.Matching);
        await Assert.That(batches).IsGreaterThan(0);
    }

    [Test]
    [MethodDataSource(nameof(Corpus))]
    [MethodDataSource(nameof(KeptOffCorpus))]
    public async Task WalkLanes_EndsEveryChunkWhereTheScalarWalkDoes(string name, string text)
    {
        if (!Vector256.IsHardwareAccelerated && !System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported)
            return;
        var charset = TextAnalyzer.Analyze(text, EciMode.Default).EciMode;
        var scalarEnds = new int[StructuredAppendPlanner.MaxSymbols];
        var laneEnds = new int[8 * StructuredAppendPlanner.MaxSymbols];
        var counts = new int[8];
        var worstApart = 0;
        // The last version below the first count indicator edge, both sides of the second edge and the largest version; the full batch and the smallest.
        // A mid-band version and a middle lane count caught no planted lane fault the others miss; the spacing of 4 did.
        foreach (var version in new[] { 9, 26, 27, 40 })
        {
            var capacity = StructuredAppendPlanner.Capacity(version, QREccLevel.L);
            foreach (var spacing in new[] { 1, 4, 13 })
                foreach (var lanes in new[] { 8, 2 })
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
        if (!Vector256.IsHardwareAccelerated && !System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported)
            return;
        await AssertAMarkInALanesLastCharactersIsPricedAsTheScalarWalkDoes(StructuredAppendPlanner.WalkLanes);
    }

    /// <summary>
    /// The text ends in digits and a mark, which the vector loop reads last and where a Byte run must not open: the filler walks
    /// each lane's last chunk across its budget, and the shifts put budgets within the few bits that opening one would save.
    /// </summary>
    internal static async Task AssertAMarkInALanesLastCharactersIsPricedAsTheScalarWalkDoes(LaneWalk walk)
    {
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

                    // Every fifth filler still catches the mark rule dropped at the last character.
                    for (var filler = 0; filler < 250; filler += 5)
                    {
                        var text = Repeat("order 20260915 item 0000123456 qty 42 " + new string(mark, 6), 1_500) + new string('x', filler) + new string('7', 30) + mark;
                        await Assert.That(walk(text, charset, version, budgets, limit, 0, 0, counts, laneEnds, out _)).IsTrue();
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
    [MethodDataSource(nameof(MarkRuns))]
    public async Task WalkLanes_ACutKeptOffARunOfMarks_LeavesTheLaneAStepBehind(string name)
    {
        if (!Vector256.IsHardwareAccelerated && !System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported)
            return;
        await AssertLanesAreApartOnlyAsLongAsTheirClosesSetThemBack(name, MarkRun(name), 40, StructuredAppendPlanner.WalkLanes, everyCloseResumes: true);
    }

    [Test]
    [MethodDataSource(nameof(KeptOffAhead))]
    public async Task WalkLanes_ACutAtARunsFirstMarkReReadsIt_AndOneAtItsSecondResumes(string ahead)
    {
        if (!Vector256.IsHardwareAccelerated && !System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported)
            return;
        await AssertLanesAreApartOnlyAsLongAsTheirClosesSetThemBack(ahead, KeptOffRun(ahead, 24, 9_000), 9, StructuredAppendPlanner.WalkLanes, everyCloseResumes: false);
    }

    [Test]
    [Arguments(8_988)]
    [Arguments(8_989)]
    public async Task WalkLanes_ClosesNearTheEnd_CatchUpInTheVectorLoop(int length)
    {
        // The last places lie two and three characters from the end.
        if (!Vector256.IsHardwareAccelerated && !System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported)
            return;
        await AssertLanesAreApartOnlyAsLongAsTheirClosesSetThemBack($"a-pair-{length}", KeptOffRun("a-pair", 24, length), 9, StructuredAppendPlanner.WalkLanes, everyCloseResumes: false);
    }

    internal delegate bool LaneWalk(ReadOnlySpan<char> text, EciMode charset, int version, ReadOnlySpan<int> budgets, int limit, int placed, int start, Span<int> counts, Span<int> laneEnds, out int apartSteps);

    /// <summary>
    /// The lanes that run out at one place close in one step and fall behind by what they re-read: the character that did not fit when marks lie between the cut and it, which they price instead, else everything from the cut.
    /// A step apart levels a step of that, so the steps apart are their sum while some lane waits at each place and the scalar finish takes none. A lane that re-read from every cut would be apart a step more for each character from the cut up to the place, and one that also resumed at a run's first mark a step or two less there.
    /// Walked at the limit and below it, where the lanes past the limit keep closing to the end of the text.
    /// </summary>
    internal static async Task AssertLanesAreApartOnlyAsLongAsTheirClosesSetThemBack(string name, string text, int version, LaneWalk walk, bool everyCloseResumes)
    {
        var charset = TextAnalyzer.Analyze(text, EciMode.Default).EciMode;
        var capacity = StructuredAppendPlanner.Capacity(version, QREccLevel.L);
        // A mark is 24 bits, so neighbouring lanes close at the same mark or the next one.
        var budgets = Enumerable.Range(0, 8).Select(lane => capacity - 6 * (7 - lane)).ToArray();

        // Where each budget runs out, before the cut is kept off a run, and how far behind the close leaves the lane.
        // A lane past the limit keeps closing to the end of the text, so every chunk of the whole walk counts.
        var allEnds = new int[256];
        var setBack = new Dictionary<int, int>();
        var lanesAt = new Dictionary<int, int>();
        int atFirstMark = 0, atSecondMark = 0;
        for (var lane = 0; lane < 8; lane++)
        {
            var chunks = StructuredAppendPlanner.CountChunks(text, charset, false, QRSegmentation.Optimal, version, budgets[lane], allEnds.Length, allEnds);
            await Assert.That(chunks).IsLessThanOrEqualTo(allEnds.Length).Because(name);
            for (int k = 0, from = 0; k < chunks - 1; from = allEnds[k], k++)
            {
                var end = allEnds[k];
                var width = char.IsHighSurrogate(text[end]) && char.IsLowSurrogate(text[end + 1]) ? 2 : 1;
                var runsOut = StructuredAppendPlanner.LongestChunkEnd(text, from, charset, version, QRSegmentation.Optimal, false, budgets[lane]);
                var marksBetween = runsOut - end - width;
                var back = marksBetween >= 1 ? 1 : 1 + (runsOut - end);
                var because = $"{name} budget {budgets[lane]}, chunk {k}";
                if (everyCloseResumes)
                {
                    await Assert.That(text[runsOut]).IsEqualTo((char)0xFEFF).Because(because);
                    await Assert.That(marksBetween).IsGreaterThanOrEqualTo(1).Because(because);
                }
                if (text[runsOut] == (char)0xFEFF)
                {
                    atFirstMark += marksBetween == 0 ? 1 : 0;
                    atSecondMark += marksBetween == 1 ? 1 : 0;
                }
                // Lanes that run out at one place share its cut.
                if (setBack.TryGetValue(runsOut, out var seen))
                    await Assert.That(back).IsEqualTo(seen).Because(because);
                setBack[runsOut] = back;
                lanesAt[runsOut] = lanesAt.GetValueOrDefault(runsOut) + 1;
                // The vector loop ends when the lane furthest ahead reaches the end, so lanes that close on the last character re-read it in scalar code, apart for no step.
                await Assert.That(runsOut).IsLessThan(text.Length - 1).Because(because);
            }
        }
        // A place where every lane closes leaves none ahead to wait, and its set-back takes no step apart.
        await Assert.That(lanesAt.Values.Max()).IsLessThan(8).Because(name);
        if (!everyCloseResumes)
        {
            // Both sides of where the resume starts: budgets that run out at a run's first mark and at its second.
            await Assert.That(atFirstMark).IsGreaterThan(0).Because(name);
            await Assert.That(atSecondMark).IsGreaterThan(0).Because(name);
        }
        var setBackSum = setBack.Values.Sum();

        foreach (var limit in new[] { StructuredAppendPlanner.MaxSymbols, 4 })
        {
            var counts = new int[8];
            var laneEnds = new int[8 * StructuredAppendPlanner.MaxSymbols];
            var scalarEnds = new int[StructuredAppendPlanner.MaxSymbols];
            await Assert.That(walk(text, charset, version, budgets, limit, 0, 0, counts, laneEnds, out var apart)).IsTrue().Because($"{name} limit {limit}");
            for (var lane = 0; lane < 8; lane++)
            {
                var because = $"{name} limit {limit} budget {budgets[lane]}";
                var expected = StructuredAppendPlanner.CountChunks(text, charset, false, QRSegmentation.Optimal, version, budgets[lane], limit, scalarEnds);
                if (limit < StructuredAppendPlanner.MaxSymbols)
                    await Assert.That(expected).IsGreaterThan(limit).Because(because);
                await Assert.That(counts[lane]).IsEqualTo(expected > limit ? limit + 1 : expected).Because(because);
                for (var k = 0; k < Math.Min(expected, limit); k++)
                    await Assert.That(laneEnds[lane * StructuredAppendPlanner.MaxSymbols + k]).IsEqualTo(scalarEnds[k]).Because($"{because}, chunk {k}");
            }

            await Assert.That(apart).IsGreaterThan(0).Because($"{name} limit {limit}");
            await Assert.That(apart).IsEqualTo(setBackSum).Because($"{name} limit {limit}");
        }
    }

    [Test]
    public async Task WalkLanes_ARunNoChunkHolds_StopsALiveLaneAndLetsAFailedOneThrough()
    {
        if (!Vector256.IsHardwareAccelerated && !System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported)
            return;
        await AssertARunNoChunkHoldsStopsALiveLaneAndLetsAFailedOneThrough(StructuredAppendPlanner.WalkLanes);
    }

    /// <summary>
    /// 2,000 marks are more than a version 9 symbol holds. A lane that still counts reports the text unplannable, as the
    /// scalar walk does; one already past its limit reports nothing more and walks on, so the batch still answers.
    /// </summary>
    internal static async Task AssertARunNoChunkHoldsStopsALiveLaneAndLetsAFailedOneThrough(LaneWalk walk)
    {
        // Few enough chunks ahead of the run that no lane is past the larger limit when it gets there, and more than the smaller one.
        var text = Repeat("0123456789012345678901234567890123456789" + new string((char)0xFEFF, 40), 480) + new string((char)0xFEFF, 2_000) + Repeat("0123456789", 500);
        var charset = TextAnalyzer.Analyze(text, EciMode.Default).EciMode;
        const int version = 9;
        var capacity = StructuredAppendPlanner.Capacity(version, QREccLevel.L);
        var budgets = Enumerable.Range(0, 8).Select(lane => capacity - 6 * (7 - lane)).ToArray();
        var counts = new int[8];
        var laneEnds = new int[8 * StructuredAppendPlanner.MaxSymbols];
        var scalarEnds = new int[64];

        await Assert.That(StructuredAppendPlanner.CountChunks(text, charset, false, QRSegmentation.Optimal, version, budgets[0], 64, scalarEnds)).IsEqualTo(int.MaxValue);
        await Assert.That(walk(text, charset, version, budgets, StructuredAppendPlanner.MaxSymbols, 0, 0, counts, laneEnds, out _)).IsFalse();

        const int limit = 2;
        await Assert.That(walk(text, charset, version, budgets, limit, 0, 0, counts, laneEnds, out _)).IsTrue();
        for (var lane = 0; lane < 8; lane++)
        {
            await Assert.That(StructuredAppendPlanner.CountChunks(text, charset, false, QRSegmentation.Optimal, version, budgets[lane], limit, scalarEnds)).IsEqualTo(limit + 1);
            await Assert.That(counts[lane]).IsEqualTo(limit + 1);
            for (var k = 0; k < limit; k++)
                await Assert.That(laneEnds[lane * StructuredAppendPlanner.MaxSymbols + k]).IsEqualTo(scalarEnds[k]);
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
        // The whole range, the top version alone, and a range across each count indicator band's edge, at the largest and the smallest capacity:
        // the ranges inside or across two bands and the middle levels caught no planted fault these miss.
        foreach (var (minVersion, maxVersion) in new[] { (1, 40), (40, 40), (9, 12), (26, 28) })
            foreach (var ecc in new[] { QREccLevel.L, QREccLevel.H })
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
