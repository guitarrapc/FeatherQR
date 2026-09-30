using TUnit.Assertions.Enums;
using FeatherQR.Internals.MicroQR;
using FeatherQR.Internals.RmQR;
using FeatherQR.Internals.StandardQR;
using static FeatherQR.Tests.KanjiStreamReference;

namespace FeatherQR.Tests;

/// <summary>
/// When a generator writes Kanji mode (kanji-encoding-plan.md, "When a text is written in Kanji mode"), through the public API, one test per class of the truth table: charset option × byte order mark × text class × symbology × version range.
/// Every expectation is a whole symbol built from a stream <see cref="KanjiStreamReference"/> writes from the standards, with the mask pinned, so "as today" is checked as exactly as "Kanji".
/// </summary>
/// <remarks>
/// A text is eligible when the library chose the charset and chose UTF-8, and every character is ASCII or has an encoder cell.
/// A single mode writes Kanji only when every character has a cell. An eligible text with ASCII in it stays UTF-8 under <see cref="QRSegmentation.Single"/>, and under <see cref="QRSegmentation.Optimal"/> takes a Kanji plan where that plan needs a smaller version; <c>KanjiOptimalTest</c> holds that rule whole.
/// </remarks>
public class KanjiEligibilityTest
{
    /// <summary>Every character has an encoder cell: kana and kanji, and the non-Japanese scripts and symbols JIS X 0208 holds (K7).</summary>
    public static IEnumerable<string> AllCells() =>
    [
        "日本語",
        "脂至肢",
        "こんにちは世界",
        "Привет",
        "ΑΒΓΔΩ",
        "─│┌┐",
        "日本×÷",
        "ＱＲ１２３",
        "「東京」・、。",
    ];

    /// <summary>Eligible, but with ASCII that one Kanji segment cannot hold.</summary>
    public static IEnumerable<string> EligibleWithAscii() => ["QRコード", "日本7777", "日本 語", "東京タワー333m"];

    /// <summary>A non-ASCII character with no encoder cell: Latin-1 outside JIS X 0208, emoji, both readings of a divergent cell, NEC row 13, halfwidth katakana, an IBM extension.</summary>
    public static IEnumerable<string> WithoutACell() => ["日本é", "日本😀", "〜", "～", "①", "ｶﾅ", "㈱日本", "纊"];

    /// <summary>Latin-1 text, including Latin-1 characters that do have a cell: the library chooses ISO-8859-1, not UTF-8.</summary>
    public static IEnumerable<string> Latin1() => ["Café", "×÷§", "°±¶"];

    // ---- Standard QR ---------------------------------------------------------------

    private static QRCodeGeneratorOptions Pinned(QRCodeGeneratorOptions options) => options with { MaskPattern = 0, QuietZoneSize = 0 };

    private static byte[] StandardCore(string text, QREccLevel ecc, QRCodeGeneratorOptions options = default)
    {
        options = Pinned(options);
        if (!QRCodeGenerator.TryGetRequiredBufferSize(text, ecc, out var size, options))
            throw new InvalidOperationException("does not fit");
        var buffer = new byte[size.BufferSize];
        QRCodeGenerator.Create(text, ecc, buffer, options);
        return buffer;
    }

    private static byte[] ExpectedStandard(Run[] runs, QREccLevel ecc, int? version = null)
    {
        var v = version ?? SmallestStandardQrVersion(runs, ecc);
        return KanjiSymbolBuilder.StandardQr(StandardQrStream(v, QRCodeConstants.GetEccInfo(v, ecc).TotalDataCodewords, runs), v, ecc, mask: 0);
    }

    [Test]
    [MethodDataSource(nameof(AllCells))]
    public async Task StandardQr_AllCells_IsOneKanjiSegment(string text)
    {
        foreach (var ecc in new[] { QREccLevel.L, QREccLevel.H })
        {
            await Assert.That(StandardCore(text, ecc)).IsEquivalentTo(ExpectedStandard([new Run('K', text)], ecc), CollectionOrdering.Matching).Because($"{ecc}");
            await Assert.That(StandardCore(text, ecc, new QRCodeGeneratorOptions { Segmentation = QRSegmentation.Optimal }))
                .IsEquivalentTo(ExpectedStandard([new Run('K', text)], ecc), CollectionOrdering.Matching).Because($"{ecc}, Optimal: one Kanji run is the optimum");
        }
    }

    /// <summary>A byte order mark asks for UTF-8, so the text stays UTF-8 with the mark (K1).</summary>
    [Test]
    [MethodDataSource(nameof(AllCells))]
    public async Task StandardQr_AllCellsWithUtf8Bom_StaysUtf8(string text)
    {
        Run[] expected = [new Run('E', "26"), new Run('U', "﻿" + text)];
        await Assert.That(StandardCore(text, QREccLevel.M, new QRCodeGeneratorOptions { Utf8Bom = true })).IsEquivalentTo(ExpectedStandard(expected, QREccLevel.M), CollectionOrdering.Matching);
        await Assert.That(StandardCore(text, QREccLevel.M, new QRCodeGeneratorOptions { Utf8Bom = true, Segmentation = QRSegmentation.Optimal })).IsEquivalentTo(ExpectedStandard(expected, QREccLevel.M), CollectionOrdering.Matching);
    }

    /// <summary>A charset the caller chose is honoured: explicit UTF-8 stays UTF-8 (K1).</summary>
    [Test]
    [MethodDataSource(nameof(AllCells))]
    public async Task StandardQr_AllCellsWithExplicitUtf8_StaysUtf8(string text)
    {
        await Assert.That(StandardCore(text, QREccLevel.M, new QRCodeGeneratorOptions { EciMode = EciMode.Utf8 }))
            .IsEquivalentTo(ExpectedStandard([new Run('E', "26"), new Run('U', text)], QREccLevel.M), CollectionOrdering.Matching);
    }

    [Test]
    [MethodDataSource(nameof(EligibleWithAscii))]
    public async Task StandardQr_EligibleWithAscii_SingleStaysUtf8(string text)
    {
        await Assert.That(StandardCore(text, QREccLevel.M)).IsEquivalentTo(ExpectedStandard([new Run('E', "26"), new Run('U', text)], QREccLevel.M), CollectionOrdering.Matching);
    }

    /// <summary>
    /// Under Optimal such a text gets its Kanji plan (Kanji runs beside runs of the ASCII, no ECI header) where that plan needs a smaller version than the UTF-8 stream, and otherwise today's UTF-8 stream, which is what the explicit charset writes.
    /// Of these texts only 「東京タワー333m」 moves: 19 UTF-8 bytes need version 2-M, and its Kanji plan fits 1-M. The others fit 1-M as UTF-8 already.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(EligibleWithAscii))]
    public async Task StandardQr_EligibleWithAscii_OptimalTakesTheKanjiPlanOnlyWhereItIsSmaller(string text)
    {
        var optimal = new QRCodeGeneratorOptions { Segmentation = QRSegmentation.Optimal };
        var utf8Version = SmallestStandardQrVersion([new Run('E', "26"), new Run('U', text)], QREccLevel.M);
        var kanjiRuns = KanjiPlanReference.Runs(text, KanjiPlanReference.StandardQr(1));
        var kanjiIsSmaller = utf8Version > 1 && StandardQrBitCount(1, kanjiRuns) <= QRCodeConstants.GetEccInfo(1, QREccLevel.M).TotalDataCodewords * 8;
        await Assert.That(kanjiIsSmaller).IsEqualTo(text == "東京タワー333m");

        var expected = kanjiIsSmaller
            ? ExpectedStandard(kanjiRuns, QREccLevel.M, 1)
            : StandardCore(text, QREccLevel.M, optimal with { EciMode = EciMode.Utf8 });
        await Assert.That(StandardCore(text, QREccLevel.M, optimal)).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    [MethodDataSource(nameof(WithoutACell))]
    public async Task StandardQr_CharacterWithoutACell_StaysUtf8(string text)
    {
        await Assert.That(StandardCore(text, QREccLevel.M)).IsEquivalentTo(ExpectedStandard([new Run('E', "26"), new Run('U', text)], QREccLevel.M), CollectionOrdering.Matching);
        await Assert.That(StandardCore(text, QREccLevel.M, new QRCodeGeneratorOptions { Segmentation = QRSegmentation.Optimal }))
            .IsEquivalentTo(StandardCore(text, QREccLevel.M, new QRCodeGeneratorOptions { Segmentation = QRSegmentation.Optimal, EciMode = EciMode.Utf8 }), CollectionOrdering.Matching);
    }

    [Test]
    [MethodDataSource(nameof(Latin1))]
    public async Task StandardQr_Latin1_StaysLatin1(string text)
    {
        await Assert.That(StandardCore(text, QREccLevel.M)).IsEquivalentTo(ExpectedStandard([new Run('E', "3"), new Run('B', text)], QREccLevel.M), CollectionOrdering.Matching);
    }

    [Test]
    public async Task StandardQr_Empty_StaysAnEmptyByteSegment()
    {
        await Assert.That(StandardCore("", QREccLevel.M)).IsEquivalentTo(ExpectedStandard([new Run('B', "")], QREccLevel.M), CollectionOrdering.Matching);
    }

    /// <summary>Ten kanji fit 1-L in Kanji mode (10 characters) where UTF-8 with its ECI header needed version 2: a range that used to refuse the text now holds it.</summary>
    [Test]
    public async Task StandardQr_ExactVersionOnlyKanjiFits_NowFits()
    {
        var text = Cells(0, 10);
        var exactly1 = new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(1) };
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(text, QREccLevel.L, out var size, exactly1)).IsTrue();
        await Assert.That(size.Version).IsEqualTo(1);
        await Assert.That(StandardCore(text, QREccLevel.L, exactly1)).IsEquivalentTo(ExpectedStandard([new Run('K', text)], QREccLevel.L, 1), CollectionOrdering.Matching);
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(text, QREccLevel.L, out _, exactly1 with { EciMode = EciMode.Utf8 })).IsFalse();
    }

    /// <summary>Sizing reports the version Create writes, under every option that decides the mode.</summary>
    [Test]
    [Arguments(QRSegmentation.Single, false)]
    [Arguments(QRSegmentation.Optimal, false)]
    [Arguments(QRSegmentation.Single, true)]
    [Arguments(QRSegmentation.Optimal, true)]
    public async Task StandardQr_Sizing_MatchesCreate(QRSegmentation segmentation, bool bom)
    {
        foreach (var text in new[] { Cells(3, 30), Cells(5, 300), "QRコード" })
        {
            var options = new QRCodeGeneratorOptions { Segmentation = segmentation, Utf8Bom = bom };
            await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(text, QREccLevel.M, out var size, options)).IsTrue();
            await Assert.That(QRCodeGenerator.Create(text, QREccLevel.M, options).Version).IsEqualTo(size.Version).Because(text);
        }
    }

    /// <summary>The boost prices the Kanji stream: 8 kanji fill 1-M (8 characters) and not 1-Q (6), so L rises to M and stops.</summary>
    [Test]
    public async Task StandardQr_BoostEccLevel_PricesTheKanjiStream()
    {
        var text = Cells(1, 8);
        var core = StandardCore(text, QREccLevel.L, new QRCodeGeneratorOptions { BoostEccLevel = true });
        await Assert.That(core).IsEquivalentTo(ExpectedStandard([new Run('K', text)], QREccLevel.M, 1), CollectionOrdering.Matching);
    }

    [Test]
    public async Task StandardQr_TooLongForVersion40_ReportsKanjiCharacters()
    {
        var text = Cells(0, 1818);
        var ex = await Assert.That(() => QRCodeGenerator.Create(text, QREccLevel.L)).Throws<InvalidOperationException>();
        await Assert.That(ex!.Message).Contains("Kanji");
        await Assert.That(ex.Message).Contains("1818");
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(Cells(0, 1817), QREccLevel.L, out var size)).IsTrue();
        await Assert.That(size.Version).IsEqualTo(40);
    }

    /// <summary>A set that fits one symbol is Create's symbol, which is now Kanji.</summary>
    [Test]
    public async Task StandardQr_StructuredAppendOfOneSymbol_IsCreatesKanjiSymbol()
    {
        var text = Cells(7, 20);
        var set = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.M, new QRCodeGeneratorOptions { MaskPattern = 0 });
        await Assert.That(set.Length).IsEqualTo(1);
        var create = QRCodeGenerator.Create(text, QREccLevel.M, new QRCodeGeneratorOptions { MaskPattern = 0 });
        await Assert.That(set[0].Version).IsEqualTo(create.Version);
        await Assert.That(Modules(set[0])).IsEquivalentTo(Modules(create), CollectionOrdering.Matching);
    }

    private static byte[] Modules(QRCodeData data)
    {
        var size = data.Size;
        var modules = new byte[size * size];
        for (var row = 0; row < size; row++)
            for (var col = 0; col < size; col++)
                modules[row * size + col] = data[row, col] ? (byte)1 : (byte)0;
        return modules;
    }

    // ---- Micro QR ------------------------------------------------------------------

    private static byte[] MicroCore(string text, MicroQREccLevel ecc, MicroQRCodeGeneratorOptions options = default)
    {
        options = options with { MaskPattern = 0, QuietZoneSize = 0 };
        if (!MicroQRCodeGenerator.TryGetRequiredBufferSize(text, ecc, out var size, options))
            throw new InvalidOperationException("does not fit");
        var buffer = new byte[size.BufferSize];
        MicroQRCodeGenerator.Create(text, ecc, buffer, options);
        return buffer;
    }

    private static byte[] ExpectedMicro(Run[] runs, MicroQREccLevel ecc)
    {
        for (var version = MicroQRVersion.M1; version <= MicroQRVersion.M4; version++)
        {
            if (!MicroQRConstants.IsValidCombination(version, ecc) || (version < MicroQRVersion.M3 && runs.Any(r => r.Header is 'B' or 'K')))
                continue;
            var capacity = MicroQRConstants.GetDataBitCapacity(version, ecc);
            if (MicroQrBits((int)version, runs).Length > capacity)
                continue;
            var stream = MicroQrStream((int)version, capacity, MicroQRConstants.GetDataCodewordCount(version, ecc), runs);
            return KanjiSymbolBuilder.MicroQr(stream, version, ecc, mask: 0);
        }
        throw new InvalidOperationException("the reference fits no version");
    }

    public static IEnumerable<string> MicroAllCells() => ["日本語", "脂至肢", "ΑΒΓΔ", "日本×÷", "こんにちは世界"];

    [Test]
    [MethodDataSource(nameof(MicroAllCells))]
    public async Task MicroQr_AllCells_IsOneKanjiSegment(string text)
    {
        await Assert.That(MicroCore(text, MicroQREccLevel.L)).IsEquivalentTo(ExpectedMicro([new Run('K', text)], MicroQREccLevel.L), CollectionOrdering.Matching);
        await Assert.That(MicroCore(text, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Segmentation = MicroQRSegmentation.Optimal }))
            .IsEquivalentTo(ExpectedMicro([new Run('K', text)], MicroQREccLevel.L), CollectionOrdering.Matching);
    }

    /// <summary>Micro QR has no ECI: today's non-Kanji text goes out as bare UTF-8 bytes, and still does.</summary>
    [Test]
    [Arguments("日本7")]      // eligible, with ASCII
    [Arguments("日本é")]      // a character without a cell
    [Arguments("～")]
    [Arguments("ｶﾅ")]
    public async Task MicroQr_NotAllCells_StaysUtf8(string text)
    {
        await Assert.That(MicroCore(text, MicroQREccLevel.L)).IsEquivalentTo(ExpectedMicro([new Run('U', text)], MicroQREccLevel.L), CollectionOrdering.Matching);
    }

    [Test]
    public async Task MicroQr_Latin1_StaysLatin1Bytes()
    {
        await Assert.That(MicroCore("×÷", MicroQREccLevel.L)).IsEquivalentTo(ExpectedMicro([new Run('B', "×÷")], MicroQREccLevel.L), CollectionOrdering.Matching);
    }

    /// <summary>Kanji exists from M3, as Byte does: a range limited to M1-M2 holds neither, as today.</summary>
    [Test]
    public async Task MicroQr_RangeBelowM3_DoesNotFit()
    {
        var options = new MicroQRCodeGeneratorOptions { Version = MicroQRVersionRange.AtMost(MicroQRVersion.M2) };
        await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize("日本", MicroQREccLevel.L, out _, options)).IsFalse();
        var ex = await Assert.That(() => MicroQRCodeGenerator.Create("日本", MicroQREccLevel.L, options)).Throws<ArgumentException>();
        await Assert.That(ex!.Message).Contains("Kanji");
    }

    /// <summary>
    /// ErrorDetectionOnly allows M1 alone. A Kanji text's refusal names Kanji among the modes M3 adds; every other text's refusal is word for word what it was before Kanji mode was written.
    /// </summary>
    [Test]
    public async Task MicroQr_ErrorDetectionOnlyRefusal_NamesKanjiOnlyForKanji()
    {
        var kanji = await Assert.That(() => MicroQRCodeGenerator.Create("日本", MicroQREccLevel.ErrorDetectionOnly)).Throws<ArgumentException>();
        await Assert.That(kanji!.Message).IsEqualTo(
            "Micro QR cannot encode Kanji mode at ECC level ErrorDetectionOnly: ErrorDetectionOnly limits the symbol to M1 (Numeric only, 5 digits); " +
            "Alphanumeric requires M2+, Byte and Kanji require M3+, and level Q requires M4. Choose another ECC level or use Standard QR (QRCodeGenerator).");

        var bytes = await Assert.That(() => MicroQRCodeGenerator.Create("日本é", MicroQREccLevel.ErrorDetectionOnly)).Throws<ArgumentException>();
        await Assert.That(bytes!.Message).IsEqualTo(
            "Micro QR cannot encode Byte mode at ECC level ErrorDetectionOnly: ErrorDetectionOnly limits the symbol to M1 (Numeric only, 5 digits); " +
            "Alphanumeric requires M2+, Byte requires M3+, and level Q requires M4. Choose another ECC level or use Standard QR (QRCodeGenerator).");
    }

    /// <summary>A version that does not offer Kanji says so, listing Kanji where M3 adds it; a Byte text's refusal is unchanged.</summary>
    [Test]
    public async Task MicroQr_ExactVersionWithoutTheMode_NamesKanjiOnlyForKanji()
    {
        var exactlyM2 = new MicroQRCodeGeneratorOptions { Version = MicroQRVersionRange.Exactly(MicroQRVersion.M2) };
        var kanji = await Assert.That(() => MicroQRCodeGenerator.Create("日本", MicroQREccLevel.L, exactlyM2)).Throws<ArgumentException>();
        await Assert.That(kanji!.Message).StartsWith("Encoding mode Kanji is not available on Micro QR version M2 (M1: Numeric; M2: +Alphanumeric; M3/M4: +Byte and Kanji).");

        var bytes = await Assert.That(() => MicroQRCodeGenerator.Create("byte", MicroQREccLevel.L, exactlyM2)).Throws<ArgumentException>();
        await Assert.That(bytes!.Message).StartsWith("Encoding mode Byte is not available on Micro QR version M2 (M1: Numeric; M2: +Alphanumeric; M3/M4: +Byte).");
    }

    /// <summary>Nine kanji fill M4-L; ten are refused in characters, not bytes.</summary>
    [Test]
    public async Task MicroQr_Capacity_IsTheKanjiColumn()
    {
        await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize(Cells(0, 9), MicroQREccLevel.L, out var size)).IsTrue();
        await Assert.That(size.Version).IsEqualTo(MicroQRVersion.M4);
        await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize(Cells(0, 6), MicroQREccLevel.L, out size)).IsTrue();
        await Assert.That(size.Version).IsEqualTo(MicroQRVersion.M3);

        var ex = await Assert.That(() => MicroQRCodeGenerator.Create(Cells(0, 10), MicroQREccLevel.L)).Throws<ArgumentException>();
        await Assert.That(ex!.Message).Contains("10 characters in Kanji mode");
        await Assert.That(ex.Message).Contains("9 characters");
    }

    // ---- rMQR ----------------------------------------------------------------------

    private static byte[] RmQrCore(string text, RmQRCodeGeneratorOptions options)
    {
        options = options with { QuietZoneSize = 0, Version = RmQRVersion.R17x139 };
        if (!RmQRCodeGenerator.TryGetRequiredBufferSize(text, RmQREccLevel.M, out var size, options))
            throw new InvalidOperationException("does not fit");
        var buffer = new byte[size.BufferSize];
        RmQRCodeGenerator.Create(text, RmQREccLevel.M, buffer, options);
        return buffer;
    }

    private static byte[] ExpectedRmQr(Run[] runs)
    {
        const RmQRVersion version = RmQRVersion.R17x139;
        var stream = RmQrStream((int)version - 1, RmQRConstants.GetDataCodewordCount(version, RmQREccLevel.M), runs);
        return KanjiSymbolBuilder.RmQr(stream, version, RmQREccLevel.M);
    }

    [Test]
    [MethodDataSource(nameof(AllCells))]
    public async Task RmQr_AllCells_IsOneKanjiSegment(string text)
    {
        await Assert.That(RmQrCore(text, default)).IsEquivalentTo(ExpectedRmQr([new Run('K', text)]), CollectionOrdering.Matching);
        await Assert.That(RmQrCore(text, new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal })).IsEquivalentTo(ExpectedRmQr([new Run('K', text)]), CollectionOrdering.Matching);
        await Assert.That(RmQrCore(text, new RmQRCodeGeneratorOptions { EciMode = EciMode.Utf8 })).IsEquivalentTo(ExpectedRmQr([new Run('E', "26"), new Run('U', text)]), CollectionOrdering.Matching);
    }

    /// <summary>
    /// With the version left to the fit, Optimal reaches the planner, which has to see a Kanji text as already optimal: priced under the Kanji analysis's charset (none), its characters would look like narrowed Latin-1 bytes.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(AllCells))]
    public async Task RmQr_AllCellsAutoFit_OptimalIsTheSingleKanjiSymbol(string text)
    {
        foreach (var strategy in new[] { RmQRFitStrategy.MinimizeArea, RmQRFitStrategy.MinimizeWidth, RmQRFitStrategy.MinimizeHeight })
        {
            var single = RmQRCodeGenerator.Create(text, RmQREccLevel.M, new RmQRCodeGeneratorOptions { FitStrategy = strategy });
            var optimal = RmQRCodeGenerator.Create(text, RmQREccLevel.M, new RmQRCodeGeneratorOptions { FitStrategy = strategy, Segmentation = RmQRSegmentation.Optimal });
            await Assert.That(optimal.Version).IsEqualTo(single.Version).Because($"{strategy}");
            await Assert.That(optimal.GetRawData()).IsEquivalentTo(single.GetRawData(), CollectionOrdering.Matching).Because($"{strategy}");
            await Assert.That(RmQRCodeDecoder.TryDecode(optimal, out var decoded)).IsTrue();
            await Assert.That(decoded).IsEqualTo(text);
        }
    }

    [Test]
    [MethodDataSource(nameof(EligibleWithAscii))]
    public async Task RmQr_EligibleWithAscii_SingleStaysUtf8(string text)
    {
        await Assert.That(RmQrCore(text, default)).IsEquivalentTo(ExpectedRmQr([new Run('E', "26"), new Run('U', text)]), CollectionOrdering.Matching);
    }

    [Test]
    [MethodDataSource(nameof(WithoutACell))]
    public async Task RmQr_CharacterWithoutACell_StaysUtf8(string text)
    {
        await Assert.That(RmQrCore(text, default)).IsEquivalentTo(ExpectedRmQr([new Run('E', "26"), new Run('U', text)]), CollectionOrdering.Matching);
    }

    [Test]
    [MethodDataSource(nameof(Latin1))]
    public async Task RmQr_Latin1_StaysLatin1(string text)
    {
        await Assert.That(RmQrCore(text, default)).IsEquivalentTo(ExpectedRmQr([new Run('E', "3"), new Run('B', text)]), CollectionOrdering.Matching);
    }

    /// <summary>Automatic fit picks the Kanji stream's version, sizing agrees, and a refusal counts characters.</summary>
    [Test]
    public async Task RmQr_FitSizingAndRefusal_UseTheKanjiStream()
    {
        var text = Cells(0, 3);   // R7x43-M holds 3 Kanji; as UTF-8 (6 bytes or more behind an ECI header) it does not fit R7x43
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(text, RmQREccLevel.M, out var size, new RmQRCodeGeneratorOptions { FitStrategy = RmQRFitStrategy.MinimizeHeight })).IsTrue();
        await Assert.That(size.Version).IsEqualTo(RmQRVersion.R7x43);
        await Assert.That(RmQRCodeGenerator.Create(text, RmQREccLevel.M, new RmQRCodeGeneratorOptions { FitStrategy = RmQRFitStrategy.MinimizeHeight }).Version).IsEqualTo(RmQRVersion.R7x43);

        var ex = await Assert.That(() => RmQRCodeGenerator.Create(Cells(0, 93), RmQREccLevel.M)).Throws<ArgumentException>();
        await Assert.That(ex!.Message).Contains("93 characters in Kanji mode");
        await Assert.That(ex.Message).Contains("92 characters");
    }
}
