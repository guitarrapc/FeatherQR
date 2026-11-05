using System.Text;
using FeatherQR.SkiaSharp;
using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// The bytes calls of <see cref="MicroQRCodeDecoder"/> and <see cref="MicroQRCodeDecoderExtensions"/>. Micro QR has no ECI, so the bytes are the undeclared Byte segments as written.
/// </summary>
public class MicroQRCodeDecoderBytesTest
{
    private static readonly byte[] payload = [0xE3, 0x81, 0x82];

    /// <summary>
    /// M4-L, one Byte segment the text call reads by a guess, as UTF-8, and the bytes call as it is.
    /// The text is one char, あ, which is not the bytes one a char, so each bytes call is seen to give bytes.
    /// </summary>
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
        await Assert.That(text).IsEqualTo("あ").Because("the premise: the text reads the bytes as UTF-8");

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
        await Assert.That(MicroQRCodeDecoderExtensions.TryDecodeBytes(bitmap, out bytes, out _)).IsTrue();
        await Assert.That(bytes).IsEquivalentTo(payload, CollectionOrdering.Matching);
    }

    /// <summary>No symbol holds more bytes than M4 does, so a destination far longer than that takes no buffer as long as itself.</summary>
    [Test]
    public async Task SpanForms_DestinationFarLargerThanAnySymbol_TakeNoBufferThatLong()
    {
        var modules = RawBytesSymbol();
        var (luminance, width, height) = PayloadSymbols.Render(modules, 17, 17);
        var destination = new byte[16 << 20];

        var (read, readImage, allocated) = DecodeBothSpanForms(modules, luminance, width, height, destination);

        await Assert.That(read).IsTrue();
        await Assert.That(readImage).IsTrue();
        await Assert.That(allocated).IsLessThan(destination.Length).Because("a char buffer as long as the destination takes twice its length");
    }

    /// <summary>The text call decodes a restored core as it stands, so the bytes call reads every matrix with a light edge that the text call reads.</summary>
    [Test]
    public async Task MicroQRCodeData_WithALightEdge_IsReadAsTheTextCallReadsIt()
    {
        var readByText = 0;
        foreach (var level in new[] { MicroQREccLevel.L, MicroQREccLevel.M, MicroQREccLevel.Q })
        {
            for (var edge = 0; edge < 4; edge++)
            {
                var restored = WithLightEdge(MicroQRCodeGenerator.Create("HELLO12", level), edge);
                if (restored is null || !MicroQRCodeDecoder.TryDecode(restored, out var text, out _))
                    continue;
                readByText++;

                await Assert.That(MicroQRCodeDecoder.TryDecodeBytes(restored, out var bytes, out var info)).IsTrue();
                await Assert.That(info.Status).IsEqualTo(DecodeStatus.Success);
                await Assert.That(bytes).IsEquivalentTo(Encoding.ASCII.GetBytes(text), CollectionOrdering.Matching);
            }
        }

        await Assert.That(readByText).IsGreaterThan(0).Because("the premise: some light edges leave the matrix readable as text");
    }

    [Test]
    public async Task NullData_Throws_AsTheTextCallDoes()
    {
        await Assert.That(() => MicroQRCodeDecoder.TryDecodeBytes((MicroQRCodeData)null!, out _, out _)).ThrowsExactly<ArgumentNullException>();
        await Assert.That(() => MicroQRCodeDecoderExtensions.TryDecodeBytes((global::SkiaSharp.SKBitmap)null!, out _, out _)).ThrowsExactly<ArgumentNullException>();
    }

    // Helpers

    private static (bool Read, bool ReadImage, long Allocated) DecodeBothSpanForms(byte[] modules, byte[] luminance, int width, int height, byte[] destination)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var read = MicroQRCodeDecoder.TryDecodeBytes(modules, 17, destination, out _, out _);
        var readImage = MicroQRCodeDecoder.TryDecodeImageBytes(luminance, width, height, destination, out _, out _);
        return (read, readImage, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>The symbol restored from its raw data with one whole edge light: 0 top, 1 bottom, 2 left, 3 right. <c>null</c> when the restore refuses the format information that leaves.</summary>
    private static MicroQRCodeData? WithLightEdge(MicroQRCodeData data, int edge)
    {
        var raw = data.GetRawData();
        int size = raw[4];
        for (var i = 0; i < size; i++)
        {
            var (row, col) = edge switch
            {
                0 => (0, i),
                1 => (size - 1, i),
                2 => (i, 0),
                _ => (i, size - 1),
            };
            var bit = row * size + col;
            raw[6 + (bit >> 3)] &= (byte)~(1 << (7 - (bit & 7)));
        }

        try
        {
            return new MicroQRCodeData(raw, 0);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
