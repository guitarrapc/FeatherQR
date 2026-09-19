using System.Text;
using FeatherQR.Internals;
using FeatherQR.Internals.StandardQR;
namespace FeatherQR.Tests;

/// <summary>
/// What the writer of a Structured Append set takes from elsewhere instead of working out per symbol:
/// the plans of several chunks from one pass over them all (which have to be the plans each chunk
/// builds alone), and the planner's word that every chunk plans as one Byte run (which has to be
/// true of every chunk it is said about, and must not be said one digit too far). Then the set as a
/// whole, which has to be the same modules whichever way its plans were come by.
/// </summary>
public class StructuredAppendWriterPlanTest
{
    public static IEnumerable<(string Name, string Text)> Corpus() => StructuredAppendLaneWalkTest.Corpus();

#if NET8_0_OR_GREATER
    [Test]
    public async Task Set_OnArm64PlansMixedChunksTogether()
    {
        if (!System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported)
            return;

        var text = Repeat("order 20260915 item 0000123456 qty 42 ", 12_000);
        var options = new QRCodeGeneratorOptions { Segmentation = QRSegmentation.Optimal };
        QRCodeGenerator.LanePlanPasses = 0;
        var together = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.L, options, planTogether: true);
        var passes = QRCodeGenerator.LanePlanPasses;
        var alone = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.L, options, planTogether: false);

        await Assert.That(passes).IsGreaterThan(0);
        await Assert.That(together.Length).IsEqualTo(alone.Length);
        for (var i = 0; i < together.Length; i++)
            await Assert.That(together[i].GetRawData().AsSpan().SequenceEqual(alone[i].GetRawData())).IsTrue();
    }

    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task PlanChunks_GivesEveryChunkThePlanItBuildsAlone(string name, string text)
    {
        if (!ModeSegmenter.LanesAccelerated)
            return;

        var charset = TextAnalyzer.Analyze(text, EciMode.Default).EciMode;
        var random = new Random(text.Length);
        var starts = new int[ModeSegmenter.Lanes];
        var lengths = new int[ModeSegmenter.Lanes];
        var costs = new int[ModeSegmenter.Lanes];
        var states = new int[ModeSegmenter.Lanes];

        foreach (var version in new[] { 9, 12, 40 })
        {
            // Even pieces, uneven pieces, pieces of a character or two beside long ones, one piece
            // alone; cut anywhere, a surrogate pair included, since a piece is priced as the text it is.
            foreach (var (lanes, shape) in new[] { (8, 0), (8, 1), (5, 1), (3, 2), (2, 1), (1, 0), (8, 2) })
            {
                var at = random.Next(0, 50);
                var longest = 0;
                for (var lane = 0; lane < lanes; lane++)
                {
                    starts[lane] = at;
                    lengths[lane] = shape switch
                    {
                        0 => 400,
                        1 => random.Next(1, 600),
                        _ => lane % 3 == 0 ? random.Next(1, 3) : random.Next(300, 500),
                    };
                    at += lengths[lane];
                    longest = Math.Max(longest, lengths[lane]);
                }

                var table = new byte[longest * ModeSegmenter.LaneTableBytesPerChar];
                // What an earlier set left in a rented table must not be what makes a walk back come out right.
                random.NextBytes(table);
                QRSegmentPlanner.PlanChunks(text, starts.AsSpan(0, lanes), lengths.AsSpan(0, lanes), charset, version, table, costs, states);

                for (var lane = 0; lane < lanes; lane++)
                {
                    var chunk = text.AsSpan(starts[lane], lengths[lane]);
                    var because = $"{name} version {version}, lane {lane} of {lanes} (shape {shape}): {lengths[lane]} characters from {starts[lane]}";
                    var expectedPlan = new ModeSegment[chunk.Length];
                    var plan = new ModeSegment[chunk.Length];
                    var expected = QRSegmentPlanner.TryBuildPlan(chunk, charset, version, QREccLevel.L, expectedPlan, out var expectedCount, out var expectedBits);
                    var built = QRSegmentPlanner.TryBuildPlanFromLane(chunk, charset, version, QREccLevel.L, table, lane, states[lane], costs[lane], plan, out var count, out var bits);

                    await Assert.That(built).IsEqualTo(expected).Because(because);
                    await Assert.That(count).IsEqualTo(expectedCount).Because(because);
                    await Assert.That(bits).IsEqualTo(expectedBits).Because(because);
                    for (var i = 0; i < expectedCount; i++)
                    {
                        await Assert.That(plan[i].ModeIndex).IsEqualTo(expectedPlan[i].ModeIndex).Because($"{because}, run {i}");
                        await Assert.That(plan[i].Start).IsEqualTo(expectedPlan[i].Start).Because($"{because}, run {i}");
                        await Assert.That(plan[i].Length).IsEqualTo(expectedPlan[i].Length).Because($"{because}, run {i}");
                        await Assert.That(plan[i].UnitCount).IsEqualTo(expectedPlan[i].UnitCount).Because($"{because}, run {i}");
                    }
                }
            }
        }
    }
#endif

    public static IEnumerable<(string Name, string Text, bool OneRunPlans)> VerdictCorpus()
    {
        // At the thresholds: two digits, five alphanumerics, never more.
        yield return ("edge", Repeat("ab12cd,WORLD,xy;A1B2C,zz 99,q", 6_000), true);
        yield return ("edge-at-both-ends", "12" + Repeat("abc,HELLO;de 42 fg,", 5_000) + "xy,AB 12", true);
        yield return ("edge-utf8", Repeat("日本語の文章です。12 個、AB。", 5_000), true);
        yield return ("prose", Repeat("The quick brown fox jumps over the lazy dog. ", 9_000), true);
        // One past them: three digits tie with Byte, and the program gives a tie to the split.
        yield return ("three-digits", Repeat("abcdefgh,123;ijkl", 5_000), false);
        yield return ("six-alphanumerics", Repeat("abcdefgh,ABCDEF;ijkl", 5_000), false);
        // Not Byte content at all.
        yield return ("alphanumeric", Repeat("AB 12 CD ", 5_000), false);

        var random = new Random(20260918);
        const string other = "abcdefghijklmnopqrstuvwxyz,;!?()";
        const string alnum = "ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";
        for (var n = 0; n < 12; n++)
        {
            var sb = new StringBuilder();
            while (sb.Length < 4_000 + 500 * n)
            {
                for (var k = random.Next(1, 6); k > 0; k--)
                    sb.Append(other[random.Next(other.Length)]);
                var digits = 0;
                for (var k = random.Next(0, 6); k > 0; k--)
                {
                    if (digits < 2 && random.Next(3) == 0)
                    {
                        sb.Append((char)('0' + random.Next(10)));
                        digits++;
                    }
                    else
                    {
                        sb.Append(alnum[random.Next(alnum.Length)]);
                        digits = 0;
                    }
                }
            }
            yield return ($"random-{n}", sb.ToString(), true);
        }
    }

    [Test]
    [MethodDataSource(nameof(VerdictCorpus))]
    public async Task OneRunPlans_IsSaidOnlyWhereEveryChunkPlansAsOneByteRun(string name, string text, bool expected)
    {
        var analysis = TextAnalyzer.Analyze(text, EciMode.Default);
        var ends = new int[StructuredAppendPlanner.MaxSymbols];
        foreach (var (minVersion, maxVersion, ecc) in new[] { (1, 40, QREccLevel.L), (1, 9, QREccLevel.L), (10, 26, QREccLevel.H), (5, 20, QREccLevel.M) })
        {
            var because = $"{name} in versions {minVersion} to {maxVersion} at {ecc}";
            if (!StructuredAppendPlanner.TryPlan(text, ecc, analysis.EciMode, analysis.EncodingMode, false, QRSegmentation.Optimal, minVersion, maxVersion, ends, out var count, out var version, out _, out var oneRunPlans, out _, allowLanes: true))
                continue;

            await Assert.That(oneRunPlans).IsEqualTo(expected).Because(because);
            if (!oneRunPlans)
                continue;

            var start = 0;
            for (var i = 0; i < count; i++)
            {
                var chunk = text.AsSpan(start, ends[i] - start);
                start = ends[i];
                if (TextAnalyzer.Analyze(chunk, analysis.EciMode).EncodingMode != EncodingMode.Byte)
                    continue;

                var plan = new ModeSegment[chunk.Length];
                await Assert.That(QRSegmentPlanner.TryBuildPlan(chunk, analysis.EciMode, version, ecc, plan, out var runs)).IsTrue().Because($"{because}, chunk {i}");
                await Assert.That(runs).IsEqualTo(1).Because($"{because}, chunk {i}");
                await Assert.That(plan[0].ModeIndex).IsEqualTo((byte)2).Because($"{because}, chunk {i}");
            }
        }
    }

    [Test]
    public async Task OneRunPlans_StopsAtThreeDigitsBecauseTheProgramGivesTheTieToTheSplit()
    {
        // Seven bytes cost 4 + 8 + 56; four bytes and three digits cost (4 + 8 + 32) + (4 + 10 + 10): the same 68 bits.
        var plan = new ModeSegment[7];
        await Assert.That(QRSegmentPlanner.TryBuildPlan("xyz,123", EciMode.Default, 5, QREccLevel.L, plan, out var runs, out var bits)).IsTrue();
        await Assert.That(bits).IsEqualTo(68);
        await Assert.That(runs).IsEqualTo(2);
        await Assert.That(QRSegmentPlanner.PlanIsOneByteRun(3, 3)).IsFalse();
        await Assert.That(QRSegmentPlanner.PlanIsOneByteRun(2, 5)).IsTrue();
        await Assert.That(QRSegmentPlanner.PlanIsOneByteRun(2, 6)).IsFalse();
    }

    [Test]
    [MethodDataSource(nameof(VerdictCorpus))]
    public async Task Set_UnderTheVerdictIsTheSingleModeSet(string name, string text, bool oneRunPlans)
    {
        if (!oneRunPlans)
            return;

        // Every chunk's plan is its single-mode stream and the split was searched as Single, so the two sets are one.
        var optimal = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.M, new QRCodeGeneratorOptions { Segmentation = QRSegmentation.Optimal });
        var single = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.M, new QRCodeGeneratorOptions { Segmentation = QRSegmentation.Single });
        await Assert.That(optimal.Length).IsEqualTo(single.Length).Because(name);
        for (var i = 0; i < optimal.Length; i++)
            await Assert.That(optimal[i].GetRawData().AsSpan().SequenceEqual(single[i].GetRawData())).IsTrue().Because($"{name}, symbol {i}");
    }

    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task Set_IsTheSameModulesHoweverItsPlansWereComeBy(string name, string text)
    {
        foreach (var ecc in new[] { QREccLevel.L, QREccLevel.H })
        {
            foreach (var bom in new[] { false, true })
            {
                var options = new QRCodeGeneratorOptions { Segmentation = QRSegmentation.Optimal, Utf8Bom = bom };
                var together = QRCodeGenerator.CreateStructuredAppend(text, ecc, options, planTogether: true);
                var alone = QRCodeGenerator.CreateStructuredAppend(text, ecc, options, planTogether: false);
                await Assert.That(together.Length).IsEqualTo(alone.Length).Because($"{name} at {ecc}, bom {bom}");
                for (var i = 0; i < together.Length; i++)
                    await Assert.That(together[i].GetRawData().AsSpan().SequenceEqual(alone[i].GetRawData())).IsTrue().Because($"{name} at {ecc}, bom {bom}, symbol {i}");
            }
        }
    }

    private static string Repeat(string unit, int length)
    {
        var sb = new StringBuilder(length + unit.Length);
        while (sb.Length < length)
            sb.Append(unit);
        return sb.ToString(0, length);
    }
}
