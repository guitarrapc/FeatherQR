using FeatherQR.SkiaSharp;
using SkiaSharp;
using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// The async writes of the image builders: the bytes their synchronous twins write, through the stream's
/// async writes alone, stopped by the token, and rendered off a caller's synchronization context or task scheduler.
/// </summary>
public class SymbolImageAsyncWriteTest
{
    private const string Content = "https://github.com/guitarrapc/FeatherQR";
    private const string MicroContent = "12345";

    private static readonly QRCodeData QRData = QRCodeGenerator.Create(Content, QREccLevel.M);
    private static readonly MicroQRCodeData MicroData = MicroQRCodeGenerator.Create(MicroContent, MicroQREccLevel.M);
    private static readonly RmQRCodeData RmQRData = RmQRCodeGenerator.Create(Content, RmQREccLevel.M);

    private static QRCodeImageBuilder QR() => new(Content);
    private static MicroQRCodeImageBuilder Micro() => new(MicroContent);
    private static RmQRCodeImageBuilder RmQR() => new(Content);

    public sealed record WriteCase(string Name, Action<Stream> Sync, Func<Stream, CancellationToken, Task> Async)
    {
        public override string ToString() => Name;
    }

    public static IEnumerable<Func<WriteCase>> WriteCases()
    {
        yield return () => new("QR SaveTo", s => QR().SaveTo(s), (s, t) => QR().SaveToAsync(s, t));
        yield return () => new("QR SaveTo, JPEG", s => QR().WithFormat(SKEncodedImageFormat.Jpeg, 80).SaveTo(s), (s, t) => QR().WithFormat(SKEncodedImageFormat.Jpeg, 80).SaveToAsync(s, t));
        yield return () => new("QR SaveTo, 2048 px", s => QR().WithSize(2048, 2048).SaveTo(s), (s, t) => QR().WithSize(2048, 2048).SaveToAsync(s, t));
        yield return () => new("QR SaveToSvg", s => QR().SaveToSvg(s), (s, t) => QR().SaveToSvgAsync(s, t));
        yield return () => new("Micro SaveTo", s => Micro().SaveTo(s), (s, t) => Micro().SaveToAsync(s, t));
        yield return () => new("Micro SaveToSvg", s => Micro().SaveToSvg(s), (s, t) => Micro().SaveToSvgAsync(s, t));
        yield return () => new("RmQR SaveTo", s => RmQR().SaveTo(s), (s, t) => RmQR().SaveToAsync(s, t));
        yield return () => new("RmQR SaveToSvg", s => RmQR().SaveToSvg(s), (s, t) => RmQR().SaveToSvgAsync(s, t));

        yield return () => new("QR SavePng(string)", s => QRCodeImageBuilder.SavePng(Content, s, QREccLevel.Q, 300), (s, t) => QRCodeImageBuilder.SavePngAsync(Content, s, QREccLevel.Q, 300, t));
        yield return () => new("QR SavePng(QRCodeData)", s => QRCodeImageBuilder.SavePng(QRData, s, 300), (s, t) => QRCodeImageBuilder.SavePngAsync(QRData, s, 300, t));
        yield return () => new("QR SaveSvg(string)", s => QRCodeImageBuilder.SaveSvg(Content, s, QREccLevel.Q, 300), (s, t) => QRCodeImageBuilder.SaveSvgAsync(Content, s, QREccLevel.Q, 300, t));
        yield return () => new("QR SaveSvg(QRCodeData)", s => QRCodeImageBuilder.SaveSvg(QRData, s, 300), (s, t) => QRCodeImageBuilder.SaveSvgAsync(QRData, s, 300, t));

        yield return () => new("Micro SavePng(string)", s => MicroQRCodeImageBuilder.SavePng(MicroContent, s, MicroQREccLevel.L, 300), (s, t) => MicroQRCodeImageBuilder.SavePngAsync(MicroContent, s, MicroQREccLevel.L, 300, t));
        yield return () => new("Micro SavePng(MicroQRCodeData)", s => MicroQRCodeImageBuilder.SavePng(MicroData, s, 300), (s, t) => MicroQRCodeImageBuilder.SavePngAsync(MicroData, s, 300, t));
        yield return () => new("Micro SaveSvg(string)", s => MicroQRCodeImageBuilder.SaveSvg(MicroContent, s, MicroQREccLevel.L, 300), (s, t) => MicroQRCodeImageBuilder.SaveSvgAsync(MicroContent, s, MicroQREccLevel.L, 300, t));
        yield return () => new("Micro SaveSvg(MicroQRCodeData)", s => MicroQRCodeImageBuilder.SaveSvg(MicroData, s, 300), (s, t) => MicroQRCodeImageBuilder.SaveSvgAsync(MicroData, s, 300, t));

        yield return () => new("RmQR SavePng(string)", s => RmQRCodeImageBuilder.SavePng(Content, s, RmQREccLevel.H, 600), (s, t) => RmQRCodeImageBuilder.SavePngAsync(Content, s, RmQREccLevel.H, 600, t));
        yield return () => new("RmQR SavePng(RmQRCodeData)", s => RmQRCodeImageBuilder.SavePng(RmQRData, s, 600), (s, t) => RmQRCodeImageBuilder.SavePngAsync(RmQRData, s, 600, t));
        yield return () => new("RmQR SaveSvg(string)", s => RmQRCodeImageBuilder.SaveSvg(Content, s, RmQREccLevel.H, 600), (s, t) => RmQRCodeImageBuilder.SaveSvgAsync(Content, s, RmQREccLevel.H, 600, t));
        yield return () => new("RmQR SaveSvg(RmQRCodeData)", s => RmQRCodeImageBuilder.SaveSvg(RmQRData, s, 600), (s, t) => RmQRCodeImageBuilder.SaveSvgAsync(RmQRData, s, 600, t));
    }

    [Test]
    [MethodDataSource(nameof(WriteCases))]
    public async Task Async_WritesTheSynchronousBytes_ThroughAsyncWritesAlone(WriteCase c)
    {
        using var expected = new MemoryStream();
        c.Sync(expected);
        var stream = new AsyncOnlyStream();

        // The stream throws on a synchronous write or flush, so a call that made one fails here
        await c.Async(stream, CancellationToken.None);

        await Assert.That(stream.WriteCalls).IsGreaterThan(0);
        await Assert.That(stream.FlushCalls).IsEqualTo(0);
        await Assert.That(stream.Written).IsEquivalentTo(expected.ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(SKEncodedImageFormat.Gif)]
    [Arguments(SKEncodedImageFormat.Bmp)]
    [Arguments(SKEncodedImageFormat.Avif)]
    public async Task FormatWithNoEncoder_ThrowsNotSupported(SKEncodedImageFormat format)
    {
        // Premise: Skia has no encoder for the format, which it says by returning no data
        using (var image = QR().ToImage())
            await Assert.That(image.Encode(format, 100)).IsNull();
        var builder = QR().WithFormat(format);
        var stream = new AsyncOnlyStream();

        Assert.Throws<NotSupportedException>(() => builder.SaveTo(new MemoryStream()));
        Assert.Throws<NotSupportedException>(() => builder.SaveTo(new System.Buffers.ArrayBufferWriter<byte>()));
        Assert.Throws<NotSupportedException>(() => builder.ToByteArray());
        Assert.Throws<NotSupportedException>(() => QRCodeImageBuilder.GetImageBytes(Content, format));
        await Assert.That(await Caught(builder.SaveToAsync(stream)) is NotSupportedException).IsTrue();
        await Assert.That(stream.WriteCalls).IsEqualTo(0);
    }

    [Test]
    [MethodDataSource(nameof(WriteCases))]
    public async Task Async_CanceledBeforeTheCall_ReturnsACanceledTaskAndWritesNothing(WriteCase c)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var stream = new AsyncOnlyStream();

        var task = c.Async(stream, cancellation.Token);
        await Caught(task);

        await Assert.That(task.IsCanceled).IsTrue();
        await Assert.That(stream.WriteCalls).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Async_CanceledDuringTheWrite_StopsTheWrite(bool svg)
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new AsyncOnlyStream
        {
            OnWrite = async token =>
            {
                entered.TrySetResult();
                // Bounded, so a write that never sees the token fails the test instead of hanging it
                await Task.Delay(TimeSpan.FromSeconds(10), token);
            },
        };

        var task = Write(QR(), stream, svg, cancellation.Token);
        // A call that fails before its first async write never enters it, and then the task is what ends the wait
        await Task.WhenAny(entered.Task, task);
        cancellation.Cancel();
        var caught = await Caught(task);

        await Assert.That(caught is OperationCanceledException).IsTrue();
        await Assert.That(stream.FirstToken).IsEqualTo(cancellation.Token);
    }

    [Test]
    public async Task Async_NullStream_ThrowsBeforeReturningATask()
    {
        var builder = QR();

        // An Action that drops the task: an exception stored in the task would not reach Assert.Throws
        Assert.Throws<ArgumentNullException>(() => { _ = builder.SaveToAsync(null!); });
        Assert.Throws<ArgumentNullException>(() => { _ = builder.SaveToSvgAsync(null!); });
        Assert.Throws<ArgumentNullException>(() => { _ = QRCodeImageBuilder.SavePngAsync(Content, null!); });
        Assert.Throws<ArgumentNullException>(() => { _ = MicroQRCodeImageBuilder.SaveSvgAsync(MicroData, null!); });
    }

    [Test]
    public async Task Async_NonWritableStream_ThrowsBeforeReturningATask()
    {
        var builder = QR();
        using var readOnly = new MemoryStream(new byte[100], writable: false);

        Assert.Throws<ArgumentException>(() => { _ = builder.SaveToAsync(readOnly); });
        Assert.Throws<ArgumentException>(() => { _ = builder.SaveToSvgAsync(readOnly); });
        Assert.Throws<ArgumentException>(() => { _ = RmQRCodeImageBuilder.SavePngAsync(RmQRData, readOnly); });
    }

    [Test]
    public async Task Async_EmptyContent_ThrowsBeforeReturningATask()
    {
        var stream = new AsyncOnlyStream();

        Assert.Throws<ArgumentException>(() => { _ = QRCodeImageBuilder.SavePngAsync("", stream); });
        Assert.Throws<ArgumentException>(() => { _ = MicroQRCodeImageBuilder.SaveSvgAsync("", stream); });
        Assert.Throws<ArgumentException>(() => { _ = RmQRCodeImageBuilder.SavePngAsync("", stream); });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Async_LayoutThatCannotFit_FaultsTheTaskAndWritesNothing(bool svg)
    {
        // A 39-character URL at level M is version 3, 37 modules with the quiet zone, which 10 px modules cannot fit into 64 px
        var builder = QR().WithSize(64, 64).WithModulePixelSize(10);
        var stream = new AsyncOnlyStream();

        var caught = await Caught(Write(builder, stream, svg, CancellationToken.None));

        await Assert.That(caught is InvalidOperationException).IsTrue();
        await Assert.That(stream.WriteCalls).IsEqualTo(0);
    }

    [Test]
    public async Task SaveToSvgAsync_IconTooLarge_FaultsTheTaskAndWritesNothing()
    {
        using var logo = new SKBitmap(16, 16);
        logo.Erase(SKColors.Red);
        var builder = QR()
            .WithErrorCorrection(QREccLevel.H)
            .WithIcon(IconData.FromImageByModules(logo, iconSizeModules: 60, iconBorderModules: 1, maxCoreOccupancyPercent: 100));

        // Premise: the icon is refused while the symbol is drawn, after the synchronous form has started writing
        using var partial = new MemoryStream();
        Assert.Throws<InvalidOperationException>(() => builder.SaveToSvg(partial));
        await Assert.That(partial.Length).IsGreaterThan(0);

        var stream = new AsyncOnlyStream();
        var caught = await Caught(builder.SaveToSvgAsync(stream));

        await Assert.That(caught is InvalidOperationException).IsTrue();
        await Assert.That(stream.WriteCalls).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Async_UnderASynchronizationContext_RendersOffIt(bool svg)
    {
        using var context = new SingleThreadSynchronizationContext();
        var shape = new RecordingModuleShape();
        var stream = new AsyncOnlyStream();
        SynchronizationContext? caller = null;

        await context.Run(() =>
        {
            caller = SynchronizationContext.Current;
            return Write(QR().WithModuleShape(shape), stream, svg, CancellationToken.None);
        });

        await Assert.That(caller).IsSameReferenceAs(context);
        await Assert.That(shape.Thread).IsNotEqualTo(context.ThreadId);
        await Assert.That(shape.Context).IsNull();
        await Assert.That(stream.WriteCalls).IsGreaterThan(0);
    }

    [Test]
    [Arguments(Caller.SynchronizationContext, false)]
    [Arguments(Caller.SynchronizationContext, true)]
    [Arguments(Caller.TaskScheduler, false)]
    [Arguments(Caller.TaskScheduler, true)]
    public async Task Async_LeavingTheCaller_RendersTheBuilderAsItWasAtTheCall(Caller caller, bool svg)
    {
        // The icon changes the builder while the call copies it, after the builder's own settings are copied and before the call returns.
        // So a render that read the caller's builder reads the change whatever the threads do, and one that read the copy does not
        QRCodeImageBuilder? builder = null;
        var icon = new CountingIconShape(onCopy: () => builder!.WithSize(300, 300));
        builder = QR().WithSize(100, 100).WithIcon(new IconData(icon, iconSizePercent: 20));
        var expected = Written(builder, svg);
        var stream = new AsyncOnlyStream();
        var completedOnReturn = true;

        await RunUnder(caller, async () =>
        {
            var task = Write(builder, stream, svg, CancellationToken.None);
            completedOnReturn = task.IsCompleted;
            await task;
        });

        // Premise: the call copied, which a call that leaves its caller does
        await Assert.That(icon.Copies).IsEqualTo(1);
        await Assert.That(completedOnReturn).IsFalse();
        await Assert.That(stream.Written.AsSpan().SequenceEqual(expected)).IsTrue();
    }

    [Test]
    [NotInParallel(SvgCaptions)]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task Async_LeavingTheCaller_IconChangedOnceTheCallReturns_IsDrawnAsItWas(bool svg, bool caption)
    {
        var gate = new GateModuleShape();
        using var logo = new SKBitmap(16, 16);
        logo.Erase(SKColors.Red);
        // Every setting off its default, so a copy that dropped one that changes the drawing draws another caption
        using var font = new SKFont(SKTypeface.Default, 20, 1.3f, 0.2f)
        {
            Embolden = true,
            Edging = SKFontEdging.Alias,
            Hinting = SKFontHinting.Full,
            Subpixel = true,
            LinearMetrics = true,
            BaselineSnap = false,
            ForceAutoHinting = true,
            EmbeddedBitmaps = true,
        };
        IconShape shape = caption ? new ImageTextIconShape(logo, "FQR", SKColors.Blue, font) : new ImageIconShape(logo);
        var builder = QR().WithErrorCorrection(QREccLevel.H).WithModuleShape(gate).WithIcon(new IconData(shape, iconSizePercent: 20));
        var expected = Written(builder, svg);
        var stream = new AsyncOnlyStream();
        var completedOnReturn = true;

        await RunUnder(Caller.SynchronizationContext, async () =>
        {
            gate.Close();
            var task = Write(builder, stream, svg, CancellationToken.None);
            completedOnReturn = task.IsCompleted;
            // The icon is drawn after the modules, so a render that drew the caller's own bitmap or font would draw these
            logo.Erase(SKColors.Green);
            font.Size = 40;
            font.ScaleX = 1;
            font.SkewX = 0;
            font.Embolden = false;
            font.Edging = SKFontEdging.SubpixelAntialias;
            font.Hinting = SKFontHinting.Normal;
            font.Subpixel = false;
            font.LinearMetrics = false;
            font.BaselineSnap = true;
            font.ForceAutoHinting = false;
            font.EmbeddedBitmaps = false;
            gate.Open();
            await task;
        });

        await Assert.That(completedOnReturn).IsFalse();
        await Assert.That(stream.Written.AsSpan().SequenceEqual(expected)).IsTrue();
    }

    [Test]
    public async Task Async_WithNoContext_CopiesNothing()
    {
        var icon = new CountingIconShape();

        await Task.Run(() => QR().WithIcon(new IconData(icon, iconSizePercent: 20)).SaveToAsync(new AsyncOnlyStream()));

        await Assert.That(icon.Copies).IsEqualTo(0);
        await Assert.That(icon.Draws).IsEqualTo(1);
    }

    [Test]
    [Arguments(Caller.SynchronizationContext, false)]
    [Arguments(Caller.SynchronizationContext, true)]
    [Arguments(Caller.TaskScheduler, false)]
    [Arguments(Caller.TaskScheduler, true)]
    public async Task Async_LeavingTheCaller_CopiesOnceAndReleasesTheCopiesAfterTheDraw(Caller caller, bool svg)
    {
        var icon = new CountingIconShape();

        await RunUnder(caller, () => Write(QR().WithIcon(new IconData(icon, iconSizePercent: 20)), new AsyncOnlyStream(), svg, CancellationToken.None));

        await Assert.That(icon.Copies).IsEqualTo(1);
        await Assert.That(icon.Draws).IsEqualTo(1);
        await Assert.That(icon.Releases).IsEqualTo(1);
        await Assert.That(icon.DrawsAfterRelease).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Async_LeavingTheCaller_CanceledOnceCopied_DrawsNothingAndReleasesTheCopies(bool svg)
    {
        // The token is canceled while the call copies, before it leaves the context, so the check after the move is what sees it
        using var cancellation = new CancellationTokenSource();
        var icon = new CountingIconShape(onCopy: cancellation.Cancel);
        var stream = new AsyncOnlyStream();
        Exception? caught = null;

        await RunUnder(Caller.SynchronizationContext, async () =>
            caught = await Caught(Write(QR().WithIcon(new IconData(icon, iconSizePercent: 20)), stream, svg, cancellation.Token)));

        await Assert.That(caught is OperationCanceledException).IsTrue();
        await Assert.That(icon.Draws).IsEqualTo(0);
        await Assert.That(icon.Releases).IsEqualTo(1);
        await Assert.That(stream.WriteCalls).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Async_LeavingTheCaller_RenderThatThrows_ReleasesTheCopies(bool svg)
    {
        var icon = new CountingIconShape(throwOnDraw: true);
        Exception? caught = null;

        await RunUnder(Caller.SynchronizationContext, async () =>
            caught = await Caught(Write(QR().WithIcon(new IconData(icon, iconSizePercent: 20)), new AsyncOnlyStream(), svg, CancellationToken.None)));

        await Assert.That(caught?.Message).IsEqualTo(CountingIconShape.Refusal);
        await Assert.That(icon.Releases).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Async_UnderASynchronizationContext_IconDataWithNoIcon_WritesWhatTheSyncWriteWrites(bool svg)
    {
        // IconData with a null Icon draws no icon, as the renderer reads it
        using var context = new SingleThreadSynchronizationContext();
        var builder = QR().WithErrorCorrection(QREccLevel.H).WithIcon(new IconData { Icon = null! });
        using var expected = new MemoryStream();
        if (svg) builder.SaveToSvg(expected); else builder.SaveTo(expected);
        var stream = new AsyncOnlyStream();

        await context.Run(() => Write(builder, stream, svg, CancellationToken.None));

        await Assert.That(stream.Written).IsEquivalentTo(expected.ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false, "unallocated")]
    [Arguments(true, "unallocated")]
    [Arguments(false, "no pixel memory")]
    [Arguments(true, "no pixel memory")]
    [Arguments(false, "zero area")]
    [Arguments(true, "zero area")]
    public async Task Async_UnderASynchronizationContext_IconWithNoPixels_FailsAsTheSyncWriteFails(bool svg, string kind)
    {
        using var context = new SingleThreadSynchronizationContext();
        using var logo = kind switch
        {
            "unallocated" => new SKBitmap(),
            "no pixel memory" => WithNoPixelMemory(),
            _ => new SKBitmap(0, 0),
        };
        // Premise: the bitmap draws nothing, though the last two have an info and the last a pixel store
        await Assert.That(logo.DrawsNothing).IsTrue();
        var gate = new GateModuleShape();
        var builder = QR().WithErrorCorrection(QREccLevel.H).WithModuleShape(gate).WithIcon(IconData.FromImage(logo, iconSizePercent: 20));
        Exception? expected = null;
        try
        {
            if (svg) builder.SaveToSvg(new MemoryStream()); else builder.SaveTo(new MemoryStream());
        }
        catch (Exception e)
        {
            expected = e;
        }
        var stream = new AsyncOnlyStream();
        Exception? caught = null;

        await context.Run(async () =>
        {
            gate.Close();
            var task = Write(builder, stream, svg, CancellationToken.None);
            // The icon is drawn after the modules, so pixels given while the render waits would be drawn by a render that read the caller's bitmap
            logo.TryAllocPixels(new SKImageInfo(8, 8));
            logo.Erase(SKColors.Red);
            gate.Open();
            caught = await Caught(task);
        });

        // Premise: a bitmap with no pixels cannot be drawn
        await Assert.That(expected).IsNotNull();
        await Assert.That(caught?.GetType()).IsEqualTo(expected!.GetType());
    }

    private static SKBitmap WithNoPixelMemory()
    {
        var bitmap = new SKBitmap();
        bitmap.InstallPixels(new SKImageInfo(8, 8), IntPtr.Zero);
        return bitmap;
    }

    [Test]
    public async Task Async_UnderASynchronizationContext_UnpremultipliedIcon_WritesTheSvgTheSyncWriteWrites()
    {
        // An SVG embeds the icon's pixels as they are. Premultiplying them clears the colour of a pixel of no alpha, so a third of these have none
        using var context = new SingleThreadSynchronizationContext();
        using var logo = new SKBitmap(new SKImageInfo(16, 16, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var pixels = logo.GetPixelSpan();
        for (var p = 0; p < pixels.Length / 4; p++)
        {
            pixels[4 * p] = (byte)(p * 7 + 1);
            pixels[4 * p + 1] = (byte)(p * 13 + 1);
            pixels[4 * p + 2] = (byte)(p * 29 + 1);
            pixels[4 * p + 3] = p % 3 == 0 ? (byte)0 : (byte)(p * 31);
        }
        var builder = QR().WithErrorCorrection(QREccLevel.H).WithIcon(IconData.FromImage(logo, iconSizePercent: 20));
        using var expected = new MemoryStream();
        builder.SaveToSvg(expected);
        var stream = new AsyncOnlyStream();

        await context.Run(() => builder.SaveToSvgAsync(stream));

        await Assert.That(stream.Written.AsSpan().SequenceEqual(expected.ToArray())).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Async_UnderATaskScheduler_RendersOnTheDefaultScheduler(bool svg)
    {
        var scheduler = new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler;
        var shape = new RecordingModuleShape();
        var stream = new AsyncOnlyStream();
        TaskScheduler? caller = null;

        await Task.Factory.StartNew(
            () =>
            {
                caller = TaskScheduler.Current;
                return Write(QR().WithModuleShape(shape), stream, svg, CancellationToken.None);
            },
            CancellationToken.None,
            TaskCreationOptions.None,
            scheduler).Unwrap();

        await Assert.That(caller).IsSameReferenceAs(scheduler);
        await Assert.That(shape.Scheduler).IsSameReferenceAs(TaskScheduler.Default);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Async_WithNoContext_RendersInTheCall(bool svg)
    {
        var shape = new RecordingModuleShape();
        var stream = new AsyncOnlyStream();

        var (premise, caller, rendered) = await Task.Run(async () =>
        {
            var premise = SynchronizationContext.Current is null && TaskScheduler.Current == TaskScheduler.Default;
            var caller = Environment.CurrentManagedThreadId;
            var task = Write(QR().WithModuleShape(shape), stream, svg, CancellationToken.None);
            // Read before awaiting: a call that moved to the pool would not have rendered yet, or would have rendered elsewhere
            var rendered = shape.Thread;
            await task;
            return (premise, caller, rendered);
        });

        await Assert.That(premise).IsTrue();
        await Assert.That(rendered).IsEqualTo(caller);
    }

    private static Task Write<TSelf>(SymbolImageBuilderBase<TSelf> builder, Stream stream, bool svg, CancellationToken cancellationToken)
        where TSelf : SymbolImageBuilderBase<TSelf>
        => svg ? builder.SaveToSvgAsync(stream, cancellationToken) : builder.SaveToAsync(stream, cancellationToken);

    private static byte[] Written<TSelf>(SymbolImageBuilderBase<TSelf> builder, bool svg)
        where TSelf : SymbolImageBuilderBase<TSelf>
    {
        using var stream = new MemoryStream();
        if (svg) builder.SaveToSvg(stream); else builder.SaveTo(stream);
        return stream.ToArray();
    }

    // SVG captions rendered at once lose their text at times, outside the library's code, so those tests run one at a time
    private const string SvgCaptions = "SVG captions";

    private static Task RunUnder(Caller caller, Func<Task> body) => Callers.RunUnder(caller, body);

    /// <summary>
    /// Draws plain squares, and holds the render at its first module while closed.
    /// The builder's settings are read before the modules and the icon is drawn after them, so a test changes the icon's bitmap and font before they are drawn.
    /// </summary>
    private sealed class GateModuleShape : ModuleShape
    {
        private readonly ManualResetEventSlim _open = new(initialState: true);

        public override bool RequiresAntialiasing => false;

        public void Close() => _open.Reset();
        public void Open() => _open.Set();

        public override void Draw(SKCanvas canvas, SKRect rect, SKPaint paint)
        {
            // Bounded, so a render that never sees the gate open fails the test instead of hanging it
            if (!_open.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The gate stayed closed.");
            canvas.DrawRect(rect, paint);
        }
    }

    /// <summary>
    /// An icon that counts the copies a render makes of it, its draws, and the copies released.
    /// Its copy is the shape itself, so the copy's draws are counted here too.
    /// </summary>
    private sealed class CountingIconShape(Action? onCopy = null, bool throwOnDraw = false) : IconShape
    {
        public const string Refusal = "The icon refuses to be drawn.";

        private int _copies;
        private int _draws;
        private int _releases;
        private int _drawsAfterRelease;

        public int Copies => Volatile.Read(ref _copies);
        public int Draws => Volatile.Read(ref _draws);
        public int Releases => Volatile.Read(ref _releases);
        public int DrawsAfterRelease => Volatile.Read(ref _drawsAfterRelease);

        public override void Draw(SKCanvas canvas, SKRect rect, SKRect borderRect, SKColor backgroundColor)
        {
            Interlocked.Increment(ref _draws);
            if (Releases > 0)
                Interlocked.Increment(ref _drawsAfterRelease);
            if (throwOnDraw)
                throw new InvalidOperationException(Refusal);
        }

        internal override IconShape CopyForLaterRender(List<IDisposable> owned)
        {
            Interlocked.Increment(ref _copies);
            owned.Add(new Release(this));
            onCopy?.Invoke();
            return this;
        }

        private sealed class Release(CountingIconShape shape) : IDisposable
        {
            public void Dispose() => Interlocked.Increment(ref shape._releases);
        }
    }

    private static async Task<Exception?> Caught(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    /// <summary>
    /// Refuses synchronous writes and flushes, as a server's response body does by default, and records what its async writes and flushes saw.
    /// Its async write is the memory overload alone, so an async write through the array overload falls to the synchronous write.
    /// </summary>
    private sealed class AsyncOnlyStream : Stream
    {
        private readonly MemoryStream _written = new();

        public Func<CancellationToken, Task>? OnWrite { get; init; }
        public int WriteCalls { get; private set; }
        public int FlushCalls { get; private set; }
        public CancellationToken FirstToken { get; private set; }
        public byte[] Written => _written.ToArray();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous writes are refused.");
        public override void Write(ReadOnlySpan<byte> buffer) => throw new InvalidOperationException("Synchronous writes are refused.");
        public override void WriteByte(byte value) => throw new InvalidOperationException("Synchronous writes are refused.");
        public override void Flush() => throw new InvalidOperationException("Synchronous flushes are refused.");
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCalls++;
            return Task.CompletedTask;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (WriteCalls++ == 0)
                FirstToken = cancellationToken;
            if (OnWrite is not null)
                await OnWrite(cancellationToken);
            _written.Write(buffer.Span);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// Draws plain squares and records where the first module was drawn, which is where the render ran.
    /// </summary>
    private sealed class RecordingModuleShape : ModuleShape
    {
        public int Thread { get; private set; } = -1;
        public SynchronizationContext? Context { get; private set; }
        public TaskScheduler? Scheduler { get; private set; }

        public override bool RequiresAntialiasing => false;

        public override void Draw(SKCanvas canvas, SKRect rect, SKPaint paint)
        {
            if (Thread == -1)
            {
                Thread = Environment.CurrentManagedThreadId;
                Context = SynchronizationContext.Current;
                Scheduler = TaskScheduler.Current;
            }
            canvas.DrawRect(rect, paint);
        }
    }
}
