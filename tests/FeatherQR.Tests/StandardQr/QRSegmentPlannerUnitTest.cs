using FeatherQR.Internals;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// Unit tests for <see cref="QRSegmentPlanner"/>: the dynamic program is held to an
/// independent brute-force optimum on short content, the plan it reconstructs is held
/// to its own cost model, and the constants the version scan relies on
/// (<see cref="QRSegmentPlanner.MaxPlannableChars"/>, the band widths) are pinned so a
/// capacity-table change cannot silently invalidate them.
/// </summary>
public class QRSegmentPlannerUnitTest
{
    /// <summary>Count indicator width triples per ISO/IEC 18004 version band.</summary>
    public static IEnumerable<(int Version, int CciNumeric, int CciAlnum, int CciByte)> BandWidths() =>
    [
        (1, 10, 9, 8),
        (9, 10, 9, 8),
        (10, 12, 11, 16),
        (26, 12, 11, 16),
        (27, 14, 13, 16),
        (40, 14, 13, 16),
    ];

    [Test]
    public async Task CountIndicatorWidths_MatchTheBandsTheScanAssumes()
    {
        foreach (var (version, cciNumeric, cciAlnum, cciByte) in BandWidths())
        {
            await Assert.That(EncodingMode.Numeric.GetCountIndicatorLength(version)).IsEqualTo(cciNumeric);
            await Assert.That(EncodingMode.Alphanumeric.GetCountIndicatorLength(version)).IsEqualTo(cciAlnum);
            await Assert.That(EncodingMode.Byte.GetCountIndicatorLength(version)).IsEqualTo(cciByte);
        }
    }

    [Test]
    public async Task MaxPlannableChars_IsTheVersion40Optimum_WithItsMargin()
    {
        // 7089 digits are an exact fit for version 40-L: 2363 groups x 10 bits, plus
        // the 4-bit mode indicator and the 14-bit count indicator.
        var capacityBits = 8 * QRCodeConstants.GetEccInfo(40, QREccLevel.L).TotalDataCodewords;
        var atLimit = new string('1', QRSegmentPlanner.MaxPlannableChars);
        var costAtLimit = QRSegmentPlanner.MinimumPayloadBits(atLimit, EciMode.Default, 14, 13, 16);
        await Assert.That(costAtLimit).IsEqualTo(capacityBits);

        // One more digit overflows by 4 bits; re-derive rather than nudge on change.
        var overLimit = new string('1', QRSegmentPlanner.MaxPlannableChars + 1);
        var costOverLimit = QRSegmentPlanner.MinimumPayloadBits(overLimit, EciMode.Default, 14, 13, 16);
        await Assert.That(costOverLimit).IsEqualTo(capacityBits + 4);
    }

    /// <summary>
    /// Content whose optimum is known to require a mode switch, plus content where a
    /// switch must not happen, across the charsets.
    /// </summary>
    public static IEnumerable<(string Content, EciMode Charset)> BruteForceCorpus() =>
    [
        ("", EciMode.Default),
        ("1", EciMode.Default),
        ("A", EciMode.Default),
        ("a", EciMode.Default),
        ("12345678", EciMode.Default),
        ("ABCD1234", EciMode.Default),
        ("ab123456", EciMode.Default),
        ("a1B2c3D4", EciMode.Default),
        ("x1234567890", EciMode.Default),
        ("1234567890x", EciMode.Default),
        ("AB 12:345/", EciMode.Default),
        ("aA1aA1aA1a", EciMode.Default),
        ("é1234567", EciMode.Iso8859_1),
        ("é1234567", EciMode.Utf8),
        ("あ123456", EciMode.Utf8),
        ("😀12345", EciMode.Utf8),
        ("12😀34", EciMode.Utf8),
        // Unpaired surrogate halves: the cost model must mirror Encoding.UTF8's
        // replacement fallback (3 bytes per lone half) in every position.
        ("\uD83D12345", EciMode.Utf8),
        ("\uDE0012345", EciMode.Utf8),
        ("12\uD83DAB", EciMode.Utf8),
        ("1234\uD83D", EciMode.Utf8),
        ("\uDE00\uD83Dab", EciMode.Utf8),
    ];

    [Test]
    [MethodDataSource(nameof(BruteForceCorpus))]
    public async Task MinimumPayloadBits_MatchesAnExhaustiveModeAssignment(string content, EciMode charset)
    {
        foreach (var (version, cciNumeric, cciAlnum, cciByte) in BandWidths())
        {
            _ = version;
            var expected = BruteForceMinimumBits(content, charset, cciNumeric, cciAlnum, cciByte);
            var actual = content.Length == 0
                ? 0
                : QRSegmentPlanner.MinimumPayloadBits(content, charset, cciNumeric, cciAlnum, cciByte);
            if (content.Length == 0)
                continue; // the planner rejects empty content before any cost run
            await Assert.That(actual).IsEqualTo(expected).Because($"content=\"{content}\", widths=({cciNumeric},{cciAlnum},{cciByte})");
        }
    }

    [Test]
    public async Task MinCountBitsAny_IsTheNarrowestWidthOfAnyModeAtAnyVersion()
    {
        // The trivial lower bound adds the cheapest header a stream can carry;
        // if a narrower count indicator ever appeared it would stop being a bound.
        var narrowest = int.MaxValue;
        foreach (var version in new[] { 1, 9, 10, 26, 27, 40 })
        {
            narrowest = Math.Min(narrowest, EncodingMode.Numeric.GetCountIndicatorLength(version));
            narrowest = Math.Min(narrowest, EncodingMode.Alphanumeric.GetCountIndicatorLength(version));
            narrowest = Math.Min(narrowest, EncodingMode.Byte.GetCountIndicatorLength(version));
        }
        await Assert.That(narrowest).IsEqualTo(8);
    }

    [Test]
    [MethodDataSource(nameof(BruteForceCorpus))]
    public async Task TrivialLowerBound_NeverExceedsTheOptimum(string content, EciMode charset)
    {
        if (content.Length == 0)
            return;

        // The optimum at the narrowest widths is the smallest cost any version can
        // see; a sound lower bound must sit at or below it.
        var optimumAtNarrowest = QRSegmentPlanner.MinimumPayloadBits(content, charset, 10, 9, 8);
        var bound = QRSegmentPlanner.TrivialLowerBoundBits(content, charset);
        await Assert.That(bound).IsLessThanOrEqualTo(optimumAtNarrowest).Because($"content=\"{content}\", charset={charset}");
    }

    [Test]
    [MethodDataSource(nameof(BruteForceCorpus))]
    public async Task TryBuildPlan_CostsExactlyWhatTheDynamicProgramComputed(string content, EciMode charset)
    {
        if (content.Length == 0)
            return;

        foreach (var version in new[] { 1, 10, 27 })
        {
            var segments = new ModeSegment[content.Length];
            if (!QRSegmentPlanner.TryBuildPlan(content, charset, version, QREccLevel.L, segments, out var count))
                continue; // the plan legitimately does not fit this version

            var cciNumeric = EncodingMode.Numeric.GetCountIndicatorLength(version);
            var cciAlnum = EncodingMode.Alphanumeric.GetCountIndicatorLength(version);
            var cciByte = EncodingMode.Byte.GetCountIndicatorLength(version);

            var planned = QRSegmentPlanner.MeasurePlan(version, segments.AsSpan(0, count));
            var optimum = QRSegmentPlanner.MinimumPayloadBits(content, charset, cciNumeric, cciAlnum, cciByte);
            await Assert.That(planned).IsEqualTo(optimum).Because($"content=\"{content}\", version={version}");

            // The plan must cover the content in order with non-empty runs.
            var expectedStart = 0;
            for (var i = 0; i < count; i++)
            {
                await Assert.That((int)segments[i].Start).IsEqualTo(expectedStart);
                await Assert.That((int)segments[i].Length).IsGreaterThan(0);
                expectedStart += segments[i].Length;
            }
            await Assert.That(expectedStart).IsEqualTo(content.Length);
        }
    }

    [Test]
    public async Task TryBuildPlan_DigitIslandBetweenByteRuns_ProducesThreeRuns()
    {
        // Byte(12) + Numeric(12) + Byte(12): the island saves 12 x (8 - 10/3) = 56
        // bits against the two extra headers (14 + 12 = 26 at versions 1-9), so the
        // optimal plan must switch modes twice. Pins the multi-switch stream shape,
        // which no round-trip test can distinguish from a 2-run plan.
        var content = "abcdefghijkl123456789012mnopqrstuvwx";
        var segments = new ModeSegment[content.Length];
        await Assert.That(QRSegmentPlanner.TryBuildPlan(content, EciMode.Default, version: 3, QREccLevel.L, segments, out var count)).IsTrue();

        await Assert.That(count).IsEqualTo(3);
        await Assert.That(segments[0].Mode).IsEqualTo(EncodingMode.Byte);
        await Assert.That((int)segments[0].Length).IsEqualTo(12);
        await Assert.That(segments[1].Mode).IsEqualTo(EncodingMode.Numeric);
        await Assert.That((int)segments[1].Length).IsEqualTo(12);
        await Assert.That(segments[2].Mode).IsEqualTo(EncodingMode.Byte);
        await Assert.That((int)segments[2].Length).IsEqualTo(12);
    }

    /// <summary>
    /// Independent optimum: every assignment of a mode to every character (3^n),
    /// invalid assignments discarded, consecutive same-mode characters merged into
    /// runs, each run priced from the ISO/IEC 18004 7.4 formulas written out here
    /// rather than taken from the planner.
    /// </summary>
    private static int BruteForceMinimumBits(string content, EciMode charset, int cciNumeric, int cciAlnum, int cciByte)
    {
        if (content.Length == 0)
            return 0;

        var n = content.Length;
        var best = int.MaxValue;
        var assignment = new int[n];
        var total = (int)Math.Pow(3, n);
        for (var mask = 0; mask < total; mask++)
        {
            var m = mask;
            var valid = true;
            for (var i = 0; i < n; i++)
            {
                assignment[i] = m % 3;
                m /= 3;
                var c = content[i];
                if (assignment[i] == 0 && !(c >= '0' && c <= '9'))
                    valid = false;
                if (assignment[i] == 1 && !IsAlphanumeric(c))
                    valid = false;
            }
            if (!valid)
                continue;

            var bits = 0;
            var start = 0;
            for (var i = 1; i <= n; i++)
            {
                if (i == n || assignment[i] != assignment[start])
                {
                    bits += RunBits(content.Substring(start, i - start), assignment[start], charset, cciNumeric, cciAlnum, cciByte);
                    start = i;
                }
            }
            best = Math.Min(best, bits);
        }
        return best;
    }

    private static int RunBits(string run, int mode, EciMode charset, int cciNumeric, int cciAlnum, int cciByte) => mode switch
    {
        0 => 4 + cciNumeric + run.Length / 3 * 10 + (run.Length % 3) switch { 2 => 7, 1 => 4, _ => 0 },
        1 => 4 + cciAlnum + run.Length / 2 * 11 + run.Length % 2 * 6,
        _ => 4 + cciByte + 8 * (charset == EciMode.Utf8 ? System.Text.Encoding.UTF8.GetByteCount(run) : run.Length),
    };

    private static bool IsAlphanumeric(char c)
        => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || " $%*+-./:".IndexOf(c) >= 0;
}
