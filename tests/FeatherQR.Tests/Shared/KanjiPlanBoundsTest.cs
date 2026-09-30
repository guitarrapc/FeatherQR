using FeatherQR.Internals;
using FeatherQR.Internals.MicroQR;
using FeatherQR.Internals.RmQR;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// The bounds the three planners screen a Kanji plan's candidates with, re-derived for the eighth state (kanji-encoding-plan.md, phase 6.4): each lower bound must never exceed the Kanji plan's optimum at any version, and each upper bound must never fall below it.
/// A lower bound that did would skip a version the plan fits; an upper bound that did would accept one it does not.
/// </summary>
/// <remarks>
/// The optimum is the eight-state program, which <c>ModeSegmenterKanjiParityTest</c> holds to an independent reference; the corpus is that test's.
/// Which old bound would have skipped a winning plan is shown through the public API in <c>KanjiOptimalTest</c>.
/// </remarks>
public class KanjiPlanBoundsTest
{
    public static IEnumerable<string> Corpus() => ModeSegmenterKanjiParityTest.Corpus().Select(static row => row.Text);

    // ---- Standard QR ---------------------------------------------------------------

    /// <summary>The screen adds the narrowest count indicator of any mode; Kanji's narrowest (versions 1-9) is 8 bits, the same as Byte's, so the constant stands.</summary>
    [Test]
    public async Task StandardQr_NarrowestCountIndicator_IsStillEightWithKanji()
    {
        var narrowest = int.MaxValue;
        for (var version = 1; version <= 40; version++)
        {
            foreach (var mode in new[] { EncodingMode.Numeric, EncodingMode.Alphanumeric, EncodingMode.Byte, EncodingMode.Kanji })
                narrowest = Math.Min(narrowest, mode.GetCountIndicatorLength(version));
        }
        await Assert.That(narrowest).IsEqualTo(8);
    }

    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task StandardQr_KanjiScreen_NeverExceedsTheOptimumOfAnyBand(string text)
    {
        var screen = QRSegmentPlanner.TrivialLowerBoundBitsKanji(text);
        foreach (var version in new[] { 1, 10, 27 })
        {
            var optimum = ModeSegmenter.ComputeCostsKanji(text, 4,
                EncodingMode.Numeric.GetCountIndicatorLength(version), EncodingMode.Alphanumeric.GetCountIndicatorLength(version),
                EncodingMode.Byte.GetCountIndicatorLength(version), EncodingMode.Kanji.GetCountIndicatorLength(version), default, out _);
            await Assert.That(screen).IsLessThanOrEqualTo(optimum).Because($"version {version}");
        }
    }

    /// <summary>
    /// The screens count a header for every stretch of characters with a cell and every stretch of ASCII, since no run of a Kanji plan crosses between them.
    /// Where each stretch is one run in its cheapest mode at the narrowest header, the screen is the optimum itself, so a header counted too many would show.
    /// </summary>
    [Test]
    [Arguments("日本語abc", 1, 1)]
    [Arguments("abc日本語", 1, 1)]
    [Arguments("日本7777日本", 2, 1)]
    [Arguments("a日b本c", 2, 3)]
    [Arguments("日", 1, 0)]
    [Arguments("xyz", 0, 1)]
    [Arguments("ЯaΩ", 2, 1)] // two-byte UTF-8 characters, which the UTF-8 screen prices at 16 bits
    public async Task KanjiScreen_CountsAHeaderPerStretch(string text, int cellRuns, int asciiRuns)
    {
        var sixths = ModeSegmenter.CheapestSixthsKanji(text, out var utf8Sixths, out var actualCellRuns, out var actualAsciiRuns);
        await Assert.That(actualCellRuns).IsEqualTo(cellRuns);
        await Assert.That(actualAsciiRuns).IsEqualTo(asciiRuns);
        await Assert.That(sixths).IsEqualTo(ModeSegmenter.CheapestSixthsKanji(text));
        await Assert.That(utf8Sixths).IsEqualTo(ModeSegmenter.CheapestSixths(text, EciMode.Utf8));
    }

    [Test]
    public async Task KanjiScreen_IsTheOptimumWhereEveryStretchIsOneRunAtTheNarrowestHeader()
    {
        // Byte for lowercase ASCII (8 bits a character, 8-bit count at versions 1-9) and Kanji (13 bits, 8-bit count).
        foreach (var text in new[] { "日本語abc", "abc日本語", "a日b本c", "日" })
        {
            var optimum = ModeSegmenter.ComputeCostsKanji(text, 4, 10, 9, 8, 8, default, out _);
            await Assert.That(QRSegmentPlanner.TrivialLowerBoundBitsKanji(text)).IsEqualTo(optimum).Because(text);
        }

        // rMQR at R7x43, whose widths are the four minima: Kanji 3 + 2, Byte 3 + 3.
        foreach (var text in new[] { "日本語abc", "a日b本c" })
            await Assert.That(RmQRSegmentPlanner.TrivialLowerBoundBitsKanji(text)).IsEqualTo(RmQRSegmentPlanner.MinimumPayloadBitsKanji(text, 4, 3, 3, 2)).Because(text);

        // Micro QR at M3: Kanji 2 + 3, Byte 2 + 4.
        foreach (var text in new[] { "日本語abc", "a日b本" })
        {
            var optimum = ModeSegmenter.ComputeCostsKanji(text, 2, 5, 4, 4, 3, default, out _);
            await Assert.That(MicroQRSegmentPlanner.KanjiScreenBits(text, MicroQRVersion.M3)).IsEqualTo(optimum).Because(text);
        }
    }

    // ---- Micro QR ------------------------------------------------------------------

    /// <summary>
    /// The screen at M3 and M4: 13 bits a character with a cell, and a header for every stretch of either kind.
    /// Every candidate a Kanji plan is priced at is one of these two, since Kanji exists from M3.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task MicroQr_KanjiScreen_NeverExceedsTheOptimum(string text)
    {
        if (text.Length > MicroQRSegmentPlanner.MaxPlannableChars)
            return;

        foreach (var version in new[] { MicroQRVersion.M3, MicroQRVersion.M4 })
        {
            var screen = MicroQRSegmentPlanner.KanjiScreenBits(text, version);
            var optimum = ModeSegmenter.ComputeCostsKanji(text,
                MicroQRConstants.GetModeIndicatorLength(version),
                MicroQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric),
                MicroQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric),
                MicroQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte),
                MicroQRConstants.GetKanjiCountIndicatorLength(version), default, out _);
            await Assert.That(screen).IsLessThanOrEqualTo(optimum).Because($"{version}");
        }
    }

    // ---- rMQR ----------------------------------------------------------------------

    private static int RmQrKanjiOptimum(string text, RmQRVersion version)
        => RmQRSegmentPlanner.MinimumPayloadBitsKanji(text,
            RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric),
            RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric),
            RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte),
            RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Kanji));

    /// <summary>The Kanji plan's floor runs at 4 / 3 / 3 / 2, the narrowest width of each mode over the 32 versions (Kanji's at R7x43 and R11x27).</summary>
    [Test]
    public async Task RmQr_NarrowestKanjiCountIndicator_IsTwo()
    {
        var narrowest = Enum.GetValues<RmQRVersion>().Min(v => RmQRConstants.GetCountIndicatorLength(v, EncodingMode.Kanji));
        await Assert.That(narrowest).IsEqualTo(2);
        await Assert.That(Enum.GetValues<RmQRVersion>().Where(v => RmQRConstants.GetCountIndicatorLength(v, EncodingMode.Kanji) == 2).ToArray())
            .IsEquivalentTo([RmQRVersion.R7x43, RmQRVersion.R11x27]);
    }

    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task RmQr_KanjiFloorAndScreen_NeverExceedAnyVersionsOptimum(string text)
    {
        var floor = RmQRSegmentPlanner.FloorKanji(text);
        var screen = RmQRSegmentPlanner.TrivialLowerBoundBitsKanji(text);
        await Assert.That(screen).IsLessThanOrEqualTo(floor);
        foreach (var version in Enum.GetValues<RmQRVersion>())
            await Assert.That(floor).IsLessThanOrEqualTo(RmQrKanjiOptimum(text, version)).Because($"{version}");
    }

    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task RmQr_KanjiUpperBound_NeverFallsBelowAnyVersionsOptimum(string text)
    {
        foreach (var version in Enum.GetValues<RmQRVersion>())
            await Assert.That(RmQRSegmentPlanner.FloorPlanUpperBoundKanji(text, version)).IsGreaterThanOrEqualTo(RmQrKanjiOptimum(text, version)).Because($"{version}");

        // R7x43's widths are all four minima, so there the floor plan is the optimum and the bound is exact.
        await Assert.That(RmQRSegmentPlanner.FloorPlanUpperBoundKanji(text, RmQRVersion.R7x43)).IsEqualTo(RmQRSegmentPlanner.FloorKanji(text));
    }

    /// <summary>
    /// The upper bound re-prices the floor plan's Kanji runs as well: at R17x139 (Kanji 7 bits against the floor's 2) a text of many Kanji runs is priced five bits a run above its floor for them.
    /// </summary>
    [Test]
    public async Task RmQr_KanjiUpperBound_RepricesTheKanjiRuns()
    {
        var text = string.Concat(Enumerable.Repeat("日本7777", 10));
        var floor = RmQRSegmentPlanner.FloorKanji(text);
        var parents = new byte[text.Length * ModeSegmenter.ParentBytesPerChar];
        ModeSegmenter.ComputeCostsKanji(text, 3, 4, 3, 3, 2, parents, out var state);
        ModeSegmenter.CountRuns(parents, state, text.Length, out var runsNumeric, out var runsAlnum, out var runsByte, out var runsKanji);
        await Assert.That(runsKanji).IsEqualTo(10);

        var version = RmQRVersion.R17x139;
        var expected = floor
            + runsNumeric * (RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric) - 4)
            + runsAlnum * (RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric) - 3)
            + runsByte * (RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte) - 3)
            + runsKanji * (RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Kanji) - 2);
        await Assert.That(RmQRSegmentPlanner.FloorPlanUpperBoundKanji(text, version)).IsEqualTo(expected);
    }
}
