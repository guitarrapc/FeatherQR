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
        // Spans several chunks of the async write
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
        await Assert.That(stream.Written).IsEquivalentTo(expected.ToArray(), CollectionOrdering.Matching);
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
        // A 40-character URL at level M is version 3, 37 modules with the quiet zone, which 10 px modules cannot fit into 64 px
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
    /// Refuses synchronous writes and flushes, as a server's response body does by default, and records what its async writes saw.
    /// </summary>
    private sealed class AsyncOnlyStream : Stream
    {
        private readonly MemoryStream _written = new();

        public Func<CancellationToken, Task>? OnWrite { get; init; }
        public int WriteCalls { get; private set; }
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
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

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
