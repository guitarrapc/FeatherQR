using TUnit.Assertions.Enums;
using FeatherQR.Internals;
using FeatherQR.Internals.MicroQR;
using FeatherQR.Internals.RmQR;
using FeatherQR.Internals.StandardQR;
using static FeatherQR.Tests.KanjiStreamReference;

namespace FeatherQR.Tests;

/// <summary>
/// <see cref="QRSegmentation.Optimal"/> and its Micro QR and rMQR twins on an eligible text with ASCII in it (kanji-encoding-plan.md, phase 6.4), through the public API.
/// </summary>
/// <remarks>
/// <para>
/// The rule the expectations are written from: below the version the single-mode (UTF-8) stream needs, take the first version a plan fits, the Kanji plan (Kanji runs beside Numeric, Alphanumeric and Byte runs of ASCII, no ECI) where it fits and today's UTF-8 plan otherwise.
/// So the output is never larger than <c>Single</c>, and never larger than the UTF-8 plan the same text got before Kanji plans existed.
/// </para>
/// <para>
/// A Kanji plan is written out whole from <see cref="KanjiPlanReference"/> and <see cref="KanjiStreamReference"/>, with the mask pinned, so the plan, its widths and the absence of an ECI header are all checked.
/// A UTF-8 plan is checked against the same text with <see cref="EciMode.Utf8"/> asked for, which is the path every such text took before.
/// The UTF-8 plan's price comes from the seven-state program, which its own parity suites pin.
/// </para>
/// </remarks>
public class KanjiOptimalTest
{
    private const int StandardQrEciBits = 12;
    private const int RmQrEciBits = 11;

    private static string Repeat(string s, int count) => string.Concat(Enumerable.Repeat(s, count));

    /// <summary>Eligible texts with ASCII, from a few characters to Standard QR's 27-40 band.</summary>
    public static IEnumerable<string> EligibleWithAscii() =>
    [
        "QRコード",
        "日本7777",
        "東京タワー333m",
        "1日2回3錠",
        "日本 語",
        "https://例え.jp/パス?q=123",
        "注文番号 20260930-000123 品名 ボールペン 数量 12",
        Repeat("日本7777", 3),
        Repeat("日本7777", 10),
        Repeat("日本7777", 40),
        Repeat("日本7777", 150),
        Repeat("日本7777", 300),
        Repeat("こんにちは世界、QRコードの分割テストです。2026年9月30日 ", 6),
        Repeat("1日", 20) + Repeat("1234567890", 6),
        Repeat("a日b本c", 12),
        "Привет 12345678901234567890",
    ];

    // ---- Analysis ------------------------------------------------------------------

    [Test]
    public async Task Analysis_MarksAnEligibleTextWithAscii_OnlyWhenThePathPlansKanji()
    {
        foreach (var text in new[] { "QRコード", "日本7777", "東京タワー333m", "Привет 1" })
        {
            var planned = TextAnalyzer.Analyze(text, EciMode.Default, allowKanji: true, planKanji: true);
            await Assert.That(planned.KanjiPlannable).IsTrue().Because(text);
            await Assert.That(planned with { KanjiPlannable = false }).IsEqualTo(TextAnalyzer.Analyze(text, EciMode.Default)).Because($"{text}: otherwise the UTF-8 analysis");

            await Assert.That(TextAnalyzer.Analyze(text, EciMode.Default, allowKanji: true).KanjiPlannable).IsFalse().Because($"{text}: a single-mode path");
            await Assert.That(TextAnalyzer.Analyze(text, EciMode.Default, allowKanji: false, planKanji: true).KanjiPlannable).IsFalse().Because($"{text}: Kanji not allowed (a byte order mark)");
            await Assert.That(TextAnalyzer.Analyze(text, EciMode.Utf8, allowKanji: true, planKanji: true).KanjiPlannable).IsFalse().Because($"{text}: UTF-8 asked for");
        }

        // All cells: the single Kanji analysis, as under Single.
        await Assert.That(TextAnalyzer.Analyze("日本語", EciMode.Default, allowKanji: true, planKanji: true)).IsEqualTo(TextAnalyzer.Analyze("日本語", EciMode.Default, allowKanji: true));

        // Not eligible: a character without a cell anywhere in the text, Latin-1, ASCII.
        foreach (var text in new[] { "日本7é", "é日本7", "日本7～", "日本7😀", "ｶﾅ7", "Café 7", "×÷7", "abc 7" })
        {
            var planned = TextAnalyzer.Analyze(text, EciMode.Default, allowKanji: true, planKanji: true);
            await Assert.That(planned).IsEqualTo(TextAnalyzer.Analyze(text, EciMode.Default)).Because(text);
        }
    }

    // ---- Standard QR ---------------------------------------------------------------

    private static byte[] StandardCore(string text, QREccLevel ecc, QRCodeGeneratorOptions options, out int version)
    {
        options = options with { MaskPattern = 0, QuietZoneSize = 0 };
        if (!QRCodeGenerator.TryGetRequiredBufferSize(text, ecc, out var size, options))
            throw new InvalidOperationException("does not fit");
        var buffer = new byte[size.BufferSize];
        QRCodeGenerator.Create(text, ecc, buffer, options);
        version = size.Version;
        return buffer;
    }

    private static readonly QRCodeGeneratorOptions StandardOptimal = new() { Segmentation = QRSegmentation.Optimal };

    private static int StandardCapacity(int version, QREccLevel ecc) => QRCodeConstants.GetEccInfo(version, ecc).TotalDataCodewords * 8;

    private static int StandardKanjiPlanBits(string text, int version, out Run[] runs)
    {
        runs = KanjiPlanReference.Runs(text, KanjiPlanReference.StandardQr(version));
        return StandardQrBitCount(version, runs);
    }

    private static int StandardUtf8PlanBits(string text, int version)
        => QRSegmentPlanner.MinimumPayloadBits(text, EciMode.Utf8, StandardQrCountBits('N', version), StandardQrCountBits('A', version), StandardQrCountBits('B', version)) + StandardQrEciBits;

    /// <summary>The first version below the single-mode fit a plan holds, Kanji first; the single-mode fit when none does; 0 when nothing fits.</summary>
    private static (int Version, Run[]? KanjiRuns) ReferenceStandard(string text, QREccLevel ecc)
    {
        var single = SmallestStandardQrVersion([new Run('E', "26"), new Run('U', text)], ecc);
        var top = single == 0 ? 40 : single - 1;
        for (var version = 1; version <= top; version++)
        {
            if (StandardKanjiPlanBits(text, version, out var runs) <= StandardCapacity(version, ecc))
                return (version, runs);
            if (StandardUtf8PlanBits(text, version) <= StandardCapacity(version, ecc))
                return (version, null);
        }
        return (single, null);
    }

    private static int FirstKanjiPlanVersion(string text, QREccLevel ecc)
    {
        for (var version = 1; version <= 40; version++)
        {
            if (StandardKanjiPlanBits(text, version, out _) <= StandardCapacity(version, ecc))
                return version;
        }
        return 0;
    }

    private static async Task AssertStandardOptimalIsTheReference(string text, QREccLevel ecc)
    {
        var (version, runs) = ReferenceStandard(text, ecc);
        if (version == 0)
            return;

        var actual = StandardCore(text, ecc, StandardOptimal, out var actualVersion);
        await Assert.That(actualVersion).IsEqualTo(version).Because($"{text} at {ecc}");
        if (runs is not null)
        {
            var expected = KanjiSymbolBuilder.StandardQr(StandardQrStream(version, QRCodeConstants.GetEccInfo(version, ecc).TotalDataCodewords, runs), version, ecc, mask: 0);
            await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching).Because($"{text} at {ecc}: the Kanji plan at {version}");
        }
        else
        {
            var utf8 = StandardCore(text, ecc, StandardOptimal with { EciMode = EciMode.Utf8 }, out _);
            await Assert.That(actual).IsEquivalentTo(utf8, CollectionOrdering.Matching).Because($"{text} at {ecc}: today's UTF-8 stream at {version}");
        }
        await Assert.That(KanjiSymbolBuilder.DecodeStandardQr(actual, actualVersion)).IsEqualTo(text).Because($"{text} at {ecc}");
    }

    [Test]
    [MethodDataSource(nameof(EligibleWithAscii))]
    public async Task StandardQr_Optimal_IsTheReferencePlanAtTheReferenceVersion(string text)
    {
        foreach (var ecc in new[] { QREccLevel.L, QREccLevel.M, QREccLevel.Q, QREccLevel.H })
            await AssertStandardOptimalIsTheReference(text, ecc);
    }

    [Test]
    [MethodDataSource(nameof(EligibleWithAscii))]
    public async Task StandardQr_Optimal_IsNeverLargerThanSingleOrTheUtf8Plan(string text)
    {
        foreach (var ecc in new[] { QREccLevel.L, QREccLevel.M, QREccLevel.Q, QREccLevel.H })
        {
            if (!QRCodeGenerator.TryGetRequiredBufferSize(text, ecc, out var optimal, StandardOptimal))
                continue;
            if (QRCodeGenerator.TryGetRequiredBufferSize(text, ecc, out var single))
                await Assert.That(optimal.Version).IsLessThanOrEqualTo(single.Version).Because($"{text} at {ecc}");
            if (QRCodeGenerator.TryGetRequiredBufferSize(text, ecc, out var utf8, StandardOptimal with { EciMode = EciMode.Utf8 }))
                await Assert.That(optimal.Version).IsLessThanOrEqualTo(utf8.Version).Because($"{text} at {ecc}");

            // Sizing reports what Create writes, with and without the boost.
            await Assert.That(QRCodeGenerator.Create(text, ecc, StandardOptimal).Version).IsEqualTo(optimal.Version).Because($"{text} at {ecc}");
            var boosted = StandardOptimal with { BoostEccLevel = true };
            await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(text, ecc, out var boostedSize, boosted)).IsTrue();
            await Assert.That(QRCodeGenerator.Create(text, ecc, boosted).Version).IsEqualTo(boostedSize.Version).Because($"{text} at {ecc}, boosted");
        }
    }

    /// <summary>
    /// The screen that skips a version before any cost run has to price a character with a cell at Kanji's 13 bits and add no ECI header.
    /// Priced as the text's UTF-8 bytes behind an ECI header, as the seven-state screen prices it, this text's winning version would have been skipped.
    /// (Text with more ASCII does not show it: the screen leaves out every header but one, and a Kanji plan of 「日本7777」 pays a header every three characters.)
    /// </summary>
    [Test]
    public async Task StandardQr_KanjiPlanWinsAtAVersionTheUtf8ScreenWouldSkip()
    {
        var text = Repeat("こんにちは世界、QRコードの分割テストです。", 3);
        var (version, runs) = ReferenceStandard(text, QREccLevel.M);
        await Assert.That(runs).IsNotNull();
        await Assert.That(QRSegmentPlanner.TrivialLowerBoundBits(text, EciMode.Utf8) + StandardQrEciBits).IsGreaterThan(StandardCapacity(version, QREccLevel.M)).Because("the seven-state screen's verdict");
        await AssertStandardOptimalIsTheReference(text, QREccLevel.M);
    }

    /// <summary>A Kanji plan that fills its version to within an ECI header's 12 bits: priced with a header it carries none of, it would be skipped.</summary>
    [Test]
    public async Task StandardQr_KanjiPlanIsPricedWithoutAnEciHeader()
    {
        var found = 0;
        for (var pairs = 1; pairs <= 60 && found < 6; pairs++)
        {
            for (var extra = 0; extra < 6 && found < 6; extra++)
            {
                var text = Repeat("日本7777", pairs) + new string('7', extra);
                foreach (var ecc in new[] { QREccLevel.L, QREccLevel.M, QREccLevel.Q, QREccLevel.H })
                {
                    var (version, runs) = ReferenceStandard(text, ecc);
                    if (runs is null)
                        continue;
                    var slack = StandardCapacity(version, ecc) - StandardQrBitCount(version, runs);
                    if (slack >= StandardQrEciBits)
                        continue;
                    found++;
                    await AssertStandardOptimalIsTheReference(text, ecc);
                }
            }
        }
        await Assert.That(found).IsGreaterThan(0).Because("the search must find a Kanji plan within 12 bits of its capacity");
    }

    /// <summary>
    /// Finely interleaved kanji and digits cost a header each as Kanji runs, so here the UTF-8 plan (the interleaved part as one UTF-8 Byte run, the digits split off) is the smaller one, and it is what Optimal writes: the Kanji plan does not get to make the symbol larger.
    /// </summary>
    [Test]
    public async Task StandardQr_Utf8PlanIsKept_WhereItIsStrictlySmaller()
    {
        var text = Repeat("1日", 20) + Repeat("1234567890", 6);
        var (version, runs) = ReferenceStandard(text, QREccLevel.M);
        await Assert.That(runs).IsNull();
        await Assert.That(FirstKanjiPlanVersion(text, QREccLevel.M)).IsGreaterThan(version).Because("the Kanji plan alone would need a larger version");
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(text, QREccLevel.M, out var single)).IsTrue();
        await Assert.That(version).IsLessThan(single.Version).Because("and the UTF-8 plan beats the single stream");
        await AssertStandardOptimalIsTheReference(text, QREccLevel.M);
    }

    /// <summary>Where both plans fit the same version, the Kanji plan is written.</summary>
    [Test]
    public async Task StandardQr_BothPlansFitTheSameVersion_TheKanjiPlanIsWritten()
    {
        var found = 0;
        foreach (var text in EligibleWithAscii())
        {
            foreach (var ecc in new[] { QREccLevel.L, QREccLevel.M, QREccLevel.Q, QREccLevel.H })
            {
                var (version, runs) = ReferenceStandard(text, ecc);
                if (runs is null || StandardUtf8PlanBits(text, version) > StandardCapacity(version, ecc))
                    continue;
                found++;
                await AssertStandardOptimalIsTheReference(text, ecc);
            }
        }
        await Assert.That(found).IsGreaterThan(0).Because("the corpus must hold a text both plans fit at the same version");
    }

    /// <summary>The boost prices the Kanji plan as written, with no ECI header: the level rises while it still fits the version.</summary>
    [Test]
    public async Task StandardQr_BoostEccLevel_PricesTheKanjiPlan()
    {
        var found = 0;
        foreach (var text in EligibleWithAscii())
        {
            var (version, runs) = ReferenceStandard(text, QREccLevel.L);
            if (runs is null)
                continue;
            var bits = StandardQrBitCount(version, runs);
            var level = QREccLevel.L;
            while (level < QREccLevel.H && bits <= StandardCapacity(version, level + 1))
                level++;
            if (level == QREccLevel.L)
                continue;
            found++;

            var actual = StandardCore(text, QREccLevel.L, StandardOptimal with { BoostEccLevel = true }, out var actualVersion);
            await Assert.That(actualVersion).IsEqualTo(version).Because(text);
            var expected = KanjiSymbolBuilder.StandardQr(StandardQrStream(version, QRCodeConstants.GetEccInfo(version, level).TotalDataCodewords, runs), version, level, mask: 0);
            await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching).Because($"{text}: boosted to {level}");
        }
        await Assert.That(found).IsGreaterThan(0);
    }

    /// <summary>A range the single stream cannot reach but a Kanji plan can: the window holds the text now.</summary>
    [Test]
    public async Task StandardQr_ExactVersionOnlyAKanjiPlanFits_NowFits()
    {
        var text = Repeat("日本7777", 10);
        var version = FirstKanjiPlanVersion(text, QREccLevel.M);
        var exactly = StandardOptimal with { Version = QRVersionRange.Exactly(version) };
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(text, QREccLevel.M, out _, exactly with { EciMode = EciMode.Utf8 })).IsFalse();
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(text, QREccLevel.M, out var size, exactly)).IsTrue();
        await Assert.That(size.Version).IsEqualTo(version);
        await Assert.That(QRCodeGenerator.Create(text, QREccLevel.M, exactly).Version).IsEqualTo(version);
    }

    // ---- Micro QR ------------------------------------------------------------------

    public static IEnumerable<string> MicroEligibleWithAscii() =>
    [
        "日本7777",
        "日本語12345",
        "日本語123456789",
        "東京12",
        "QRコード",
        "Aあ",
        "1日2回3錠",
        "日本 語",
        "ABC日本",
        "東京タワー333m",
        "xЯxЯxЯ12345678",
    ];

    private static readonly MicroQRCodeGeneratorOptions MicroOptimal = new() { Segmentation = MicroQRSegmentation.Optimal };

    private static int MicroCapacity(MicroQRVersion version, MicroQREccLevel ecc) => MicroQRConstants.GetDataBitCapacity(version, ecc);

    private static IEnumerable<MicroQRVersion> MicroVersionsWithBytes(MicroQREccLevel ecc)
        => new[] { MicroQRVersion.M3, MicroQRVersion.M4 }.Where(v => MicroQRConstants.IsValidCombination(v, ecc));

    /// <summary>The Micro QR scan: Byte and Kanji exist from M3, so an eligible text is planned at M3 and M4 only.</summary>
    private static (MicroQRVersion? Version, Run[]? KanjiRuns) ReferenceMicro(string text, MicroQREccLevel ecc)
    {
        MicroQRVersion? single = null;
        foreach (var version in MicroVersionsWithBytes(ecc))
        {
            if (MicroQrBitCount((int)version, [new Run('U', text)]) <= MicroCapacity(version, ecc))
            {
                single = version;
                break;
            }
        }

        foreach (var version in MicroVersionsWithBytes(ecc))
        {
            if (version == single)
                break;
            var runs = KanjiPlanReference.Runs(text, KanjiPlanReference.MicroQr((int)version));
            if (MicroQrBitCount((int)version, runs) <= MicroCapacity(version, ecc))
                return (version, runs);
            if (MicroQRSegmentPlanner.MinimumPayloadBits(text, EciMode.Utf8, version) <= MicroCapacity(version, ecc))
                return (version, null);
        }
        return (single, null);
    }

    [Test]
    [MethodDataSource(nameof(MicroEligibleWithAscii))]
    public async Task MicroQr_Optimal_IsTheReferencePlanAtTheReferenceVersion(string text)
    {
        foreach (var ecc in new[] { MicroQREccLevel.L, MicroQREccLevel.M, MicroQREccLevel.Q })
        {
            var (version, runs) = ReferenceMicro(text, ecc);
            var options = MicroOptimal with { MaskPattern = 0, QuietZoneSize = 0 };
            if (version is null)
            {
                await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize(text, ecc, out _, options)).IsFalse().Because($"{text} at {ecc}");
                continue;
            }

            await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize(text, ecc, out var size, options)).IsTrue().Because($"{text} at {ecc}");
            await Assert.That(size.Version).IsEqualTo(version.Value).Because($"{text} at {ecc}");
            var actual = new byte[size.BufferSize];
            MicroQRCodeGenerator.Create(text, ecc, actual, options);
            if (runs is not null)
            {
                var stream = MicroQrStream((int)version.Value, MicroCapacity(version.Value, ecc), MicroQRConstants.GetDataCodewordCount(version.Value, ecc), runs);
                await Assert.That(actual).IsEquivalentTo(KanjiSymbolBuilder.MicroQr(stream, version.Value, ecc, mask: 0), CollectionOrdering.Matching).Because($"{text} at {ecc}: the Kanji plan at {version}");
            }
            await Assert.That(KanjiSymbolBuilder.DecodeMicroQr(actual, version.Value)).IsEqualTo(text).Because($"{text} at {ecc}");

            if (MicroQRCodeGenerator.TryGetRequiredBufferSize(text, ecc, out var single))
                await Assert.That((int)size.Version).IsLessThanOrEqualTo((int)single.Version).Because($"{text} at {ecc}");
        }
    }

    /// <summary>
    /// 「日本語12345」 is 14 UTF-8 bytes, which only M4-L holds; its Kanji plan fits M3-L.
    /// The seven-state screen, pricing each kanji as its three UTF-8 bytes, would have skipped M3.
    /// </summary>
    [Test]
    public async Task MicroQr_KanjiPlanWinsAtAVersionTheUtf8ScreenWouldSkip()
    {
        const string text = "日本語12345";
        var (version, runs) = ReferenceMicro(text, MicroQREccLevel.L);
        await Assert.That(version).IsEqualTo(MicroQRVersion.M3);
        await Assert.That(runs).IsNotNull();
        var utf8Screen = (ModeSegmenter.CheapestSixths(text, EciMode.Utf8) + 5) / 6 + 2 * (int)MicroQRVersion.M3;
        await Assert.That(utf8Screen).IsGreaterThan(MicroCapacity(MicroQRVersion.M3, MicroQREccLevel.L));
        await Assert.That(MicroQRCodeGenerator.Create(text, MicroQREccLevel.L, MicroOptimal).Version).IsEqualTo(MicroQRVersion.M3);
        await Assert.That(MicroQRCodeGenerator.Create(text, MicroQREccLevel.L).Version).IsEqualTo(MicroQRVersion.M4);
    }

    /// <summary>
    /// 「日本語123456789」 is 18 UTF-8 bytes, more than any Micro QR symbol holds; its Kanji plan, Kanji(日本語) + Numeric(123456789), is 81 bits and fits M3-L.
    /// With no single-mode fit the scan runs the whole range, so this is also the case where no ceiling stops it.
    /// </summary>
    [Test]
    public async Task MicroQr_TextNoSymbolHeldAsUtf8_NowFitsAsAKanjiPlan()
    {
        const string text = "日本語123456789";
        await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize(text, MicroQREccLevel.L, out _)).IsFalse();
        await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize(text, MicroQREccLevel.L, out var size, MicroOptimal)).IsTrue();
        await Assert.That(size.Version).IsEqualTo(MicroQRVersion.M3);
        await Assert.That(ReferenceMicro(text, MicroQREccLevel.L).Version).IsEqualTo(MicroQRVersion.M3);
    }

    /// <summary>
    /// A two-byte character beside a lowercase letter is 24 bits as UTF-8 in one Byte run and 36 as a Byte run and a Kanji run at M4, so 「xЯxЯxЯ12345678」 has a UTF-8 plan (Byte, then the digits split off) that fits M4-L and a Kanji plan that does not; as one UTF-8 Byte run it is 17 bytes, which no Micro QR symbol holds.
    /// The UTF-8 plan is what fits, and it is written: the Kanji plan does not get to refuse a text that fitted before.
    /// </summary>
    [Test]
    public async Task MicroQr_Utf8PlanIsKept_WhereTheKanjiPlanDoesNotFit()
    {
        const string text = "xЯxЯxЯ12345678";
        var kanjiRuns = KanjiPlanReference.Runs(text, KanjiPlanReference.MicroQr(4));
        await Assert.That(MicroQrBitCount(4, kanjiRuns)).IsGreaterThan(MicroCapacity(MicroQRVersion.M4, MicroQREccLevel.L));
        await Assert.That(MicroQRSegmentPlanner.MinimumPayloadBits(text, EciMode.Utf8, MicroQRVersion.M4)).IsLessThanOrEqualTo(MicroCapacity(MicroQRVersion.M4, MicroQREccLevel.L));
        await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize(text, MicroQREccLevel.L, out _)).IsFalse();

        await Assert.That(ReferenceMicro(text, MicroQREccLevel.L)).IsEqualTo((MicroQRVersion.M4, (Run[]?)null));
        var options = MicroOptimal with { MaskPattern = 0, QuietZoneSize = 0 };
        await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize(text, MicroQREccLevel.L, out var size, options)).IsTrue();
        await Assert.That(size.Version).IsEqualTo(MicroQRVersion.M4);
        var actual = new byte[size.BufferSize];
        MicroQRCodeGenerator.Create(text, MicroQREccLevel.L, actual, options);
        await Assert.That(KanjiSymbolBuilder.DecodeMicroQr(actual, MicroQRVersion.M4)).IsEqualTo(text);
    }

    // ---- rMQR ----------------------------------------------------------------------

    public static IEnumerable<string> RmQrEligibleWithAscii() =>
    [
        "あい123",
        "日本7777",
        "日本語1234567890",
        "東京タワー333m",
        "QRコード",
        "1日2回3錠",
        "https://例え.jp/パス?q=123",
        Repeat("日本7777", 3),
        Repeat("日本7777", 10),
        Repeat("日本7777", 20),
        Repeat("1日", 8) + Repeat("1234567890", 4),
        Repeat("こんにちは世界、QRコードの分割テストです。", 2) + "2026",
    ];

    public static IEnumerable<RmQRFitStrategy> Strategies() => [RmQRFitStrategy.MinimizeArea, RmQRFitStrategy.MinimizeWidth, RmQRFitStrategy.MinimizeHeight];

    private static int RmQrCapacity(RmQRVersion version, RmQREccLevel ecc) => 8 * RmQRConstants.GetDataCodewordCount(version, ecc);

    private static int RmQrIndex(RmQRVersion version) => (int)version - 1;

    private static bool RmQrKanjiPlanFits(string text, RmQRVersion version, RmQREccLevel ecc, out Run[] runs)
    {
        runs = KanjiPlanReference.Runs(text, KanjiPlanReference.RmQr(RmQrIndex(version)));
        return RmQrBitCount(RmQrIndex(version), runs) <= RmQrCapacity(version, ecc);
    }

    private static bool RmQrUtf8PlanFits(string text, RmQRVersion version, RmQREccLevel ecc)
        => RmQRSegmentPlanner.MinimumPayloadBits(text, EciMode.Utf8,
            RmQrCountBits('N', RmQrIndex(version)), RmQrCountBits('A', RmQrIndex(version)), RmQrCountBits('B', RmQrIndex(version))) + RmQrEciBits <= RmQrCapacity(version, ecc);

    /// <summary>
    /// Every version priced from scratch, the best by the strategy among those the single stream or a plan fits; at the single stream's own version the single stream is written, and elsewhere the Kanji plan where it fits.
    /// </summary>
    private static (RmQRVersion? Version, Run[]? KanjiRuns) ReferenceRmQr(string text, RmQREccLevel ecc, RmQRFitStrategy strategy)
    {
        RmQRVersion? single = null;
        RmQRVersion? best = null;
        foreach (var version in Enum.GetValues<RmQRVersion>())
        {
            var singleFits = RmQrBitCount(RmQrIndex(version), [new Run('E', "26"), new Run('U', text)]) <= RmQrCapacity(version, ecc);
            if (singleFits && (single is null || RmQRVersionSelector.IsBetter(version, single.Value, strategy)))
                single = version;
            if ((singleFits || RmQrKanjiPlanFits(text, version, ecc, out _) || RmQrUtf8PlanFits(text, version, ecc))
                && (best is null || RmQRVersionSelector.IsBetter(version, best.Value, strategy)))
                best = version;
        }

        if (best is not { } chosen)
            return (null, null);
        if (chosen == single)
            return (chosen, null);
        return RmQrKanjiPlanFits(text, chosen, ecc, out var runs) ? (chosen, runs) : (chosen, null);
    }

    [Test]
    [MethodDataSource(nameof(RmQrEligibleWithAscii))]
    public async Task RmQr_Optimal_IsTheReferencePlanAtTheReferenceVersion(string text)
    {
        foreach (var strategy in Strategies())
        {
            foreach (var ecc in new[] { RmQREccLevel.M, RmQREccLevel.H })
            {
                var (reference, runs) = ReferenceRmQr(text, ecc, strategy);
                var options = new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal, FitStrategy = strategy };
                var because = $"{text} at {ecc}, {strategy}";
                if (reference is not { } version)
                {
                    await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(text, ecc, out _, options)).IsFalse().Because(because);
                    continue;
                }

                await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(text, ecc, out var size, options)).IsTrue().Because(because);
                await Assert.That(size.Version).IsEqualTo(version).Because(because);
                var actual = RmQRCodeGenerator.Create(text, ecc, options);
                await Assert.That(actual.Version).IsEqualTo(version).Because(because);
                if (runs is not null)
                {
                    var expected = KanjiSymbolBuilder.RmQr(RmQrStream(RmQrIndex(version), RmQRConstants.GetDataCodewordCount(version, ecc), runs), version, ecc);
                    await Assert.That(Core(actual)).IsEquivalentTo(expected, CollectionOrdering.Matching).Because($"{because}: the Kanji plan");
                }
                else
                {
                    var utf8 = RmQRCodeGenerator.Create(text, ecc, options with { EciMode = EciMode.Utf8 });
                    await Assert.That(Core(actual)).IsEquivalentTo(Core(utf8), CollectionOrdering.Matching).Because($"{because}: today's UTF-8 stream");
                }

                await Assert.That(RmQRCodeDecoder.TryDecode(actual, out var decoded)).IsTrue().Because(because);
                await Assert.That(decoded).IsEqualTo(text).Because(because);

                if (RmQRCodeGenerator.TryGetRequiredBufferSize(text, ecc, out var single, new RmQRCodeGeneratorOptions { FitStrategy = strategy }))
                    await Assert.That(version == single.Version || RmQRVersionSelector.IsBetter(version, single.Version, strategy)).IsTrue().Because($"{because}: never larger than Single");
                if (RmQRCodeGenerator.TryGetRequiredBufferSize(text, ecc, out var utf8Optimal, options with { EciMode = EciMode.Utf8 }))
                    await Assert.That(version == utf8Optimal.Version || RmQRVersionSelector.IsBetter(version, utf8Optimal.Version, strategy)).IsTrue().Because($"{because}: never larger than the UTF-8 plan");
            }
        }
    }

    private static byte[] Core(RmQRCodeData data)
    {
        var width = RmQRConstants.GetWidth(data.Version);
        var height = RmQRConstants.GetHeight(data.Version);
        var quietZone = (data.Width - width) / 2;
        var modules = new byte[width * height];
        for (var row = 0; row < height; row++)
            for (var col = 0; col < width; col++)
                modules[row * width + col] = data[row + quietZone, col + quietZone] ? (byte)1 : (byte)0;
        return modules;
    }

    /// <summary>
    /// 「あい123」 costs exactly R7x43-M's 48 bits as Kanji(あい) + Numeric(123), with Kanji's 2-bit count indicator there.
    /// A floor priced at a 3-bit Kanji width, or a screen at the seven-state program's narrowest header, would put it at 49 and skip the smallest symbol.
    /// </summary>
    [Test]
    public async Task RmQr_KanjiPlanFillingR7x43_IsNotSkippedByTheFloor()
    {
        const string text = "あい123";
        await Assert.That(RmQrKanjiPlanFits(text, RmQRVersion.R7x43, RmQREccLevel.M, out var runs)).IsTrue();
        await Assert.That(RmQrBits(RmQrIndex(RmQRVersion.R7x43), runs).Length).IsEqualTo(RmQrCapacity(RmQRVersion.R7x43, RmQREccLevel.M));
        await Assert.That(KanjiPlanReference.Cost(text, new KanjiPlanReference.Widths(3, 4, 3, 3, 3), null, out _)).IsGreaterThan(RmQrCapacity(RmQRVersion.R7x43, RmQREccLevel.M));

        foreach (var strategy in Strategies())
        {
            var (version, _) = ReferenceRmQr(text, RmQREccLevel.M, strategy);
            var actual = RmQRCodeGenerator.Create(text, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal, FitStrategy = strategy });
            await Assert.That(actual.Version).IsEqualTo(version!.Value).Because($"{strategy}");
        }

        // R7x43 is the first rank by height; by area R11x27 (297 modules against 301, Kanji width 2 as well) comes first.
        var lowest = RmQRCodeGenerator.Create(text, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal, FitStrategy = RmQRFitStrategy.MinimizeHeight });
        await Assert.That(lowest.Version).IsEqualTo(RmQRVersion.R7x43);
    }

    /// <summary>
    /// R15x59 and R13x77 share their Numeric, Alphanumeric and Byte widths (7 / 7 / 6) and differ in Kanji's (5 against 6), and by area R15x59 (885 modules) is priced before R13x77 (1,001).
    /// 「1日」×9 then 28 digits costs 419 bits as a Kanji plan at R15x59 and 428 at R13x77, which holds 424 at M: a memo key without Kanji's width would hand R13x77 R15x59's price, accept it for a Kanji plan that does not fit, and fall back to the single stream.
    /// R13x77 is right, for the UTF-8 plan (412 bits behind its ECI header), which is also the case where only that plan fits the winning version.
    /// </summary>
    [Test]
    public async Task RmQr_MemoKeyTellsApartVersionsThatShareTheirOtherWidths()
    {
        var text = Repeat("1日", 9) + new string('7', 28);
        static int KanjiBits(string text, RmQRVersion version) => RmQRSegmentPlanner.MinimumPayloadBitsKanji(text,
            RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric), RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric),
            RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte), RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Kanji));
        await Assert.That(KanjiBits(text, RmQRVersion.R15x59)).IsLessThanOrEqualTo(RmQrCapacity(RmQRVersion.R13x77, RmQREccLevel.M));
        await Assert.That(KanjiBits(text, RmQRVersion.R13x77)).IsGreaterThan(RmQrCapacity(RmQRVersion.R13x77, RmQREccLevel.M));
        await Assert.That(RmQrUtf8PlanFits(text, RmQRVersion.R13x77, RmQREccLevel.M)).IsTrue();

        var options = new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal };
        var actual = RmQRCodeGenerator.Create(text, RmQREccLevel.M, options);
        await Assert.That(actual.Version).IsEqualTo(RmQRVersion.R13x77);
        await Assert.That(Core(actual)).IsEquivalentTo(Core(RmQRCodeGenerator.Create(text, RmQREccLevel.M, options with { EciMode = EciMode.Utf8 })), CollectionOrdering.Matching);
    }

    /// <summary>A requested version the single stream does not fit: the Kanji plan is priced there, and holds the text.</summary>
    [Test]
    public async Task RmQr_RequestedVersionOnlyAKanjiPlanFits_NowFits()
    {
        const string text = "あい123";
        var options = new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal, Version = RmQRVersion.R7x43 };
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(text, RmQREccLevel.M, out _, options with { EciMode = EciMode.Utf8 })).IsFalse();
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(text, RmQREccLevel.M, out var size, options)).IsTrue();
        await Assert.That(size.Version).IsEqualTo(RmQRVersion.R7x43);

        var actual = RmQRCodeGenerator.Create(text, RmQREccLevel.M, options);
        RmQrKanjiPlanFits(text, RmQRVersion.R7x43, RmQREccLevel.M, out var runs);
        var expected = KanjiSymbolBuilder.RmQr(RmQrStream(RmQrIndex(RmQRVersion.R7x43), RmQRConstants.GetDataCodewordCount(RmQRVersion.R7x43, RmQREccLevel.M), runs), RmQRVersion.R7x43, RmQREccLevel.M);
        await Assert.That(Core(actual)).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }
}
