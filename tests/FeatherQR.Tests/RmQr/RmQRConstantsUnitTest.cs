using FeatherQR.Internals;
using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// Structural invariants of the rMQR symbol tables (ISO/IEC 23941), pinned so a
/// transcription error in one table is caught by its disagreement with the others:
/// geometry ↔ codeword counts ↔ ECC bounds ↔ published capacities ↔ format words.
/// The oracle-symbol checks live in <see cref="RmQRConstantsOracleTest"/>.
/// </summary>
public class RmQRConstantsUnitTest
{
    public static IEnumerable<RmQRVersion> AllVersions() => Enum.GetValues<RmQRVersion>();

    public static IEnumerable<(RmQRVersion version, RmQREccLevel ecc)> AllVersionEcc()
    {
        foreach (var v in AllVersions())
        {
            yield return (v, RmQREccLevel.M);
            yield return (v, RmQREccLevel.H);
        }
    }

    // Published data capacities (characters), Numeric / Alphanumeric / Byte, per
    // version at M then H (Denso Wave capacity table; also measured against qrtool
    // 192/192, see specs/rmqr-encoder.md). Index = (int)version - 1.
    private static readonly (int NumM, int AlnM, int ByteM, int NumH, int AlnH, int ByteH)[] publishedCapacities =
    [
        (12, 7, 5, 5, 3, 2), (26, 16, 11, 14, 8, 6), (45, 27, 19, 21, 13, 9), (64, 39, 27, 30, 18, 13), (102, 62, 42, 54, 33, 22),
        (26, 16, 11, 14, 8, 6), (47, 29, 20, 23, 14, 10), (71, 43, 30, 37, 23, 16), (97, 59, 40, 49, 30, 20), (147, 89, 61, 75, 46, 31),
        (14, 8, 6, 9, 6, 4), (42, 26, 18, 23, 14, 10), (71, 43, 30, 33, 20, 14), (100, 60, 41, 52, 31, 21), (133, 81, 55, 66, 40, 27), (198, 120, 82, 97, 59, 40),
        (26, 16, 11, 14, 8, 6), (62, 37, 26, 28, 17, 12), (88, 53, 36, 45, 27, 18), (124, 75, 51, 66, 40, 27), (171, 104, 71, 80, 49, 33), (251, 152, 104, 126, 76, 52),
        (76, 46, 31, 33, 20, 13), (112, 68, 46, 59, 36, 24), (157, 95, 65, 71, 43, 29), (207, 126, 86, 111, 68, 46), (301, 182, 125, 162, 98, 67),
        (90, 55, 37, 47, 28, 19), (131, 79, 54, 63, 38, 26), (183, 111, 76, 87, 53, 36), (236, 143, 98, 131, 79, 54), (361, 219, 150, 178, 108, 74),
    ];

    [Test]
    public async Task VersionTable_Has32Entries_InIsoHeightMajorOrder()
    {
        var versions = AllVersions().ToArray();
        await Assert.That(versions.Length).IsEqualTo(32);
        var versionCount = RmQRConstants.VersionCount;
        await Assert.That(versionCount).IsEqualTo(32);

        for (var i = 0; i < 32; i++)
        {
            var (height, width) = RmQRNaiveReference.Versions[i];
            await Assert.That((int)versions[i]).IsEqualTo(i + 1);
            await Assert.That(RmQRConstants.GetHeight(versions[i])).IsEqualTo(height);
            await Assert.That(RmQRConstants.GetWidth(versions[i])).IsEqualTo(width);
            await Assert.That(versions[i].ToString()).IsEqualTo($"R{height}x{width}");
        }
    }

    [Test]
    [MethodDataSource(nameof(AllVersions))]
    public async Task TryGetVersion_IsInverseOfDimensions(RmQRVersion version)
    {
        var found = RmQRConstants.TryGetVersion(RmQRConstants.GetHeight(version), RmQRConstants.GetWidth(version), out var back);
        await Assert.That(found).IsTrue();
        await Assert.That(back).IsEqualTo(version);
    }

    [Test]
    [Arguments(7, 27)]   // width 27 exists only for heights 11 and 13
    [Arguments(9, 27)]
    [Arguments(15, 27)]
    [Arguments(17, 27)]
    [Arguments(8, 43)]
    [Arguments(11, 28)]
    [Arguments(43, 7)]   // transposed
    [Arguments(0, 0)]
    [Arguments(21, 21)]  // Standard QR v1
    [Arguments(11, 11)]  // Micro QR M1
    public async Task TryGetVersion_RejectsNonRmqrDimensions(int height, int width)
    {
        await Assert.That(RmQRConstants.TryGetVersion(height, width, out _)).IsFalse();
    }

    [Test]
    [MethodDataSource(nameof(AllVersions))]
    public async Task AlignmentColumns_MatchWidthTable_AndAreInsideTheSymbol(RmQRVersion version)
    {
        var width = RmQRConstants.GetWidth(version);
        var columns = RmQRConstants.GetAlignmentColumns(version).ToArray();
        await Assert.That(columns.Select(c => (int)c).ToArray()).IsEquivalentTo(RmQRNaiveReference.AlignmentColumns(width));
        foreach (var c in columns)
        {
            // A 3×3 alignment pattern around the column must not touch the finder
            // separator (col 7 / format cols 8-11) or the sub-finder side format region.
            await Assert.That((int)c).IsGreaterThan(12);
            await Assert.That((int)c).IsLessThan(width - 9);
        }
    }

    [Test]
    [MethodDataSource(nameof(AllVersionEcc))]
    public async Task CodewordTables_AreInternallyConsistent(RmQRVersion version, RmQREccLevel ecc)
    {
        var total = RmQRConstants.GetTotalCodewordCount(version);
        var data = RmQRConstants.GetDataCodewordCount(version, ecc);
        var info = RmQRConstants.GetEccInfo(version, ecc);

        await Assert.That(info.TotalDataCodewords).IsEqualTo(data);
        await Assert.That(data).IsGreaterThan(0);

        var blocks = info.BlocksInGroup1 + info.BlocksInGroup2;
        await Assert.That(blocks).IsGreaterThan(0);
        await Assert.That(info.BlocksInGroup1 * info.CodewordsInGroup1 + info.BlocksInGroup2 * info.CodewordsInGroup2).IsEqualTo(data);
        if (info.BlocksInGroup2 > 0)
        {
            await Assert.That(info.CodewordsInGroup2).IsEqualTo(info.CodewordsInGroup1 + 1);
        }

        // data + ecc == total, ecc per block within the RS generator range
        // the library builds (7..30).
        await Assert.That(data + blocks * info.ECCPerBlock).IsEqualTo(total);
        await Assert.That(info.ECCPerBlock).IsGreaterThanOrEqualTo(7);
        await Assert.That(info.ECCPerBlock).IsLessThanOrEqualTo(30);

        // H must not have more data than M for the same version.
        await Assert.That(RmQRConstants.GetDataCodewordCount(version, RmQREccLevel.H)).IsLessThan(RmQRConstants.GetDataCodewordCount(version, RmQREccLevel.M));
    }

    [Test]
    [MethodDataSource(nameof(AllVersionEcc))]
    public async Task ErrorCorrectionCapacity_IsFullReedSolomonStrength(RmQRVersion version, RmQREccLevel ecc)
    {
        // The rMQR correction capacity is held as a hand-written table so a future
        // reading of ISO/IEC 23941 Table 8 showing a reserved misdecode-protection
        // count p is a data edit (see RmQRConstants.errorCorrectionCapacities). This
        // pins every one of the 64 rows against the block structure, which is itself
        // oracle-verified: as long as no p is reserved, t must be exactly ⌊ecc/2⌋, so
        // a typo in the table or a divergence from GetEccInfo fails here.
        var info = RmQRConstants.GetEccInfo(version, ecc);
        var capacity = RmQRConstants.GetErrorCorrectionCapacity(version, ecc);

        await Assert.That(capacity).IsEqualTo(info.ECCPerBlock / 2);
        await Assert.That(capacity).IsGreaterThan(0);
    }

    [Test]
    public async Task ErrorCorrectionCapacity_HasNoMicroQrStyleProtectionCodewords()
    {
        // ISO/IEC 18004 Micro QR reserves p codewords (2t + p = ecc) so the capacity is
        // below the RS strength; rMQR reserves none. 60 of the 64 rows have an even ECC
        // count per block, where any p >= 1 would visibly lower t — those are the rows
        // that make the claim falsifiable. The 4 odd-ECC rows cannot distinguish p = 1
        // from p = 0, so the 60/4 split is pinned below to keep the limit of this check visible.
        var falsifiable = 0;
        foreach (var (version, ecc) in AllVersionEcc())
        {
            var info = RmQRConstants.GetEccInfo(version, ecc);
            var slack = info.ECCPerBlock - 2 * RmQRConstants.GetErrorCorrectionCapacity(version, ecc);

            await Assert.That(slack).IsLessThan(2).Because($"{version}-{ecc}: {slack} ECC codewords per block are unused, which is a reserved p");
            if (info.ECCPerBlock % 2 == 0)
                falsifiable++;
        }

        await Assert.That(falsifiable).IsEqualTo(60);
    }

    [Test]
    [MethodDataSource(nameof(AllVersions))]
    public async Task TotalCodewords_MatchFreeModuleCount_FromIndependentPainter(RmQRVersion version)
    {
        // Geometry ↔ codeword table cross-check: 8 × total codewords must fit the
        // non-function modules with at most 7 remainder bits. This is the check that
        // caught the R17x59 transcription error during pre-verification.
        var height = RmQRConstants.GetHeight(version);
        var width = RmQRConstants.GetWidth(version);
        var free = RmQRNaiveReference.FunctionModuleMap(height, width).Count(f => !f);
        var remainder = free - 8 * RmQRConstants.GetTotalCodewordCount(version);

        await Assert.That(remainder).IsGreaterThanOrEqualTo(0);
        await Assert.That(remainder).IsLessThanOrEqualTo(7);
        await Assert.That(RmQRConstants.GetRemainderBitCount(version)).IsEqualTo(remainder);
    }

    [Test]
    [MethodDataSource(nameof(AllVersionEcc))]
    public async Task DataCodewordsAndCountWidths_ReproducePublishedCapacities(RmQRVersion version, RmQREccLevel ecc)
    {
        var bits = 8 * RmQRConstants.GetDataCodewordCount(version, ecc);
        var (numM, alnM, byteM, numH, alnH, byteH) = publishedCapacities[(int)version - 1];
        var (expectedNum, expectedAln, expectedByte) = ecc == RmQREccLevel.M ? (numM, alnM, byteM) : (numH, alnH, byteH);

        static int NumericBits(int n) => n / 3 * 10 + (n % 3) switch { 0 => 0, 1 => 4, _ => 7 };
        static int AlphanumericBits(int n) => n / 2 * 11 + (n % 2) * 6;

        var numeric = 0;
        while (RmQRConstants.ModeIndicatorLength + RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric) + NumericBits(numeric + 1) <= bits)
            numeric++;
        var alphanumeric = 0;
        while (RmQRConstants.ModeIndicatorLength + RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric) + AlphanumericBits(alphanumeric + 1) <= bits)
            alphanumeric++;
        var bytes = (bits - RmQRConstants.ModeIndicatorLength - RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte)) / 8;

        await Assert.That(numeric).IsEqualTo(expectedNum);
        await Assert.That(alphanumeric).IsEqualTo(expectedAln);
        await Assert.That(bytes).IsEqualTo(expectedByte);
    }

    [Test]
    [MethodDataSource(nameof(AllVersions))]
    public async Task CountIndicatorWidths_FitTheirCapacities(RmQRVersion version)
    {
        // A count field must be able to express the maximum count of its mode at M
        // (the larger capacity), and widths are monotone: Numeric ≥ Alphanumeric ≥ Byte.
        var (numM, alnM, byteM, _, _, _) = publishedCapacities[(int)version - 1];
        var n = RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric);
        var a = RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric);
        var b = RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte);
        var k = RmQRConstants.GetKanjiCountIndicatorLength(version);

        await Assert.That(1 << n).IsGreaterThan(numM);
        await Assert.That(1 << a).IsGreaterThan(alnM);
        await Assert.That(1 << b).IsGreaterThan(byteM);
        await Assert.That(n).IsGreaterThanOrEqualTo(a);
        await Assert.That(a).IsGreaterThanOrEqualTo(b);
        await Assert.That(b).IsGreaterThanOrEqualTo(k);
        await Assert.That(k).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    [MethodDataSource(nameof(AllVersions))]
    public async Task CountIndicatorWidths_AreTheNarrowestThatHoldTheirMaximumCount(RmQRVersion version)
    {
        // ISO/IEC 23941 Table 3 takes, per version and mode, the narrowest count field
        // that can still express the largest count the version's M-level data capacity
        // allows. Derived from the data codeword count alone, that rule reproduces all
        // 96 oracle-verified Numeric / Alphanumeric / Byte widths — which is what makes
        // it usable on the Kanji column, which had no oracle when it was written (qrtool
        // can emit Kanji, found later; its four fixtures now read widths 4, 5 and 7, so
        // 2, 3 and 6 still rest on this derivation alone). It caught R17x99 Kanji
        // transcribed as 7 where the rule gives 6.
        var dataBits = RmQRConstants.GetDataCodewordCount(version, RmQREccLevel.M) * 8;

        await Assert.That(RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric)).IsEqualTo(NarrowestCountWidth(dataBits, NumericBits));
        await Assert.That(RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric)).IsEqualTo(NarrowestCountWidth(dataBits, AlphanumericBits));
        await Assert.That(RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte)).IsEqualTo(NarrowestCountWidth(dataBits, ByteBits));
        await Assert.That(RmQRConstants.GetKanjiCountIndicatorLength(version)).IsEqualTo(NarrowestCountWidth(dataBits, KanjiBits));
    }

    /// <summary>
    /// The narrowest count field that still holds every count the segment can reach:
    /// a segment costs 3 mode bits + the count field + the mode's payload bits, so the
    /// largest count is capped by <paramref name="dataBits"/>, and the field must be
    /// able to express it.
    /// </summary>
    private static int NarrowestCountWidth(int dataBits, Func<int, int> payloadBits)
    {
        for (var width = 1; width <= 12; width++)
        {
            var maxCount = 0;
            while (3 + width + payloadBits(maxCount + 1) <= dataBits)
                maxCount++;

            if (maxCount < 1 << width)
                return width;
        }

        throw new InvalidOperationException($"no count width up to 12 bits holds the counts {dataBits} data bits allow");
    }

    // ISO/IEC 23941 Section 7.4: numeric packs 3 digits into 10 bits (a 1- or 2-digit
    // tail costs 4 or 7), alphanumeric 2 characters into 11 (a 1-character tail 6),
    // byte 8 bits each, Kanji 13 bits each.
    private static int NumericBits(int count) => 10 * (count / 3) + (count % 3) switch { 1 => 4, 2 => 7, _ => 0 };
    private static int AlphanumericBits(int count) => 11 * (count / 2) + (count % 2 == 1 ? 6 : 0);
    private static int ByteBits(int count) => 8 * count;
    private static int KanjiBits(int count) => 13 * count;

    [Test]
    public async Task ModeIndicators_AreThreeBits_WithIsoValues()
    {
        int modeIndicatorLength = RmQRConstants.ModeIndicatorLength, terminatorLength = RmQRConstants.TerminatorLength, kanjiIndicator = RmQRConstants.KanjiModeIndicatorValue;
        await Assert.That(modeIndicatorLength).IsEqualTo(3);
        await Assert.That(terminatorLength).IsEqualTo(3);
        await Assert.That(RmQRConstants.GetModeIndicatorValue(EncodingMode.Numeric)).IsEqualTo(0b001);
        await Assert.That(RmQRConstants.GetModeIndicatorValue(EncodingMode.Alphanumeric)).IsEqualTo(0b010);
        await Assert.That(RmQRConstants.GetModeIndicatorValue(EncodingMode.Byte)).IsEqualTo(0b011);
        await Assert.That(RmQRConstants.GetModeIndicatorValue(EncodingMode.ECI)).IsEqualTo(0b111);
        await Assert.That(kanjiIndicator).IsEqualTo(0b100);
    }

    [Test]
    [MethodDataSource(nameof(AllVersionEcc))]
    public async Task FormatBits_MatchNaiveBch18_WithPerCopyXorMasks(RmQRVersion version, RmQREccLevel ecc)
    {
        var data = ((int)ecc << 5) | ((int)version - 1);
        var expected = RmQRNaiveReference.Bch18(data);

        await Assert.That(RmQRConstants.GetFormatBits(version, ecc, subFinderSide: false)).IsEqualTo(expected ^ RmQRNaiveReference.FormatXorFinderSide);
        await Assert.That(RmQRConstants.GetFormatBits(version, ecc, subFinderSide: true)).IsEqualTo(expected ^ RmQRNaiveReference.FormatXorSubFinderSide);
        await Assert.That(RmQRConstants.GetFormatBits(version, ecc, subFinderSide: false) >> 18).IsEqualTo(0);
        await Assert.That(RmQRConstants.GetFormatBits(version, ecc, subFinderSide: true) >> 18).IsEqualTo(0);
    }

    [Test]
    public async Task FormatWords_ArePairwiseDistant_ForBothCopies()
    {
        // BCH(18,6) minimum distance is what a Phase 6 decoder's ≤ 3-bit correction
        // relies on; the XOR mask does not change pairwise distances, so both copies
        // share it. Also, no finder-side word may equal a sub-finder-side word (the
        // two masks differ by 0x3F1C9, weight 11), so a copy cannot be mistaken for the other.
        var words = new List<(int Left, int Right)>();
        foreach (var (version, ecc) in AllVersionEcc())
            words.Add((RmQRConstants.GetFormatBits(version, ecc, false), RmQRConstants.GetFormatBits(version, ecc, true)));

        var minDistance = int.MaxValue;
        for (var i = 0; i < words.Count; i++)
        {
            for (var j = i + 1; j < words.Count; j++)
            {
                minDistance = Math.Min(minDistance, System.Numerics.BitOperations.PopCount((uint)(words[i].Left ^ words[j].Left)));
                minDistance = Math.Min(minDistance, System.Numerics.BitOperations.PopCount((uint)(words[i].Right ^ words[j].Right)));
            }
        }
        await Assert.That(minDistance).IsGreaterThanOrEqualTo(7);
        await Assert.That(words.Select(w => w.Left).Distinct().Count()).IsEqualTo(64);
        await Assert.That(words.Select(w => w.Right).Distinct().Count()).IsEqualTo(64);
        await Assert.That(words.Select(w => w.Left).Intersect(words.Select(w => w.Right)).Any()).IsFalse();
    }

    [Test]
    public async Task Constants_SymbolTypeAndQuietZone()
    {
        byte symbolType = RmQRConstants.SymbolTypeRmQR;
        int quietZone = RmQRConstants.QuietZoneModules;
        await Assert.That(symbolType).IsEqualTo((byte)2);
        await Assert.That(quietZone).IsEqualTo(2);
    }
}
