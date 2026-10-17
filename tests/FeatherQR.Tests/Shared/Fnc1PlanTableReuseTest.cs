using FeatherQR.Internals;
using FeatherQR.Internals.RmQR;
using FeatherQR.Internals.StandardQR;
using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// The plan under FNC1 built from the table the version scan left behind (specs/standardqr-encoder.md, FNC1):
/// the scan prices a candidate with the FNC1 program, and the build reads that run's table instead of running the
/// program again. The plan must be the one the build makes on its own, run for run, and the scan must choose what
/// it chose without a table.
/// </summary>
/// <remarks>
/// Standard QR prices once per count indicator band and accepts the first version that holds the band's cost, so
/// the accepting run is always the last one. A table and a state kept from an earlier band's run can both be
/// wrong, so the corpus holds a text whose rejected band ends in another state than the accepting one, where the
/// walk back from the wrong state gives another plan. rMQR walks a strategy's order and remembers a cost per
/// width triple, so a candidate can be accepted on a remembered cost whose run was not the last, and the table
/// then belongs to another triple. The scan says whether the table holds the plan, and the build runs the program
/// itself where it does not. The tests count every one of these outcomes.
/// </remarks>
public class Fnc1PlanTableReuseTest
{
    private static readonly QREccLevel[] StandardLevels = [QREccLevel.L, QREccLevel.M, QREccLevel.Q, QREccLevel.H];
    private static readonly RmQREccLevel[] RmQrLevels = [RmQREccLevel.M, RmQREccLevel.H];
    private static readonly Fnc1Mode[] Modes = [Fnc1Mode.FirstPosition, Fnc1Mode.SecondPosition(37)];

    /// <summary>GS1-shaped texts: the labels, the standard's examples, and random element strings of digits, letters, separators and percents.</summary>
    private static IEnumerable<string> Texts(int maxLength, int randomCount)
    {
        yield return "0109520123456788" + "17251231" + "10ABC123";
        yield return "00195201234567891232" + "0209520123456788" + "11250504" + "3725\u001D" + "10ABC123";
        yield return "0109520123456788" + "3103000195" + "39220299\u001D" + "17251231";
        yield return "0109520123456788" + "17251231" + "10ABC123\u001D" + "21SN123456789";
        yield return "01049123451234591597033130128\u001D10ABC123";
        yield return "AB%\u001DCD";
        // 237 characters whose tail "1234567AB" is a Numeric run and an Alphanumeric pair at the first band's widths
        // and one Alphanumeric run of nine at the second's, so the rejected band ends in Alnum0 and the accepting
        // band in Alnum1.
        yield return string.Concat(Enumerable.Repeat("1234567890ab", 19)) + "1234567AB";

        const string Alphabet = "0123456789012345678ABCDEFGHIJKLMNOPQRSTUVWXYZ\u001D\u001D%abcxyz-./";
        var random = new Random(68);
        for (var i = 0; i < randomCount; i++)
        {
            var length = random.Next(1, maxLength + 1);
            var chars = new char[length];
            for (var j = 0; j < length; j++)
                chars[j] = Alphabet[random.Next(Alphabet.Length)];
            yield return new string(chars);
        }
    }

    [Test]
    public async Task StandardQr_PlanFromTheScansTable_IsThePlanTheBuildMakes()
    {
        var bands = new int[3];
        var afterABandRejection = 0;
        var afterARejectionInAnotherState = 0;

        foreach (var text in Texts(600, 300))
        {
            foreach (var fnc1 in Modes)
            {
                var analysis = TextAnalyzer.AnalyzeFnc1(text, EciMode.Default, allowKanji: false);
                var table = new byte[text.Length * ModeSegmenter.Fnc1ParentBytesPerChar];
                var fromTable = new ModeSegment[text.Length];
                var fresh = new ModeSegment[text.Length];
                foreach (var level in StandardLevels)
                {
                    foreach (var (min, max) in new[] { (1, 40), (10, 40), (5, 30) })
                    {
                        var because = $"「{text}」 {fnc1} at {level}, versions {min}-{max}";
                        var selected = QRSegmentPlanner.TrySelectVersion(text, in analysis, level, min, max, table, fnc1, out var version, out var useSegments, out var kanjiPlan, out var finalState, out var plannedBits);
                        var expected = QRSegmentPlanner.TrySelectVersion(text, in analysis, level, min, max, default, fnc1, out var expectedVersion, out var expectedUseSegments, out var expectedKanjiPlan, out _, out _);
                        await Assert.That((selected, version, useSegments, kanjiPlan)).IsEqualTo((expected, expectedVersion, expectedUseSegments, expectedKanjiPlan)).Because(because);
                        if (!useSegments)
                            continue;

                        Array.Clear(fromTable);
                        Array.Clear(fresh);
                        var built = QRSegmentPlanner.TryBuildPlanFnc1(text, analysis.EciMode, version, level, fnc1, table, finalState, plannedBits, fromTable, out var count);
                        var expectedBuilt = QRSegmentPlanner.TryBuildPlan(text, analysis.EciMode, version, level, fresh, out var expectedCount, out _, fnc1);
                        await Assert.That(built).IsEqualTo(expectedBuilt).Because(because);
                        await Assert.That(fromTable.AsSpan(0, count).ToArray()).IsEquivalentTo(fresh.AsSpan(0, expectedCount).ToArray(), CollectionOrdering.Matching).Because(because);

                        var band = version < 10 ? 0 : version < 27 ? 1 : 2;
                        bands[band]++;
                        var firstBand = min < 10 ? 0 : min < 27 ? 1 : 2;
                        var firstBandEnd = BandEnd(firstBand, max);
                        if (band > firstBand && QRSegmentPlanner.TrivialLowerBoundBitsFnc1(text, analysis.EciMode) + fnc1.StandardQrHeaderBits <= QRCodeConstants.GetEccInfo(firstBandEnd, level).TotalDataCodewords * 8)
                        {
                            afterABandRejection++;
                            // The state the rejected band's run ended in. Where it differs from the accepting run's, the
                            // accepting band's table walked back from it gives another plan, which the build re-prices
                            // and refuses where it costs more.
                            ModeSegmenter.ComputeCostsFnc1(text, analysis.EciMode, 4, EncodingMode.Numeric.GetCountIndicatorLength(firstBandEnd), EncodingMode.Alphanumeric.GetCountIndicatorLength(firstBandEnd), EncodingMode.Byte.GetCountIndicatorLength(firstBandEnd), default, out var rejectedState);
                            if (rejectedState != finalState)
                                afterARejectionInAnotherState++;
                        }
                    }
                }
            }
        }

        await Assert.That(bands[0]).IsGreaterThan(20);
        await Assert.That(bands[1]).IsGreaterThan(20);
        await Assert.That(afterABandRejection).IsGreaterThan(10).Because("a later band accepted after an earlier band's run was priced and rejected, where a table kept from the earlier run can be wrong");
        await Assert.That(afterARejectionInAnotherState).IsGreaterThan(0).Because("a later band accepted after an earlier band's run ended in another state, where a state kept from the earlier run would walk the table to another plan");
    }

    [Test]
    public async Task RmQr_PlanFromTheScansTable_IsThePlanTheBuildMakes()
    {
        var fromTheTable = 0;
        var builtAgain = 0;

        foreach (var text in Texts(Gs1TextLengths.RmQr, 300))
        {
            foreach (var fnc1 in Modes)
            {
                var analysis = TextAnalyzer.AnalyzeFnc1(text, EciMode.Default, allowKanji: false);
                var table = new byte[text.Length * ModeSegmenter.Fnc1ParentBytesPerChar];
                var fromTable = new ModeSegment[RmQRSegmentPlanner.MaxSegments];
                var fresh = new ModeSegment[RmQRSegmentPlanner.MaxSegments];
                foreach (var level in RmQrLevels)
                {
                    foreach (var strategy in new[] { RmQRFitStrategy.MinimizeArea, RmQRFitStrategy.MinimizeWidth, RmQRFitStrategy.MinimizeHeight })
                    {
                        var because = $"「{text}」 {fnc1} at {level}, {strategy}";
                        var selected = RmQRSegmentPlanner.TrySelectVersionFnc1(text, in analysis, level, null, strategy, null, fnc1, table, out var version, out var useSegments, out var tableHoldsPlan, out var finalState, out var plannedBits);
                        var expected = RmQRSegmentPlanner.TrySelectVersionFnc1(text, in analysis, level, null, strategy, null, fnc1, out var expectedVersion, out var expectedUseSegments);
                        await Assert.That((selected, version, useSegments)).IsEqualTo((expected, expectedVersion, expectedUseSegments)).Because(because);
                        if (!useSegments)
                            continue;

                        Array.Clear(fresh);
                        var expectedBuilt = RmQRSegmentPlanner.TryBuildPlanFnc1(text, analysis.EciMode, version, level, fnc1, fresh, out var expectedCount);
                        if (!tableHoldsPlan)
                        {
                            builtAgain++;
                            continue;
                        }

                        fromTheTable++;
                        Array.Clear(fromTable);
                        var built = RmQRSegmentPlanner.TryBuildPlanFnc1(text, analysis.EciMode, version, level, fnc1, table, finalState, plannedBits, fromTable, out var count);
                        await Assert.That(built).IsEqualTo(expectedBuilt).Because(because);
                        await Assert.That(fromTable.AsSpan(0, count).ToArray()).IsEquivalentTo(fresh.AsSpan(0, expectedCount).ToArray(), CollectionOrdering.Matching).Because(because);
                    }
                }
            }
        }

        await Assert.That(fromTheTable).IsGreaterThan(50);
        await Assert.That(builtAgain).IsGreaterThan(0).Because("a candidate accepted on a remembered cost whose run was not the last, where the table belongs to another width triple");
    }

    /// <summary>The last version of a count-indicator band within the window.</summary>
    private static int BandEnd(int band, int max) => Math.Min(max, band == 0 ? 9 : band == 1 ? 26 : 40);

    private static class Gs1TextLengths
    {
        /// <summary>
        /// Past the Byte fit of every rMQR symbol at H and of the largest at M, and within what the largest holds in
        /// Numeric, so the scan runs both under a single-mode ceiling and without one.
        /// </summary>
        public const int RmQr = 180;
    }
}
