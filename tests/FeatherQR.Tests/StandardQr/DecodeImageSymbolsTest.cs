using FeatherQR.Internals.StandardQR;
using FeatherQR.SkiaSharp;
using FeatherQR.SkiaSharp.Internals;
using SkiaSharp;
using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// The public read for every symbol of a grayscale image, in its array and span forms, and the result types the reads return.
/// </summary>
public class DecodeImageSymbolsTest
{
    /// <summary>Codes on a white page, each drawn whole with its quiet zone, row by row.</summary>
    internal static (SKBitmap Sheet, string[] Texts) Sheet(int across, int down)
    {
        const int Side = 150;
        var sheet = new SKBitmap(across * Side, down * Side);
        using var canvas = new SKCanvas(sheet);
        canvas.Clear(SKColors.White);
        var texts = new string[across * down];
        for (var i = 0; i < texts.Length; i++)
        {
            texts[i] = $"FQR SHEET {i}";
            using var code = new QRCodeImageBuilder(texts[i]).WithSize(Side, Side).ToImage();
            canvas.DrawImage(code, i % across * Side, i / across * Side, SKSamplingOptions.Default);
        }
        return (sheet, texts);
    }

    internal static byte[] Luminance(SKBitmap bitmap)
    {
        var luminance = new byte[bitmap.Width * bitmap.Height];
        BitmapLuminanceConverter.Convert(bitmap, luminance);
        return luminance;
    }

    [Test]
    public async Task Array_SheetOfMoreThanItsFirstRoom_ReadsEverySymbolOnce()
    {
        // Twelve codes, more than the array form's room holds before it grows
        var (sheet, texts) = Sheet(4, 3);
        using var _ = sheet;
        var luminance = Luminance(sheet);

        var results = QRCodeDecoder.DecodeImageSymbols(luminance, sheet.Width, sheet.Height);

        await Assert.That(results.Select(static r => r.Text)).IsEquivalentTo(texts, CollectionOrdering.Any);
        await Assert.That(results.All(static r => r.Success && r.Info.Status == DecodeStatus.Success)).IsTrue();
    }

    [Test]
    public async Task Array_ReadsWhatTheSpanFormReadsWithRoomForAll()
    {
        var (sheet, _) = Sheet(4, 3);
        using var __ = sheet;
        var luminance = Luminance(sheet);
        var room = new QRCodeDecodeResult[64];

        var count = QRCodeDecoder.DecodeImageSymbols(luminance, sheet.Width, sheet.Height, room);
        var results = QRCodeDecoder.DecodeImageSymbols(luminance, sheet.Width, sheet.Height);

        await Assert.That(results).IsEquivalentTo(room.AsSpan(0, count).ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Span_RoomForOne_HoldsTheFirstSymbolTheArrayHolds()
    {
        var (sheet, _) = Sheet(3, 2);
        using var __ = sheet;
        var luminance = Luminance(sheet);
        var room = new QRCodeDecodeResult[1];

        var count = QRCodeDecoder.DecodeImageSymbols(luminance, sheet.Width, sheet.Height, room);
        var results = QRCodeDecoder.DecodeImageSymbols(luminance, sheet.Width, sheet.Height);

        await Assert.That(count).IsEqualTo(1);
        await Assert.That(room[0]).IsEqualTo(results[0]);
    }

    [Test]
    public async Task Span_EmptyRoom_ReadsNothing()
    {
        var (sheet, _) = Sheet(2, 1);
        using var __ = sheet;

        var count = QRCodeDecoder.DecodeImageSymbols(Luminance(sheet), sheet.Width, sheet.Height, Span<QRCodeDecodeResult>.Empty);

        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task ImageWithNoSymbol_ReadsNone()
    {
        var blank = new byte[300 * 200];
        blank.AsSpan().Fill(255);
        var room = new QRCodeDecodeResult[4];

        await Assert.That(QRCodeDecoder.DecodeImageSymbols(blank, 300, 200)).IsEmpty();
        await Assert.That(QRCodeDecoder.DecodeImageSymbols(blank, 300, 200, room)).IsEqualTo(0);
    }

    [Test]
    [Arguments(10, 10, 99)]
    public async Task BufferThatDoesNotFitTheDimensions_Throws(int width, int height, int length)
    {
        var luminance = new byte[length];

        Assert.Throws<ArgumentException>(() => QRCodeDecoder.DecodeImageSymbols(luminance, width, height));
        Assert.Throws<ArgumentException>(() => QRCodeDecoder.DecodeImageSymbols(luminance, width, height, new QRCodeDecodeResult[4]));
    }

    [Test]
    public async Task DefaultResult_IsNotASuccess()
    {
        // DecodeStatus.Success is 0, so a default result's status reads as a success, which Success must not follow
        await Assert.That(default(QRCodeDecodeResult).Info.Status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(default(QRCodeDecodeResult).Success).IsFalse();
        await Assert.That(default(MicroQRCodeDecodeResult).Success).IsFalse();
        await Assert.That(default(RmQRCodeDecodeResult).Success).IsFalse();
    }

    [Test]
    public async Task InternalEntry_WritesTheResultsThePublicSpanFormReturns()
    {
        var (sheet, _) = Sheet(3, 2);
        using var __ = sheet;
        var luminance = Luminance(sheet);
        var internalRoom = new QRCodeDecodeResult[16];
        var publicRoom = new QRCodeDecodeResult[16];

        var internalCount = QRImageDecoder.DecodeSymbols(luminance, sheet.Width, sheet.Height, internalRoom);
        var publicCount = QRCodeDecoder.DecodeImageSymbols(luminance, sheet.Width, sheet.Height, publicRoom);

        await Assert.That(publicCount).IsEqualTo(internalCount);
        await Assert.That(publicRoom).IsEquivalentTo(internalRoom, CollectionOrdering.Matching);
    }
}
