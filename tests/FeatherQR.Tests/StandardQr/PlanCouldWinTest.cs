using System.Text;
using FeatherQR.Internals;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// The rule that lets a search skip the segmentation program: a mixed plan can only be cheaper
/// than one run when the content holds a run of a denser mode long enough to repay the mode
/// header it adds. The predicate may say yes and be wrong, which only costs a cost run; saying
/// no when a plan would have won changes what the library emits, so that direction is held to
/// the program itself over content built around every length near the thresholds.
/// </summary>
public class PlanCouldWinTest
{
    private const int ModeIndicatorBits = 4;

    public static IEnumerable<(int Version, int CciNumeric, int CciAlnum, int CciByte)> Bands() =>
    [
        (1, 10, 9, 8),
        (10, 12, 11, 16),
        (27, 14, 13, 16),
    ];

    /// <summary>Byte-mode content with one run of a denser mode at the start, the middle and the end, at every length a threshold could sit on.</summary>
    public static IEnumerable<string> Corpus()
    {
        foreach (var run in new[] { "1234567890123456789012", "ABCDEFGHIJKLMNOPQRSTUV", "       $%*+-./:       " })
        {
            for (var length = 0; length <= 22; length++)
            {
                var dense = run[..length];
                yield return dense + "xyz";
                yield return "xyz" + dense;
                yield return "xy" + dense + "z";
                yield return "the quick brown fox " + dense + " jumps over the lazy dog";
                yield return dense;
            }
        }
        yield return "";
        yield return "order 20260915 item 0000123456 qty 42 ";
        yield return "The quick brown fox jumps over the lazy dog. ";
        yield return "こんにちは世界、QRコードの分割テストです。";
        yield return "https://example.com/item?id=123456789012345678901234567890";
    }

    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task Predicate_NeverRefusesContentAPlanActuallyWins(string text)
    {
        if (text.Length == 0)
            return;

        ModeSegmenter.LongestDenseRuns(text, out var numericRun, out var alnumRun);
        var couldWin = QRSegmentPlanner.PlanCouldBeatSingleMode(numericRun, alnumRun);
        if (couldWin)
            return; // the predicate is allowed to be generous

        foreach (var requested in new[] { EciMode.Default, EciMode.Utf8 })
        {
            // The charset the analysis resolves to is the one both costs are priced in, as
            // the planner prices them; asking the program about a charset the text was not
            // analysed under compares two different byte sequences.
            var analysis = TextAnalyzer.Analyze(text, requested);
            foreach (var (version, cciNumeric, cciAlnum, cciByte) in Bands())
            {
                var single = ModeIndicatorBits + analysis.EncodingMode.GetCountIndicatorLength(version)
                    + ModeSegmenter.PayloadBits(analysis.EncodingMode, analysis.DataLength);
                var planned = QRSegmentPlanner.MinimumPayloadBits(text, analysis.EciMode, cciNumeric, cciAlnum, cciByte);

                await Assert.That(planned).IsGreaterThanOrEqualTo(single)
                    .Because($"refused \"{Describe(text)}\" (numericRun={numericRun}, alnumRun={alnumRun}) at version {version} in {analysis.EciMode}, but a plan is cheaper");
            }
        }
    }

    [Test]
    [Arguments("The quick brown fox jumps over the lazy dog. ", false)]
    [Arguments("こんにちは世界、QRコードの分割テストです。", false)]
    [Arguments("order 20260915 item 0000123456 qty 42 ", true)]
    [Arguments("https://example.com/item?id=123456789012345678901234567890", true)]
    public async Task Predicate_AnswersTheShapesTheCorpusIsMadeOf(string text, bool expected)
    {
        ModeSegmenter.LongestDenseRuns(text, out var numericRun, out var alnumRun);

        await Assert.That(QRSegmentPlanner.PlanCouldBeatSingleMode(numericRun, alnumRun)).IsEqualTo(expected);
    }

    [Test]
    public async Task LongestDenseRuns_CountsTheLongestRunOfEach()
    {
        ModeSegmenter.LongestDenseRuns("ab123cdAB CD12345ef", out var numericRun, out var alnumRun);

        await Assert.That(numericRun).IsEqualTo(5);
        await Assert.That(alnumRun).IsEqualTo(10); // "AB CD12345": the space is in the alphabet, the lowercase around it is not
    }

    [Test]
    public async Task LongestDenseRuns_EmptyText_IsZero()
    {
        ModeSegmenter.LongestDenseRuns("", out var numericRun, out var alnumRun);

        await Assert.That(numericRun).IsEqualTo(0);
        await Assert.That(alnumRun).IsEqualTo(0);
    }

    private static string Describe(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
            sb.Append(c < 0x80 ? c : '?');
        return sb.ToString();
    }
}
