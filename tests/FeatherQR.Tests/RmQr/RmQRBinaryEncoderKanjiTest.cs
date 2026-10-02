using TUnit.Assertions.Enums;
using FeatherQR.Internals;
using FeatherQR.Internals.RmQR;
using static FeatherQR.Tests.KanjiStreamReference;

namespace FeatherQR.Tests;

/// <summary>
/// rMQR Kanji mode's writers, below the public API: indicator 100, a count of 2 to 7 bits, 13 bits a character.
/// The writers are checked against a stream written from ISO/IEC 23941 (<see cref="KanjiStreamReference"/>), the capacity and the fit tables against the standard's Kanji column, and the symbols through the matrix decoder.
/// </summary>
public class RmQRBinaryEncoderKanjiTest
{
    /// <summary>
    /// ISO/IEC 23941 Table 7, Kanji column (M, H) per version in ISO index order, as the English Wikipedia article on rMQR reproduces it (retrieved 2026-09-29).
    /// That table's R11x77-M row is corrupt: its Numeric, Alphanumeric and Byte cells (33, 20, 14) are R11x59-H's, where FeatherQR's oracle-verified capacities are 100, 60 and 41. Its Kanji cell (8) is left out for the same reason, and that one value rests on the arithmetic the other 63 confirm.
    /// </summary>
    private static readonly (int M, int H)[] PublishedKanjiCapacity =
    [
        (3, 1), (6, 3), (11, 5), (16, 8), (26, 14),
        (6, 3), (12, 6), (18, 9), (25, 12), (38, 19),
        (3, 2), (11, 6), (18, 8), (-1, 13), (34, 17), (51, 25),
        (6, 3), (16, 7), (22, 11), (31, 17), (44, 20), (64, 32),
        (19, 8), (28, 15), (40, 18), (53, 28), (77, 41),
        (23, 12), (33, 16), (47, 22), (60, 33), (92, 46),
    ];

    public static IEnumerable<(RmQRVersion Version, RmQREccLevel Ecc)> AllVersionsAndLevels()
    {
        foreach (var version in Enum.GetValues<RmQRVersion>())
        {
            yield return (version, RmQREccLevel.M);
            yield return (version, RmQREccLevel.H);
        }
    }

    private static int Index(RmQRVersion version) => (int)version - 1;

    private static int Capacity(RmQRVersion version, RmQREccLevel ecc)
    {
        var (m, h) = PublishedKanjiCapacity[Index(version)];
        var published = ecc == RmQREccLevel.M ? m : h;
        return published >= 0 ? published : (8 * RmQRConstants.GetDataCodewordCount(version, ecc) - 3 - RmQrCountBits('K', Index(version))) / 13;
    }

    private static byte[] Reference(RmQRVersion version, RmQREccLevel ecc, Run[] runs)
        => RmQrStream(Index(version), RmQRConstants.GetDataCodewordCount(version, ecc), runs);

    private static byte[] EncodeSingle(string text, RmQRVersion version, RmQREccLevel ecc)
    {
        var destination = new byte[RmQRConstants.GetDataCodewordCount(version, ecc)];
        var analysis = new TextAnalysisResult(EncodingMode.Kanji, EciMode.Default, text.Length);
        RmQRBinaryEncoder.EncodeDataCodewordsWithoutEci(text, version, ecc, in analysis, destination);
        return destination;
    }

    private static int ModeIndex(char mode) => mode switch { 'N' => 0, 'A' => 1, 'B' => 2, _ => 3 };

    private static ModeSegment[] Segments(Run[] runs)
    {
        var segments = new ModeSegment[runs.Length];
        var start = 0;
        for (var i = 0; i < runs.Length; i++)
        {
            segments[i] = new ModeSegment(ModeIndex(runs[i].Mode), start, runs[i].Text.Length, runs[i].Text.Length);
            start += runs[i].Text.Length;
        }
        return segments;
    }

    private static byte[] EncodeSegmented(RmQRVersion version, RmQREccLevel ecc, Run[] runs, EciMode charset = EciMode.Default)
    {
        var destination = new byte[RmQRConstants.GetDataCodewordCount(version, ecc)];
        RmQRBinaryEncoder.EncodeDataCodewordsSegmented(string.Concat(runs.Select(r => r.Text)), version, ecc, charset, Segments(runs), destination);
        return destination;
    }

    // ---- count widths --------------------------------------------------------------

    /// <summary>
    /// The count widths of all four modes against the published transcription of ISO/IEC 23941 Table 3.
    /// The Kanji column had rested on a derivation (the narrowest field that holds the M-level capacity) and on fixtures at widths 4, 5 and 7; this is a second, independent reading of widths 2, 3 and 6.
    /// </summary>
    [Test]
    public async Task CountWidths_MatchThePublishedTable()
    {
        foreach (var version in Enum.GetValues<RmQRVersion>())
        {
            var index = Index(version);
            await Assert.That(RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric)).IsEqualTo(RmQrCountBits('N', index)).Because($"{version} Numeric");
            await Assert.That(RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric)).IsEqualTo(RmQrCountBits('A', index)).Because($"{version} Alphanumeric");
            await Assert.That(RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte)).IsEqualTo(RmQrCountBits('B', index)).Because($"{version} Byte");
            await Assert.That(RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Kanji)).IsEqualTo(RmQrCountBits('K', index)).Because($"{version} Kanji");
            await Assert.That(RmQRConstants.GetKanjiCountIndicatorLength(version)).IsEqualTo(RmQrCountBits('K', index)).Because($"{version} Kanji, decoder's accessor");
        }
    }

    // ---- the writers against the reference -----------------------------------------

    /// <summary>Every version and level filled to its Kanji capacity: all six count widths, each at the largest count its versions write.</summary>
    [Test]
    [MethodDataSource(nameof(AllVersionsAndLevels))]
    public async Task SingleSegment_AtCapacity_MatchesTheReference(RmQRVersion version, RmQREccLevel ecc)
    {
        foreach (var length in new[] { 1, Capacity(version, ecc) })
        {
            var text = Cells((int)version * 37 + (int)ecc * 5 + length, length);
            await Assert.That(EncodeSingle(text, version, ecc)).IsEquivalentTo(Reference(version, ecc, [new Run('K', text)]), CollectionOrdering.Matching)
                .Because($"{length} Kanji");
        }
    }

    [Test]
    public async Task SingleSegment_EveryEncoderCell_MatchesTheReference()
    {
        const RmQRVersion version = RmQRVersion.R17x139;
        var capacity = Capacity(version, RmQREccLevel.M);
        for (var start = 0; start < EncoderCells.Length; start += capacity)
        {
            var text = Cells(start, Math.Min(capacity, EncoderCells.Length - start));
            await Assert.That(EncodeSingle(text, version, RmQREccLevel.M)).IsEquivalentTo(Reference(version, RmQREccLevel.M, [new Run('K', text)]), CollectionOrdering.Matching)
                .Because($"cells {start}..{start + text.Length - 1}");
        }
    }

    /// <summary>A Kanji run behind prefixes of every length modulo 64, followed by a digit run: the 64-bit accumulator meets a Kanji value at every starting bit.</summary>
    [Test]
    [Arguments(RmQRVersion.R17x139)]
    [Arguments(RmQRVersion.R13x77)]
    public async Task Segmented_KanjiRunAtEveryStartingBit_MatchesTheReference(RmQRVersion version)
    {
        const RmQREccLevel ecc = RmQREccLevel.M;
        var capacity = 8 * RmQRConstants.GetDataCodewordCount(version, ecc);
        var kanji = Cells((int)version * 11, 4);
        var covered = new bool[64];
        foreach (var prefix in QRBinaryEncoderKanjiTest.Prefixes())
        {
            var offset = RmQrBits(Index(version), prefix).Length % 64;
            Run[] runs = [.. prefix, new Run('K', kanji), new Run('N', "2026")];
            if (covered[offset] || RmQrBits(Index(version), runs).Length > capacity)
                continue;
            covered[offset] = true;

            await Assert.That(EncodeSegmented(version, ecc, runs)).IsEquivalentTo(Reference(version, ecc, runs), CollectionOrdering.Matching)
                .Because($"Kanji run starting at bit offset {offset} mod 64");
        }

        await Assert.That(covered.Count(static c => c)).IsEqualTo(64).Because("every starting bit mod 64 is exercised");
    }

    [Test]
    [Arguments(RmQRVersion.R7x43)]
    [Arguments(RmQRVersion.R11x27)]
    [Arguments(RmQRVersion.R17x139)]
    public async Task Segmented_OneKanjiRun_MatchesTheSingleSegmentWriter(RmQRVersion version)
    {
        var text = Cells((int)version, Capacity(version, RmQREccLevel.M));
        await Assert.That(EncodeSegmented(version, RmQREccLevel.M, [new Run('K', text)])).IsEquivalentTo(EncodeSingle(text, version, RmQREccLevel.M), CollectionOrdering.Matching);
    }

    // ---- what the writers refuse ---------------------------------------------------

    [Test]
    [Arguments("日A")]   // ASCII
    [Arguments("〜")]    // JIS X 0208 reading of a divergent cell
    [Arguments("～")]    // CP932 reading of the same cell
    [Arguments("①")]    // NEC row 13
    [Arguments("ｶ")]    // halfwidth katakana
    public async Task SingleSegment_CharacterWithoutACell_Throws(string text)
    {
        await Assert.That(() => EncodeSingle(text, RmQRVersion.R17x139, RmQREccLevel.M)).Throws<ArgumentException>();
    }

    [Test]
    public async Task Segmented_CharacterWithoutACell_Throws()
    {
        await Assert.That(() => EncodeSegmented(RmQRVersion.R17x139, RmQREccLevel.M, [new Run('N', "1"), new Run('K', "～")])).Throws<ArgumentException>();
    }

    /// <summary>The writer that emits an ECI header has no Kanji arm: Kanji is written only by the writer without one (plan K4).</summary>
    [Test]
    [Arguments(EciMode.Utf8)]
    [Arguments(EciMode.Iso8859_1)]
    public async Task SingleSegment_KanjiUnderAnEciHeader_Throws(EciMode eci)
    {
        await Assert.That(() =>
        {
            var destination = new byte[RmQRConstants.GetDataCodewordCount(RmQRVersion.R17x139, RmQREccLevel.M)];
            var analysis = new TextAnalysisResult(EncodingMode.Kanji, eci, 2);
            RmQRBinaryEncoder.EncodeDataCodewords("日本", RmQRVersion.R17x139, RmQREccLevel.M, in analysis, destination);
        }).Throws<ArgumentException>();
    }

    /// <summary>Kanji beside an ECI header is out of scope (plan K4): the segmented writer refuses a plan that would write it.</summary>
    [Test]
    [Arguments(EciMode.Utf8)]
    [Arguments(EciMode.Iso8859_1)]
    public async Task Segmented_KanjiRunUnderAnEciHeader_Throws(EciMode eci)
    {
        await Assert.That(() => EncodeSegmented(RmQRVersion.R17x139, RmQREccLevel.M, [new Run('N', "1"), new Run('K', Cells(0, 2))], eci)).Throws<ArgumentException>();
    }

    // ---- capacity and version selection -------------------------------------------

    [Test]
    [MethodDataSource(nameof(AllVersionsAndLevels))]
    public async Task Capacity_MatchesThePublishedKanjiColumn(RmQRVersion version, RmQREccLevel ecc)
    {
        var capacity = Capacity(version, ecc);
        await Assert.That(RmQRVersionSelector.GetMaxDataLength(version, ecc, EncodingMode.Kanji)).IsEqualTo(capacity);
        await Assert.That(RmQRVersionSelector.GetMaxDataLength(version, ecc, EncodingMode.Kanji, EciMode.Default)).IsEqualTo(capacity);
        await Assert.That(RmQRVersionSelector.Fits(version, ecc, EncodingMode.Kanji, capacity)).IsTrue();
        await Assert.That(RmQRVersionSelector.Fits(version, ecc, EncodingMode.Kanji, capacity + 1)).IsFalse();
        await Assert.That(RmQRVersionSelector.GetRequiredBits(version, EncodingMode.Kanji, capacity))
            .IsEqualTo(3 + RmQrCountBits('K', Index(version)) + 13L * capacity);

        // The count field holds the capacity, so the width never binds below the bit budget.
        await Assert.That(capacity).IsLessThan(1 << RmQRConstants.GetKanjiCountIndicatorLength(version));
    }

    /// <summary>The precomputed fit table picks what a scan of every version under the strategy's order picks, for every Kanji count.</summary>
    [Test]
    [Arguments(RmQRFitStrategy.MinimizeArea)]
    [Arguments(RmQRFitStrategy.MinimizeWidth)]
    [Arguments(RmQRFitStrategy.MinimizeHeight)]
    public async Task AutoFit_MatchesTheDefinitionalScan(RmQRFitStrategy strategy)
    {
        foreach (var ecc in new[] { RmQREccLevel.M, RmQREccLevel.H })
        {
            for (var count = 0; count <= 93; count++)
            {
                RmQRVersion? expected = null;
                foreach (var candidate in Enum.GetValues<RmQRVersion>())
                {
                    if (count <= Capacity(candidate, ecc) && (expected is null || RmQRVersionSelector.IsBetter(candidate, expected.Value, strategy)))
                        expected = candidate;
                }

                var fits = RmQRVersionSelector.TrySelectAutoFit(EncodingMode.Kanji, count, EciMode.Default, ecc, strategy, null, out var actual);
                await Assert.That(fits).IsEqualTo(expected is not null).Because($"{count} Kanji at {ecc}");
                if (expected is { } version)
                    await Assert.That(actual).IsEqualTo(version).Because($"{count} Kanji at {ecc}");
            }
        }
    }

    [Test]
    public async Task MeasurePlan_PricesAKanjiRunAt13BitsACharacter()
    {
        ModeSegment[] plan = [new ModeSegment(3, 0, 5, 5)];
        await Assert.That(RmQRSegmentPlanner.MeasurePlan(RmQRVersion.R7x43, plan)).IsEqualTo(3 + 2 + 5 * 13);
        await Assert.That(RmQRSegmentPlanner.MeasurePlan(RmQRVersion.R17x139, plan)).IsEqualTo(3 + 7 + 5 * 13);
    }

    // ---- round trip ----------------------------------------------------------------

    [Test]
    [MethodDataSource(nameof(AllVersionsAndLevels))]
    public async Task RoundTrip_ThroughTheMatrixDecoder(RmQRVersion version, RmQREccLevel ecc)
    {
        var text = Cells((int)version * 3 + (int)ecc, Capacity(version, ecc));
        await Assert.That(Decode(EncodeSingle(text, version, ecc), version, ecc)).IsEqualTo(text);

        if (Capacity(version, ecc) >= 4)
        {
            Run[] runs = [new Run('N', "1"), new Run('K', Cells((int)version, 2)), new Run('A', "Q")];
            if (RmQrBits(Index(version), runs).Length <= 8 * RmQRConstants.GetDataCodewordCount(version, ecc))
                await Assert.That(Decode(EncodeSegmented(version, ecc, runs), version, ecc)).IsEqualTo(string.Concat(runs.Select(r => r.Text)));
        }
    }

    private static string Decode(byte[] data, RmQRVersion version, RmQREccLevel ecc)
        => KanjiSymbolBuilder.DecodeRmQr(KanjiSymbolBuilder.RmQr(data, version, ecc), version);
}
