using TUnit.Assertions.Enums;
using FeatherQR.Internals;
using FeatherQR.Internals.StandardQR;
using static FeatherQR.Tests.KanjiStreamReference;

namespace FeatherQR.Tests;

/// <summary>
/// Standard QR Kanji mode's writers, below the public API: the single-segment and segmented writers against a stream written from ISO/IEC 18004 (<see cref="KanjiStreamReference"/>), the capacity against the standard's Kanji column, and a round trip through the matrix decoder.
/// </summary>
public class QRBinaryEncoderKanjiTest
{
    private static readonly QREccLevel[] Levels = [QREccLevel.L, QREccLevel.M, QREccLevel.Q, QREccLevel.H];

    /// <summary>ISO/IEC 18004 Table 7 as QRCodeConstants carries it: the Kanji column of each version and level.</summary>
    private static int PublishedKanjiCapacity(int version, QREccLevel ecc)
        => QRCodeConstants.CapacityBaseValues[(version - 1) * 16 + (int)ecc * 4 + 3];

    private static int DataCodewords(int version, QREccLevel ecc) => QRCodeConstants.GetEccInfo(version, ecc).TotalDataCodewords;

    private static byte[] EncodeSingle(string text, int version, QREccLevel ecc)
    {
        var buffer = new byte[DataCodewords(version, ecc)];
        var encoder = new QRBinaryEncoder(buffer);
        encoder.WriteMode(EncodingMode.Kanji, EciMode.Default);
        encoder.WriteCharacterCount(text.Length, EncodingMode.Kanji.GetCountIndicatorLength(version));
        encoder.WriteData(text, EncodingMode.Kanji, EciMode.Default, utf8Bom: false);
        encoder.WritePadding(buffer.Length * 8);
        return encoder.GetEncodedData().ToArray();
    }

    private static int ModeIndex(char mode) => mode switch { 'N' => 0, 'A' => 1, 'B' => 2, _ => 3 };

    private static byte[] EncodeSegmented(string text, int version, QREccLevel ecc, Run[] runs)
    {
        var segments = new ModeSegment[runs.Length];
        var start = 0;
        for (var i = 0; i < runs.Length; i++)
        {
            segments[i] = new ModeSegment(ModeIndex(runs[i].Mode), start, runs[i].Text.Length, runs[i].Text.Length);
            start += runs[i].Text.Length;
        }

        var buffer = new byte[DataCodewords(version, ecc)];
        var encoder = new QRBinaryEncoder(buffer);
        encoder.WriteSegments(text, segments, version, EciMode.Default);
        encoder.WritePadding(buffer.Length * 8);
        return encoder.GetEncodedData().ToArray();
    }

    public static IEnumerable<(int Version, QREccLevel Ecc)> AllVersionsAndLevels()
    {
        for (var version = 1; version <= 40; version++)
            foreach (var ecc in Levels)
                yield return (version, ecc);
    }

    // ---- the writers against the reference -----------------------------------------

    /// <summary>A symbol filled to its Kanji capacity: every count width (8, 10, 12) and the largest count each version writes.</summary>
    [Test]
    [MethodDataSource(nameof(AllVersionsAndLevels))]
    public async Task SingleSegment_AtCapacity_MatchesTheReference(int version, QREccLevel ecc)
    {
        var text = Cells(version * 101 + (int)ecc * 29, PublishedKanjiCapacity(version, ecc));
        var expected = StandardQrStream(version, DataCodewords(version, ecc), [new Run('K', text)]);

        await Assert.That(EncodeSingle(text, version, ecc)).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    /// <summary>Every encoder cell once, at version 40-L: each character's 13-bit value is the one its Shift_JIS pair gives.</summary>
    [Test]
    public async Task SingleSegment_EveryEncoderCell_MatchesTheReference()
    {
        var capacity = PublishedKanjiCapacity(40, QREccLevel.L);
        for (var start = 0; start < EncoderCells.Length; start += capacity)
        {
            var text = Cells(start, Math.Min(capacity, EncoderCells.Length - start));
            var expected = StandardQrStream(40, DataCodewords(40, QREccLevel.L), [new Run('K', text)]);
            await Assert.That(EncodeSingle(text, 40, QREccLevel.L)).IsEquivalentTo(expected, CollectionOrdering.Matching).Because($"cells {start}..{start + text.Length - 1}");
        }
    }

    /// <summary>
    /// A Kanji run behind prefixes of every length modulo 64, one version per count band, followed by an ASCII run: the writer's accumulator meets a Kanji value at every starting bit.
    /// </summary>
    [Test]
    [Arguments(5)]
    [Arguments(10)]
    [Arguments(27)]
    public async Task Segmented_KanjiRunAtEveryStartingBit_MatchesTheReference(int version)
    {
        const QREccLevel ecc = QREccLevel.L;
        var covered = new bool[64];
        var kanji = Cells(version * 7, 5);
        foreach (var prefix in Prefixes())
        {
            var offset = StandardQrBits(version, prefix).Length % 64;
            Run[] runs = [.. prefix, new Run('K', kanji), new Run('A', "QR1")];
            if (covered[offset] || StandardQrBits(version, runs).Length > DataCodewords(version, ecc) * 8)
                continue;
            covered[offset] = true;

            var text = string.Concat(runs.Select(r => r.Text));
            var expected = StandardQrStream(version, DataCodewords(version, ecc), runs);
            await Assert.That(EncodeSegmented(text, version, ecc, runs)).IsEquivalentTo(expected, CollectionOrdering.Matching).Because($"Kanji run starting at bit offset {offset} mod 64");
        }

        await Assert.That(covered.Count(static c => c)).IsEqualTo(64).Because("every starting bit mod 64 is exercised");
    }

    /// <summary>Numeric, Alphanumeric and Byte prefixes (and none), shortest first.</summary>
    internal static IEnumerable<Run[]> Prefixes()
    {
        yield return [];
        for (var digits = 0; digits <= 9; digits++)
            for (var letters = 0; letters <= 5; letters++)
                for (var bytes = 0; bytes <= 7; bytes++)
                {
                    var runs = new List<Run>();
                    if (digits > 0) runs.Add(new Run('N', "123456789".Substring(0, digits)));
                    if (letters > 0) runs.Add(new Run('A', "AB-C:".Substring(0, letters)));
                    if (bytes > 0) runs.Add(new Run('B', "abcdefg".Substring(0, bytes)));
                    if (runs.Count > 0)
                        yield return [.. runs];
                }
    }

    /// <summary>A one-run plan writes the same stream as the single-segment writer.</summary>
    [Test]
    [Arguments(1)]
    [Arguments(9)]
    [Arguments(10)]
    [Arguments(26)]
    [Arguments(27)]
    [Arguments(40)]
    public async Task Segmented_OneKanjiRun_MatchesTheSingleSegmentWriter(int version)
    {
        var text = Cells(version, PublishedKanjiCapacity(version, QREccLevel.M));
        await Assert.That(EncodeSegmented(text, version, QREccLevel.M, [new Run('K', text)]))
            .IsEquivalentTo(EncodeSingle(text, version, QREccLevel.M), CollectionOrdering.Matching);
    }

    // ---- what the writers refuse ---------------------------------------------------

    /// <summary>A character with no encoder cell reaching the Kanji writer is a defect upstream; the writer refuses rather than write some other character.</summary>
    [Test]
    [Arguments("日本A")]      // ASCII
    [Arguments("〜")]         // JIS X 0208 reading of a divergent cell
    [Arguments("～")]         // CP932 reading of the same cell
    [Arguments("日①")]        // NEC row 13
    [Arguments("ｶﾅ")]         // halfwidth katakana
    public async Task SingleSegment_CharacterWithoutACell_Throws(string text)
    {
        await Assert.That(() => EncodeSingle(text, 10, QREccLevel.L)).Throws<ArgumentException>();
    }

    [Test]
    public async Task Segmented_CharacterWithoutACell_Throws()
    {
        await Assert.That(() => EncodeSegmented("12～", 10, QREccLevel.L, [new Run('N', "12"), new Run('K', "～")])).Throws<ArgumentException>();
    }

    /// <summary>Kanji beside an ECI header is out of scope (plan K4): the single-segment writer refuses it too.</summary>
    [Test]
    [Arguments(EciMode.Utf8)]
    [Arguments(EciMode.Iso8859_1)]
    public async Task SingleSegment_KanjiUnderAnEciHeader_Throws(EciMode eci)
    {
        await Assert.That(() =>
        {
            var buffer = new byte[DataCodewords(10, QREccLevel.L)];
            var encoder = new QRBinaryEncoder(buffer);
            encoder.WriteData("日本", EncodingMode.Kanji, eci, utf8Bom: false);
        }).Throws<ArgumentException>();
    }

    /// <summary>Kanji beside an ECI header is out of scope (plan K4): the segmented writer refuses a plan that would write it.</summary>
    [Test]
    [Arguments(EciMode.Utf8)]
    [Arguments(EciMode.Iso8859_1)]
    public async Task Segmented_KanjiRunUnderAnEciHeader_Throws(EciMode eci)
    {
        await Assert.That(() =>
        {
            var buffer = new byte[DataCodewords(10, QREccLevel.L)];
            var segments = new[] { new ModeSegment(0, 0, 2, 2), new ModeSegment(3, 2, 2, 2) };
            var encoder = new QRBinaryEncoder(buffer);
            encoder.WriteSegments("12日本", segments, 10, eci);
        }).Throws<ArgumentException>();
    }

    // ---- capacity and version selection -------------------------------------------

    /// <summary>The largest Kanji count each version and level takes is the standard's Kanji column, and one more does not fit.</summary>
    [Test]
    [MethodDataSource(nameof(AllVersionsAndLevels))]
    public async Task Capacity_MatchesTheStandardsKanjiColumn(int version, QREccLevel ecc)
    {
        var capacity = PublishedKanjiCapacity(version, ecc);
        await Assert.That(QRCodeGenerator.FitsVersion(capacity, EncodingMode.Kanji, ecc, EciMode.Default, false, version)).IsTrue();
        await Assert.That(QRCodeGenerator.FitsVersion(capacity + 1, EncodingMode.Kanji, ecc, EciMode.Default, false, version)).IsFalse();
    }

    [Test]
    [Arguments(10, QREccLevel.L, 1)]     // 1-L holds 10
    [Arguments(11, QREccLevel.L, 2)]
    [Arguments(1817, QREccLevel.L, 40)]  // 40-L holds 1,817
    [Arguments(784, QREccLevel.H, 40)]   // 40-H holds 784
    public async Task VersionSelection_PicksTheSmallestVersionThatHoldsTheKanji(int count, QREccLevel ecc, int expected)
    {
        await Assert.That(QRCodeGenerator.TryGetVersion(count, EncodingMode.Kanji, ecc, EciMode.Default, false, out var version)).IsTrue();
        await Assert.That(version).IsEqualTo(expected);
    }

    [Test]
    public async Task VersionSelection_OneKanjiPastVersion40_DoesNotFit()
    {
        await Assert.That(QRCodeGenerator.TryGetVersion(1818, EncodingMode.Kanji, QREccLevel.L, EciMode.Default, false, out _)).IsFalse();
    }

    /// <summary>The planner's exact cost of a Kanji run: 4 + the count width + 13 a character.</summary>
    [Test]
    [Arguments(9, 8)]
    [Arguments(10, 10)]
    [Arguments(27, 12)]
    public async Task MeasurePlan_PricesAKanjiRunAt13BitsACharacter(int version, int countBits)
    {
        ModeSegment[] plan = [new ModeSegment(3, 0, 7, 7)];
        await Assert.That(plan[0].Mode).IsEqualTo(EncodingMode.Kanji);
        await Assert.That(QRSegmentPlanner.MeasurePlan(version, plan)).IsEqualTo(4 + countBits + 7 * 13);
    }

    // ---- round trip ----------------------------------------------------------------

    public static IEnumerable<(int Version, QREccLevel Ecc)> RoundTripSymbols()
    {
        foreach (var version in new[] { 1, 2, 9, 10, 26, 27, 40 })
            foreach (var ecc in Levels)
                yield return (version, ecc);
    }

    /// <summary>A Kanji symbol at capacity, and a mixed one, decode back to their text.</summary>
    [Test]
    [MethodDataSource(nameof(RoundTripSymbols))]
    public async Task RoundTrip_ThroughTheMatrixDecoder(int version, QREccLevel ecc)
    {
        var text = Cells(version * 13 + (int)ecc, PublishedKanjiCapacity(version, ecc));
        await Assert.That(Decode(BuildSymbol(EncodeSingle(text, version, ecc), version, ecc), version)).IsEqualTo(text);

        Run[] runs = [new Run('A', "QR"), new Run('K', Cells(version, 3)), new Run('N', "2026"), new Run('K', Cells(version + 3, 2)), new Run('B', "ok")];
        if (StandardQrBits(version, runs).Length <= DataCodewords(version, ecc) * 8)
        {
            var mixed = string.Concat(runs.Select(r => r.Text));
            await Assert.That(Decode(BuildSymbol(EncodeSegmented(mixed, version, ecc, runs), version, ecc), version)).IsEqualTo(mixed);
        }
    }

    private static byte[] BuildSymbol(byte[] data, int version, QREccLevel ecc) => KanjiSymbolBuilder.StandardQr(data, version, ecc, mask: 0);

    private static string Decode(byte[] modules, int version) => KanjiSymbolBuilder.DecodeStandardQr(modules, version);
}
