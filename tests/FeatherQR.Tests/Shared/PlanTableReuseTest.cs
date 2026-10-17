using FeatherQR.Internals;
using FeatherQR.Internals.RmQR;
using FeatherQR.Internals.StandardQR;
using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// The plan without FNC1 or Kanji built from the table the version scan left behind (standardqr-encoder.md,
/// "Mixed-mode segmentation"): the scan prices a band with the program, and the build reads that run's table
/// instead of running the program again, as the Kanji plan and the plan under FNC1 do. The plan must be the one
/// the build makes on its own, run for run, and the scan must choose what it chose without a table.
/// </summary>
/// <remarks>
/// The scan prices once per count indicator band and accepts the first version that holds the band's cost, so the
/// accepting run is always the last one. A table and a state kept from an earlier band's run can both be wrong,
/// so the corpus holds a text whose rejected band ends in another state than the accepting one, where the walk
/// back from the wrong state gives another plan. UTF-8 texts take the program's general loop, with a byte order
/// mark inside the text and surrogate pairs among them, and Kanji is off, so no text takes the Kanji scan.
/// </remarks>
public class PlanTableReuseTest
{
    private static readonly QREccLevel[] Levels = [QREccLevel.L, QREccLevel.M, QREccLevel.Q, QREccLevel.H];

    // U+FEFF, spelled out: as a literal it is invisible in the source.
    private static readonly string Bom = ((char)0xFEFF).ToString();

    /// <summary>Latin texts shaped like GS1 labels and the segmentation benchmark, the text that exposes a kept state, two fixed UTF-8 texts (one with byte order marks and an emoji, one the benchmark's utf8-60), then random Latin and UTF-8 strings.</summary>
    private static IEnumerable<string> Texts(int maxLength, int randomCount)
    {
        yield return "0109520123456788" + "17251231" + "10ABC123";
        yield return "00195201234567891232" + "0209520123456788" + "11250504" + "3725X" + "10ABC123";
        yield return "0109520123456788" + "3103000195" + "39220299X" + "17251231";
        yield return "https://example.com/item?id=123456789012345678901234567890";
        yield return new string('a', 60) + new string('7', 60);
        yield return string.Concat(Enumerable.Repeat("a7", 60));
        // 237 characters whose tail "1234567AB" is a Numeric run and an Alphanumeric pair at the first band's widths
        // and one Alphanumeric run of nine at the second's, so the rejected band ends in Alnum0 and the accepting
        // band in Alnum1.
        yield return string.Concat(Enumerable.Repeat("1234567890ab", 19)) + "1234567AB";
        yield return "order 20260915 item 0000123456 qty 42 " + Bom + "日本語 7777 " + Bom + "😀 123456";
        yield return string.Concat(Enumerable.Repeat("日本7777", 10));

        const string Latin = "0123456789012345678ABCDEFGHIJKLMNOPQRSTUVWXYZ abcxyz-./:$%*+";
        var random = new Random(68);
        for (var i = 0; i < randomCount; i++)
        {
            if (i % 4 == 3)
            {
                yield return Utf8Stretches(random);
                continue;
            }

            var length = random.Next(1, maxLength + 1);
            var chars = new char[length];
            for (var j = 0; j < length; j++)
                chars[j] = Latin[random.Next(Latin.Length)];
            yield return new string(chars);
        }
    }

    /// <summary>
    /// A UTF-8 text in stretches, so a digit run long enough to repay its header stands beside characters of two and
    /// three bytes, a pair or a byte order mark: the shape whose split the general loop plans.
    /// </summary>
    private static string Utf8Stretches(Random random)
    {
        const string Wide = "あいう日本語テスト東京é" + "😀";
        var text = new System.Text.StringBuilder();
        var stretches = random.Next(2, 7);
        for (var s = 0; s < stretches; s++)
        {
            switch (random.Next(3))
            {
                case 0:
                    text.Append(Pick(random, "0123456789", random.Next(3, 16)));
                    break;
                case 1:
                    text.Append(Pick(random, "ABCDEFGHIJ abcxyz", random.Next(1, 7)));
                    break;
                default:
                    text.Append(Pick(random, Wide, random.Next(1, 6)));
                    if (random.Next(4) == 0)
                        text.Append(Bom);
                    break;
            }
        }
        return text.ToString();
    }

    private static string Pick(Random random, string alphabet, int count)
    {
        var chars = new char[count];
        for (var i = 0; i < count; i++)
            chars[i] = alphabet[random.Next(alphabet.Length)];
        return new string(chars);
    }

    [Test]
    public async Task StandardQr_PlanFromTheScansTable_IsThePlanTheBuildMakes()
    {
        var bands = new int[3];
        var afterABandRejection = 0;
        var afterARejectionInAnotherState = 0;
        var utf8Splits = 0;

        foreach (var text in Texts(600, 300))
        {
            var analysis = TextAnalyzer.Analyze(text, EciMode.Default, allowKanji: false, planKanji: true);
            await Assert.That(analysis.KanjiPlannable).IsFalse().Because("Kanji is off, so no text takes the Kanji scan");
            var eciBits = analysis.EciMode == EciMode.Default ? 0 : 12;

            var table = new byte[text.Length * ModeSegmenter.ParentBytesPerChar];
            var fromTable = new ModeSegment[text.Length];
            var fresh = new ModeSegment[text.Length];
            foreach (var level in Levels)
            {
                foreach (var (min, max) in new[] { (1, 40), (10, 40), (5, 30) })
                {
                    var because = $"「{text}」 at {level}, versions {min}-{max}";
                    var selected = QRSegmentPlanner.TrySelectVersion(text, in analysis, level, min, max, table, default, out var version, out var useSegments, out var kanjiPlan, out var finalState, out var plannedBits);
                    var expected = QRSegmentPlanner.TrySelectVersion(text, in analysis, level, min, max, out var expectedVersion, out var expectedUseSegments, out var expectedKanjiPlan);
                    await Assert.That((selected, version, useSegments, kanjiPlan)).IsEqualTo((expected, expectedVersion, expectedUseSegments, expectedKanjiPlan)).Because(because);
                    if (!useSegments)
                        continue;

                    Array.Clear(fromTable);
                    Array.Clear(fresh);
                    var built = QRSegmentPlanner.TryBuildPlan(text, analysis.EciMode, version, level, table, finalState, plannedBits, fromTable, out var count);
                    var expectedBuilt = QRSegmentPlanner.TryBuildPlan(text, analysis.EciMode, version, level, fresh, out var expectedCount);
                    await Assert.That(built).IsEqualTo(expectedBuilt).Because(because);
                    await Assert.That(fromTable.AsSpan(0, count).ToArray()).IsEquivalentTo(fresh.AsSpan(0, expectedCount).ToArray(), CollectionOrdering.Matching).Because(because);
                    if (analysis.EciMode == EciMode.Utf8)
                        utf8Splits++;

                    var band = version < 10 ? 0 : version < 27 ? 1 : 2;
                    bands[band]++;
                    var firstBand = min < 10 ? 0 : min < 27 ? 1 : 2;
                    var firstBandEnd = BandEnd(firstBand, max);
                    if (band > firstBand && QRSegmentPlanner.TrivialLowerBoundBits(text, analysis.EciMode) + eciBits <= QRCodeConstants.GetEccInfo(firstBandEnd, level).TotalDataCodewords * 8)
                    {
                        afterABandRejection++;
                        // The state the rejected band's run ended in. Where it differs from the accepting run's, the
                        // accepting band's table walked back from it gives another plan, which the build re-prices and
                        // refuses where it costs more.
                        ModeSegmenter.ComputeCosts(text, analysis.EciMode, 4, EncodingMode.Numeric.GetCountIndicatorLength(firstBandEnd), EncodingMode.Alphanumeric.GetCountIndicatorLength(firstBandEnd), EncodingMode.Byte.GetCountIndicatorLength(firstBandEnd), default, out var rejectedState);
                        if (rejectedState != finalState)
                            afterARejectionInAnotherState++;
                    }
                }
            }
        }

        await Assert.That(bands[0]).IsGreaterThan(20);
        await Assert.That(bands[1]).IsGreaterThan(20);
        await Assert.That(utf8Splits).IsGreaterThan(20).Because("the general loop, which prices each character at its byte count, keeps a table too");
        await Assert.That(afterABandRejection).IsGreaterThan(10).Because("a later band accepted after an earlier band's run was priced and rejected, where a table kept from the earlier run can be wrong");
        await Assert.That(afterARejectionInAnotherState).IsGreaterThan(0).Because("a later band accepted after an earlier band's run ended in another state, where a state kept from the earlier run would walk the table to another plan");
    }

    [Test]
    public async Task RmQr_PlanFromTheScansTable_IsThePlanTheBuildMakes()
    {
        var fromTheTable = 0;
        var builtAgain = 0;
        var afterAPricedRejection = 0;
        var strategies = new[] { RmQRFitStrategy.MinimizeArea, RmQRFitStrategy.MinimizeWidth, RmQRFitStrategy.MinimizeHeight };

        foreach (var text in Texts(360, 300))
        {
            var analysis = TextAnalyzer.Analyze(text, EciMode.Default, allowKanji: false, planKanji: true);
            var eciBits = analysis.EciMode == EciMode.Default ? 0 : RmQRConstants.ModeIndicatorLength + 8;
            var table = new byte[text.Length * ModeSegmenter.ParentBytesPerChar];
            var fromTable = new ModeSegment[RmQRSegmentPlanner.MaxSegments];
            var fresh = new ModeSegment[RmQRSegmentPlanner.MaxSegments];
            foreach (var level in new[] { RmQREccLevel.M, RmQREccLevel.H })
            {
                foreach (var strategy in strategies)
                {
                    var because = $"「{text}」 at {level}, {strategy}";
                    var selected = RmQRSegmentPlanner.TrySelectVersion(text, in analysis, level, null, strategy, null, table, out var version, out var useSegments, out var kanjiPlan, out var tableHoldsPlan, out var finalState, out var plannedBits);
                    var expected = RmQRSegmentPlanner.TrySelectVersion(text, in analysis, level, null, strategy, null, out var expectedVersion, out var expectedUseSegments, out var expectedKanjiPlan);
                    await Assert.That((selected, version, useSegments, kanjiPlan)).IsEqualTo((expected, expectedVersion, expectedUseSegments, expectedKanjiPlan)).Because(because);
                    if (!useSegments)
                        continue;

                    Array.Clear(fresh);
                    var expectedBuilt = RmQRSegmentPlanner.TryBuildPlan(text, analysis.EciMode, version, level, fresh, out var expectedCount);
                    if (!tableHoldsPlan)
                    {
                        builtAgain++;
                        continue;
                    }

                    fromTheTable++;
                    Array.Clear(fromTable);
                    var built = RmQRSegmentPlanner.TryBuildPlan(text, analysis.EciMode, version, level, table, finalState, plannedBits, fromTable, out var count);
                    await Assert.That(built).IsEqualTo(expectedBuilt).Because(because);
                    await Assert.That(fromTable.AsSpan(0, count).ToArray()).IsEquivalentTo(fresh.AsSpan(0, expectedCount).ToArray(), CollectionOrdering.Matching).Because(because);

                    // The first candidate of the order past the trivial bound, where the scan made its first run. When
                    // that run did not fit, the accepting run came later, and a table kept from the first run would
                    // hold another version's plan.
                    var first = FirstPricedCandidate(text, in analysis, level, strategy, eciBits);
                    if (first is { } candidate && candidate != version)
                    {
                        var cost = RmQRSegmentPlanner.MinimumPayloadBits(text, analysis.EciMode, RmQRConstants.GetCountIndicatorLength(candidate, EncodingMode.Numeric), RmQRConstants.GetCountIndicatorLength(candidate, EncodingMode.Alphanumeric), RmQRConstants.GetCountIndicatorLength(candidate, EncodingMode.Byte));
                        if (cost + eciBits > 8 * RmQRConstants.GetDataCodewordCount(candidate, level))
                            afterAPricedRejection++;
                    }
                }
            }
        }

        await Assert.That(fromTheTable).IsGreaterThan(50);
        await Assert.That(afterAPricedRejection).IsGreaterThan(10).Because("a candidate accepted after the scan's first run was priced and rejected, where a table kept from the first run would be wrong");
        await Assert.That(builtAgain).IsGreaterThan(0).Because("a candidate accepted on a remembered cost whose run was not the last, where the table belongs to another width triple");
    }

    /// <summary>The first version of the strategy's order, ahead of the single-mode fit, whose capacity clears the trivial bound: where the scan makes its first run.</summary>
    private static RmQRVersion? FirstPricedCandidate(ReadOnlySpan<char> text, in TextAnalysisResult analysis, RmQREccLevel level, RmQRFitStrategy strategy, int eciBits)
    {
        var hasSingle = RmQRVersionSelector.TrySelectAutoFit(analysis.EncodingMode, analysis.DataLength, analysis.EciMode, level, strategy, null, out var single);
        var trivial = RmQRSegmentPlanner.TrivialLowerBoundBits(text, analysis.EciMode) + eciBits;
        foreach (var rank in RmQRVersionSelector.GetFitOrder(strategy))
        {
            var candidate = (RmQRVersion)rank;
            if (hasSingle && candidate == single)
                return null;
            if (8 * RmQRConstants.GetDataCodewordCount(candidate, level) >= trivial)
                return candidate;
        }

        return null;
    }

    /// <summary>The last version of a count-indicator band within the window.</summary>
    private static int BandEnd(int band, int max) => Math.Min(max, band == 0 ? 9 : band == 1 ? 26 : 40);
}
