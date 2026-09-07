using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace FeatherQR.Internals.MicroQR;

/// <summary>
/// Micro QR symbol tables and format information (ISO/IEC 18004).
/// </summary>
/// <remarks>
/// All tables are flat arrays indexed by <c>(version - 1) * 4 + eccLevel</c> where eccLevel follows <see cref="MicroQREccLevel"/> ordering (ErrorDetectionOnly, L, M, Q).
/// A zero (or negative symbol number) marks an invalid version/ECC combination — the valid set is: M1 detection-only, M2/M3 L+M, M4 L+M+Q.
/// </remarks>
internal static class MicroQRConstants
{
    /// <summary>Symbol type identifier in the QRX serialization header.</summary>
    public const byte SymbolTypeMicroQR = 1;

    // Symbol number (3-bit format information field) per version/ECC combination,
    // -1 for invalid combinations (ISO/IEC 18004 Micro QR format information).
    private static ReadOnlySpan<sbyte> symbolNumbers =>
    [
        0, -1, -1, -1, // M1: detection only
        -1, 1, 2, -1,  // M2: L, M
        -1, 3, 4, -1,  // M3: L, M
        -1, 5, 6, 7,   // M4: L, M, Q
    ];

    // Data capacity in bits (ISO/IEC 18004 Table 7). M1 and M3 capacities end on a
    // half byte: their final data codeword is 4 bits (stored as the high nibble).
    private static ReadOnlySpan<byte> dataBitCapacities =>
    [
        20, 0, 0, 0,
        0, 40, 32, 0,
        0, 84, 68, 0,
        0, 128, 112, 80,
    ];

    // Data codeword counts (final codeword is 4-bit for M1/M3) and error correction
    // codeword counts (always full bytes), ISO/IEC 18004 Table 9.
    private static ReadOnlySpan<byte> dataCodewordCounts =>
    [
        3, 0, 0, 0,
        0, 5, 4, 0,
        0, 11, 9, 0,
        0, 16, 14, 10,
    ];

    private static ReadOnlySpan<byte> eccCodewordCounts =>
    [
        2, 0, 0, 0,
        0, 5, 6, 0,
        0, 6, 8, 0,
        0, 8, 10, 14,
    ];

    // Error correction capacity t in codewords (ISO/IEC 18004 Table 9): the ECC
    // codeword count includes p misdecode-protection codewords (2t + p = ecc;
    // M1 p=2, M2-L p=3, M2-M/M3-L/M4-L p=2, others p=0), so a decoder must not
    // correct more than t errors even where the Reed-Solomon code itself could.
    private static ReadOnlySpan<byte> errorCorrectionCapacities =>
    [
        0, 0, 0, 0,
        0, 1, 2, 0,
        0, 2, 4, 0,
        0, 3, 5, 7,
    ];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int TableIndex(MicroQRVersion version, MicroQREccLevel eccLevel)
        => ((int)version - 1) * 4 + (int)eccLevel;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValidCombination(MicroQRVersion version, MicroQREccLevel eccLevel)
        => symbolNumbers[TableIndex(version, eccLevel)] >= 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetSymbolNumber(MicroQRVersion version, MicroQREccLevel eccLevel)
        => symbolNumbers[TableIndex(version, eccLevel)];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetDataBitCapacity(MicroQRVersion version, MicroQREccLevel eccLevel)
        => dataBitCapacities[TableIndex(version, eccLevel)];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetDataCodewordCount(MicroQRVersion version, MicroQREccLevel eccLevel)
        => dataCodewordCounts[TableIndex(version, eccLevel)];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetEccCodewordCount(MicroQRVersion version, MicroQREccLevel eccLevel)
        => eccCodewordCounts[TableIndex(version, eccLevel)];

    /// <summary>
    /// Maximum number of codeword errors a decoder may correct (ISO/IEC 18004 Table 9); 0 for M1 (error detection only).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetErrorCorrectionCapacity(MicroQRVersion version, MicroQREccLevel eccLevel)
        => errorCorrectionCapacities[TableIndex(version, eccLevel)];

    /// <summary>
    /// Inverse of <see cref="GetSymbolNumber"/>: maps the 3-bit format information symbol number (0-7) back to its version/ECC combination.
    /// </summary>
    public static void GetVersionAndEccFromSymbolNumber(int symbolNumber, out MicroQRVersion version, out MicroQREccLevel eccLevel)
    {
        Debug.Assert(symbolNumber is >= 0 and <= 7, "symbol number is a 3-bit field");
        // Symbol numbers enumerate the 8 valid combinations in version-major order.
        (version, eccLevel) = symbolNumber switch
        {
            0 => (MicroQRVersion.M1, MicroQREccLevel.ErrorDetectionOnly),
            1 => (MicroQRVersion.M2, MicroQREccLevel.L),
            2 => (MicroQRVersion.M2, MicroQREccLevel.M),
            3 => (MicroQRVersion.M3, MicroQREccLevel.L),
            4 => (MicroQRVersion.M3, MicroQREccLevel.M),
            5 => (MicroQRVersion.M4, MicroQREccLevel.L),
            6 => (MicroQRVersion.M4, MicroQREccLevel.M),
            _ => (MicroQRVersion.M4, MicroQREccLevel.Q),
        };
    }

    /// <summary>Symbol side length in modules: 11/13/15/17 for M1-M4.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SizeFromVersion(MicroQRVersion version) => 9 + 2 * (int)version;

    /// <summary>Inverse of <see cref="SizeFromVersion"/>; 0 when the size is not a Micro QR size.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MicroQRVersion VersionFromSize(int size)
        => size is 11 or 13 or 15 or 17 ? (MicroQRVersion)((size - 9) / 2) : 0;

    /// <summary>
    /// Mode indicator width in bits: 0 for M1 (numeric implied), version − 1 otherwise.
    /// Indicator values: Numeric = 0, Alphanumeric = 1, Byte = 2 (ISO/IEC 18004 Table 2).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetModeIndicatorLength(MicroQRVersion version) => (int)version - 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetModeIndicatorValue(EncodingMode mode) => mode switch
    {
        EncodingMode.Numeric => 0,
        EncodingMode.Alphanumeric => 1,
        EncodingMode.Byte => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), $"Encoding mode {mode} is not supported by Micro QR."),
    };

    /// <summary>
    /// Mode availability per version (ISO/IEC 18004): M1 numeric only, M2 adds alphanumeric, M3/M4 add byte.
    /// Kanji is decode-only, so it is absent here; its count width lives in <see cref="GetKanjiCountIndicatorLength"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsModeSupported(MicroQRVersion version, EncodingMode mode) => mode switch
    {
        EncodingMode.Numeric => true,
        EncodingMode.Alphanumeric => version >= MicroQRVersion.M2,
        EncodingMode.Byte => version >= MicroQRVersion.M3,
        _ => false,
    };

    /// <summary>
    /// Character count indicator width in bits (ISO/IEC 18004 Table 3): Numeric = version + 2, Alphanumeric/Byte = version + 1.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetCountIndicatorLength(MicroQRVersion version, EncodingMode mode) => mode switch
    {
        EncodingMode.Numeric => (int)version + 2,
        EncodingMode.Alphanumeric or EncodingMode.Byte => (int)version + 1,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), $"Encoding mode {mode} is not supported by Micro QR."),
    };

    /// <summary>
    /// Character count indicator width for Kanji mode (ISO/IEC 18004 Table 3): 3 bits for M3, 4 for M4.
    /// M1 and M2 do not define Kanji mode, and their mode indicators (0 and 1 bit wide) cannot express its value, so it never reaches here.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetKanjiCountIndicatorLength(MicroQRVersion version)
    {
        if (version < MicroQRVersion.M3)
            ThrowKanjiNotDefined(version);

        return (int)version;
    }

    // Outlined so the inlined body above stays a compare and a move; the interpolated
    // message would otherwise be built into every call site.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowKanjiNotDefined(MicroQRVersion version)
        => throw new ArgumentOutOfRangeException(nameof(version), $"Micro QR version {version} does not define Kanji mode.");

    /// <summary>Terminator length in bits: 3/5/7/9 for M1-M4 (ISO/IEC 18004 Table 2).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetTerminatorLength(MicroQRVersion version) => 2 * (int)version + 1;

    /// <summary>
    /// Computes the 15 format information bits for the given version, ECC level and mask pattern: 5 data bits (3-bit symbol number + 2-bit mask) protected by BCH(15,5) with generator polynomial 0x537, XOR-masked with 0x4445 (ISO/IEC 18004 Micro QR format information; Standard QR uses XOR 0x5412).
    /// </summary>
    public static ushort GetFormatBits(MicroQRVersion version, MicroQREccLevel eccLevel, int maskPattern)
    {
        var data = (GetSymbolNumber(version, eccLevel) << 2) | maskPattern;

        // BCH(15,5) remainder of data·x^10 mod 0x537.
        var remainder = data << 10;
        for (var bit = 14; bit >= 10; bit--)
        {
            if ((remainder & (1 << bit)) != 0)
            {
                remainder ^= 0x537 << (bit - 10);
            }
        }

        return (ushort)(((data << 10) | remainder) ^ 0x4445);
    }
}
