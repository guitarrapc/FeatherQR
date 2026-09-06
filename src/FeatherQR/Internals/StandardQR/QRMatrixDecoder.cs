using System.Buffers;
using FeatherQR.Internals.BinaryDecoders;

namespace FeatherQR.Internals.StandardQR;

/// <summary>
/// Decodes a QR module matrix (one byte per module, no quiet zone) back into text.
/// </summary>
/// <remarks>
/// Inverse of <see cref="QRCodeGenerator"/>'s matrix writing pipeline:
/// <code>
/// 1. Version from matrix size (size = 17 + 4·version)
/// 2. Format information → ECC level + mask pattern (BCH, two redundant copies)
/// 3. Codeword extraction: inverse zigzag walk, unmasking on the fly
/// 4. Deinterleave codewords into Reed-Solomon blocks
/// 5. Reed-Solomon error correction per block
/// 6. Bitstream decoding (mode segments → text)
/// </code>
/// The function-pattern layout comes from the encoder's own per-version placement tables (<see cref="ModulePlacer.GetLayout"/>, built by <see cref="QRCodeGenerator.PlaceFunctionModulesReference"/>), so the decoder can never disagree with the encoder about which modules carry data.
/// </remarks>
internal static class QRMatrixDecoder
{
    // Steady-state decode allocates nothing: buffers are stackalloc/pooled and the
    // blocked-module mask comes from the encoder's per-version placement tables.
    private const int StackAllocThreshold = 512;

    /// <summary>
    /// Decodes a core module matrix into characters.
    /// </summary>
    /// <param name="modules">Core module matrix, one byte per module (0 = light, non-zero = dark), row-major, no quiet zone.</param>
    /// <param name="size">Matrix size in modules per side.</param>
    /// <param name="destination">Destination buffer for decoded characters.</param>
    /// <param name="charsWritten">Number of characters written.</param>
    /// <param name="info">Diagnostic information (version, ECC level, mask, corrected errors).</param>
    public static DecodeStatus DecodeMatrix(ReadOnlySpan<byte> modules, int size, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info)
    {
        charsWritten = 0;

        // 1. Version from size
        if (size < 21 || size > 177 || (size - 21) % 4 != 0 || modules.Length < size * size)
        {
            info = new QRCodeDecodeInfo(DecodeStatus.InvalidMatrix, 0, default, -1, 0);
            return DecodeStatus.InvalidMatrix;
        }
        var version = (size - 21) / 4 + 1;

        // 2. Format information (ECC level + mask pattern)
        ReadFormatBits(modules, size, out var rawFormat1, out var rawFormat2);
        if (!FormatInformationDecoder.TryDecode(rawFormat1, rawFormat2, out var eccLevel, out var maskPattern))
        {
            info = new QRCodeDecodeInfo(DecodeStatus.FormatInformationInvalid, version, default, -1, 0);
            return DecodeStatus.FormatInformationInvalid;
        }

        var eccInfo = QRCodeConstants.GetEccInfo(version, eccLevel);
        var totalBlocks = eccInfo.BlocksInGroup1 + eccInfo.BlocksInGroup2;
        var totalCodewords = eccInfo.TotalDataCodewords + totalBlocks * eccInfo.ECCPerBlock;

        // Working buffer: [interleaved codewords | per-block codewords].
        // After deinterleaving, the interleaved half is reused to gather the
        // corrected data codewords for bitstream decoding.
        byte[]? rented = null;
        var workSize = totalCodewords * 2;
        Span<byte> work = workSize <= StackAllocThreshold
            ? stackalloc byte[StackAllocThreshold]
            : (rented = ArrayPool<byte>.Shared.Rent(workSize)).AsSpan();
        try
        {
            var interleaved = work.Slice(0, totalCodewords);
            var blocks = work.Slice(totalCodewords, totalCodewords);
            interleaved.Clear();

            // 3. Extract codewords (inverse zigzag + unmask)
            var blockedMask = GetBlockedMask(version, size);
            ExtractCodewords(modules, size, blockedMask, maskPattern, interleaved);

            // 4. Deinterleave into per-block [data | ecc] codewords
            DeinterleaveCodewords(interleaved, blocks, eccInfo);

            // 5. Reed-Solomon correction per block; corrected data codewords are
            // gathered sequentially (block order matches the encoder's split).
            var data = interleaved.Slice(0, eccInfo.TotalDataCodewords);
            var errorsCorrected = 0;
            var dataOffset = 0;
            var blockOffset = 0;
            for (var b = 0; b < totalBlocks; b++)
            {
                var dataCodewords = b < eccInfo.BlocksInGroup1 ? eccInfo.CodewordsInGroup1 : eccInfo.CodewordsInGroup2;
                var blockLength = dataCodewords + eccInfo.ECCPerBlock;
                var block = blocks.Slice(blockOffset, blockLength);

                if (!EccBinaryDecoder.TryCorrect(block, eccInfo.ECCPerBlock, out var blockErrors))
                {
                    info = new QRCodeDecodeInfo(DecodeStatus.DataUncorrectable, version, eccLevel, maskPattern, errorsCorrected);
                    return DecodeStatus.DataUncorrectable;
                }

                errorsCorrected += blockErrors;
                block.Slice(0, dataCodewords).CopyTo(data.Slice(dataOffset));
                dataOffset += dataCodewords;
                blockOffset += blockLength;
            }

            // 6. Bitstream → text
            var status = QRBinaryDecoder.DecodeBitStream(data, version, destination, out charsWritten);
            info = new QRCodeDecodeInfo(status, version, eccLevel, maskPattern, errorsCorrected);
            return status;
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }
    }

    /// <summary>
    /// Upper bound of decoded characters for a version, across all ECC levels and modes.
    /// </summary>
    /// <remarks>
    /// Numeric mode is the densest: 10 bits → 3 characters, so one data codeword (8 bits) yields at most 2.4 characters; 3× codewords is a safe bound.
    /// </remarks>
    public static int GetMaxCharCount(int version)
        => QRCodeConstants.GetEccInfo(version, QREccLevel.L).TotalDataCodewords * 3;

    /// <summary>
    /// Reads the two redundant 15-bit format information copies.
    /// Positions mirror <see cref="ModulePlacer.PlaceFormat"/> exactly (bit i, LSB first).
    /// </summary>
    private static void ReadFormatBits(ReadOnlySpan<byte> modules, int size, out ushort rawCopy1, out ushort rawCopy2)
    {
        ReadOnlySpan<(int x1, int y1, int x2, int y2)> positions = stackalloc (int, int, int, int)[15]
        {
            ( 8, 0, size - 1, 8 ),
            ( 8, 1, size - 2, 8 ),
            ( 8, 2, size - 3, 8 ),
            ( 8, 3, size - 4, 8 ),
            ( 8, 4, size - 5, 8 ),
            ( 8, 5, size - 6, 8 ),
            ( 8, 7, size - 7, 8 ),
            ( 8, 8, size - 8, 8 ),
            ( 7, 8, 8, size - 7 ),
            ( 5, 8, 8, size - 6 ),
            ( 4, 8, 8, size - 5 ),
            ( 3, 8, 8, size - 4 ),
            ( 2, 8, 8, size - 3 ),
            ( 1, 8, 8, size - 2 ),
            ( 0, 8, 8, size - 1 ),
        };

        rawCopy1 = 0;
        rawCopy2 = 0;
        for (var i = 0; i < 15; i++)
        {
            var (x1, y1, x2, y2) = positions[i];
            if (modules[y1 * size + x1] != 0)
                rawCopy1 |= (ushort)(1 << i);
            if (modules[y2 * size + x2] != 0)
                rawCopy2 |= (ushort)(1 << i);
        }
    }

    /// <summary>
    /// Reads data/ECC codeword bits from the matrix in placement order (inverse of <see cref="ModulePlacer.PlaceDataWords(Span{byte}, int, ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>), unmasking each module on the fly.
    /// Remainder bits beyond the output capacity are ignored.
    /// </summary>
    private static void ExtractCodewords(ReadOnlySpan<byte> modules, int size, ReadOnlySpan<byte> blockedMask, int maskPattern, Span<byte> output)
    {
        var bitPos = 0;
        var totalBits = output.Length * 8;
        var up = true;

        for (var x = size - 1; x >= 0; x -= 2)
        {
            // Skip timing pattern column
            if (x == 6)
                x--;

            for (var rows = 0; rows < size; rows++)
            {
                var y = up ? size - 1 - rows : rows;

                for (var xOffset = 0; xOffset < 2; xOffset++)
                {
                    var xModule = x - xOffset;
                    var bitIndex = y * size + xModule;

                    // Function patterns and reserved areas carry no data
                    if ((blockedMask[bitIndex >> 3] & (1 << (bitIndex & 7))) != 0)
                        continue;

                    if (bitPos >= totalBits)
                        return; // remaining modules are remainder bits (always 0)

                    var dark = modules[bitIndex] != 0;
                    if (GetMaskBit(maskPattern, y, xModule))
                        dark = !dark;

                    if (dark)
                        output[bitPos >> 3] |= (byte)(0x80 >> (bitPos & 7));
                    bitPos++;
                }
            }

            up = !up;
        }
    }

    /// <summary>
    /// Mask predicates (ISO/IEC 18004 7.8.2), row = y, col = x.
    /// Must match the encoder's mask templates (see ModulePlacer.Masking).
    /// </summary>
    private static bool GetMaskBit(int pattern, int row, int col) => pattern switch
    {
        0 => ((row + col) & 1) == 0,
        1 => (row & 1) == 0,
        2 => col % 3 == 0,
        3 => (row + col) % 3 == 0,
        4 => ((row / 2 + col / 3) & 1) == 0,
        5 => (row * col) % 2 + (row * col) % 3 == 0,
        6 => ((row * col % 2 + row * col % 3) & 1) == 0,
        7 => (((row + col) % 2 + row * col % 3) & 1) == 0,
        _ => false,
    };

    /// <summary>
    /// Distributes interleaved codewords into per-block contiguous [data | ecc] layout, the exact inverse of <see cref="BinaryEncoders.BinaryInterleaver.InterleaveCodewords"/>.
    /// </summary>
    private static void DeinterleaveCodewords(ReadOnlySpan<byte> interleaved, Span<byte> blocks, in ECCInfo eccInfo)
    {
        var g1Blocks = eccInfo.BlocksInGroup1;
        var g1Cw = eccInfo.CodewordsInGroup1;
        var g2Blocks = eccInfo.BlocksInGroup2;
        var g2Cw = g2Blocks > 0 ? eccInfo.CodewordsInGroup2 : 0;
        var eccPerBlock = eccInfo.ECCPerBlock;
        var g1BlockLength = g1Cw + eccPerBlock;
        var g2BlockLength = g2Cw + eccPerBlock;
        var group2Base = g1Blocks * g1BlockLength;

        var pos = 0;

        // Data rows where every block contributes
        var common = g2Blocks > 0 ? Math.Min(g1Cw, g2Cw) : g1Cw;
        for (var i = 0; i < common; i++)
        {
            for (var b = 0; b < g1Blocks; b++)
                blocks[b * g1BlockLength + i] = interleaved[pos++];
            for (var b = 0; b < g2Blocks; b++)
                blocks[group2Base + b * g2BlockLength + i] = interleaved[pos++];
        }

        // Tail rows: only the group with longer blocks still has codewords
        for (var i = common; i < g1Cw; i++)
        {
            for (var b = 0; b < g1Blocks; b++)
                blocks[b * g1BlockLength + i] = interleaved[pos++];
        }
        for (var t = common; t < g2Cw; t++)
        {
            for (var b = 0; b < g2Blocks; b++)
                blocks[group2Base + b * g2BlockLength + t] = interleaved[pos++];
        }

        // ECC rows: all blocks have exactly eccPerBlock codewords
        for (var e = 0; e < eccPerBlock; e++)
        {
            for (var b = 0; b < g1Blocks; b++)
                blocks[b * g1BlockLength + g1Cw + e] = interleaved[pos++];
            for (var b = 0; b < g2Blocks; b++)
                blocks[group2Base + b * g2BlockLength + g2Cw + e] = interleaved[pos++];
        }
    }

    /// <summary>
    /// Gets the blocked-module bitmask for a version: the encoder's cached placement tables (<see cref="ModulePlacer.GetLayout"/>), built by its own function-pattern placement, so the decoder can never disagree with the encoder.
    /// </summary>
    private static byte[] GetBlockedMask(int version, int size)
    {
        var layout = ModulePlacer.GetLayout(version);
        if (layout.Size != size)
            throw new ArgumentException($"size {size} does not match version {version} ({layout.Size} modules)", nameof(size));
        return layout.BlockedMask;
    }
}
