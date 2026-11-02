using System.Text;
using TUnit.Assertions.Enums;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace FeatherQR.Tests;

/// <summary>
/// Bytes across an independent reader and writer, ZXing.Net: what the bytes calls write it reads as the same byte segment, and what it writes from bytes the bytes calls read back.
/// Every byte value is in the payload, so it is no text under any charset, and ZXing.Net's text of it is not what is compared.
/// Only assignments ZXing.Net has a charset for are written, since it refuses a symbol under any other.
/// </summary>
public class BytesZXingCrossTest
{
    private static byte[] EveryByteValue()
    {
        var payload = new byte[256];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)i;
        return payload;
    }

    [Test]
    [Arguments(-1)]
    [Arguments(3)]
    [Arguments(26)]
    public async Task BytesCreate_IsReadByZXing_AsOneByteSegmentOfTheBytes(int assignment)
    {
        int? eci = assignment < 0 ? null : assignment;
        var payload = EveryByteValue();
        var symbol = QRCodeGenerator.Create(payload, QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 0, Segmentation = QRSegmentation.Single }, eci);

        var result = new ZXing.QrCode.Internal.Decoder().decode(ToBitMatrix(symbol), null);

        await Assert.That(result).IsNotNull().Because($"version {symbol.Version}");
        await Assert.That(result!.ByteSegments.Count).IsEqualTo(1);
        await Assert.That(result.ByteSegments[0]).IsEquivalentTo(payload, CollectionOrdering.Matching);
    }

    /// <summary>ZXing.Net writes text it is told is ISO-8859-1 one byte a character, so a string of the chars U+0000 to U+00FF is the payload's bytes.</summary>
    [Test]
    public async Task ZXingBytes_AreReadByTheBytesCall()
    {
        var payload = EveryByteValue();
        var writer = new BarcodeWriterGeneric
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new QrCodeEncodingOptions { ErrorCorrection = ErrorCorrectionLevel.M, Margin = 4, CharacterSet = "ISO-8859-1" },
        };
        var matrix = writer.Encode(Encoding.Latin1.GetString(payload));
        var size = matrix.Width;
        var modules = new byte[size * size];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                modules[y * size + x] = matrix[x, y] ? (byte)1 : (byte)0;

        await Assert.That(QRCodeDecoder.TryDecodeBytes(modules, size, out var bytes, out var info)).IsTrue().Because($"status {info.Status}");
        await Assert.That(bytes).IsEquivalentTo(payload, CollectionOrdering.Matching);
    }

    private static BitMatrix ToBitMatrix(QRCodeData symbol)
    {
        var matrix = new BitMatrix(symbol.Size);
        for (var row = 0; row < symbol.Size; row++)
            for (var col = 0; col < symbol.Size; col++)
                matrix[col, row] = symbol[row, col];
        return matrix;
    }
}
