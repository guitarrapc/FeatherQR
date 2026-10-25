using FeatherQR.Internals.StandardQR;
using FeatherQR.SkiaSharp;
using FeatherQR.SkiaSharp.Internals;
using SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// The token of a read for every symbol: checked before each symbol is tried, so a cancel stops the search between symbols, and handed to the search from the stream read.
/// </summary>
public class SeveralSymbolsCancellationTest
{
    [Test]
    public async Task Span_CanceledOnceTheFirstSymbolIsRead_StopsBeforeTheRest()
    {
        var (sheet, texts) = DecodeImageSymbolsTest.Sheet(8, 6);
        using var _ = sheet;
        var luminance = DecodeImageSymbolsTest.Luminance(sheet);
        // Premise: with no cancel the sheet reads whole
        await Assert.That(QRCodeDecoder.DecodeImageSymbols(luminance, sheet.Width, sheet.Height).Length).IsEqualTo(texts.Length);

        var room = new QRCodeDecodeResult[64];
        using var cancellation = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        var done = false;
        // The span form writes each symbol into the caller's room as it reads it, so a first entry is the moment to cancel
        var watcher = new Thread(() =>
        {
            started.Set();
            while (room[0].Text is null && !Volatile.Read(ref done))
                Thread.SpinWait(20);
            cancellation.Cancel();
        });
        watcher.Start();
        started.Wait();

        Exception? caught = null;
        try
        {
            QRImageDecoder.DecodeSymbols(luminance, sheet.Width, sheet.Height, room, cancellation.Token);
        }
        catch (Exception e)
        {
            caught = e;
        }
        Volatile.Write(ref done, true);
        watcher.Join();

        var written = room.Count(static r => r.Text is not null);
        await Assert.That(caught is OperationCanceledException).IsTrue();
        await Assert.That(written).IsGreaterThan(0);
        await Assert.That(written).IsLessThan(texts.Length);
    }

    [Test]
    public async Task CanceledBeforeTheCall_ThrowsAndWritesNothing()
    {
        var (sheet, _) = DecodeImageSymbolsTest.Sheet(3, 2);
        using var __ = sheet;
        var luminance = DecodeImageSymbolsTest.Luminance(sheet);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var room = new QRCodeDecodeResult[8];

        Assert.Throws<OperationCanceledException>(() => QRImageDecoder.DecodeSymbols(luminance, sheet.Width, sheet.Height, room, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => QRImageDecoder.DecodeSymbols(luminance, sheet.Width, sheet.Height, cancellation.Token));

        await Assert.That(room.All(static r => r.Text is null)).IsTrue();
    }

    [Test]
    public async Task ImageWithNoCandidate_CanceledBeforeTheCall_Throws()
    {
        // Nothing reads on a blank image, so this holds the check to readings whose tries find no symbol
        var blank = new byte[600 * 400];
        blank.AsSpan().Fill(255);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => QRImageDecoder.DecodeSymbols(blank, 600, 400, new QRCodeDecodeResult[4], cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => QRImageDecoder.DecodeSymbols(blank, 600, 400, cancellation.Token));
    }

    [Test]
    public async Task Bitmap_TokenReachesTheSearch()
    {
        // The bitmap's conversion to luminance checks no token, so a canceled one is seen by the search alone
        var (sheet, _) = DecodeImageSymbolsTest.Sheet(3, 2);
        using var __ = sheet;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => QRCodeImageDecoder.DecodeEverySymbol(sheet, cancellation.Token));
    }

    [Test]
    public async Task StreamRead_HandsItsTokenToTheRead()
    {
        var png = QRCodeImageBuilder.GetPngBytes("https://github.com/guitarrapc/FeatherQR");
        using var cancellation = new CancellationTokenSource();
        CancellationToken viaAsync = default;
        var viaSync = new CancellationTokenSource().Token;

        await EncodedImage.ReadAsync(new MemoryStream(png), (_, token) => viaAsync = token, cancellation.Token);
        EncodedImage.Read(new MemoryStream(png), (_, token) => viaSync = token);

        await Assert.That(viaAsync).IsEqualTo(cancellation.Token);
        await Assert.That(viaSync).IsEqualTo(CancellationToken.None);
    }
}
