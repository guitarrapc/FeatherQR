using System.Text;
using FeatherQR.SkiaSharp;
using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// The bytes calls of <see cref="MicroQRCodeDecoder"/> and <see cref="MicroQRCodeImageDecoder"/>. Micro QR has no ECI, so the bytes are the undeclared Byte segments as written.
/// </summary>
public class MicroQRCodeDecoderBytesTest
{
    private static readonly byte[] payload = [0xC3, 0x28, 0xFF];

    /// <summary>M4-L, one Byte segment of bytes that are not UTF-8: the text call reads them by a guess, the bytes call as they are.</summary>
    private static byte[] RawBytesSymbol()
        => PayloadSymbols.MicroQrM4(MicroQREccLevel.L, (0b010, 3), (payload.Length, 5), (payload[0], 8), (payload[1], 8), (payload[2], 8));

    [Test]
    [Arguments("ABC123")]
    [Arguments("日本")]
    public async Task GeneratorText_BytesAreWhatItWrote(string text)
    {
        var data = MicroQRCodeGenerator.Create(text, MicroQREccLevel.L);

        await Assert.That(MicroQRCodeDecoder.TryDecodeBytes(data, out var bytes, out var info)).IsTrue();
        await Assert.That(bytes).IsEquivalentTo(Encoding.UTF8.GetBytes(text), CollectionOrdering.Matching);
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.Success);
    }

    [Test]
    public async Task UndeclaredBytes_AreGivenAsWritten_WhereTheTextGuesses()
    {
        var modules = RawBytesSymbol();

        await Assert.That(MicroQRCodeDecoder.TryDecode(modules, 17, out var text, out _)).IsTrue();
        await Assert.That(text).IsEqualTo("Ã(ÿ").Because("the premise: the text reads the bytes as ISO-8859-1");

        await Assert.That(MicroQRCodeDecoder.TryDecodeBytes(modules, 17, out var bytes, out var info)).IsTrue();
        await Assert.That(bytes).IsEquivalentTo(payload, CollectionOrdering.Matching);
        await Assert.That(info.Version).IsEqualTo(MicroQRVersion.M4);
    }

    [Test]
    public async Task SpanForm_ExactRoomReads_OneShortIsDestinationTooSmall()
    {
        var modules = RawBytesSymbol();

        var exact = new byte[payload.Length];
        await Assert.That(MicroQRCodeDecoder.TryDecodeBytes(modules, 17, exact, out var written, out _)).IsTrue();
        await Assert.That(written).IsEqualTo(payload.Length);
        await Assert.That(exact).IsEquivalentTo(payload, CollectionOrdering.Matching);

        await Assert.That(MicroQRCodeDecoder.TryDecodeBytes(modules, 17, new byte[payload.Length - 1], out written, out var info)).IsFalse();
        await Assert.That(written).IsEqualTo(0);
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.DestinationTooSmall);
    }

    [Test]
    public async Task NoSymbol_IsFalse_WithNoBytes()
    {
        await Assert.That(MicroQRCodeDecoder.TryDecodeBytes(new byte[17 * 17], 17, out var bytes, out _)).IsFalse();
        await Assert.That(bytes).IsEmpty();
    }

    [Test]
    public async Task Image_BothForms_AndBitmap_ReadTheBytes()
    {
        var modules = RawBytesSymbol();
        var (luminance, width, height) = PayloadSymbols.Render(modules, 17, 17);

        await Assert.That(MicroQRCodeDecoder.TryDecodeImageBytes(luminance, width, height, out var bytes, out var info)).IsTrue();
        await Assert.That(bytes).IsEquivalentTo(payload, CollectionOrdering.Matching);
        await Assert.That(info.Corners.IsEmpty).IsFalse();

        var destination = new byte[MicroQRCodeDecoder.GetMaxDecodedLength(MicroQRVersion.M4)];
        await Assert.That(MicroQRCodeDecoder.TryDecodeImageBytes(luminance, width, height, destination, out var written, out _)).IsTrue();
        await Assert.That(destination.AsSpan(0, written).ToArray()).IsEquivalentTo(payload, CollectionOrdering.Matching);

        using var bitmap = PayloadSymbols.Bitmap(modules, 17, 17);
        await Assert.That(MicroQRCodeImageDecoder.TryDecodeBytes(bitmap, out bytes, out _)).IsTrue();
        await Assert.That(bytes).IsEquivalentTo(payload, CollectionOrdering.Matching);
    }
}
