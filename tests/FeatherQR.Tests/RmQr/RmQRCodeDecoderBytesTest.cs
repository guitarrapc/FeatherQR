using System.Text;
using FeatherQR.Internals.RmQR;
using FeatherQR.SkiaSharp;
using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// The bytes calls of <see cref="RmQRCodeDecoder"/> and <see cref="RmQRCodeImageDecoder"/>, and the ECI assignment both outputs report.
/// </summary>
public class RmQRCodeDecoderBytesTest
{
    private const RmQRVersion Version = RmQRVersion.R13x99;

    private static readonly byte[] binaryPayload = [0x00, 0xFF, 0x10, 0x80];

    private static readonly int width = RmQRConstants.GetWidth(Version);
    private static readonly int height = RmQRConstants.GetHeight(Version);

    /// <summary>ECI 899 (binary data), then the payload in one Byte segment: content the text calls refuse.</summary>
    private static byte[] Eci899Symbol()
        => PayloadSymbols.RmQr(Version, RmQREccLevel.M, (0b111, 3), ((0b10 << 14) | 899, 16),
            (0b011, 3), (binaryPayload.Length, RmQRConstants.GetCountIndicatorLength(Version, Internals.EncodingMode.Byte)),
            (binaryPayload[0], 8), (binaryPayload[1], 8), (binaryPayload[2], 8), (binaryPayload[3], 8));

    [Test]
    [Arguments("hello", -1)]
    [Arguments("こんにちは", 26)]
    public async Task GeneratorText_BytesAreWhatItWrote_AndBothCallsReportTheAssignment(string text, int assignment)
    {
        var data = RmQRCodeGenerator.Create(text, RmQREccLevel.M);
        int? expectedAssignment = assignment < 0 ? null : assignment;

        await Assert.That(RmQRCodeDecoder.TryDecode(data, out _, out var textInfo)).IsTrue();
        await Assert.That(textInfo.EciAssignment).IsEqualTo(expectedAssignment);

        await Assert.That(RmQRCodeDecoder.TryDecodeBytes(data, out var bytes, out var info)).IsTrue();
        await Assert.That(bytes).IsEquivalentTo(Encoding.UTF8.GetBytes(text), CollectionOrdering.Matching);
        await Assert.That(info.EciAssignment).IsEqualTo(expectedAssignment);
    }

    [Test]
    public async Task AssignmentWithoutACharset_TextFailsAndNamesIt_BytesRead()
    {
        var modules = Eci899Symbol();

        await Assert.That(RmQRCodeDecoder.TryDecode(modules, width, height, out _, out var textInfo)).IsFalse();
        await Assert.That(textInfo.Status).IsEqualTo(DecodeStatus.UnsupportedContent);
        await Assert.That(textInfo.EciAssignment).IsEqualTo(899);

        await Assert.That(RmQRCodeDecoder.TryDecodeBytes(modules, width, height, out var bytes, out var info)).IsTrue();
        await Assert.That(bytes).IsEquivalentTo(binaryPayload, CollectionOrdering.Matching);
        await Assert.That(info.EciAssignment).IsEqualTo(899);
        await Assert.That(info.Version).IsEqualTo(Version);
    }

    [Test]
    public async Task SpanForm_ExactRoomReads_OneShortIsDestinationTooSmall()
    {
        var modules = Eci899Symbol();

        var exact = new byte[binaryPayload.Length];
        await Assert.That(RmQRCodeDecoder.TryDecodeBytes(modules, width, height, exact, out var written, out _)).IsTrue();
        await Assert.That(exact).IsEquivalentTo(binaryPayload, CollectionOrdering.Matching);
        await Assert.That(written).IsEqualTo(binaryPayload.Length);

        await Assert.That(RmQRCodeDecoder.TryDecodeBytes(modules, width, height, new byte[binaryPayload.Length - 1], out written, out var info)).IsFalse();
        await Assert.That(written).IsEqualTo(0);
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.DestinationTooSmall);
    }

    [Test]
    public async Task NoSymbol_IsFalse_WithNoBytes()
    {
        await Assert.That(RmQRCodeDecoder.TryDecodeBytes(new byte[width * height], width, height, out var bytes, out var info)).IsFalse();
        await Assert.That(bytes).IsEmpty();
        await Assert.That(info.EciAssignment).IsNull();
    }

    [Test]
    public async Task Image_BothForms_AndBitmap_ReadTheBytes()
    {
        var modules = Eci899Symbol();
        var (luminance, imageWidth, imageHeight) = PayloadSymbols.Render(modules, width, height);

        await Assert.That(RmQRCodeDecoder.TryDecodeImageBytes(luminance, imageWidth, imageHeight, out var bytes, out var info)).IsTrue();
        await Assert.That(bytes).IsEquivalentTo(binaryPayload, CollectionOrdering.Matching);
        await Assert.That(info.EciAssignment).IsEqualTo(899);
        await Assert.That(info.Corners.IsEmpty).IsFalse();

        var destination = new byte[RmQRCodeDecoder.GetMaxDecodedLength(Version)];
        await Assert.That(RmQRCodeDecoder.TryDecodeImageBytes(luminance, imageWidth, imageHeight, destination, out var written, out _)).IsTrue();
        await Assert.That(destination.AsSpan(0, written).ToArray()).IsEquivalentTo(binaryPayload, CollectionOrdering.Matching);

        using var bitmap = PayloadSymbols.Bitmap(modules, width, height);
        await Assert.That(RmQRCodeImageDecoder.TryDecodeBytes(bitmap, out bytes, out info)).IsTrue();
        await Assert.That(bytes).IsEquivalentTo(binaryPayload, CollectionOrdering.Matching);
        await Assert.That(info.EciAssignment).IsEqualTo(899);
    }
}
