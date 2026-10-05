using TUnit.Assertions.Enums;
using FeatherQR.Internals;
using FeatherQR.Internals.MicroQR;
using static FeatherQR.Tests.KanjiStreamReference;

namespace FeatherQR.Tests;

/// <summary>
/// Micro QR Kanji mode's writers, below the public API: M3 and M4 only, 13 bits a character behind a 3-bit (M3) or 4-bit (M4) count.
/// The writers are checked against a stream written from ISO/IEC 18004 (<see cref="KanjiStreamReference"/>), the capacity against the standard's Table 7, and the symbols through the matrix decoder.
/// </summary>
public class MicroQRBinaryEncoderKanjiTest
{
    /// <summary>ISO/IEC 18004 Table 7, Kanji column: M1 and M2 have none.</summary>
    public static IEnumerable<(MicroQRVersion Version, MicroQREccLevel Ecc, int Capacity)> KanjiSymbols() =>
    [
        (MicroQRVersion.M3, MicroQREccLevel.L, 6),
        (MicroQRVersion.M3, MicroQREccLevel.M, 4),
        (MicroQRVersion.M4, MicroQREccLevel.L, 9),
        (MicroQRVersion.M4, MicroQREccLevel.M, 8),
        (MicroQRVersion.M4, MicroQREccLevel.Q, 5),
    ];

    private static byte[] Reference(MicroQRVersion version, MicroQREccLevel ecc, Run[] runs)
        => MicroQrStream((int)version, MicroQRConstants.GetDataBitCapacity(version, ecc), MicroQRConstants.GetDataCodewordCount(version, ecc), runs);

    private static byte[] EncodeSingle(string text, MicroQRVersion version, MicroQREccLevel ecc)
    {
        var destination = new byte[16];
        var count = MicroQRBinaryEncoder.EncodeDataCodewords(text, version, ecc, EncodingMode.Kanji, destination);
        return destination.AsSpan(0, count).ToArray();
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

    private static byte[] EncodeSegmented(MicroQRVersion version, MicroQREccLevel ecc, Run[] runs)
    {
        var destination = new byte[16];
        var count = MicroQRBinaryEncoder.EncodeDataCodewordsSegmented(string.Concat(runs.Select(r => r.Text)), version, ecc, EciMode.Default, Segments(runs), destination);
        return destination.AsSpan(0, count).ToArray();
    }

    // ---- the writers against the reference -----------------------------------------

    /// <summary>Every Kanji length at every Kanji-capable version and level: both count widths, and the M3 half codeword under a full symbol.</summary>
    [Test]
    [MethodDataSource(nameof(KanjiSymbols))]
    public async Task SingleSegment_EveryLength_MatchesTheReference(MicroQRVersion version, MicroQREccLevel ecc, int capacity)
    {
        for (var length = 1; length <= capacity; length++)
        {
            var text = Cells((int)version * 31 + (int)ecc * 7 + length, length);
            await Assert.That(EncodeSingle(text, version, ecc)).IsEquivalentTo(Reference(version, ecc, [new Run('K', text)]), CollectionOrdering.Matching)
                .Because($"{length} Kanji");
        }
    }

    /// <summary>Every encoder cell once, nine to an M4-L symbol.</summary>
    [Test]
    public async Task SingleSegment_EveryEncoderCell_MatchesTheReference()
    {
        for (var start = 0; start < EncoderCells.Length; start += 9)
        {
            var text = Cells(start, Math.Min(9, EncoderCells.Length - start));
            var actual = EncodeSingle(text, MicroQRVersion.M4, MicroQREccLevel.L);
            var expected = Reference(MicroQRVersion.M4, MicroQREccLevel.L, [new Run('K', text)]);
            if (!actual.AsSpan().SequenceEqual(expected))
                await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching).Because($"cells {start}..{start + text.Length - 1}");
        }
    }

    /// <summary>
    /// A Kanji character behind prefixes of every length that fits, followed by a digit: its 13 bits start at every residue modulo 8, and in particular at every bit from which they straddle the accumulator's 64-bit word boundary.
    /// </summary>
    [Test]
    [Arguments(MicroQRVersion.M3, MicroQREccLevel.L)]
    [Arguments(MicroQRVersion.M4, MicroQREccLevel.L)]
    public async Task Segmented_KanjiAtEveryStartingBit_MatchesTheReference(MicroQRVersion version, MicroQREccLevel ecc)
    {
        var capacity = MicroQRConstants.GetDataBitCapacity(version, ecc);
        var kanji = Cells((int)version * 3, 1);
        var starts = new HashSet<int>();
        foreach (var prefix in QRBinaryEncoderKanjiTest.Prefixes())
        {
            if (prefix.Length == 0 || prefix.Any(r => !MicroQRConstants.IsModeSupported(version, r.Mode switch { 'N' => EncodingMode.Numeric, 'A' => EncodingMode.Alphanumeric, _ => EncodingMode.Byte })))
                continue;

            var start = MicroQrBits((int)version, prefix).Length + ((int)version - 1) + (int)version;
            Run[] runs = [.. prefix, new Run('K', kanji), new Run('N', "7")];
            if (MicroQrBits((int)version, runs).Length > capacity || !starts.Add(start))
                continue;

            await Assert.That(EncodeSegmented(version, ecc, runs)).IsEquivalentTo(Reference(version, ecc, runs), CollectionOrdering.Matching)
                .Because($"Kanji value starting at bit {start}");
        }

        for (var residue = 0; residue < 8; residue++)
            await Assert.That(starts.Any(s => s % 8 == residue)).IsTrue().Because($"a Kanji value starting at bit {residue} mod 8");
        if (version == MicroQRVersion.M4)
        {
            for (var start = 52; start <= 64; start++)
                await Assert.That(starts).Contains(start).Because("the 13 bits straddle, or begin at, the second 64-bit word");
        }
    }

    [Test]
    [MethodDataSource(nameof(KanjiSymbols))]
    public async Task Segmented_OneKanjiRun_MatchesTheSingleSegmentWriter(MicroQRVersion version, MicroQREccLevel ecc, int capacity)
    {
        var text = Cells(capacity, capacity);
        await Assert.That(EncodeSegmented(version, ecc, [new Run('K', text)])).IsEquivalentTo(EncodeSingle(text, version, ecc), CollectionOrdering.Matching);
    }

    // ---- mode availability and refusals ---------------------------------------------

    /// <summary>Kanji exists from M3 (ISO/IEC 18004 Table 2): indicator 11 on M3, 011 on M4, count 3 and 4 bits.</summary>
    [Test]
    [Arguments(MicroQRVersion.M1, false)]
    [Arguments(MicroQRVersion.M2, false)]
    [Arguments(MicroQRVersion.M3, true)]
    [Arguments(MicroQRVersion.M4, true)]
    public async Task ModeAvailability_StartsAtM3(MicroQRVersion version, bool supported)
    {
        await Assert.That(MicroQRConstants.IsModeSupported(version, EncodingMode.Kanji)).IsEqualTo(supported);
        if (supported)
        {
            await Assert.That(MicroQRConstants.GetModeIndicatorValue(EncodingMode.Kanji)).IsEqualTo(3);
            await Assert.That(MicroQRConstants.GetCountIndicatorLength(version, EncodingMode.Kanji)).IsEqualTo((int)version);
        }
    }

    [Test]
    [Arguments(MicroQRVersion.M2, MicroQREccLevel.L)]
    [Arguments(MicroQRVersion.M2, MicroQREccLevel.M)]
    public async Task Segmented_KanjiRunOnAVersionWithoutKanji_Throws(MicroQRVersion version, MicroQREccLevel ecc)
    {
        await Assert.That(() => EncodeSegmented(version, ecc, [new Run('N', "1"), new Run('K', Cells(0, 1))])).Throws<ArgumentException>();
    }

    [Test]
    [Arguments("日A")]   // ASCII
    [Arguments("〜")]    // JIS X 0208 reading of a divergent cell
    [Arguments("①")]    // NEC row 13
    [Arguments("ｶ")]    // halfwidth katakana
    public async Task SingleSegment_CharacterWithoutACell_Throws(string text)
    {
        await Assert.That(() => EncodeSingle(text, MicroQRVersion.M4, MicroQREccLevel.L)).Throws<ArgumentException>();
    }

    [Test]
    public async Task Segmented_CharacterWithoutACell_Throws()
    {
        await Assert.That(() => EncodeSegmented(MicroQRVersion.M4, MicroQREccLevel.L, [new Run('N', "1"), new Run('K', "～")])).Throws<ArgumentException>();
    }

    // ---- capacity and version selection -------------------------------------------

    private static TextAnalysisResult Kanji(int count) => new(EncodingMode.Kanji, EciMode.Default, count);

    [Test]
    [MethodDataSource(nameof(KanjiSymbols))]
    public async Task Capacity_MatchesTheStandardsKanjiColumn(MicroQRVersion version, MicroQREccLevel ecc, int capacity)
    {
        await Assert.That(MicroQRCodeGenerator.TrySelectVersionInRange(Kanji(capacity), ecc, MicroQRVersionRange.Exactly(version), out _)).IsTrue();
        await Assert.That(MicroQRCodeGenerator.TrySelectVersionInRange(Kanji(capacity + 1), ecc, MicroQRVersionRange.Exactly(version), out _)).IsFalse();
    }

    [Test]
    [Arguments(1, MicroQREccLevel.L, MicroQRVersion.M3)]   // M2 holds alphanumerics but no Kanji
    [Arguments(6, MicroQREccLevel.L, MicroQRVersion.M3)]
    [Arguments(7, MicroQREccLevel.L, MicroQRVersion.M4)]
    [Arguments(4, MicroQREccLevel.M, MicroQRVersion.M3)]
    [Arguments(5, MicroQREccLevel.M, MicroQRVersion.M4)]
    [Arguments(5, MicroQREccLevel.Q, MicroQRVersion.M4)]   // Q exists on M4 only
    public async Task VersionSelection_PicksTheSmallestKanjiVersion(int count, MicroQREccLevel ecc, MicroQRVersion expected)
    {
        await Assert.That(MicroQRCodeGenerator.TrySelectVersion(Kanji(count), ecc, out var version)).IsTrue();
        await Assert.That(version).IsEqualTo(expected);
    }

    [Test]
    [Arguments(10, MicroQREccLevel.L)]
    [Arguments(9, MicroQREccLevel.M)]
    [Arguments(6, MicroQREccLevel.Q)]
    public async Task VersionSelection_PastM4_DoesNotFit(int count, MicroQREccLevel ecc)
    {
        await Assert.That(MicroQRCodeGenerator.TrySelectVersion(Kanji(count), ecc, out _)).IsFalse();
    }

    /// <summary>A range limited to M1-M2 holds no Kanji at all: an ordinary "does not fit", not an argument error.</summary>
    [Test]
    public async Task VersionSelection_RangeBelowM3_DoesNotFit()
    {
        await Assert.That(MicroQRCodeGenerator.TrySelectVersionInRange(Kanji(1), MicroQREccLevel.L, MicroQRVersionRange.AtMost(MicroQRVersion.M2), out _)).IsFalse();
        await Assert.That(MicroQRCodeGenerator.TrySelectVersionInRange(Kanji(1), MicroQREccLevel.L, MicroQRVersionRange.Exactly(MicroQRVersion.M2), out _)).IsFalse();
    }

    [Test]
    [Arguments(MicroQRVersion.M3)]
    [Arguments(MicroQRVersion.M4)]
    public async Task MeasurePlan_PricesAKanjiRunAt13BitsACharacter(MicroQRVersion version)
    {
        ModeSegment[] plan = [new ModeSegment(0, 0, 2, 2), new ModeSegment(3, 2, 3, 3)];
        var header = (int)version - 1;
        await Assert.That(MicroQRSegmentPlanner.MeasurePlan(version, plan)).IsEqualTo(header + (int)version + 2 + 7 + header + (int)version + 3 * 13);
    }

    // ---- round trip ----------------------------------------------------------------

    [Test]
    [MethodDataSource(nameof(KanjiSymbols))]
    public async Task RoundTrip_ThroughTheMatrixDecoder(MicroQRVersion version, MicroQREccLevel ecc, int capacity)
    {
        for (var length = 1; length <= capacity; length++)
        {
            var text = Cells(length * 17 + (int)ecc, length);
            await Assert.That(Decode(EncodeSingle(text, version, ecc), version, ecc)).IsEqualTo(text);
        }

        Run[] runs = [new Run('N', "12"), new Run('K', Cells(3, 2))];
        await Assert.That(Decode(EncodeSegmented(version, ecc, runs), version, ecc)).IsEqualTo(string.Concat(runs.Select(r => r.Text)));
    }

    private static string Decode(byte[] data, MicroQRVersion version, MicroQREccLevel ecc)
        => KanjiSymbolBuilder.DecodeMicroQr(KanjiSymbolBuilder.MicroQr(data, version, ecc, mask: -1), version);
}
