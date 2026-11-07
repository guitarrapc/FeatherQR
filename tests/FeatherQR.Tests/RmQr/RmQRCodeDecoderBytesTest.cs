using System.Text;
using FeatherQR.Internals.RmQR;
using FeatherQR.SkiaSharp;
using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// The bytes calls of <see cref="RmQRCodeDecoder"/> and <see cref="RmQRCodeDecoderExtensions"/>, and the ECI assignment both outputs report.
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

        var shortRoom = new byte[binaryPayload.Length - 1];
        shortRoom.AsSpan().Fill(0xA5);
        await Assert.That(RmQRCodeDecoder.TryDecodeBytes(modules, width, height, shortRoom, out written, out var info)).IsFalse();
        await Assert.That(written).IsEqualTo(0);
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.DestinationTooSmall);
        await Assert.That(info.EciAssignment).IsNull().Because("the symbol declares 899, and only a read or a refused content reports it");
        await Assert.That(shortRoom.All(b => b == 0xA5)).IsTrue().Because("the destination is left as it was");
    }

    /// <summary>No symbol holds more bytes than R17x139 does, so a destination far longer than that takes no buffer as long as itself.</summary>
    [Test]
    public async Task SpanForms_DestinationFarLargerThanAnySymbol_TakeNoBufferThatLong()
    {
        var modules = Eci899Symbol();
        var (luminance, imageWidth, imageHeight) = PayloadSymbols.Render(modules, width, height);
        var destination = new byte[16 << 20];

        var (read, readImage, allocated) = DecodeBothSpanForms(modules, luminance, imageWidth, imageHeight, destination);

        await Assert.That(read).IsTrue();
        await Assert.That(readImage).IsTrue();
        await Assert.That(allocated).IsLessThan(destination.Length).Because("a char buffer as long as the destination takes twice its length");
    }

    /// <summary>
    /// The text call decodes a restored core as it stands, so the bytes call reads every matrix with a light edge that the text call reads.
    /// A search for the dark border would cut such an edge off, since rMQR finds its core by the dark modules every edge carries.
    /// </summary>
    [Test]
    public async Task RmQRCodeData_WithALightEdge_IsReadAsTheTextCallReadsIt()
    {
        var readByText = 0;
        foreach (var level in new[] { RmQREccLevel.M, RmQREccLevel.H })
        {
            for (var edge = 0; edge < 4; edge++)
            {
                var restored = WithLightEdge(RmQRCodeGenerator.Create("HELLO BYTES", level, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R17x43 }), edge);
                if (restored is null || !RmQRCodeDecoder.TryDecode(restored, out var text, out _))
                    continue;
                readByText++;

                await Assert.That(RmQRCodeDecoder.TryDecodeBytes(restored, out var bytes, out var info)).IsTrue();
                await Assert.That(info.Status).IsEqualTo(DecodeStatus.Success);
                await Assert.That(bytes).IsEquivalentTo(Encoding.ASCII.GetBytes(text), CollectionOrdering.Matching);
            }
        }

        await Assert.That(readByText).IsGreaterThan(0).Because("the premise: some light edges leave the matrix readable as text");
    }

    [Test]
    public async Task NullData_Throws_AsTheTextCallDoes()
    {
        await Assert.That(() => RmQRCodeDecoder.TryDecodeBytes((RmQRCodeData)null!, out _, out _)).ThrowsExactly<ArgumentNullException>();
        await Assert.That(() => RmQRCodeDecoderExtensions.TryDecodeBytes((global::SkiaSharp.SKBitmap)null!, out _, out _)).ThrowsExactly<ArgumentNullException>();
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
        await Assert.That(RmQRCodeDecoderExtensions.TryDecodeBytes(bitmap, out bytes, out info)).IsTrue();
        await Assert.That(bytes).IsEquivalentTo(binaryPayload, CollectionOrdering.Matching);
        await Assert.That(info.EciAssignment).IsEqualTo(899);
    }

    // Helpers

    private static (bool Read, bool ReadImage, long Allocated) DecodeBothSpanForms(byte[] modules, byte[] luminance, int imageWidth, int imageHeight, byte[] destination)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var read = RmQRCodeDecoder.TryDecodeBytes(modules, width, height, destination, out _, out _);
        var readImage = RmQRCodeDecoder.TryDecodeImageBytes(luminance, imageWidth, imageHeight, destination, out _, out _);
        return (read, readImage, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>The symbol restored from its raw data with one whole edge light: 0 top, 1 bottom, 2 left, 3 right. <c>null</c> when the restore refuses the format information that leaves.</summary>
    private static RmQRCodeData? WithLightEdge(RmQRCodeData data, int edge)
    {
        var raw = data.GetRawData();
        int coreWidth = raw[4];
        int coreHeight = raw[5];
        var length = edge < 2 ? coreWidth : coreHeight;
        for (var i = 0; i < length; i++)
        {
            var (row, col) = edge switch
            {
                0 => (0, i),
                1 => (coreHeight - 1, i),
                2 => (i, 0),
                _ => (i, coreWidth - 1),
            };
            var bit = row * coreWidth + col;
            raw[6 + (bit >> 3)] &= (byte)~(1 << (7 - (bit & 7)));
        }

        try
        {
            return new RmQRCodeData(raw, 0);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
