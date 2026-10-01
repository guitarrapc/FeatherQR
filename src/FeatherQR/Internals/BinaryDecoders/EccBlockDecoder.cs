namespace FeatherQR.Internals.BinaryDecoders;

/// <summary>
/// The Reed-Solomon block stage of the matrix decoders, the inverse of what the encoders do after their data codewords:
/// the codeword stream is deinterleaved into its blocks (the inverse of <see cref="BinaryEncoders.BinaryInterleaver"/>), each block is corrected, and the data codewords are gathered in block order.
/// </summary>
/// <remarks>
/// Shared by Standard QR and rMQR, which interleave identically: the layout depends only on the <see cref="ECCInfo"/> block structure.
/// Micro QR has one block and a half codeword at the end of the data on M1 and M3, and corrects its block itself.
/// Until 2026-09-29 Standard QR deinterleaved the whole stream and rMQR gathered each block from it by index, correcting it before gathering the next.
/// </remarks>
internal static class EccBlockDecoder
{
    /// <summary>
    /// Deinterleaves <paramref name="codewords"/> into <paramref name="blocks"/>, corrects each block, and writes the corrected data codewords over the front of <paramref name="codewords"/>, in block order.
    /// </summary>
    /// <param name="codewords">The interleaved stream, data then ECC codewords (<see cref="ECCInfo.TotalDataCodewords"/> plus the ECC codewords of every block). On success its first <see cref="ECCInfo.TotalDataCodewords"/> bytes are the corrected data.</param>
    /// <param name="blocks">Work space as long as the stream; it holds each block's data then ECC codewords, group 1 first.</param>
    /// <param name="eccInfo">The block structure.</param>
    /// <param name="correctionCapacity">The most errors a block may correct and still be read. A block Reed-Solomon corrects with more is refused, which is how a symbology reserves misdecode protection codewords; the full strength is <see cref="ECCInfo.ECCPerBlock"/> / 2, which Reed-Solomon never exceeds.</param>
    /// <param name="errorsCorrected">The errors corrected over every block. On failure, those of the blocks before the one that failed, plus that block's own when the capacity refused it.</param>
    /// <returns>Whether every block was read. It stops at the first block that is not.</returns>
    public static bool TryCorrect(Span<byte> codewords, Span<byte> blocks, in ECCInfo eccInfo, int correctionCapacity, out int errorsCorrected)
    {
        Deinterleave(codewords, blocks, eccInfo);

        var totalBlocks = eccInfo.BlocksInGroup1 + eccInfo.BlocksInGroup2;
        errorsCorrected = 0;
        var dataOffset = 0;
        var blockOffset = 0;
        for (var b = 0; b < totalBlocks; b++)
        {
            var dataCodewords = b < eccInfo.BlocksInGroup1 ? eccInfo.CodewordsInGroup1 : eccInfo.CodewordsInGroup2;
            var blockLength = dataCodewords + eccInfo.ECCPerBlock;
            var block = blocks.Slice(blockOffset, blockLength);

            if (!EccBinaryDecoder.TryCorrect(block, eccInfo.ECCPerBlock, out var blockErrors) || blockErrors > correctionCapacity)
            {
                // A block Reed-Solomon could not correct reports none of its own (TryCorrect leaves the count 0).
                errorsCorrected += blockErrors;
                return false;
            }

            errorsCorrected += blockErrors;
            block.Slice(0, dataCodewords).CopyTo(codewords.Slice(dataOffset));
            dataOffset += dataCodewords;
            blockOffset += blockLength;
        }
        return true;
    }

    /// <summary>
    /// Distributes interleaved codewords into each block's data then ECC codewords, group 1 first: the exact inverse of <see cref="BinaryEncoders.BinaryInterleaver.InterleaveCodewords"/>.
    /// </summary>
    internal static void Deinterleave(ReadOnlySpan<byte> interleaved, Span<byte> blocks, in ECCInfo eccInfo)
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
}
