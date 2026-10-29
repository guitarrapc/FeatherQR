using FeatherQR.Internals.MicroQR;
using FeatherQR.Internals.RmQR;
using FeatherQR.SkiaSharp;
using FeatherQR.SkiaSharp.Internals;
using SkiaSharp;
using TUnit.Assertions.Enums;
using TestSymbology = FeatherQR.Tests.SingleFinderSymbolsDecodeTest.Symbology;

namespace FeatherQR.Tests;

/// <summary>
/// The public reads for every Micro QR or rMQR symbol of an image: on luminance in an array form and a span form, and on a bitmap and a stream.
/// What they read is held by <see cref="SingleFinderSymbolsDecodeTest"/> on the internal entries. These hold each public form to them.
/// </summary>
public class SingleFinderDecodeImageSymbolsTest
{
    /// <summary>A result in the parts both symbologies have.</summary>
    private sealed record Read(string Text, bool Success, DecodeStatus Status, SymbolCorners Corners);

    private static Read[] Array(TestSymbology symbology, byte[] luminance, int width, int height) => symbology == TestSymbology.MicroQR
        ? [.. MicroQRCodeDecoder.DecodeImageSymbols(luminance, width, height).Select(static r => new Read(r.Text, r.Success, r.Info.Status, r.Info.Corners))]
        : [.. RmQRCodeDecoder.DecodeImageSymbols(luminance, width, height).Select(static r => new Read(r.Text, r.Success, r.Info.Status, r.Info.Corners))];

    private static Read[] Span(TestSymbology symbology, byte[] luminance, int width, int height, int room)
    {
        if (symbology == TestSymbology.MicroQR)
        {
            var micro = new MicroQRCodeDecodeResult[room];
            var count = MicroQRCodeDecoder.DecodeImageSymbols(luminance, width, height, micro);
            return [.. micro.AsSpan(0, count).ToArray().Select(static r => new Read(r.Text, r.Success, r.Info.Status, r.Info.Corners))];
        }
        var rmqr = new RmQRCodeDecodeResult[room];
        var rmqrCount = RmQRCodeDecoder.DecodeImageSymbols(luminance, width, height, rmqr);
        return [.. rmqr.AsSpan(0, rmqrCount).ToArray().Select(static r => new Read(r.Text, r.Success, r.Info.Status, r.Info.Corners))];
    }

    private static Read[] Bitmap(TestSymbology symbology, SKBitmap bitmap) => symbology == TestSymbology.MicroQR
        ? [.. MicroQRCodeDecoder.DecodeSymbols(bitmap).Select(static r => new Read(r.Text, r.Success, r.Info.Status, r.Info.Corners))]
        : [.. RmQRCodeDecoder.DecodeSymbols(bitmap).Select(static r => new Read(r.Text, r.Success, r.Info.Status, r.Info.Corners))];

    /// <summary>A grid of symbols as a grey bitmap, its pixels the luminance the reads are handed.</summary>
    private static (SKBitmap Bitmap, byte[] Luminance) Sheet(TestSymbology symbology, int across, int down)
    {
        var (luminance, width, height) = SingleFinderSymbolsDecodeTest.RenderGrid(symbology, across, down);
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque));
        var pixels = bitmap.GetPixelSpan();
        for (var y = 0; y < height; y++)
            luminance.AsSpan(y * width, width).CopyTo(pixels.Slice(y * bitmap.RowBytes, width));
        return (bitmap, luminance);
    }

    [Test]
    [Arguments(TestSymbology.MicroQR)]
    [Arguments(TestSymbology.RmQR)]
    public async Task Array_GridOfMoreThanItsFirstRoom_ReadsEverySymbolOnce(TestSymbology symbology)
    {
        // Twelve symbols, more than the array form's room holds before it grows
        var (bitmap, luminance) = Sheet(symbology, 4, 3);
        using var _ = bitmap;

        var results = Array(symbology, luminance, bitmap.Width, bitmap.Height);

        await Assert.That(results.Length).IsEqualTo(12);
        await Assert.That(results.Select(static r => r.Text).Distinct().Count()).IsEqualTo(12);
        await Assert.That(results.All(static r => r.Success && r.Status == DecodeStatus.Success)).IsTrue();
    }

    [Test]
    [Arguments(TestSymbology.MicroQR)]
    [Arguments(TestSymbology.RmQR)]
    public async Task Array_ReadsWhatTheSpanFormReadsWithRoomForAll(TestSymbology symbology)
    {
        var (bitmap, luminance) = Sheet(symbology, 4, 3);
        using var _ = bitmap;

        var span = Span(symbology, luminance, bitmap.Width, bitmap.Height, 64);
        var array = Array(symbology, luminance, bitmap.Width, bitmap.Height);

        await Assert.That(array).IsEquivalentTo(span, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(TestSymbology.MicroQR)]
    [Arguments(TestSymbology.RmQR)]
    public async Task Span_RoomForOne_HoldsTheFirstSymbolTheArrayHolds(TestSymbology symbology)
    {
        var (bitmap, luminance) = Sheet(symbology, 3, 2);
        using var _ = bitmap;

        var one = Span(symbology, luminance, bitmap.Width, bitmap.Height, 1);
        var array = Array(symbology, luminance, bitmap.Width, bitmap.Height);

        await Assert.That(one.Length).IsEqualTo(1);
        await Assert.That(one[0]).IsEqualTo(array[0]);
    }

    [Test]
    [Arguments(TestSymbology.MicroQR)]
    [Arguments(TestSymbology.RmQR)]
    public async Task Span_EmptyRoom_ReadsNothing(TestSymbology symbology)
    {
        var (bitmap, luminance) = Sheet(symbology, 2, 1);
        using var _ = bitmap;

        await Assert.That(Span(symbology, luminance, bitmap.Width, bitmap.Height, 0)).IsEmpty();
    }

    [Test]
    [Arguments(TestSymbology.MicroQR)]
    [Arguments(TestSymbology.RmQR)]
    public async Task ImageWithNoSymbol_ReadsNone(TestSymbology symbology)
    {
        var blank = new byte[300 * 200];
        blank.AsSpan().Fill(255);

        await Assert.That(Array(symbology, blank, 300, 200)).IsEmpty();
        await Assert.That(Span(symbology, blank, 300, 200, 4)).IsEmpty();
    }

    [Test]
    [Arguments(10, 10, 99)]
    // A size whose pixel count overflows an int
    [Arguments(65_536, 65_536, 0)]
    public async Task BufferThatDoesNotFitTheDimensions_Throws(int width, int height, int length)
    {
        var luminance = new byte[length];

        Assert.Throws<ArgumentException>(() => MicroQRCodeDecoder.DecodeImageSymbols(luminance, width, height));
        Assert.Throws<ArgumentException>(() => MicroQRCodeDecoder.DecodeImageSymbols(luminance, width, height, new MicroQRCodeDecodeResult[4]));
        Assert.Throws<ArgumentException>(() => RmQRCodeDecoder.DecodeImageSymbols(luminance, width, height));
        Assert.Throws<ArgumentException>(() => RmQRCodeDecoder.DecodeImageSymbols(luminance, width, height, new RmQRCodeDecodeResult[4]));
    }

    [Test]
    [Arguments(TestSymbology.MicroQR)]
    [Arguments(TestSymbology.RmQR)]
    public async Task Array_GridThatFillsItsFirstRoom_ReadsEverySymbolOnce(TestSymbology symbology)
    {
        // Eight symbols, as many as the array form's first room holds, so the room itself is returned
        var (bitmap, luminance) = Sheet(symbology, 4, 2);
        using var _ = bitmap;

        var results = Array(symbology, luminance, bitmap.Width, bitmap.Height);

        await Assert.That(results.Length).IsEqualTo(8);
        await Assert.That(results.Select(static r => r.Text).Distinct().Count()).IsEqualTo(8);
        await Assert.That(results).IsEquivalentTo(Span(symbology, luminance, bitmap.Width, bitmap.Height, 16), CollectionOrdering.Matching);
    }

    /// <summary>The public forms take a buffer longer than the image. Whether the bytes past it are left out is held by the internal entry's test on a symbol whose read depends on the thresholds.</summary>
    [Test]
    [Arguments(TestSymbology.MicroQR)]
    [Arguments(TestSymbology.RmQR)]
    public async Task BufferLongerThanTheImage_ReadsWhatTheImageHolds(TestSymbology symbology)
    {
        var (bitmap, luminance) = Sheet(symbology, 2, 1);
        using var _ = bitmap;
        var longer = new byte[luminance.Length + 4 * luminance.Length];
        luminance.CopyTo(longer, 0);
        var expected = Array(symbology, luminance, bitmap.Width, bitmap.Height);
        await Assert.That(expected.Length).IsEqualTo(2);

        await Assert.That(Array(symbology, longer, bitmap.Width, bitmap.Height)).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(Span(symbology, longer, bitmap.Width, bitmap.Height, 4)).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(TestSymbology.MicroQR)]
    [Arguments(TestSymbology.RmQR)]
    public async Task InternalEntry_WritesTheResultsThePublicSpanFormReturns(TestSymbology symbology)
    {
        var (bitmap, luminance) = Sheet(symbology, 3, 2);
        using var _ = bitmap;
        int width = bitmap.Width, height = bitmap.Height;
        if (symbology == TestSymbology.MicroQR)
        {
            MicroQRCodeDecodeResult[] internalRoom = new MicroQRCodeDecodeResult[16], publicRoom = new MicroQRCodeDecodeResult[16];
            var internalCount = MicroQRImageDecoder.DecodeSymbols(luminance, width, height, internalRoom);
            await Assert.That(MicroQRCodeDecoder.DecodeImageSymbols(luminance, width, height, publicRoom)).IsEqualTo(internalCount);
            await Assert.That(publicRoom).IsEquivalentTo(internalRoom, CollectionOrdering.Matching);
        }
        else
        {
            RmQRCodeDecodeResult[] internalRoom = new RmQRCodeDecodeResult[16], publicRoom = new RmQRCodeDecodeResult[16];
            var internalCount = RmQRImageDecoder.DecodeSymbols(luminance, width, height, internalRoom);
            await Assert.That(RmQRCodeDecoder.DecodeImageSymbols(luminance, width, height, publicRoom)).IsEqualTo(internalCount);
            await Assert.That(publicRoom).IsEquivalentTo(internalRoom, CollectionOrdering.Matching);
        }
    }

    [Test]
    [Arguments(TestSymbology.MicroQR)]
    [Arguments(TestSymbology.RmQR)]
    public async Task DecodeSymbols_Bitmap_ReadsWhatTheLuminanceFormReads(TestSymbology symbology)
    {
        var (bitmap, luminance) = Sheet(symbology, 3, 2);
        using var _ = bitmap;
        // The premise: the bitmap converts to the luminance it was drawn from
        var converted = new byte[luminance.Length];
        BitmapLuminanceConverter.Convert(bitmap, converted);
        await Assert.That(converted).IsEquivalentTo(luminance, CollectionOrdering.Matching);

        var results = Bitmap(symbology, bitmap);

        await Assert.That(results.Length).IsEqualTo(6);
        await Assert.That(results).IsEquivalentTo(Array(symbology, luminance, bitmap.Width, bitmap.Height), CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(TestSymbology.MicroQR)]
    [Arguments(TestSymbology.RmQR)]
    public async Task DecodeSymbols_Stream_ReadsWhatTheBitmapFormReads(TestSymbology symbology)
    {
        var (bitmap, _) = Sheet(symbology, 3, 2);
        using var __ = bitmap;
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var png = data.ToArray();
        var expected = Bitmap(symbology, bitmap);

        Read[] viaAsync, viaSync;
        if (symbology == TestSymbology.MicroQR)
        {
            viaAsync = [.. (await MicroQRCodeDecoder.DecodeSymbolsAsync(new ImageStreamDecodeTest.ReadStream(png, perRead: 4096, refuseSync: true))).Select(static r => new Read(r.Text, r.Success, r.Info.Status, r.Info.Corners))];
            viaSync = [.. MicroQRCodeDecoder.DecodeSymbols(new ImageStreamDecodeTest.ReadStream(png, perRead: 4096, refuseAsync: true)).Select(static r => new Read(r.Text, r.Success, r.Info.Status, r.Info.Corners))];
        }
        else
        {
            viaAsync = [.. (await RmQRCodeDecoder.DecodeSymbolsAsync(new ImageStreamDecodeTest.ReadStream(png, perRead: 4096, refuseSync: true))).Select(static r => new Read(r.Text, r.Success, r.Info.Status, r.Info.Corners))];
            viaSync = [.. RmQRCodeDecoder.DecodeSymbols(new ImageStreamDecodeTest.ReadStream(png, perRead: 4096, refuseAsync: true)).Select(static r => new Read(r.Text, r.Success, r.Info.Status, r.Info.Corners))];
        }

        await Assert.That(expected.Length).IsEqualTo(6);
        await Assert.That(viaAsync).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(viaSync).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    /// <summary>A bitmap smaller than the smallest symbol gives no result and does not throw. A blank one reads nothing either way, so this holds the result, not the shortcut that skips the search.</summary>
    [Test]
    [Arguments(TestSymbology.MicroQR, 10)]
    [Arguments(TestSymbology.RmQR, 6)]
    public async Task DecodeSymbols_BitmapTooSmallForASymbol_ReadsNone(TestSymbology symbology, int side)
    {
        using var tiny = new SKBitmap(side, side);
        tiny.Erase(SKColors.White);

        await Assert.That(Bitmap(symbology, tiny)).IsEmpty();
    }

    [Test]
    [Arguments(TestSymbology.MicroQR)]
    [Arguments(TestSymbology.RmQR)]
    public async Task Bitmap_TokenReachesTheSearch(TestSymbology symbology)
    {
        // The bitmap's conversion to luminance checks no token, so a canceled one is seen by the search alone
        var (bitmap, _) = Sheet(symbology, 2, 1);
        using var __ = bitmap;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        if (symbology == TestSymbology.MicroQR)
            Assert.Throws<OperationCanceledException>(() => MicroQRCodeImageDecoder.DecodeEverySymbol(bitmap, cancellation.Token));
        else
            Assert.Throws<OperationCanceledException>(() => RmQRCodeImageDecoder.DecodeEverySymbol(bitmap, cancellation.Token));
    }
}
