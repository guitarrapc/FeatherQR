using FeatherQR.Internals.BinaryDecoders;

namespace FeatherQR.Internals.MicroQR;

/// <summary>
/// Decodes a Micro QR module matrix (one byte per module, no quiet zone) back into text.
/// </summary>
/// <remarks>
/// Inverse of <see cref="MicroQRCodeGenerator"/>'s matrix writing pipeline, the same internal boundary as the Standard QR <c>QRMatrixDecoder</c>:
/// <code>
/// 1. Version from matrix size (11/13/15/17 → M1-M4)
/// 2. Format information → version cross-check + ECC level + mask pattern
///    (single 15-bit copy, Standard QR has two)
/// 3. Codeword extraction: inverse two-column zigzag, unmasking on the fly
///    (single Reed-Solomon block, so there is no deinterleaving stage)
/// 4. Reed-Solomon error correction, capped at the ISO Table 9 capacity t
///    (the ECC codewords include misdecode-protection codewords p; M1 is
///    error detection only, t = 0) and, on a grid sampled from an image, at
///    what its structure earns (MicroQRGridEvidence)
/// 5. Bitstream decoding (mode segments → text), bounded by the bit capacity
///    (M1/M3 end on a 4-bit half codeword)
/// </code>
/// The function-module predicate and mask conditions are the encoder's own (<see cref="MicroQRModulePlacer"/>), so the decoder can never disagree with the encoder about which modules carry data.
/// </remarks>
internal static class MicroQRMatrixDecoder
{
    // Largest Micro QR block: M4 has 24 total codewords (data + ECC).
    private const int MaxTotalCodewords = 24;

    /// <summary>
    /// Decodes a core module matrix into characters.
    /// </summary>
    /// <param name="modules">Core module matrix, one byte per module (0 = light, non-zero = dark), row-major, no quiet zone.</param>
    /// <param name="size">Matrix size in modules per side (11/13/15/17).</param>
    /// <param name="destination">Destination buffer for decoded characters.</param>
    /// <param name="charsWritten">Number of characters written.</param>
    /// <param name="info">Diagnostic information (version, ECC level, mask, corrected errors).</param>
    public static DecodeStatus DecodeMatrix(ReadOnlySpan<byte> modules, int size, Span<char> destination, out int charsWritten, out MicroQRCodeDecodeInfo info)
    {
        if (modules.Length < size * size)
        {
            charsWritten = 0;
            info = new MicroQRCodeDecodeInfo(DecodeStatus.InvalidMatrix, 0, default, -1, 0);
            return DecodeStatus.InvalidMatrix;
        }

        return DecodeMatrix(modules, new MatrixModules(size), size, destination, out charsWritten, out info);
    }

    /// <summary>
    /// Decodes the grid <paramref name="modules"/> reads out of <paramref name="pixels"/>, asking only for the modules the decode reaches: the format information first, the data and ECC modules once it names this size.
    /// </summary>
    public static DecodeStatus DecodeMatrix<TModules>(ReadOnlySpan<byte> pixels, in TModules modules, int size, Span<char> destination, out int charsWritten, out MicroQRCodeDecodeInfo info)
        where TModules : struct, IMicroQRModules
        => Decode(pixels, modules, size, sampled: false, default, 0, 0, 0, new CountedQuietZone(0), destination, out charsWritten, out info);

    /// <summary>
    /// Decodes a grid sampled from an image, where the grid may lie on anything: the corrections a read may use are what its timing patterns, format word and quiet zone earn (<see cref="MicroQRGridEvidence"/>), and a read beyond what they earn fails as a correction failure does.
    /// </summary>
    /// <remarks>
    /// The structure is counted only for a grid Reed-Solomon reads, which texture rarely reaches, and the quiet zone, read from <paramref name="luminance"/>, only when the timing patterns and format word fall short.
    /// </remarks>
    public static DecodeStatus DecodeSampledGrid<TModules, TQuietZone>(ReadOnlySpan<byte> pixels, in TModules modules, int size, ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in TQuietZone quietZone, Span<char> destination, out int charsWritten, out MicroQRCodeDecodeInfo info)
        where TModules : struct, IMicroQRModules
        where TQuietZone : struct, IMicroQRQuietZone
        => Decode(pixels, modules, size, sampled: true, luminance, width, height, threshold, quietZone, destination, out charsWritten, out info);

    private static DecodeStatus Decode<TModules, TQuietZone>(ReadOnlySpan<byte> pixels, in TModules modules, int size, bool sampled, ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in TQuietZone quietZone, Span<char> destination, out int charsWritten, out MicroQRCodeDecodeInfo info)
        where TModules : struct, IMicroQRModules
        where TQuietZone : struct, IMicroQRQuietZone
    {
        charsWritten = 0;

        // 1. Version from size
        var version = MicroQRConstants.VersionFromSize(size);
        if (version == 0)
        {
            info = new MicroQRCodeDecodeInfo(DecodeStatus.InvalidMatrix, 0, default, -1, 0);
            return DecodeStatus.InvalidMatrix;
        }

        // 2. Format information (symbol number → version/ECC, mask pattern)
        var rawFormat = ReadFormatBits(pixels, modules);
        if (!MicroQRFormatInformationDecoder.TryDecode(rawFormat, out var formatVersion, out var eccLevel, out var maskPattern, out var formatDistance)
            || formatVersion != version)
        {
            // A decodable format word naming a different version than the physical
            // matrix size is corruption, not a smaller symbol.
            info = new MicroQRCodeDecodeInfo(DecodeStatus.FormatInformationInvalid, version, default, -1, 0);
            return DecodeStatus.FormatInformationInvalid;
        }

        var dataBitCount = MicroQRConstants.GetDataBitCapacity(version, eccLevel);
        var dataCodewords = MicroQRConstants.GetDataCodewordCount(version, eccLevel);
        var eccCodewords = MicroQRConstants.GetEccCodewordCount(version, eccLevel);

        // Single block [data | ecc]; the M1/M3 half codeword occupies a full byte
        // with a zero low nibble (the same byte value Reed-Solomon was computed over).
        Span<byte> block = stackalloc byte[MaxTotalCodewords].Slice(0, dataCodewords + eccCodewords);
        block.Clear();

        // 3. Extract codewords (inverse zigzag + unmask)
        ExtractCodewords(pixels, modules, size, maskPattern, dataBitCount, dataCodewords, block);

        // 4. Reed-Solomon correction, capped at the ISO Table 9 capacity and, on a sampled grid, at what its
        //    structure earns: counted only for a read, which texture rarely reaches, the quiet zone last
        if (!EccBinaryDecoder.TryCorrect(block, eccCodewords, out var errorsCorrected)
            || errorsCorrected > MicroQRConstants.GetErrorCorrectionCapacity(version, eccLevel)
            || (sampled && !MicroQRGridEvidence.SuspendedOnThisThread && !Earns(pixels, modules, size, version, eccLevel, formatDistance, errorsCorrected, luminance, width, height, threshold, quietZone)))
        {
            info = new MicroQRCodeDecodeInfo(DecodeStatus.DataUncorrectable, version, eccLevel, maskPattern, errorsCorrected);
            return DecodeStatus.DataUncorrectable;
        }

        // 5. Bitstream → text
        var status = MicroQRBinaryDecoder.DecodeBitStream(block.Slice(0, dataCodewords), dataBitCount, version, destination, out charsWritten);
        info = new MicroQRCodeDecodeInfo(status, version, eccLevel, maskPattern, errorsCorrected);
        return status;
    }

    /// <summary>
    /// Upper bound of decoded characters for a version, across all ECC levels and modes.
    /// </summary>
    /// <remarks>
    /// Numeric mode is the densest: 10 bits → 3 characters, so one data codeword (8 bits) yields at most 2.4 characters; 3× codewords is a safe bound.
    /// The L (or detection-only) level has the most data codewords per version.
    /// </remarks>
    public static int GetMaxCharCount(MicroQRVersion version)
    {
        var eccLevel = version == MicroQRVersion.M1 ? MicroQREccLevel.ErrorDetectionOnly : MicroQREccLevel.L;
        return MicroQRConstants.GetDataCodewordCount(version, eccLevel) * 3;
    }

    /// <summary>Whether the grid's timing patterns and format word, and its quiet zone when they fall short, earn <paramref name="errorsCorrected"/> corrections.</summary>
    private static bool Earns<TModules, TQuietZone>(ReadOnlySpan<byte> pixels, in TModules modules, int size, MicroQRVersion version, MicroQREccLevel eccLevel, int formatDistance, int errorsCorrected, ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in TQuietZone quietZone)
        where TModules : struct, IMicroQRModules
        where TQuietZone : struct, IMicroQRQuietZone
    {
        var timingMismatches = CountTimingMismatches(pixels, modules, size);
        return errorsCorrected <= MicroQRGridEvidence.MaxCorrections(version, eccLevel, formatDistance, timingMismatches, MicroQRGridEvidence.QuietZoneUnread)
            || errorsCorrected <= MicroQRGridEvidence.MaxCorrections(version, eccLevel, formatDistance, timingMismatches, quietZone.CountDark(luminance, width, height, threshold, size));
    }

    /// <summary>Timing modules (row 0 and column 0 from index 8, dark on even indices) that read the other way; the same count for a grid and its transpose.</summary>
    private static int CountTimingMismatches<TModules>(ReadOnlySpan<byte> pixels, in TModules modules, int size)
        where TModules : struct, IMicroQRModules
    {
        var mismatches = 0;
        for (var i = 8; i < size; i++)
        {
            var dark = (i & 1) == 0;
            if (modules.IsDark(pixels, 0, i) != dark)
                mismatches++;
            if (modules.IsDark(pixels, i, 0) != dark)
                mismatches++;
        }
        return mismatches;
    }

    /// <summary>Whether the grid's format word is one of the 32 exactly and names the version its size is.</summary>
    public static bool HasExactFormat<TModules>(ReadOnlySpan<byte> pixels, in TModules modules, int size)
        where TModules : struct, IMicroQRModules
        => MicroQRFormatInformationDecoder.TryDecode(ReadFormatBits(pixels, modules), out var formatVersion, out _, out _, out var distance)
            && distance == 0
            && formatVersion == MicroQRConstants.VersionFromSize(size);

    /// <summary>
    /// Reads the 15 format information bits.
    /// Positions mirror <see cref="MicroQRModulePlacer.PlaceFormat"/> exactly: bits 14…7 along row 8 columns 1-8, bits 6…0 down column 8 rows 7-1.
    /// </summary>
    private static ushort ReadFormatBits<TModules>(ReadOnlySpan<byte> pixels, in TModules modules)
        where TModules : struct, IMicroQRModules
    {
        var raw = 0;
        var bit = 14;
        for (var col = 1; col <= 8; col++, bit--)
        {
            if (modules.IsDark(pixels, 8, col))
                raw |= 1 << bit;
        }
        for (var row = 7; row >= 1; row--, bit--)
        {
            if (modules.IsDark(pixels, row, 8))
                raw |= 1 << bit;
        }

        return (ushort)raw;
    }

    /// <summary>
    /// Reads data/ECC codeword bits from the matrix in placement order (inverse of <see cref="MicroQRModulePlacer.PlaceDataCodewords"/>), unmasking each module on the fly.
    /// Data bits fill <paramref name="block"/> from byte 0 (the M1/M3 half codeword naturally ends as a high nibble because the stream stops at <paramref name="dataBitCount"/>); ECC bits fill full bytes from <paramref name="dataCodewords"/> on.
    /// </summary>
    private static void ExtractCodewords<TModules>(ReadOnlySpan<byte> pixels, in TModules modules, int size, int maskPattern, int dataBitCount, int dataCodewords, Span<byte> block)
        where TModules : struct, IMicroQRModules
    {
        var placement = Placements[(size - 11) >> 1];
        // The stream length always equals the free-module count (ISO tables), but
        // guard the write anyway so a table inconsistency cannot corrupt memory.
        var totalBits = Math.Min(dataBitCount + (block.Length - dataCodewords) * 8, placement.Length);
        for (var bitIndex = 0; bitIndex < totalBits; bitIndex++)
        {
            var module = placement[bitIndex];
            var dark = modules.IsDark(pixels, module.Row, module.Col) ^ ((module.MaskBits >> maskPattern & 1) != 0);
            if (!dark)
                continue;

            if (bitIndex < dataBitCount)
            {
                block[bitIndex >> 3] |= (byte)(0x80 >> (bitIndex & 7));
            }
            else
            {
                var eccBit = bitIndex - dataBitCount;
                block[dataCodewords + (eccBit >> 3)] |= (byte)(0x80 >> (eccBit & 7));
            }
        }
    }

    /// <summary>A data module in placement order, with bit <c>m</c> of <see cref="MaskBits"/> set where mask pattern <c>m</c> inverts it.</summary>
    private readonly record struct PlacedModule(byte Row, byte Col, byte MaskBits);

    /// <summary>The data modules of each size (11, 13, 15, 17) in placement order: the codeword stream reads them in this order, whatever grid they are read from.</summary>
    private static readonly PlacedModule[][] Placements = [BuildPlacement(11), BuildPlacement(13), BuildPlacement(15), BuildPlacement(17)];

    private static PlacedModule[] BuildPlacement(int size)
    {
        var placement = new List<PlacedModule>(size * size);
        var upward = true;
        // Column pairs (size-1, size-2) … (2, 1); column 0 is all function modules.
        for (var right = size - 1; right >= 2; right -= 2)
        {
            for (var step = 0; step < size; step++)
            {
                var row = upward ? size - 1 - step : step;
                for (var side = 0; side < 2; side++)
                {
                    var col = right - side;
                    if (MicroQRModulePlacer.IsFunctionModule(row, col))
                        continue;

                    var maskBits = 0;
                    for (var mask = 0; mask < 4; mask++)
                    {
                        if (MicroQRModulePlacer.GetMaskBit(mask, row, col))
                            maskBits |= 1 << mask;
                    }
                    placement.Add(new PlacedModule((byte)row, (byte)col, (byte)maskBits));
                }
            }
            upward = !upward;
        }
        return placement.ToArray();
    }
}
