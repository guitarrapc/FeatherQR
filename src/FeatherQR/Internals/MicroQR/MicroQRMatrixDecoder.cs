using FeatherQR.Internals.BinaryDecoders;

namespace FeatherQR.Internals.MicroQR;

/// <summary>
/// Decodes a Micro QR module matrix (one byte per module, no quiet zone) back into text.
/// </summary>
/// <remarks>
/// Inverse of <see cref="MicroQRCodeGenerator"/>'s matrix writing pipeline, the
/// same internal boundary as the Standard QR <c>QRMatrixDecoder</c>:
/// <code>
/// 1. Version from matrix size (11/13/15/17 → M1-M4)
/// 2. Format information → version cross-check + ECC level + mask pattern
///    (single 15-bit copy, Standard QR has two)
/// 3. Codeword extraction: inverse two-column zigzag, unmasking on the fly
///    (single Reed-Solomon block, so there is no deinterleaving stage)
/// 4. Reed-Solomon error correction, capped at the ISO Table 9 capacity t
///    (the ECC codewords include misdecode-protection codewords p; M1 is
///    error detection only, t = 0)
/// 5. Bitstream decoding (mode segments → text), bounded by the bit capacity
///    (M1/M3 end on a 4-bit half codeword)
/// </code>
/// The function-module predicate and mask conditions are the encoder's own
/// (<see cref="MicroQRModulePlacer"/>), so the decoder can never disagree with
/// the encoder about which modules carry data.
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
        charsWritten = 0;

        // 1. Version from size
        var version = MicroQRConstants.VersionFromSize(size);
        if (version == 0 || modules.Length < size * size)
        {
            info = new MicroQRCodeDecodeInfo(DecodeStatus.InvalidMatrix, 0, default, -1, 0);
            return DecodeStatus.InvalidMatrix;
        }

        // 2. Format information (symbol number → version/ECC, mask pattern)
        var rawFormat = ReadFormatBits(modules, size);
        if (!MicroQRFormatInformationDecoder.TryDecode(rawFormat, out var formatVersion, out var eccLevel, out var maskPattern)
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
        ExtractCodewords(modules, size, maskPattern, dataBitCount, dataCodewords, block);

        // 4. Reed-Solomon correction, capped at the symbol's correction capacity
        if (!EccBinaryDecoder.TryCorrect(block, eccCodewords, out var errorsCorrected)
            || errorsCorrected > MicroQRConstants.GetErrorCorrectionCapacity(version, eccLevel))
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
    /// Numeric mode is the densest: 10 bits → 3 characters, so one data codeword
    /// (8 bits) yields at most 2.4 characters; 3× codewords is a safe bound. The
    /// L (or detection-only) level has the most data codewords per version.
    /// </remarks>
    public static int GetMaxCharCount(MicroQRVersion version)
    {
        var eccLevel = version == MicroQRVersion.M1 ? MicroQREccLevel.ErrorDetectionOnly : MicroQREccLevel.L;
        return MicroQRConstants.GetDataCodewordCount(version, eccLevel) * 3;
    }

    /// <summary>
    /// Reads the 15 format information bits. Positions mirror
    /// <see cref="MicroQRModulePlacer.PlaceFormat"/> exactly: bits 14…7 along
    /// row 8 columns 1-8, bits 6…0 down column 8 rows 7-1.
    /// </summary>
    private static ushort ReadFormatBits(ReadOnlySpan<byte> modules, int size)
    {
        var raw = 0;
        var bit = 14;
        var row8Offset = 8 * size;
        for (var col = 1; col <= 8; col++, bit--)
        {
            if (modules[row8Offset + col] != 0)
                raw |= 1 << bit;
        }
        for (var row = 7; row >= 1; row--, bit--)
        {
            if (modules[row * size + 8] != 0)
                raw |= 1 << bit;
        }

        return (ushort)raw;
    }

    /// <summary>
    /// Reads data/ECC codeword bits from the matrix in placement order (inverse of
    /// <see cref="MicroQRModulePlacer.PlaceDataCodewords"/>), unmasking each module
    /// on the fly. Data bits fill <paramref name="block"/> from byte 0 (the M1/M3
    /// half codeword naturally ends as a high nibble because the stream stops at
    /// <paramref name="dataBitCount"/>); ECC bits fill full bytes from
    /// <paramref name="dataCodewords"/> on.
    /// </summary>
    private static void ExtractCodewords(ReadOnlySpan<byte> modules, int size, int maskPattern, int dataBitCount, int dataCodewords, Span<byte> block)
    {
        // The stream length always equals the free-module count (ISO tables), but
        // guard the write anyway so a table inconsistency cannot corrupt memory.
        var totalBits = dataBitCount + (block.Length - dataCodewords) * 8;
        var bitIndex = 0;
        var upward = true;

        // Column pairs (size-1, size-2) … (2, 1); column 0 is all function modules.
        for (var right = size - 1; right >= 2; right -= 2)
        {
            for (var step = 0; step < size; step++)
            {
                var row = upward ? size - 1 - step : step;
                var rowOffset = row * size;

                for (var side = 0; side < 2; side++)
                {
                    var col = right - side;
                    if (MicroQRModulePlacer.IsFunctionModule(row, col))
                        continue;
                    if (bitIndex >= totalBits)
                        return;

                    var dark = modules[rowOffset + col] != 0;
                    if (MicroQRModulePlacer.GetMaskBit(maskPattern, row, col))
                        dark = !dark;

                    if (dark)
                    {
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
                    bitIndex++;
                }
            }
            upward = !upward;
        }
    }
}
