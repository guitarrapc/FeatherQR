using TUnit.Assertions.Enums;
using FeatherQR.Internals.MicroQR;
using FeatherQR.Internals.RmQR;
using FeatherQR.Internals.StandardQR;
using static FeatherQR.Tests.KanjiStreamReference;

namespace FeatherQR.Tests;

/// <summary>
/// When a generator writes Kanji mode (kanji-encoding-plan.md, "When a text is written in Kanji mode"), through the public API, one test per class of the truth table: <c>AllowKanji</c> × charset option × byte order mark × text class × symbology × version range.
/// Every expectation is a whole symbol built from a stream <see cref="KanjiStreamReference"/> writes from the standards, with the mask pinned, so "UTF-8" is checked as exactly as "Kanji".
/// </summary>
/// <remarks>
/// Kanji mode is written only when the caller sets <c>AllowKanji</c> (phase 6.6a): Android's own scanners read none of it, so by default every generator writes what it wrote before Kanji mode existed, UTF-8 behind an ECI header (none on Micro QR, which has no ECI).
/// With the option a text is eligible when the library chose the charset and chose UTF-8, and every character is ASCII or has an encoder cell.
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

    private static readonly QRCodeGeneratorOptions Kanji = new() { AllowKanji = true };

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

    /// <summary>Without <c>AllowKanji</c> a text whose every character has a cell is UTF-8 behind ECI 26, as before Kanji mode was written, under both segmentations.</summary>
    [Test]
    [MethodDataSource(nameof(AllCells))]
    public async Task StandardQr_AllCellsByDefault_IsUtf8(string text)
    {
        Run[] utf8 = [new Run('E', "26"), new Run('U', text)];
        await Assert.That(StandardCore(text, QREccLevel.M)).IsEquivalentTo(ExpectedStandard(utf8, QREccLevel.M), CollectionOrdering.Matching);
        var optimal = new QRCodeGeneratorOptions { Segmentation = QRSegmentation.Optimal };
        await Assert.That(StandardCore(text, QREccLevel.M, optimal)).IsEquivalentTo(StandardCore(text, QREccLevel.M, optimal with { EciMode = EciMode.Utf8 }), CollectionOrdering.Matching);

        // A version range or the boost takes the path that resolves version and level ahead of the encode; it is UTF-8 there too.
        foreach (var options in new[] { new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(40) }, new QRCodeGeneratorOptions { BoostEccLevel = true } })
            await Assert.That(StandardCore(text, QREccLevel.M, options)).IsEquivalentTo(StandardCore(text, QREccLevel.M, options with { EciMode = EciMode.Utf8 }), CollectionOrdering.Matching).Because($"{options.Version} boost {options.BoostEccLevel}");
    }

    [Test]
    [MethodDataSource(nameof(AllCells))]
    public async Task StandardQr_AllCellsWithAllowKanji_IsOneKanjiSegment(string text)
    {
        foreach (var ecc in new[] { QREccLevel.L, QREccLevel.H })
        {
            await Assert.That(StandardCore(text, ecc, Kanji)).IsEquivalentTo(ExpectedStandard([new Run('K', text)], ecc), CollectionOrdering.Matching).Because($"{ecc}");
            await Assert.That(StandardCore(text, ecc, Kanji with { Segmentation = QRSegmentation.Optimal }))
                .IsEquivalentTo(ExpectedStandard([new Run('K', text)], ecc), CollectionOrdering.Matching).Because($"{ecc}, Optimal: one Kanji run is the optimum");
        }
    }

    /// <summary>A byte order mark asks for UTF-8, so the text stays UTF-8 with the mark, <c>AllowKanji</c> or not (K1).</summary>
    [Test]
    [MethodDataSource(nameof(AllCells))]
    public async Task StandardQr_AllCellsWithUtf8Bom_StaysUtf8(string text)
    {
        Run[] expected = [new Run('E', "26"), new Run('U', "﻿" + text)];
        foreach (var allowKanji in new[] { false, true })
        {
            var bom = new QRCodeGeneratorOptions { Utf8Bom = true, AllowKanji = allowKanji };
            await Assert.That(StandardCore(text, QREccLevel.M, bom)).IsEquivalentTo(ExpectedStandard(expected, QREccLevel.M), CollectionOrdering.Matching).Because($"AllowKanji {allowKanji}");
            await Assert.That(StandardCore(text, QREccLevel.M, bom with { Segmentation = QRSegmentation.Optimal })).IsEquivalentTo(ExpectedStandard(expected, QREccLevel.M), CollectionOrdering.Matching).Because($"AllowKanji {allowKanji}, Optimal");
        }
    }

    /// <summary>A charset the caller chose is honoured: explicit UTF-8 stays UTF-8, <c>AllowKanji</c> or not (K1).</summary>
    [Test]
    [MethodDataSource(nameof(AllCells))]
    public async Task StandardQr_AllCellsWithExplicitUtf8_StaysUtf8(string text)
    {
        foreach (var allowKanji in new[] { false, true })
        {
            await Assert.That(StandardCore(text, QREccLevel.M, new QRCodeGeneratorOptions { EciMode = EciMode.Utf8, AllowKanji = allowKanji }))
                .IsEquivalentTo(ExpectedStandard([new Run('E', "26"), new Run('U', text)], QREccLevel.M), CollectionOrdering.Matching).Because($"AllowKanji {allowKanji}");
        }
    }

    [Test]
    [MethodDataSource(nameof(EligibleWithAscii))]
    public async Task StandardQr_EligibleWithAscii_SingleStaysUtf8(string text)
    {
        foreach (var options in new[] { default, Kanji })
            await Assert.That(StandardCore(text, QREccLevel.M, options)).IsEquivalentTo(ExpectedStandard([new Run('E', "26"), new Run('U', text)], QREccLevel.M), CollectionOrdering.Matching).Because($"AllowKanji {options.AllowKanji}");
    }

    /// <summary>Without <c>AllowKanji</c>, Optimal plans such a text as UTF-8, which is what the explicit charset writes; 「東京タワー333m」 included, whose Kanji plan would be smaller.</summary>
    [Test]
    [MethodDataSource(nameof(EligibleWithAscii))]
    public async Task StandardQr_EligibleWithAsciiByDefault_OptimalIsTheUtf8Plan(string text)
    {
        var optimal = new QRCodeGeneratorOptions { Segmentation = QRSegmentation.Optimal };
        await Assert.That(StandardCore(text, QREccLevel.M, optimal)).IsEquivalentTo(StandardCore(text, QREccLevel.M, optimal with { EciMode = EciMode.Utf8 }), CollectionOrdering.Matching);
    }

    /// <summary>
    /// With <c>AllowKanji</c> and Optimal such a text gets its Kanji plan (Kanji runs beside runs of the ASCII, no ECI header) where that plan needs a smaller version than the UTF-8 stream, and otherwise the UTF-8 stream, which is what the explicit charset writes.
    /// Of these texts only 「東京タワー333m」 moves: 19 UTF-8 bytes need version 2-M, and its Kanji plan fits 1-M. The others fit 1-M as UTF-8 already.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(EligibleWithAscii))]
    public async Task StandardQr_EligibleWithAsciiWithAllowKanji_OptimalTakesTheKanjiPlanOnlyWhereItIsSmaller(string text)
    {
        var optimal = Kanji with { Segmentation = QRSegmentation.Optimal };
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
        foreach (var options in new[] { default, Kanji })
        {
            await Assert.That(StandardCore(text, QREccLevel.M, options)).IsEquivalentTo(ExpectedStandard([new Run('E', "26"), new Run('U', text)], QREccLevel.M), CollectionOrdering.Matching).Because($"AllowKanji {options.AllowKanji}");
            await Assert.That(StandardCore(text, QREccLevel.M, options with { Segmentation = QRSegmentation.Optimal }))
                .IsEquivalentTo(StandardCore(text, QREccLevel.M, new QRCodeGeneratorOptions { Segmentation = QRSegmentation.Optimal, EciMode = EciMode.Utf8 }), CollectionOrdering.Matching).Because($"AllowKanji {options.AllowKanji}, Optimal");
        }
    }

    [Test]
    [MethodDataSource(nameof(Latin1))]
    public async Task StandardQr_Latin1_StaysLatin1(string text)
    {
        foreach (var options in new[] { default, Kanji })
            await Assert.That(StandardCore(text, QREccLevel.M, options)).IsEquivalentTo(ExpectedStandard([new Run('E', "3"), new Run('B', text)], QREccLevel.M), CollectionOrdering.Matching).Because($"AllowKanji {options.AllowKanji}");
    }

    [Test]
    public async Task StandardQr_Empty_StaysAnEmptyByteSegment()
    {
        foreach (var options in new[] { default, Kanji })
            await Assert.That(StandardCore("", QREccLevel.M, options)).IsEquivalentTo(ExpectedStandard([new Run('B', "")], QREccLevel.M), CollectionOrdering.Matching).Because($"AllowKanji {options.AllowKanji}");
    }

    /// <summary>Ten kanji fit 1-L in Kanji mode (10 characters) where UTF-8 with its ECI header needs version 2: with <c>AllowKanji</c> a range of version 1 holds the text, and without it does not, as before.</summary>
    [Test]
    public async Task StandardQr_ExactVersionOnlyKanjiFits_FitsWithAllowKanjiOnly()
    {
        var text = Cells(0, 10);
        var exactly1 = new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(1) };
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(text, QREccLevel.L, out var size, exactly1 with { AllowKanji = true })).IsTrue();
        await Assert.That(size.Version).IsEqualTo(1);
        await Assert.That(StandardCore(text, QREccLevel.L, exactly1 with { AllowKanji = true })).IsEquivalentTo(ExpectedStandard([new Run('K', text)], QREccLevel.L, 1), CollectionOrdering.Matching);
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(text, QREccLevel.L, out _, exactly1)).IsFalse();
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(text, QREccLevel.L, out _, exactly1 with { AllowKanji = true, EciMode = EciMode.Utf8 })).IsFalse();
    }

    /// <summary>Sizing reports the version Create writes, under every option that decides the mode.</summary>
    [Test]
    [Arguments(QRSegmentation.Single, false, false)]
    [Arguments(QRSegmentation.Optimal, false, false)]
    [Arguments(QRSegmentation.Single, true, false)]
    [Arguments(QRSegmentation.Optimal, true, false)]
    [Arguments(QRSegmentation.Single, false, true)]
    [Arguments(QRSegmentation.Optimal, false, true)]
    [Arguments(QRSegmentation.Single, true, true)]
    [Arguments(QRSegmentation.Optimal, true, true)]
    public async Task StandardQr_Sizing_MatchesCreate(QRSegmentation segmentation, bool bom, bool allowKanji)
    {
        foreach (var text in new[] { Cells(3, 30), Cells(5, 300), "QRコード" })
        {
            var options = new QRCodeGeneratorOptions { Segmentation = segmentation, Utf8Bom = bom, AllowKanji = allowKanji };
            await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(text, QREccLevel.M, out var size, options)).IsTrue();
            await Assert.That(QRCodeGenerator.Create(text, QREccLevel.M, options).Version).IsEqualTo(size.Version).Because(text);
        }
    }

    /// <summary>The boost prices the Kanji stream: 8 kanji fill 1-M (8 characters) and not 1-Q (6), so L rises to M and stops.</summary>
    [Test]
    public async Task StandardQr_BoostEccLevelWithAllowKanji_PricesTheKanjiStream()
    {
        var text = Cells(1, 8);
        var core = StandardCore(text, QREccLevel.L, Kanji with { BoostEccLevel = true });
        await Assert.That(core).IsEquivalentTo(ExpectedStandard([new Run('K', text)], QREccLevel.M, 1), CollectionOrdering.Matching);
    }

    /// <summary>The refusal counts what the text is written in: Kanji characters with <c>AllowKanji</c>, UTF-8 bytes without.</summary>
    [Test]
    public async Task StandardQr_TooLongForVersion40_ReportsTheUnitsOfItsMode()
    {
        var text = Cells(0, 1818);
        var kanji = await Assert.That(() => QRCodeGenerator.Create(text, QREccLevel.L, Kanji)).Throws<InvalidOperationException>();
        await Assert.That(kanji!.Message).Contains("Kanji");
        await Assert.That(kanji.Message).Contains("1818");
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(Cells(0, 1817), QREccLevel.L, out var size, Kanji)).IsTrue();
        await Assert.That(size.Version).IsEqualTo(40);

        var utf8 = await Assert.That(() => QRCodeGenerator.Create(text, QREccLevel.L)).Throws<InvalidOperationException>();
        await Assert.That(utf8!.Message).DoesNotContain("Kanji");
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(Cells(0, 1817), QREccLevel.L, out _)).IsFalse();
    }

    /// <summary>A set that fits one symbol is Create's symbol: Kanji with <c>AllowKanji</c>, UTF-8 without.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task StandardQr_StructuredAppendOfOneSymbol_IsCreatesSymbol(bool allowKanji)
    {
        var text = Cells(7, 20);
        var options = new QRCodeGeneratorOptions { MaskPattern = 0, AllowKanji = allowKanji };
        var set = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.M, options);
        await Assert.That(set.Length).IsEqualTo(1);
        var create = QRCodeGenerator.Create(text, QREccLevel.M, options);
        await Assert.That(set[0].Version).IsEqualTo(create.Version);
        await Assert.That(Modules(set[0])).IsEquivalentTo(Modules(create), CollectionOrdering.Matching);
        var expected = allowKanji ? ExpectedStandard([new Run('K', text)], QREccLevel.M) : ExpectedStandard([new Run('E', "26"), new Run('U', text)], QREccLevel.M);
        await Assert.That(StandardCore(text, QREccLevel.M, options)).IsEquivalentTo(expected, CollectionOrdering.Matching);
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

    private static readonly MicroQRCodeGeneratorOptions MicroKanji = new() { AllowKanji = true };

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

    /// <summary>
    /// Without <c>AllowKanji</c> a text whose every character has a cell goes out as bare UTF-8 bytes (Micro QR has no ECI), as before Kanji mode was written.
    /// 「こんにちは世界」 is 21 UTF-8 bytes, past M4-L's 15, so it does not fit, as it did not then.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(MicroAllCells))]
    public async Task MicroQr_AllCellsByDefault_IsUtf8(string text)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(text) > 15)
        {
            await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize(text, MicroQREccLevel.L, out _)).IsFalse();
            return;
        }
        await Assert.That(MicroCore(text, MicroQREccLevel.L)).IsEquivalentTo(ExpectedMicro([new Run('U', text)], MicroQREccLevel.L), CollectionOrdering.Matching);
        await Assert.That(MicroCore(text, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Segmentation = MicroQRSegmentation.Optimal }))
            .IsEquivalentTo(ExpectedMicro([new Run('U', text)], MicroQREccLevel.L), CollectionOrdering.Matching);
    }

    [Test]
    [MethodDataSource(nameof(MicroAllCells))]
    public async Task MicroQr_AllCellsWithAllowKanji_IsOneKanjiSegment(string text)
    {
        await Assert.That(MicroCore(text, MicroQREccLevel.L, MicroKanji)).IsEquivalentTo(ExpectedMicro([new Run('K', text)], MicroQREccLevel.L), CollectionOrdering.Matching);
        await Assert.That(MicroCore(text, MicroQREccLevel.L, MicroKanji with { Segmentation = MicroQRSegmentation.Optimal }))
            .IsEquivalentTo(ExpectedMicro([new Run('K', text)], MicroQREccLevel.L), CollectionOrdering.Matching);
    }

    /// <summary>Micro QR has no ECI: text that is not all cells goes out as bare UTF-8 bytes, <c>AllowKanji</c> or not.</summary>
    [Test]
    [Arguments("日本7")]      // eligible, with ASCII
    [Arguments("日本é")]      // a character without a cell
    [Arguments("～")]
    [Arguments("ｶﾅ")]
    public async Task MicroQr_NotAllCells_StaysUtf8(string text)
    {
        foreach (var options in new[] { default, MicroKanji })
            await Assert.That(MicroCore(text, MicroQREccLevel.L, options)).IsEquivalentTo(ExpectedMicro([new Run('U', text)], MicroQREccLevel.L), CollectionOrdering.Matching).Because($"AllowKanji {options.AllowKanji}");
    }

    [Test]
    public async Task MicroQr_Latin1_StaysLatin1Bytes()
    {
        foreach (var options in new[] { default, MicroKanji })
            await Assert.That(MicroCore("×÷", MicroQREccLevel.L, options)).IsEquivalentTo(ExpectedMicro([new Run('B', "×÷")], MicroQREccLevel.L), CollectionOrdering.Matching).Because($"AllowKanji {options.AllowKanji}");
    }

    /// <summary>A version range takes its own sizing path: 「こんにちは」 is M4 as UTF-8 (15 bytes) and M3 with <c>AllowKanji</c> (5 of 6 characters), and sizing agrees with Create either way.</summary>
    [Test]
    [Arguments(false, MicroQRVersion.M4)]
    [Arguments(true, MicroQRVersion.M3)]
    public async Task MicroQr_RangedSizing_FollowsTheOption(bool allowKanji, MicroQRVersion expected)
    {
        var options = new MicroQRCodeGeneratorOptions { Version = MicroQRVersionRange.AtLeast(MicroQRVersion.M3), AllowKanji = allowKanji };
        await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize("こんにちは", MicroQREccLevel.L, out var size, options)).IsTrue();
        await Assert.That(size.Version).IsEqualTo(expected);
        await Assert.That(MicroQRCodeGenerator.Create("こんにちは", MicroQREccLevel.L, options).Version).IsEqualTo(expected);
    }

    /// <summary>Kanji exists from M3, as Byte does: a range limited to M1-M2 holds neither, and the refusal names the mode the text is written in.</summary>
    [Test]
    public async Task MicroQr_RangeBelowM3_DoesNotFit()
    {
        var options = new MicroQRCodeGeneratorOptions { Version = MicroQRVersionRange.AtMost(MicroQRVersion.M2) };
        foreach (var allowKanji in new[] { false, true })
        {
            var withOption = options with { AllowKanji = allowKanji };
            await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize("日本", MicroQREccLevel.L, out _, withOption)).IsFalse();
            var ex = await Assert.That(() => MicroQRCodeGenerator.Create("日本", MicroQREccLevel.L, withOption)).Throws<ArgumentException>();
            await Assert.That(ex!.Message).Contains(allowKanji ? "Kanji" : "Byte");
        }
    }

    /// <summary>
    /// ErrorDetectionOnly allows M1 alone. A Kanji text's refusal names Kanji among the modes M3 adds; every other text's refusal, and the same text's without <c>AllowKanji</c>, is word for word what it was before Kanji mode was written.
    /// </summary>
    [Test]
    public async Task MicroQr_ErrorDetectionOnlyRefusal_NamesKanjiOnlyForKanji()
    {
        const string ByteRefusal =
            "Micro QR cannot encode Byte mode at ECC level ErrorDetectionOnly: ErrorDetectionOnly limits the symbol to M1 (Numeric only, 5 digits); " +
            "Alphanumeric requires M2+, Byte requires M3+, and level Q requires M4. Choose another ECC level or use Standard QR (QRCodeGenerator).";

        var kanji = await Assert.That(() => MicroQRCodeGenerator.Create("日本", MicroQREccLevel.ErrorDetectionOnly, MicroKanji)).Throws<ArgumentException>();
        await Assert.That(kanji!.Message).IsEqualTo(
            "Micro QR cannot encode Kanji mode at ECC level ErrorDetectionOnly: ErrorDetectionOnly limits the symbol to M1 (Numeric only, 5 digits); " +
            "Alphanumeric requires M2+, Byte and Kanji require M3+, and level Q requires M4. Choose another ECC level or use Standard QR (QRCodeGenerator).");

        var utf8 = await Assert.That(() => MicroQRCodeGenerator.Create("日本", MicroQREccLevel.ErrorDetectionOnly)).Throws<ArgumentException>();
        await Assert.That(utf8!.Message).IsEqualTo(ByteRefusal);

        var bytes = await Assert.That(() => MicroQRCodeGenerator.Create("日本é", MicroQREccLevel.ErrorDetectionOnly, MicroKanji)).Throws<ArgumentException>();
        await Assert.That(bytes!.Message).IsEqualTo(ByteRefusal);
    }

    /// <summary>A version that does not offer Kanji says so, listing Kanji where M3 adds it; a Byte text's refusal, and the same text's without <c>AllowKanji</c>, is unchanged.</summary>
    [Test]
    public async Task MicroQr_ExactVersionWithoutTheMode_NamesKanjiOnlyForKanji()
    {
        const string ByteRefusal = "Encoding mode Byte is not available on Micro QR version M2 (M1: Numeric; M2: +Alphanumeric; M3/M4: +Byte).";
        var exactlyM2 = new MicroQRCodeGeneratorOptions { Version = MicroQRVersionRange.Exactly(MicroQRVersion.M2) };
        var kanji = await Assert.That(() => MicroQRCodeGenerator.Create("日本", MicroQREccLevel.L, exactlyM2 with { AllowKanji = true })).Throws<ArgumentException>();
        await Assert.That(kanji!.Message).StartsWith("Encoding mode Kanji is not available on Micro QR version M2 (M1: Numeric; M2: +Alphanumeric; M3/M4: +Byte and Kanji).");

        var utf8 = await Assert.That(() => MicroQRCodeGenerator.Create("日本", MicroQREccLevel.L, exactlyM2)).Throws<ArgumentException>();
        await Assert.That(utf8!.Message).StartsWith(ByteRefusal);

        var bytes = await Assert.That(() => MicroQRCodeGenerator.Create("byte", MicroQREccLevel.L, exactlyM2 with { AllowKanji = true })).Throws<ArgumentException>();
        await Assert.That(bytes!.Message).StartsWith(ByteRefusal);
    }

    /// <summary>With <c>AllowKanji</c> nine kanji fill M4-L and ten are refused in characters, not bytes; without it the same nine are 27 UTF-8 bytes and do not fit at all.</summary>
    [Test]
    public async Task MicroQr_CapacityWithAllowKanji_IsTheKanjiColumn()
    {
        await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize(Cells(0, 9), MicroQREccLevel.L, out var size, MicroKanji)).IsTrue();
        await Assert.That(size.Version).IsEqualTo(MicroQRVersion.M4);
        await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize(Cells(0, 6), MicroQREccLevel.L, out size, MicroKanji)).IsTrue();
        await Assert.That(size.Version).IsEqualTo(MicroQRVersion.M3);

        var ex = await Assert.That(() => MicroQRCodeGenerator.Create(Cells(0, 10), MicroQREccLevel.L, MicroKanji)).Throws<ArgumentException>();
        await Assert.That(ex!.Message).Contains("10 characters in Kanji mode");
        await Assert.That(ex.Message).Contains("9 characters");

        await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize(Cells(0, 9), MicroQREccLevel.L, out _)).IsFalse();
    }

    // ---- rMQR ----------------------------------------------------------------------

    private static readonly RmQRCodeGeneratorOptions RmKanji = new() { AllowKanji = true };

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

    /// <summary>Without <c>AllowKanji</c> a text whose every character has a cell is UTF-8 behind ECI 26, as before Kanji mode was written, under both segmentations.</summary>
    [Test]
    [MethodDataSource(nameof(AllCells))]
    public async Task RmQr_AllCellsByDefault_IsUtf8(string text)
    {
        Run[] utf8 = [new Run('E', "26"), new Run('U', text)];
        await Assert.That(RmQrCore(text, default)).IsEquivalentTo(ExpectedRmQr(utf8), CollectionOrdering.Matching);
        await Assert.That(RmQrCore(text, new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal }))
            .IsEquivalentTo(RmQrCore(text, new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal, EciMode = EciMode.Utf8 }), CollectionOrdering.Matching);
    }

    [Test]
    [MethodDataSource(nameof(AllCells))]
    public async Task RmQr_AllCellsWithAllowKanji_IsOneKanjiSegment(string text)
    {
        await Assert.That(RmQrCore(text, RmKanji)).IsEquivalentTo(ExpectedRmQr([new Run('K', text)]), CollectionOrdering.Matching);
        await Assert.That(RmQrCore(text, RmKanji with { Segmentation = RmQRSegmentation.Optimal })).IsEquivalentTo(ExpectedRmQr([new Run('K', text)]), CollectionOrdering.Matching);
        await Assert.That(RmQrCore(text, RmKanji with { EciMode = EciMode.Utf8 })).IsEquivalentTo(ExpectedRmQr([new Run('E', "26"), new Run('U', text)]), CollectionOrdering.Matching);
    }

    /// <summary>
    /// With the version left to the fit, Optimal reaches the planner, which has to see a Kanji text as already optimal: priced under the Kanji analysis's charset (none), its characters would look like narrowed Latin-1 bytes.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(AllCells))]
    public async Task RmQr_AllCellsAutoFitWithAllowKanji_OptimalIsTheSingleKanjiSymbol(string text)
    {
        foreach (var strategy in new[] { RmQRFitStrategy.MinimizeArea, RmQRFitStrategy.MinimizeWidth, RmQRFitStrategy.MinimizeHeight })
        {
            var single = RmQRCodeGenerator.Create(text, RmQREccLevel.M, RmKanji with { FitStrategy = strategy });
            var optimal = RmQRCodeGenerator.Create(text, RmQREccLevel.M, RmKanji with { FitStrategy = strategy, Segmentation = RmQRSegmentation.Optimal });
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
        foreach (var options in new[] { default, RmKanji })
            await Assert.That(RmQrCore(text, options)).IsEquivalentTo(ExpectedRmQr([new Run('E', "26"), new Run('U', text)]), CollectionOrdering.Matching).Because($"AllowKanji {options.AllowKanji}");
    }

    [Test]
    [MethodDataSource(nameof(WithoutACell))]
    public async Task RmQr_CharacterWithoutACell_StaysUtf8(string text)
    {
        foreach (var options in new[] { default, RmKanji })
            await Assert.That(RmQrCore(text, options)).IsEquivalentTo(ExpectedRmQr([new Run('E', "26"), new Run('U', text)]), CollectionOrdering.Matching).Because($"AllowKanji {options.AllowKanji}");
    }

    [Test]
    [MethodDataSource(nameof(Latin1))]
    public async Task RmQr_Latin1_StaysLatin1(string text)
    {
        foreach (var options in new[] { default, RmKanji })
            await Assert.That(RmQrCore(text, options)).IsEquivalentTo(ExpectedRmQr([new Run('E', "3"), new Run('B', text)]), CollectionOrdering.Matching).Because($"AllowKanji {options.AllowKanji}");
    }

    /// <summary>With <c>AllowKanji</c> the automatic fit picks the Kanji stream's version, sizing agrees, and a refusal counts characters; without it the same text is UTF-8 and does not fit R7x43.</summary>
    [Test]
    public async Task RmQr_FitSizingAndRefusal_FollowTheOption()
    {
        var text = Cells(0, 3);   // R7x43-M holds 3 Kanji; as UTF-8 (6 bytes or more behind an ECI header) it does not fit R7x43
        var minimizeHeight = new RmQRCodeGeneratorOptions { FitStrategy = RmQRFitStrategy.MinimizeHeight };
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(text, RmQREccLevel.M, out var size, minimizeHeight with { AllowKanji = true })).IsTrue();
        await Assert.That(size.Version).IsEqualTo(RmQRVersion.R7x43);
        await Assert.That(RmQRCodeGenerator.Create(text, RmQREccLevel.M, minimizeHeight with { AllowKanji = true }).Version).IsEqualTo(RmQRVersion.R7x43);
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(text, RmQREccLevel.M, out size, minimizeHeight)).IsTrue();
        await Assert.That(size.Version).IsNotEqualTo(RmQRVersion.R7x43);
        await Assert.That(RmQRCodeGenerator.Create(text, RmQREccLevel.M, minimizeHeight).Version).IsEqualTo(size.Version);

        var kanji = await Assert.That(() => RmQRCodeGenerator.Create(Cells(0, 93), RmQREccLevel.M, RmKanji)).Throws<ArgumentException>();
        await Assert.That(kanji!.Message).Contains("93 characters in Kanji mode");
        await Assert.That(kanji.Message).Contains("92 characters");
        var utf8 = await Assert.That(() => RmQRCodeGenerator.Create(Cells(0, 93), RmQREccLevel.M)).Throws<ArgumentException>();
        await Assert.That(utf8!.Message).DoesNotContain("Kanji");
    }
}
