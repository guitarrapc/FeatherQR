using FeatherQR.Internals;
using FeatherQR.Internals.MicroQR;
using FeatherQR.Internals.StandardQR;
using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// The Kanji plan built from the table the version scan left behind (standardqr-encoder.md, "Speed"): Standard QR and Micro QR price a Kanji plan with its program at the selected version's widths before they accept it, so the build reads that run's table instead of running the program again.
/// </summary>
/// <remarks>
/// The plan must be the one the build makes on its own, run for run, and the scan must choose what it chose without a table.
/// The scan can price more than one candidate before it accepts (Micro QR M3 then M4, Standard QR one run per count-indicator band), so the corpus has to reach an acceptance after a rejection, where a table kept from the first run would be the wrong one; the tests count those cases.
/// </remarks>
public class KanjiPlanTableReuseTest
{
    private static readonly MicroQREccLevel[] MicroLevels = [MicroQREccLevel.L, MicroQREccLevel.M, MicroQREccLevel.Q];
    private static readonly QREccLevel[] StandardLevels = [QREccLevel.L, QREccLevel.M, QREccLevel.Q, QREccLevel.H];

    private const string Cells = "日本語漢字東京あいうえおアイウエオ、。ー×ΩП─";

    /// <summary>
    /// The Kanji parity corpus, then random eligible texts of ASCII beside characters with a cell, then digits, a few characters with a cell and digits again.
    /// The last sit at Micro QR M3-L's 84 bits, where the screen lets M3 through and its run turns it down (「3771。え字3146」: screen 83, plan 86), because the screen prices a digit at 3⅓ bits and its stretch at Alphanumeric's header, and Numeric's header is a bit wider and a short group dearer.
    /// </summary>
    private static IEnumerable<string> Texts(int maxLength, int randomCount)
    {
        foreach (var (_, text) in ModeSegmenterKanjiParityTest.Corpus())
        {
            if (text.Length <= maxLength)
                yield return text;
        }

        const string Ascii = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:abcdefxyz!?#";
        var random = new Random(68);
        for (var i = 0; i < randomCount; i++)
        {
            var length = random.Next(2, maxLength + 1);
            var chars = new char[length];
            var stretch = 0;
            var cell = random.Next(2) == 0;
            for (var j = 0; j < length; j++)
            {
                if (stretch == 0)
                {
                    cell = !cell;
                    stretch = random.Next(1, 8);
                }
                chars[j] = cell ? Cells[random.Next(Cells.Length)] : Ascii[random.Next(Ascii.Length)];
                stretch--;
            }
            yield return new string(chars);
        }

        for (var i = 0; i < randomCount; i++)
            yield return Digits(random, random.Next(1, 6)) + Pick(random, Cells, random.Next(2, 6)) + Digits(random, random.Next(1, 6));
    }

    private static string Digits(Random random, int count) => Pick(random, "0123456789", count);

    private static string Pick(Random random, string alphabet, int count)
    {
        var chars = new char[count];
        for (var i = 0; i < count; i++)
            chars[i] = alphabet[random.Next(alphabet.Length)];
        return new string(chars);
    }

    [Test]
    public async Task MicroQr_PlanFromTheScansTable_IsThePlanTheBuildMakes()
    {
        var table = new byte[MicroQRSegmentPlanner.MaxPlannableChars * ModeSegmenter.ParentBytesPerChar];
        var fromTable = new ModeSegment[MicroQRSegmentPlanner.MaxPlannableChars];
        var fresh = new ModeSegment[MicroQRSegmentPlanner.MaxPlannableChars];
        var accepted = new int[5];
        var afterARejection = 0;

        foreach (var text in Texts(MicroQRSegmentPlanner.MaxPlannableChars, 400))
        {
            var analysis = TextAnalyzer.Analyze(text, EciMode.Default, allowKanji: true, planKanji: true);
            if (!analysis.KanjiPlannable)
                continue;

            foreach (var level in MicroLevels)
            {
                foreach (var range in new[] { MicroQRVersionRange.Any, MicroQRVersionRange.AtLeast(MicroQRVersion.M4) })
                {
                    var because = $"「{text}」 at {level}, {range}";
                    var selected = MicroQRSegmentPlanner.TrySelectVersion(text, in analysis, level, range, table, out var version, out var useSegments, out var kanjiPlan, out var finalState, out var plannedBits);
                    var expected = MicroQRSegmentPlanner.TrySelectVersion(text, in analysis, level, range, out var expectedVersion, out var expectedUseSegments, out var expectedKanjiPlan);
                    await Assert.That((selected, version, useSegments, kanjiPlan)).IsEqualTo((expected, expectedVersion, expectedUseSegments, expectedKanjiPlan)).Because(because);
                    if (!kanjiPlan)
                        continue;

                    Array.Clear(fromTable);
                    Array.Clear(fresh);
                    var built = MicroQRSegmentPlanner.TryBuildKanjiPlan(text, version, level, table, finalState, plannedBits, fromTable, out var count);
                    var expectedBuilt = MicroQRSegmentPlanner.TryBuildKanjiPlan(text, version, level, fresh, out var expectedCount);
                    await Assert.That(built).IsEqualTo(expectedBuilt).Because(because);
                    await Assert.That(fromTable.AsSpan(0, count).ToArray()).IsEquivalentTo(fresh.AsSpan(0, expectedCount).ToArray(), CollectionOrdering.Matching).Because(because);

                    // M4 taken from a range that starts lower: M3 was visited, and its run ran when the screen let it through.
                    accepted[(int)version]++;
                    if (version == MicroQRVersion.M4 && range.Min < MicroQRVersion.M4 && MicroQRConstants.IsValidCombination(MicroQRVersion.M3, level)
                        && MicroQRSegmentPlanner.KanjiScreenBits(text, MicroQRVersion.M3) <= MicroQRConstants.GetDataBitCapacity(MicroQRVersion.M3, level))
                        afterARejection++;
                }
            }
        }

        await Assert.That(accepted[(int)MicroQRVersion.M3]).IsGreaterThan(20);
        await Assert.That(accepted[(int)MicroQRVersion.M4]).IsGreaterThan(20);
        await Assert.That(afterARejection).IsGreaterThan(10).Because("M4 accepted after M3 was priced and rejected, where a table kept from M3's run would be wrong");
    }

    [Test]
    public async Task StandardQr_PlanFromTheScansTable_IsThePlanTheBuildMakes()
    {
        var bands = new int[3];
        var afterABandRejection = 0;

        foreach (var text in Texts(600, 300))
        {
            var analysis = TextAnalyzer.Analyze(text, EciMode.Default, allowKanji: true, planKanji: true);
            if (!analysis.KanjiPlannable)
                continue;

            var table = new byte[text.Length * ModeSegmenter.ParentBytesPerChar];
            var fromTable = new ModeSegment[text.Length];
            var fresh = new ModeSegment[text.Length];
            foreach (var level in StandardLevels)
            {
                foreach (var (min, max) in new[] { (1, 40), (10, 40), (5, 30) })
                {
                    var because = $"「{text}」 at {level}, versions {min}-{max}";
                    var selected = QRSegmentPlanner.TrySelectVersion(text, in analysis, level, min, max, table, out var version, out var useSegments, out var kanjiPlan, out var finalState, out var plannedBits);
                    var expected = QRSegmentPlanner.TrySelectVersion(text, in analysis, level, min, max, out var expectedVersion, out var expectedUseSegments, out var expectedKanjiPlan);
                    await Assert.That((selected, version, useSegments, kanjiPlan)).IsEqualTo((expected, expectedVersion, expectedUseSegments, expectedKanjiPlan)).Because(because);
                    if (!kanjiPlan)
                        continue;

                    Array.Clear(fromTable);
                    Array.Clear(fresh);
                    var built = QRSegmentPlanner.TryBuildKanjiPlan(text, version, level, table, finalState, plannedBits, fromTable, out var count);
                    var expectedBuilt = QRSegmentPlanner.TryBuildKanjiPlan(text, version, level, fresh, out var expectedCount);
                    await Assert.That(built).IsEqualTo(expectedBuilt).Because(because);
                    await Assert.That(fromTable.AsSpan(0, count).ToArray()).IsEquivalentTo(fresh.AsSpan(0, expectedCount).ToArray(), CollectionOrdering.Matching).Because(because);

                    var band = version < 10 ? 0 : version < 27 ? 1 : 2;
                    bands[band]++;
                    var firstBand = min < 10 ? 0 : min < 27 ? 1 : 2;
                    if (band > firstBand && QRSegmentPlanner.TrivialLowerBoundBitsKanji(text) <= QRCodeConstants.GetEccInfo(BandEnd(firstBand, max), level).TotalDataCodewords * 8)
                        afterABandRejection++;
                }
            }
        }

        await Assert.That(bands[0]).IsGreaterThan(20);
        await Assert.That(bands[1]).IsGreaterThan(20);
        await Assert.That(afterABandRejection).IsGreaterThan(10).Because("a later band accepted after an earlier band's run was priced and rejected, where a table kept from the earlier run would be wrong");
    }

    /// <summary>The last version of a count-indicator band within the window.</summary>
    private static int BandEnd(int band, int max) => Math.Min(max, band == 0 ? 9 : band == 1 ? 26 : 40);
}
