using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace FeatherQR.Tests;

/// <summary>
/// Cross-validation against ZXing.Net: QR codes produced by a completely independent
/// encoder must decode correctly with this library's decoder. Complements the
/// existing ZXing-decodes-our-output tests (QRCodeDecodabilityTest) in the other direction.
/// </summary>
public class QRCodeDecoderZXingCrossTest
{
    [Test]
    [Arguments("0123456789")]
    [Arguments("HELLO WORLD $%*+-./:")]
    [Arguments("Hello, World! lowercase")]
    [Arguments("https://example.com/path?query=value")]
    public async Task Decode_ZXingEncoded_Ascii(string content)
        => await AssertDecodesZXingQr(content, ErrorCorrectionLevel.M);

    [Test]
    [Arguments("こんにちは世界")]
    [Arguments("你好世界")]
    [Arguments("Привет мир")]
    [Arguments("🎉 emoji content 🎊")]
    public async Task Decode_ZXingEncoded_Utf8(string content)
        => await AssertDecodesZXingQr(content, ErrorCorrectionLevel.M, characterSet: "UTF-8");

    [Test]
    [Arguments("Café")]
    [Arguments("Zürich Résumé")]
    public async Task Decode_ZXingEncoded_Iso8859_1(string content)
        => await AssertDecodesZXingQr(content, ErrorCorrectionLevel.M, characterSet: "ISO-8859-1");

    [Test]
    public async Task Decode_ZXingEncoded_AllEccLevels()
    {
        foreach (var (zxingLevel, expectedLevel) in new[]
        {
            (ErrorCorrectionLevel.L, QREccLevel.L),
            (ErrorCorrectionLevel.M, QREccLevel.M),
            (ErrorCorrectionLevel.Q, QREccLevel.Q),
            (ErrorCorrectionLevel.H, QREccLevel.H),
        })
        {
            var content = $"ecc level {zxingLevel}";
            var modules = EncodeWithZXing(content, zxingLevel, characterSet: null, out var size);

            await Assert.That(QRCodeDecoder.TryDecode(modules, size, out var decoded, out var info)).IsTrue().Because($"level={zxingLevel}, status={info.Status}");
            await Assert.That(decoded).IsEquivalentTo(content);
            await Assert.That(info.EccLevel).IsEquivalentTo(expectedLevel);
        }
    }

    [Test]
    public async Task Decode_ZXingEncoded_LargeContent_HigherVersion()
    {
        var content = string.Join(" ", Enumerable.Range(0, 60).Select(i => $"chunk{i:D3}"));
        var modules = EncodeWithZXing(content, ErrorCorrectionLevel.Q, characterSet: null, out var size);

        await Assert.That(QRCodeDecoder.TryDecode(modules, size, out var decoded, out var info)).IsTrue().Because($"status={info.Status}");
        await Assert.That(decoded).IsEquivalentTo(content);
        await Assert.That(info.Version > 5).IsTrue().Because($"expected a higher version, got {info.Version}");
    }

    private static async Task AssertDecodesZXingQr(string content, ErrorCorrectionLevel level, string? characterSet = null)
    {
        var modules = EncodeWithZXing(content, level, characterSet, out var size);

        await Assert.That(QRCodeDecoder.TryDecode(modules, size, out var decoded, out var info)).IsTrue().Because($"decode failed: status={info.Status}, version={info.Version}");
        await Assert.That(decoded).IsEquivalentTo(content);
    }

    private static byte[] EncodeWithZXing(string content, ErrorCorrectionLevel level, string? characterSet, out int size)
    {
        var options = new QrCodeEncodingOptions
        {
            ErrorCorrection = level,
            Margin = 4,
        };
        if (characterSet is not null)
        {
            options.CharacterSet = characterSet;
        }

        var writer = new BarcodeWriterGeneric
        {
            Format = BarcodeFormat.QR_CODE,
            Options = options,
        };

        BitMatrix matrix = writer.Encode(content);

        size = matrix.Width;
        var modules = new byte[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                modules[y * size + x] = matrix[x, y] ? (byte)1 : (byte)0;
            }
        }
        return modules;
    }
}
