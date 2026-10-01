using FeatherQR.Internals;
using FeatherQR.Internals.BinaryEncoders;
using FeatherQR.Internals.RmQR;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// What a matrix decode reports as <c>ErrorsCorrected</c> when a Reed-Solomon block does not read: the errors corrected in
/// the blocks before it, and none of the blocks after it, which are not decoded. Standard QR and rMQR share the block stage
/// that decides this (<see cref="Internals.BinaryDecoders.EccBlockDecoder"/>).
/// </summary>
/// <remarks>
/// A symbol is built from random data codewords, each block's ECC computed on them, then the data codewords of each block
/// changed as the case says; a block marked uncorrectable has every codeword changed, data and ECC, past what its ECC
/// codewords locate. The data is never parsed, since the decode stops at Reed-Solomon.
/// </remarks>
public class MatrixDecodeErrorsCorrectedTest
{
    private const int Uncorrectable = -1;

    [Test]
    [Arguments(new[] { 2, Uncorrectable, 1, 1 }, 2)]
    [Arguments(new[] { Uncorrectable, 1, 1, 1 }, 0)]
    [Arguments(new[] { 1, 2, 3, Uncorrectable }, 6)]
    public async Task StandardQR_DataUncorrectable_CountsTheBlocksBeforeTheFailingOne(int[] errors, int expected)
    {
        // Version 5-Q: 2 + 2 blocks, 15 and 16 data codewords, 18 ECC codewords each
        const int version = 5;
        var eccInfo = QRCodeConstants.GetEccInfo(version, QREccLevel.Q);
        await Assert.That(errors.Length).IsEqualTo(eccInfo.BlocksInGroup1 + eccInfo.BlocksInGroup2);
        var (data, ecc) = Codewords(eccInfo, errors, seed: 5);
        var codewords = new byte[BinaryInterleaver.CalculateInterleavedSize(eccInfo, QRCodeConstants.GetRemainderBits(version))];
        BinaryInterleaver.InterleaveCodewords(data, ecc, codewords, eccInfo);

        var size = 17 + 4 * version;
        var layout = ModulePlacer.GetLayout(version);
        var modules = new byte[size * size];
        layout.Template.AsSpan().CopyTo(modules);
        ModulePlacer.PlaceDataWords(modules, layout, codewords);
        var mask = ModulePlacer.MaskCode(modules, size, version, layout.BlockedMask, QREccLevel.Q);
        ModulePlacer.PlaceFormat(modules, size, QRCodeConstants.GetFormatBits(QREccLevel.Q, mask));

        var ok = QRCodeDecoder.TryDecode(modules, size, new char[256], out _, out var info);

        await Assert.That(ok).IsFalse();
        await Assert.That((info.Status, info.Version, info.ErrorsCorrected)).IsEqualTo((DecodeStatus.DataUncorrectable, version, expected));
    }

    [Test]
    [Arguments(new[] { 2, Uncorrectable, 1 }, 2)]
    [Arguments(new[] { Uncorrectable, 1, 1 }, 0)]
    [Arguments(new[] { 1, 2, Uncorrectable }, 3)]
    public async Task RmQR_DataUncorrectable_CountsTheBlocksBeforeTheFailingOne(int[] errors, int expected)
    {
        const RmQRVersion version = RmQRVersion.R17x139;
        var eccInfo = RmQRConstants.GetEccInfo(version, RmQREccLevel.H);
        var blocks = eccInfo.BlocksInGroup1 + eccInfo.BlocksInGroup2;
        var perBlock = new int[blocks];
        await Assert.That(errors.Length).IsLessThanOrEqualTo(blocks);
        errors.CopyTo(perBlock, 0); // blocks past the listed ones are clean
        var (data, ecc) = Codewords(eccInfo, perBlock, seed: 6);
        var finalMessage = new byte[RmQRCodewordEncoder.GetFinalMessageSize(version)];
        BinaryInterleaver.InterleaveCodewords(data, ecc, finalMessage, eccInfo);

        var width = RmQRConstants.GetWidth(version);
        var height = RmQRConstants.GetHeight(version);
        var modules = new byte[width * height];
        RmQRModulePlacer.PlaceSymbol(modules, version, RmQREccLevel.H, finalMessage);

        var ok = RmQRCodeDecoder.TryDecode(modules, width, height, new char[256], out _, out var info);

        await Assert.That(ok).IsFalse();
        await Assert.That((info.Status, info.Version, info.ErrorsCorrected)).IsEqualTo((DecodeStatus.DataUncorrectable, version, expected));
    }

    /// <summary>
    /// Random data codewords and each block's ECC over them, then block b's first <paramref name="errors"/>[b] data codewords
    /// changed, or for <see cref="Uncorrectable"/> every codeword of the block, data and ECC, each by a different value: a
    /// block of R17x139-H has 26 ECC codewords, which correct 13 errors, against 12 or 13 data codewords, so changing its data
    /// alone stays within what Reed-Solomon corrects.
    /// </summary>
    private static (byte[] Data, byte[] Ecc) Codewords(in ECCInfo eccInfo, int[] errors, int seed)
    {
        var blocks = eccInfo.BlocksInGroup1 + eccInfo.BlocksInGroup2;
        var data = new byte[eccInfo.TotalDataCodewords];
        new Random(seed).NextBytes(data);
        var ecc = new byte[blocks * eccInfo.ECCPerBlock];
        for (int b = 0, d = 0; b < blocks; b++)
        {
            var length = b < eccInfo.BlocksInGroup1 ? eccInfo.CodewordsInGroup1 : eccInfo.CodewordsInGroup2;
            var blockEcc = ecc.AsSpan(b * eccInfo.ECCPerBlock, eccInfo.ECCPerBlock);
            EccBinaryEncoder.CalculateECC(data.AsSpan(d, length), blockEcc, eccInfo.ECCPerBlock);
            if (errors[b] == Uncorrectable)
            {
                for (var k = 0; k < length; k++)
                    data[d + k] ^= (byte)(1 + k * 37 % 255);
                for (var k = 0; k < blockEcc.Length; k++)
                    blockEcc[k] ^= (byte)(1 + (k + length) * 37 % 255);
            }
            else
            {
                for (var k = 0; k < errors[b]; k++)
                    data[d + k] ^= 0xA5;
            }
            d += length;
        }
        return (data, ecc);
    }
}
