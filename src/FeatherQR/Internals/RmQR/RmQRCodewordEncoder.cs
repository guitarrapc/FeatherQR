using FeatherQR.Internals.BinaryEncoders;

namespace FeatherQR.Internals.RmQr;

/// <summary>
/// rMQR final message (ISO/IEC 23941 7.5-7.6): Reed-Solomon ECC per block over the
/// data codewords, then Standard-QR-style block interleaving (data round-robin,
/// ECC round-robin, zero remainder bits). Composes the shared kernels only
/// (<see cref="EccBinaryEncoder"/>, <see cref="BinaryInterleaver"/>); the block
/// structure comes from <see cref="RmQRConstants.GetEccInfo"/>. Allocation-free:
/// the per-block ECC scratch is a fixed stack budget.
/// </summary>
internal static class RmQRCodewordEncoder
{
    /// <summary>Largest total ECC codeword count of any version × ECC (R17x139-H: 6 blocks × 26).</summary>
    public const int MaxEccCodewords = 156;

    /// <summary>
    /// Bytes of the interleaved final message: total codewords plus one byte when
    /// the version has remainder bits (they are always zero).
    /// </summary>
    public static int GetFinalMessageSize(RmQRVersion version)
        => BinaryInterleaver.CalculateInterleavedSize(RmQRConstants.GetEccInfo(version, RmQREccLevel.M), RmQRConstants.GetRemainderBitCount(version));

    /// <summary>
    /// Computes per-block ECC for <paramref name="dataCodewords"/> (exactly the
    /// version × ECC data codeword count) and writes the interleaved final message
    /// (data, ECC, zeroed remainder tail) into <paramref name="output"/> of at least
    /// <see cref="GetFinalMessageSize"/> bytes.
    /// </summary>
    public static void AssembleFinalMessage(ReadOnlySpan<byte> dataCodewords, RmQRVersion version, RmQREccLevel eccLevel, Span<byte> output)
    {
        var info = RmQRConstants.GetEccInfo(version, eccLevel);
        var eccTotal = (info.BlocksInGroup1 + info.BlocksInGroup2) * info.ECCPerBlock;
        var outputSize = BinaryInterleaver.CalculateInterleavedSize(info, RmQRConstants.GetRemainderBitCount(version));
        if (dataCodewords.Length < info.TotalDataCodewords)
            throw new ArgumentException($"Data buffer too small: required {info.TotalDataCodewords}, got {dataCodewords.Length}", nameof(dataCodewords));
        if (output.Length < outputSize)
            throw new ArgumentException($"Output buffer too small: required {outputSize}, got {output.Length}", nameof(output));

        Span<byte> ecc = stackalloc byte[MaxEccCodewords];
        ecc = ecc.Slice(0, eccTotal);

        // Group 1 (shorter blocks) then group 2 (one more data codeword each); every
        // block has the same ECC codeword count.
        var dataOffset = 0;
        var eccOffset = 0;
        for (var b = 0; b < info.BlocksInGroup1; b++)
        {
            EccBinaryEncoder.CalculateECC(dataCodewords.Slice(dataOffset, info.CodewordsInGroup1), ecc.Slice(eccOffset, info.ECCPerBlock), info.ECCPerBlock);
            dataOffset += info.CodewordsInGroup1;
            eccOffset += info.ECCPerBlock;
        }
        for (var b = 0; b < info.BlocksInGroup2; b++)
        {
            EccBinaryEncoder.CalculateECC(dataCodewords.Slice(dataOffset, info.CodewordsInGroup2), ecc.Slice(eccOffset, info.ECCPerBlock), info.ECCPerBlock);
            dataOffset += info.CodewordsInGroup2;
            eccOffset += info.ECCPerBlock;
        }

        // Interleave into exactly the final-message window so the remainder tail is
        // the only byte beyond data + ECC and gets zeroed by the interleaver.
        BinaryInterleaver.InterleaveCodewords(dataCodewords.Slice(0, info.TotalDataCodewords), ecc, output.Slice(0, outputSize), info);
    }
}
