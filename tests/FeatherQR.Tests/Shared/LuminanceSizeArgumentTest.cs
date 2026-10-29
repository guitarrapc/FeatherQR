namespace FeatherQR.Tests;

/// <summary>
/// The luminance reads of the three symbologies, and the read for every symbology, refuse a width or height below one by naming it, apart from a buffer too short for the size.
/// </summary>
public class LuminanceSizeArgumentTest
{
    private static (string Read, Action Call)[] Calls(int width, int height)
    {
        var luminance = new byte[100];
        return
        [
            ("QR TryDecodeImage", () => QRCodeDecoder.TryDecodeImage(luminance, width, height, out _, out _)),
            ("QR TryDecodeImage span", () => QRCodeDecoder.TryDecodeImage(luminance, width, height, new char[64], out _, out _)),
            ("QR DecodeImageSymbols", () => QRCodeDecoder.DecodeImageSymbols(luminance, width, height)),
            ("QR DecodeImageSymbols span", () => QRCodeDecoder.DecodeImageSymbols(luminance, width, height, new QRCodeDecodeResult[4])),
            ("Micro QR TryDecodeImage", () => MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, out _, out _)),
            ("Micro QR TryDecodeImage span", () => MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, new char[64], out _, out _)),
            ("Micro QR DecodeImageSymbols", () => MicroQRCodeDecoder.DecodeImageSymbols(luminance, width, height)),
            ("Micro QR DecodeImageSymbols span", () => MicroQRCodeDecoder.DecodeImageSymbols(luminance, width, height, new MicroQRCodeDecodeResult[4])),
            ("rMQR TryDecodeImage", () => RmQRCodeDecoder.TryDecodeImage(luminance, width, height, out _, out _)),
            ("rMQR TryDecodeImage span", () => RmQRCodeDecoder.TryDecodeImage(luminance, width, height, new char[64], out _, out _)),
            ("rMQR DecodeImageSymbols", () => RmQRCodeDecoder.DecodeImageSymbols(luminance, width, height)),
            ("rMQR DecodeImageSymbols span", () => RmQRCodeDecoder.DecodeImageSymbols(luminance, width, height, new RmQRCodeDecodeResult[4])),
            ("Every symbology DecodeImageSymbols", () => SymbolDecoder.DecodeImageSymbols(luminance, width, height)),
            ("Every symbology DecodeImageSymbols span", () => SymbolDecoder.DecodeImageSymbols(luminance, width, height, new SymbolDecodeResult[4])),
        ];
    }

    [Test]
    [Arguments(0, 10, "width")]
    [Arguments(-3, 10, "width")]
    [Arguments(10, 0, "height")]
    [Arguments(10, -1, "height")]
    public async Task SizeBelowOne_ThrowsNamingIt(int width, int height, string parameter)
    {
        // The buffer holds 100 bytes, as many as a 10 x 10 image, so only the size is wrong
        foreach (var (read, call) in Calls(width, height))
        {
            var thrown = Assert.Throws<ArgumentOutOfRangeException>(call);
            await Assert.That(thrown.ParamName).IsEqualTo(parameter).Because(read);
        }
    }

    [Test]
    public async Task BufferShorterThanTheSize_ThrowsNamingTheBuffer()
    {
        foreach (var (read, call) in Calls(11, 10))
        {
            var thrown = Assert.Throws<ArgumentException>(call);
            await Assert.That(thrown.GetType()).IsEqualTo(typeof(ArgumentException)).Because(read);
            await Assert.That(thrown.ParamName).IsEqualTo("luminance").Because(read);
        }
    }
}
