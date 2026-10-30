using FeatherQR.SkiaSharp;
using FeatherQR.SkiaSharp.Internals;
using SkiaSharp;
using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// The public reads for every symbol of any symbology (<see cref="SymbolDecoder"/>): on luminance in an array form and a span form, and on a bitmap and a stream.
/// What they read is held by <see cref="MixedSymbolsDecodeTest"/> on the internal entry. These hold each public form to it, and the result to the symbology it reads.
/// </summary>
public class SymbolDecoderTest
{
    /// <summary>A sheet of the three symbologies as a grey bitmap, its pixels the luminance the reads are handed.</summary>
    private static (SKBitmap Bitmap, byte[] Luminance) Sheet(int across, int down)
    {
        var (luminance, width, height) = MixedSymbolsDecodeTest.RenderGrid(MixedSymbolsDecodeTest.Mix.AllThree, across, down);
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque));
        var pixels = bitmap.GetPixelSpan();
        for (var y = 0; y < height; y++)
            luminance.AsSpan(y * width, width).CopyTo(pixels.Slice(y * bitmap.RowBytes, width));
        return (bitmap, luminance);
    }

    [Test]
    public async Task Array_ReadsWhatTheInternalEntryReads()
    {
        var (bitmap, luminance) = Sheet(3, 2);
        using var _ = bitmap;
        var expected = MixedImageDecoder.DecodeSymbols(luminance, bitmap.Width, bitmap.Height);
        // The premise: the sheet's six symbols, two of each symbology, all read
        await Assert.That(expected.Length).IsEqualTo(6);

        await Assert.That(SymbolDecoder.DecodeImageSymbols(luminance, bitmap.Width, bitmap.Height)).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    /// <summary>A room of any size holds the first results the array holds, and an empty one reads nothing.</summary>
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(4)]
    [Arguments(16)]
    public async Task Span_HoldsTheFirstResultsTheArrayHolds(int room)
    {
        var (bitmap, luminance) = Sheet(3, 2);
        using var _ = bitmap;
        var all = SymbolDecoder.DecodeImageSymbols(luminance, bitmap.Width, bitmap.Height);
        var symbols = new SymbolDecodeResult[room];

        var count = SymbolDecoder.DecodeImageSymbols(luminance, bitmap.Width, bitmap.Height, symbols);

        await Assert.That(count).IsEqualTo(Math.Min(room, all.Length));
        await Assert.That(symbols.Take(count)).IsEquivalentTo(all.Take(count), CollectionOrdering.Matching);
    }

    /// <summary>
    /// A result holds its symbology's own result, whose text and corners are its own, and the other two are default, read as no symbol.
    /// A default result is no read.
    /// </summary>
    [Test]
    public async Task Result_HoldsItsSymbologysOwnResult()
    {
        var (bitmap, luminance) = Sheet(3, 1);
        using var _ = bitmap;
        var read = SymbolDecoder.DecodeImageSymbols(luminance, bitmap.Width, bitmap.Height);
        // The premise: one symbol of each symbology
        await Assert.That(read.Select(static r => r.Symbology)).IsEquivalentTo([Symbology.StandardQR, Symbology.MicroQR, Symbology.RmQR], CollectionOrdering.Any);

        foreach (var symbol in read)
        {
            await Assert.That(symbol.Success).IsTrue();
            await Assert.That(symbol.StandardQR.Success).IsEqualTo(symbol.Symbology == Symbology.StandardQR);
            await Assert.That(symbol.MicroQR.Success).IsEqualTo(symbol.Symbology == Symbology.MicroQR);
            await Assert.That(symbol.RmQR.Success).IsEqualTo(symbol.Symbology == Symbology.RmQR);
            var (text, corners) = symbol.Symbology switch
            {
                Symbology.StandardQR => (symbol.StandardQR.Text, symbol.StandardQR.Info.Corners),
                Symbology.MicroQR => (symbol.MicroQR.Text, symbol.MicroQR.Info.Corners),
                _ => (symbol.RmQR.Text, symbol.RmQR.Info.Corners),
            };
            await Assert.That(symbol.Text).IsEqualTo(text);
            await Assert.That(symbol.Corners).IsEqualTo(corners);
        }
        await Assert.That(default(SymbolDecodeResult).Success).IsFalse();
        await Assert.That(default(SymbolDecodeResult).Text).IsNull();
    }

    /// <summary>An image with no symbol of any symbology reads none through either form.</summary>
    [Test]
    public async Task ImageWithNoSymbol_ReadsNone()
    {
        var luminance = new byte[240 * 240];
        new Random(20261029).NextBytes(luminance);

        await Assert.That(SymbolDecoder.DecodeImageSymbols(luminance, 240, 240)).IsEmpty();
        await Assert.That(SymbolDecoder.DecodeImageSymbols(luminance, 240, 240, new SymbolDecodeResult[4])).IsEqualTo(0);
    }

    /// <summary>A buffer longer than the image is read for the image's pixels alone.</summary>
    [Test]
    public async Task BufferLongerThanTheImage_ReadsWhatTheImageHolds()
    {
        var (bitmap, luminance) = Sheet(3, 1);
        using var _ = bitmap;
        var longer = new byte[luminance.Length + 1_000];
        luminance.CopyTo(longer, 0);
        new Random(7).NextBytes(longer.AsSpan(luminance.Length));

        await Assert.That(SymbolDecoder.DecodeImageSymbols(longer, bitmap.Width, bitmap.Height)).IsEquivalentTo(SymbolDecoder.DecodeImageSymbols(luminance, bitmap.Width, bitmap.Height), CollectionOrdering.Matching);
    }

    [Test]
    public async Task DecodeSymbols_Bitmap_ReadsWhatTheLuminanceFormReads()
    {
        var (bitmap, luminance) = Sheet(3, 2);
        using var _ = bitmap;
        // The premise: the bitmap converts to the luminance it was drawn from
        var converted = new byte[luminance.Length];
        BitmapLuminanceConverter.Convert(bitmap, converted);
        await Assert.That(converted).IsEquivalentTo(luminance, CollectionOrdering.Matching);

        var results = SymbolDecoder.DecodeSymbols(bitmap);

        await Assert.That(results.Length).IsEqualTo(6);
        await Assert.That(results).IsEquivalentTo(SymbolDecoder.DecodeImageSymbols(luminance, bitmap.Width, bitmap.Height), CollectionOrdering.Matching);
    }

    [Test]
    public async Task DecodeSymbols_Stream_ReadsWhatTheBitmapFormReads()
    {
        var (bitmap, _) = Sheet(3, 2);
        using var __ = bitmap;
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var png = data.ToArray();
        var expected = SymbolDecoder.DecodeSymbols(bitmap);

        var viaAsync = await SymbolDecoder.DecodeSymbolsAsync(new ImageStreamDecodeTest.ReadStream(png, perRead: 4096, refuseSync: true));
        var viaSync = SymbolDecoder.DecodeSymbols(new ImageStreamDecodeTest.ReadStream(png, perRead: 4096, refuseAsync: true));

        await Assert.That(expected.Length).IsEqualTo(6);
        await Assert.That(viaAsync).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(viaSync).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    /// <summary>A bitmap smaller than the smallest symbol of any symbology, rMQR's seven modules, gives no result and does not throw.</summary>
    [Test]
    public async Task DecodeSymbols_BitmapTooSmallForASymbol_ReadsNone()
    {
        using var tiny = new SKBitmap(6, 6);
        tiny.Erase(SKColors.White);

        await Assert.That(SymbolDecoder.DecodeSymbols(tiny)).IsEmpty();
    }

    [Test]
    public async Task Bitmap_TokenReachesTheSearch()
    {
        // The bitmap's conversion to luminance checks no token, so a canceled one is seen by the search alone
        var (bitmap, _) = Sheet(2, 1);
        using var __ = bitmap;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => SymbolImageDecoder.DecodeEverySymbol(bitmap, cancellation.Token));
    }

    [Test]
    public async Task Async_CanceledBeforeTheCall_ReturnsACanceledTaskAndReadsNothing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var stream = new ImageStreamDecodeTest.ReadStream(QRCodeImageBuilder.GetPngBytes("FQR MIXED STREAM"));

        // The public call itself, so an exception thrown at the call escapes and fails the test
        var task = SymbolDecoder.DecodeSymbolsAsync(stream, cancellation.Token);
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }

        await Assert.That(task.IsCanceled).IsTrue();
        await Assert.That(stream.ReadCalls).IsEqualTo(0);
    }

    /// <summary>The read for every symbology hands its token to the stream's read as each symbology's does.</summary>
    [Test]
    public async Task Async_CanceledDuringARead_StopsTheRead()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new ImageStreamDecodeTest.ReadStream(QRCodeImageBuilder.GetPngBytes("FQR MIXED STREAM"))
        {
            OnRead = async token =>
            {
                entered.TrySetResult();
                // Bounded, so a read that never sees the token fails the test instead of hanging it
                await Task.Delay(TimeSpan.FromSeconds(10), token);
            },
        };

        var task = SymbolDecoder.DecodeSymbolsAsync(stream, cancellation.Token);
        await Task.WhenAny(entered.Task, task);
        cancellation.Cancel();
        Exception? caught = null;
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        await Assert.That(caught is OperationCanceledException).IsTrue();
        await Assert.That(stream.FirstToken).IsEqualTo(cancellation.Token);
    }

    [Test]
    public async Task NullOrUnreadableInput_ThrowsBeforeReturningATask()
    {
        var unreadable = new ImageStreamDecodeTest.ReadStream([], canRead: false);

        // Actions that drop the task: an exception stored in it would not reach Assert.Throws
        Assert.Throws<ArgumentNullException>(() => { _ = SymbolDecoder.DecodeSymbolsAsync(null!); });
        Assert.Throws<ArgumentNullException>(() => SymbolDecoder.DecodeSymbols((Stream)null!));
        Assert.Throws<ArgumentNullException>(() => SymbolDecoder.DecodeSymbols((SKBitmap)null!));
        Assert.Throws<ArgumentException>(() => { _ = SymbolDecoder.DecodeSymbolsAsync(unreadable); });
        Assert.Throws<ArgumentException>(() => SymbolDecoder.DecodeSymbols(unreadable));
    }
}
