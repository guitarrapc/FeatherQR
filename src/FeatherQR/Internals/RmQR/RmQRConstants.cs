using System.Runtime.CompilerServices;

namespace FeatherQR.Internals.RmQr;

/// <summary>
/// rMQR Code symbol tables and format information (ISO/IEC 23941).
/// </summary>
/// <remarks>
/// All per-version tables are indexed by the ISO version index
/// <c>(int)version - 1</c> (0-31, height-major: all widths of height 7, then 9,
/// 11, 13, 15, 17); per-version × ECC tables by <c>index * 2 + (int)eccLevel</c>
/// (M = 0, H = 1). Every value was verified against external oracle symbols before
/// implementation (see specs/rmqr-encoder.md, "Verification record"); the
/// structural and oracle tests in tests/RmQr pin them permanently.
/// </remarks>
internal static class RmQRConstants
{
    /// <summary>Symbol type identifier in the QRX serialization header (Micro QR is 1).</summary>
    public const byte SymbolTypeRmQR = 2;

    /// <summary>ISO/IEC 23941 quiet zone: 2 modules on every side.</summary>
    public const int QuietZoneModules = 2;

    public const int VersionCount = 32;

    /// <summary>Every mode indicator is 3 bits (ISO/IEC 23941 Table 2).</summary>
    public const int ModeIndicatorLength = 3;

    /// <summary>Terminator is 000 (3 bits), shortened at capacity.</summary>
    public const int TerminatorLength = 3;

    /// <summary>Kanji mode indicator value; decoded but never encoded (symbology scope decision).</summary>
    public const int KanjiModeIndicatorValue = 0b100;

    // XOR masks applied to the 18-bit BCH word of each format-information copy.
    public const int FormatXorFinderSide = 0x1FAB2;
    public const int FormatXorSubFinderSide = 0x20A7B;

    // Symbol height and width in modules per version index.
    private static ReadOnlySpan<byte> heights =>
    [
        7, 7, 7, 7, 7,
        9, 9, 9, 9, 9,
        11, 11, 11, 11, 11, 11,
        13, 13, 13, 13, 13, 13,
        15, 15, 15, 15, 15,
        17, 17, 17, 17, 17,
    ];

    private static ReadOnlySpan<byte> widths =>
    [
        43, 59, 77, 99, 139,
        43, 59, 77, 99, 139,
        27, 43, 59, 77, 99, 139,
        27, 43, 59, 77, 99, 139,
        43, 59, 77, 99, 139,
        43, 59, 77, 99, 139,
    ];

    // Total codewords (data + ECC) per version index. Free-module count is
    // 8 × total + remainder (0..7), see GetRemainderBitCount.
    private static ReadOnlySpan<byte> totalCodewords =>
    [
        13, 21, 32, 44, 68,
        21, 33, 49, 66, 99,
        15, 31, 47, 67, 89, 132,
        21, 41, 60, 85, 113, 166,
        51, 74, 103, 136, 199,
        61, 88, 122, 160, 232,
    ];

    // Remainder bits (free modules − 8 × total codewords) per version index; always light.
    private static ReadOnlySpan<byte> remainderBits =>
    [
        0, 3, 5, 6, 1,
        2, 3, 1, 4, 5,
        2, 1, 0, 2, 7, 6,
        4, 1, 6, 4, 3, 0,
        1, 4, 6, 7, 2,
        1, 2, 0, 3, 4,
    ];

    // Data codewords per version × ECC (index * 2 + ecc; M, H).
    private static ReadOnlySpan<byte> dataCodewords =>
    [
        6, 3, 12, 7, 20, 10, 28, 14, 44, 24,
        12, 7, 21, 11, 31, 17, 42, 22, 63, 33,
        7, 5, 19, 11, 31, 15, 43, 23, 57, 29, 84, 42,
        12, 7, 27, 13, 38, 20, 53, 29, 73, 35, 106, 54,
        33, 15, 48, 26, 67, 31, 88, 48, 127, 69,
        39, 21, 56, 28, 78, 38, 100, 56, 152, 76,
    ];

    // Reed-Solomon block count per version × ECC (M, H). Every block of a symbol
    // carries the same ECC codeword count ((total − data) / blocks); data codewords
    // split into blocks whose sizes differ by at most one, smaller blocks first.
    private static ReadOnlySpan<byte> blockCounts =>
    [
        1, 1, 1, 1, 1, 1, 1, 1, 1, 2,
        1, 1, 1, 1, 1, 2, 1, 2, 2, 3,
        1, 1, 1, 1, 1, 2, 1, 2, 2, 2, 2, 3,
        1, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 4,
        1, 2, 1, 2, 2, 3, 2, 4, 3, 5,
        1, 2, 2, 2, 2, 3, 3, 4, 4, 6,
    ];

    // Error correction capacity t in codewords per RS block, per version × ECC (M, H):
    // the maximum a decoder may correct in one block. Every entry is the full
    // Reed-Solomon strength ⌊ECC codewords per block / 2⌋ — unlike ISO/IEC 18004
    // Micro QR, no misdecode-protection codewords p are reserved (there 2t + p = ecc,
    // so a decoder wired to full RS strength over-corrects).
    //
    // Provenance: derived from the block-structure tables above ((total − data) /
    // blocks / 2), which are oracle-verified (specs/rmqr-encoder.md, "Verification
    // record"), and consistent with zxing-cpp — the only maintained OSS rMQR decode
    // lineage — which corrects rMQR at full Reed-Solomon strength. The ISO/IEC 23941
    // Table 8 capacity column itself has NOT been read: the standard is paywalled and
    // the public preview stops short of Table 8. Held as data rather than as the
    // ⌊ecc/2⌋ expression so that a future reading of Table 8 showing a reserved p on
    // some row is a table edit alone; RmQRMatrixDecoder already applies the value per
    // block. RmQRConstantsUnitTest pins every row against ECCPerBlock / 2.
    private static ReadOnlySpan<byte> errorCorrectionCapacities =>
    [
        3, 5, 4, 7, 6, 11, 8, 15, 12, 11,
        4, 7, 6, 11, 9, 8, 12, 11, 9, 11,
        4, 5, 6, 10, 8, 8, 12, 11, 8, 15, 12, 15,
        4, 7, 7, 14, 11, 10, 8, 14, 10, 13, 10, 14,
        9, 9, 13, 12, 9, 12, 12, 11, 12, 13,
        11, 10, 8, 15, 11, 14, 10, 13, 10, 13,
    ];

    // Character count indicator widths per version index (ISO/IEC 23941 Table 3).
    // Numeric / Alphanumeric / Byte were read back from oracle bit streams (96/96).
    // The count widths below predate Kanji decode support: no oracle emitted Kanji then, so that
    // column is pinned by derivation instead: Table 3 takes the narrowest count field
    // that still expresses the largest count the version's M-level data capacity
    // allows, a rule that reproduces all 96 verified widths exactly. The column is now read on every Kanji
    // segment the decoder meets.
    private static ReadOnlySpan<byte> numericCountBits =>
    [
        4, 5, 6, 7, 7,
        5, 6, 7, 7, 8,
        4, 6, 7, 7, 8, 8,
        5, 6, 7, 7, 8, 8,
        7, 7, 8, 8, 9,
        7, 8, 8, 8, 9,
    ];

    private static ReadOnlySpan<byte> alphanumericCountBits =>
    [
        3, 5, 5, 6, 6,
        5, 5, 6, 6, 7,
        4, 5, 6, 6, 7, 7,
        5, 6, 6, 7, 7, 8,
        6, 7, 7, 7, 8,
        6, 7, 7, 8, 8,
    ];

    private static ReadOnlySpan<byte> byteCountBits =>
    [
        3, 4, 5, 5, 6,
        4, 5, 5, 6, 6,
        3, 5, 5, 6, 6, 7,
        4, 5, 6, 6, 7, 7,
        6, 6, 7, 7, 7,
        6, 6, 7, 7, 8,
    ];

    private static ReadOnlySpan<byte> kanjiCountBits =>
    [
        2, 3, 4, 5, 5,
        3, 4, 5, 5, 6,
        2, 4, 5, 5, 6, 6,
        3, 5, 5, 6, 6, 7,
        5, 5, 6, 6, 7,
        5, 6, 6, 6, 7,
    ];

    // Vertical timing / alignment column positions per width (0-based columns);
    // each column carries a 3×3 alignment pattern at its top and bottom end.
    private static ReadOnlySpan<byte> alignmentColumns43 => [21];
    private static ReadOnlySpan<byte> alignmentColumns59 => [19, 39];
    private static ReadOnlySpan<byte> alignmentColumns77 => [25, 51];
    private static ReadOnlySpan<byte> alignmentColumns99 => [23, 49, 75];
    private static ReadOnlySpan<byte> alignmentColumns139 => [27, 55, 83, 111];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Index(RmQRVersion version) => (int)version - 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Index(RmQRVersion version, RmQREccLevel eccLevel) => ((int)version - 1) * 2 + (int)eccLevel;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValidVersion(RmQRVersion version) => (uint)((int)version - 1) < VersionCount;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValidEccLevel(RmQREccLevel eccLevel) => (uint)eccLevel <= 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetHeight(RmQRVersion version) => heights[Index(version)];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetWidth(RmQRVersion version) => widths[Index(version)];

    /// <summary>Maps physical dimensions to a version; false when (height, width) is not an rMQR size.</summary>
    public static bool TryGetVersion(int height, int width, out RmQRVersion version)
    {
        for (var i = 0; i < VersionCount; i++)
        {
            if (heights[i] == height && widths[i] == width)
            {
                version = (RmQRVersion)(i + 1);
                return true;
            }
        }

        version = 0;
        return false;
    }

    /// <summary>Vertical timing column positions for the version's width (empty for width 27).</summary>
    public static ReadOnlySpan<byte> GetAlignmentColumns(RmQRVersion version) => GetWidth(version) switch
    {
        43 => alignmentColumns43,
        59 => alignmentColumns59,
        77 => alignmentColumns77,
        99 => alignmentColumns99,
        139 => alignmentColumns139,
        _ => [],
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetTotalCodewordCount(RmQRVersion version) => totalCodewords[Index(version)];

    /// <summary>Free modules beyond 8 × total codewords (0..7); placed as light modules.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetRemainderBitCount(RmQRVersion version) => remainderBits[Index(version)];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetDataCodewordCount(RmQRVersion version, RmQREccLevel eccLevel) => dataCodewords[Index(version, eccLevel)];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetEccCodewordCount(RmQRVersion version, RmQREccLevel eccLevel)
        => totalCodewords[Index(version)] - dataCodewords[Index(version, eccLevel)];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetBlockCount(RmQRVersion version, RmQREccLevel eccLevel) => blockCounts[Index(version, eccLevel)];

    /// <summary>
    /// Maximum number of codeword errors a decoder may correct in one Reed-Solomon
    /// block; see the <c>errorCorrectionCapacities</c> table for its provenance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetErrorCorrectionCapacity(RmQRVersion version, RmQREccLevel eccLevel)
        => errorCorrectionCapacities[Index(version, eccLevel)];

    /// <summary>
    /// Reed-Solomon block structure as the shared <see cref="ECCInfo"/> (group 1 =
    /// the shorter blocks, group 2 = blocks with one more data codeword; every block
    /// has the same ECC codeword count). <see cref="ECCInfo.Version"/> carries the
    /// <see cref="RmQRVersion"/> value and <see cref="ECCInfo.ErrorCorrectionLevel"/>
    /// the corresponding <see cref="ECCLevel"/> (M or H).
    /// </summary>
    public static ECCInfo GetEccInfo(RmQRVersion version, RmQREccLevel eccLevel)
    {
        var data = GetDataCodewordCount(version, eccLevel);
        var blocks = GetBlockCount(version, eccLevel);
        var eccPerBlock = GetEccCodewordCount(version, eccLevel) / blocks;
        var shortLength = data / blocks;
        var longBlocks = data % blocks;
        var shortBlocks = blocks - longBlocks;

        return new ECCInfo(
            (int)version,
            eccLevel == RmQREccLevel.M ? ECCLevel.M : ECCLevel.H,
            data,
            eccPerBlock,
            shortBlocks,
            shortLength,
            longBlocks,
            longBlocks > 0 ? shortLength + 1 : 0);
    }

    /// <summary>Mode indicator values (ISO/IEC 23941 Table 2), all 3 bits wide.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetModeIndicatorValue(EncodingMode mode) => mode switch
    {
        EncodingMode.Numeric => 0b001,
        EncodingMode.Alphanumeric => 0b010,
        EncodingMode.Byte => 0b011,
        EncodingMode.ECI => 0b111,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), $"Encoding mode {mode} is not supported by rMQR."),
    };

    /// <summary>
    /// Dense index of the encodable modes (Numeric 0, Alphanumeric 1, Byte 2) for
    /// per-mode tables; <see cref="EncodingMode"/> values themselves are the Standard QR
    /// mode-indicator bits (1, 2, 4) and are not contiguous.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetModeIndex(EncodingMode mode) => mode switch
    {
        EncodingMode.Numeric => 0,
        EncodingMode.Alphanumeric => 1,
        EncodingMode.Byte => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), $"Encoding mode {mode} is not supported by rMQR."),
    };

    /// <summary>Number of dense-indexed encodable modes (the range of <see cref="GetModeIndex"/>: Numeric, Alphanumeric, Byte); ECI / Kanji are not part of it.</summary>
    public const int ModeCount = 3;

    /// <summary>Character count indicator width in bits (ISO/IEC 23941 Table 3).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetCountIndicatorLength(RmQRVersion version, EncodingMode mode) => GetModeIndex(mode) switch
    {
        0 => numericCountBits[Index(version)],
        1 => alphanumericCountBits[Index(version)],
        _ => byteCountBits[Index(version)],
    }; // unsupported modes throw from GetModeIndex, the single "not supported by rMQR" message on every path

    /// <summary>
    /// Kanji count indicator width; spec-transcribed, pinned by the narrowest-field
    /// derivation and read for real by the qrtool Kanji fixtures, which cover widths
    /// 4, 5 and 7 (R11x43 / R13x59 / R15x59 / R17x139); 2, 3 and 6 rest on the derivation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetKanjiCountIndicatorLength(RmQRVersion version) => kanjiCountBits[Index(version)];

    /// <summary>
    /// Computes the 18 format information bits of one copy: 6 data bits (ECC level
    /// bit above the 5-bit version index) protected by BCH(18,6) with generator
    /// polynomial 0x1F25, XOR-masked with the copy's constant (finder side 0x1FAB2,
    /// sub-finder side 0x20A7B). Bit i of the result is module i of the region in
    /// placement order (see the placer).
    /// </summary>
    public static int GetFormatBits(RmQRVersion version, RmQREccLevel eccLevel, bool subFinderSide)
    {
        var data = ((int)eccLevel << 5) | Index(version);

        // BCH(18,6) remainder of data·x^12 mod 0x1F25.
        var remainder = data << 12;
        for (var bit = 17; bit >= 12; bit--)
        {
            if ((remainder & (1 << bit)) != 0)
            {
                remainder ^= 0x1F25 << (bit - 12);
            }
        }

        return ((data << 12) | remainder) ^ (subFinderSide ? FormatXorSubFinderSide : FormatXorFinderSide);
    }
}
