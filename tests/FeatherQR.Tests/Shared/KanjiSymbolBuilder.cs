using FeatherQR.Internals.BinaryEncoders;
using FeatherQR.Internals.MicroQR;
using FeatherQR.Internals.RmQR;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// The generators' pipelines after the data stream, assembled from their internal stages: a symbol from data codewords, for a stream the test wrote itself.
/// The Standard QR and Micro QR mask is pinned, so the result compares module for module with a generator's symbol made with the same <c>MaskPattern</c>; rMQR has one mask.
/// Every symbol is the core only, row-major, one byte per module.
/// </summary>
internal static class KanjiSymbolBuilder
{
    public static byte[] StandardQr(byte[] data, int version, QREccLevel ecc, int mask)
    {
        var eccInfo = QRCodeConstants.GetEccInfo(version, ecc);
        var eccCodewords = new byte[(eccInfo.BlocksInGroup1 + eccInfo.BlocksInGroup2) * eccInfo.ECCPerBlock];
        var dataOffset = 0;
        var eccOffset = 0;
        for (var block = 0; block < eccInfo.BlocksInGroup1 + eccInfo.BlocksInGroup2; block++)
        {
            var length = block < eccInfo.BlocksInGroup1 ? eccInfo.CodewordsInGroup1 : eccInfo.CodewordsInGroup2;
            EccBinaryEncoder.CalculateECC(data.AsSpan(dataOffset, length), eccCodewords.AsSpan(eccOffset, eccInfo.ECCPerBlock), eccInfo.ECCPerBlock);
            dataOffset += length;
            eccOffset += eccInfo.ECCPerBlock;
        }

        var interleaved = new byte[BinaryInterleaver.CalculateInterleavedSize(eccInfo, QRCodeConstants.GetRemainderBits(version))];
        BinaryInterleaver.InterleaveCodewords(data, eccCodewords, interleaved, eccInfo);

        var size = 17 + 4 * version;
        var layout = ModulePlacer.GetLayout(version);
        var modules = new byte[size * size];
        layout.Template.AsSpan().CopyTo(modules);
        ModulePlacer.PlaceDataWords(modules, layout, interleaved);
        ModulePlacer.ApplyMaskPattern(modules, size, layout.BlockedMask, mask);
        ModulePlacer.PlaceFormat(modules, size, QRCodeConstants.GetFormatBits(ecc, mask));
        if (version >= 7)
            ModulePlacer.PlaceVersion(modules, size, QRCodeConstants.GetVersionBits(version));
        return modules;
    }

    public static byte[] MicroQr(byte[] data, MicroQRVersion version, MicroQREccLevel ecc, int mask)
    {
        var eccCount = MicroQRConstants.GetEccCodewordCount(version, ecc);
        var eccCodewords = new byte[eccCount];
        EccBinaryEncoder.CalculateECC(data, eccCodewords, eccCount);

        var size = MicroQRConstants.SizeFromVersion(version);
        var modules = new byte[size * size];
        MicroQRModulePlacer.PlaceSymbol(modules, size, data, eccCodewords, MicroQRConstants.GetDataBitCapacity(version, ecc), version, ecc, mask);
        return modules;
    }

    public static byte[] RmQr(byte[] data, RmQRVersion version, RmQREccLevel ecc)
    {
        var finalMessage = new byte[RmQRCodewordEncoder.GetFinalMessageSize(version)];
        RmQRCodewordEncoder.AssembleFinalMessage(data, version, ecc, finalMessage);

        var modules = new byte[RmQRConstants.GetWidth(version) * RmQRConstants.GetHeight(version)];
        RmQRModulePlacer.PlaceSymbol(modules, version, ecc, finalMessage);
        return modules;
    }

    public static string DecodeStandardQr(byte[] modules, int version)
    {
        var destination = new char[QRMatrixDecoder.GetMaxCharCount(version)];
        var status = QRMatrixDecoder.DecodeMatrix(modules, 17 + 4 * version, destination, out var written, out _);
        return status == DecodeStatus.Success ? new string(destination, 0, written) : throw new InvalidOperationException($"decode failed: {status}");
    }

    public static string DecodeMicroQr(byte[] modules, MicroQRVersion version)
    {
        var destination = new char[MicroQRMatrixDecoder.GetMaxCharCount(version)];
        var status = MicroQRMatrixDecoder.DecodeMatrix(modules, MicroQRConstants.SizeFromVersion(version), destination, out var written, out _);
        return status == DecodeStatus.Success ? new string(destination, 0, written) : throw new InvalidOperationException($"decode failed: {status}");
    }

    public static string DecodeRmQr(byte[] modules, RmQRVersion version)
    {
        var destination = new char[RmQRMatrixDecoder.GetMaxCharCount(version)];
        var status = RmQRMatrixDecoder.DecodeMatrix(modules, RmQRConstants.GetWidth(version), RmQRConstants.GetHeight(version), destination, out var written, out _);
        return status == DecodeStatus.Success ? new string(destination, 0, written) : throw new InvalidOperationException($"decode failed: {status}");
    }
}
