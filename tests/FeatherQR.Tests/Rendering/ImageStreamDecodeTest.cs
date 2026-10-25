using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using FeatherQR.SkiaSharp;
using SkiaSharp;
using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// The stream reads of the image decoders: what decoding the image into a bitmap and reading that gives, through the stream's
/// own reads of either kind, refused past the limits before the pixels are allocated, stopped by the token, and decoded off a
/// caller's synchronization context or task scheduler.
/// </summary>
public class ImageStreamDecodeTest
{
    private const string Content = "https://github.com/guitarrapc/FeatherQR";

    /// <summary>What a read found, the same shape for the three symbologies.</summary>
    public sealed record Read(string Text, object Info, bool Success);

    public sealed record ReadCase(string Name, byte[] Image, Func<SKBitmap, Read> FromBitmap, Func<Stream, CancellationToken, Task<Read>> Async, Func<Stream, Read> Sync)
    {
        public override string ToString() => Name;
    }

    private static Read QRBitmap(SKBitmap bitmap)
    {
        QRCodeDecoder.TryDecode(bitmap, out var text, out var info);
        return new(text, info, info.Status == DecodeStatus.Success);
    }

    private static Read MicroBitmap(SKBitmap bitmap)
    {
        MicroQRCodeDecoder.TryDecode(bitmap, out var text, out var info);
        return new(text, info, info.Status == DecodeStatus.Success);
    }

    private static Read RmQRBitmap(SKBitmap bitmap)
    {
        RmQRCodeDecoder.TryDecode(bitmap, out var text, out var info);
        return new(text, info, info.Status == DecodeStatus.Success);
    }

    private static async Task<Read> QRAsync(Stream stream, CancellationToken token) { var r = await QRCodeDecoder.DecodeAsync(stream, token); return new(r.Text, r.Info, r.Success); }
    private static async Task<Read> MicroAsync(Stream stream, CancellationToken token) { var r = await MicroQRCodeDecoder.DecodeAsync(stream, token); return new(r.Text, r.Info, r.Success); }
    private static async Task<Read> RmQRAsync(Stream stream, CancellationToken token) { var r = await RmQRCodeDecoder.DecodeAsync(stream, token); return new(r.Text, r.Info, r.Success); }
    private static Read QRSync(Stream stream) { var r = QRCodeDecoder.Decode(stream); return new(r.Text, r.Info, r.Success); }
    private static Read MicroSync(Stream stream) { var r = MicroQRCodeDecoder.Decode(stream); return new(r.Text, r.Info, r.Success); }
    private static Read RmQRSync(Stream stream) { var r = RmQRCodeDecoder.Decode(stream); return new(r.Text, r.Info, r.Success); }

    private static byte[] Blank()
    {
        using var bitmap = new SKBitmap(300, 300);
        bitmap.Erase(SKColors.White);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static IEnumerable<Func<ReadCase>> ReadCases()
    {
        yield return () => new("QR PNG", QRCodeImageBuilder.GetPngBytes(Content), QRBitmap, QRAsync, QRSync);
        yield return () => new("QR JPEG", QRCodeImageBuilder.GetImageBytes(Content, SKEncodedImageFormat.Jpeg, quality: 90), QRBitmap, QRAsync, QRSync);
        yield return () => new("QR blank", Blank(), QRBitmap, QRAsync, QRSync);
        yield return () => new("Micro PNG", MicroQRCodeImageBuilder.GetPngBytes("12345"), MicroBitmap, MicroAsync, MicroSync);
        yield return () => new("Micro blank", Blank(), MicroBitmap, MicroAsync, MicroSync);
        yield return () => new("RmQR PNG", RmQRCodeImageBuilder.GetPngBytes(Content), RmQRBitmap, RmQRAsync, RmQRSync);
        yield return () => new("RmQR blank", Blank(), RmQRBitmap, RmQRAsync, RmQRSync);
    }

    // The stream reads say they decode as SKBitmap.Decode(Stream) does, so that is what they are held to
    private static Read FromBitmap(ReadCase c)
    {
        using var bitmap = SKBitmap.Decode(new MemoryStream(c.Image));
        return c.FromBitmap(bitmap);
    }

    [Test]
    [MethodDataSource(nameof(ReadCases))]
    public async Task Async_ThroughAsyncReadsAlone_ReadsWhatTheBitmapReads(ReadCase c)
    {
        var stream = new ReadStream(c.Image, refuseSync: true);

        var read = await c.Async(stream, CancellationToken.None);

        await Assert.That(read).IsEqualTo(FromBitmap(c));
    }

    [Test]
    [MethodDataSource(nameof(ReadCases))]
    public async Task Async_SevenBytesARead_ReadsWhatTheBitmapReads(ReadCase c)
    {
        var stream = new ReadStream(c.Image, perRead: 7, refuseSync: true);

        var read = await c.Async(stream, CancellationToken.None);

        await Assert.That(read).IsEqualTo(FromBitmap(c));
    }

    [Test]
    [MethodDataSource(nameof(ReadCases))]
    public async Task Async_SeekableStream_ReadsWhatTheBitmapReads(ReadCase c)
    {
        var stream = new ReadStream(c.Image, seekable: true, refuseSync: true);

        var read = await c.Async(stream, CancellationToken.None);

        await Assert.That(read).IsEqualTo(FromBitmap(c));
    }

    [Test]
    [MethodDataSource(nameof(ReadCases))]
    public async Task Sync_SevenBytesARead_ReadsWhatTheBitmapReads(ReadCase c)
    {
        // The stream refuses async reads, so a synchronous read made by blocking on the async one fails here
        var stream = new ReadStream(c.Image, perRead: 7, refuseAsync: true);

        var read = c.Sync(stream);

        await Assert.That(read).IsEqualTo(FromBitmap(c));
    }

    [Test]
    [MethodDataSource(nameof(ReadCases))]
    public async Task ExposedMemoryStream_ReadsFromItsPositionToItsEnd(ReadCase c)
    {
        var stream = new MemoryStream();
        stream.Write([1, 2, 3]);
        stream.Write(c.Image);
        stream.Position = 3;

        var read = await c.Async(stream, CancellationToken.None);

        await Assert.That(read).IsEqualTo(FromBitmap(c));
        await Assert.That(stream.Position).IsEqualTo(stream.Length);
    }

    [Test]
    [MethodDataSource(nameof(ReadCases))]
    public async Task UnexposedMemoryStream_ReadsWhatTheBitmapReads(ReadCase c)
    {
        // A MemoryStream made over an array hides it, so the read copies the stream as it does any other
        var stream = new MemoryStream(c.Image);
        await Assert.That(stream.TryGetBuffer(out _)).IsFalse();

        await Assert.That(await c.Async(stream, CancellationToken.None)).IsEqualTo(FromBitmap(c));
        stream.Position = 0;
        await Assert.That(c.Sync(stream)).IsEqualTo(FromBitmap(c));
    }

    [Test]
    public async Task ImageWithNoCode_ReturnsAFailedResultWithItsReason()
    {
        var result = await QRCodeDecoder.DecodeAsync(new MemoryStream(Blank()));

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Text).IsEqualTo(string.Empty);
        await Assert.That(result.Info.Status).IsEqualTo(DecodeStatus.NotDetected);
    }

    [Test]
    [MethodDataSource(nameof(ReadCases))]
    public async Task NotAnImage_ThrowsInvalidData(ReadCase c)
    {
        byte[][] notImages = [[], Encoding.UTF8.GetBytes("not an image, only text")];
        foreach (var bytes in notImages)
        {
            await Assert.That(await Caught(() => c.Async(new ReadStream(bytes), CancellationToken.None)) is InvalidDataException).IsTrue();
            await Assert.That(Caught(() => c.Sync(new ReadStream(bytes))) is InvalidDataException).IsTrue();
        }
    }

    [Test]
    [MethodDataSource(nameof(ReadCases))]
    public async Task ImageOverThePixelLimit_ThrowsInvalidDataBeforeDecoding(ReadCase c)
    {
        // A 1-bit PNG declaring 40,000 × 40,000 pixels: 1.6 GB as grey and 6.4 GB as colour. Only its first row is in it, since the header is what the read looks at
        var png = PngDeclaring(40_000, 40_000);
        var bounds = SKBitmap.DecodeBounds(png);
        await Assert.That(bounds.Width).IsEqualTo(40_000);
        await Assert.That(bounds.Height).IsEqualTo(40_000);

        // The size in the message is what tells the limit's refusal from a decode that ran and failed
        var viaAsync = await Caught(() => c.Async(new ReadStream(png), CancellationToken.None));
        var viaSync = Caught(() => c.Sync(new ReadStream(png)));
        await Assert.That(viaAsync is InvalidDataException && viaAsync.Message.Contains("40000 × 40000")).IsTrue();
        await Assert.That(viaSync is InvalidDataException && viaSync.Message.Contains("40000 × 40000")).IsTrue();
    }

    [Test]
    public async Task ImageOneRowOverThePixelLimit_ThrowsInvalidData()
    {
        // The documented limit is 16,384 × 16,384 pixels, so a row more is over it
        var png = PngDeclaring(16_384, 16_385);

        var caught = await Caught(() => QRCodeDecoder.DecodeAsync(new ReadStream(png)));

        await Assert.That(caught is InvalidDataException && caught.Message.Contains("16384 × 16385")).IsTrue();
    }

    [Test]
    public async Task SeekableStreamOverTheByteLimit_ThrowsInvalidDataBeforeReading()
    {
        // The documented limit is 256 MiB
        var asyncStream = new OverLengthStream(256L * 1024 * 1024 + 1);
        var syncStream = new OverLengthStream(256L * 1024 * 1024 + 1);

        await Assert.That(await Caught(() => QRCodeDecoder.DecodeAsync(asyncStream)) is InvalidDataException).IsTrue();
        await Assert.That(Caught(() => QRCodeDecoder.Decode(syncStream)) is InvalidDataException).IsTrue();
        await Assert.That(asyncStream.Reads + syncStream.Reads).IsEqualTo(0);
    }

    [Test]
    [NotInParallel(nameof(StreamThatDoesNotEnd_ThrowsInvalidDataOneBytePastTheLimit))]
    [Arguments(false)]
    [Arguments(true)]
    public async Task StreamThatDoesNotEnd_ThrowsInvalidDataOneBytePastTheLimit(bool async)
    {
        // The stream hands over whatever it is asked for without writing it, so the test costs the read's buffer and not a copy
        var stream = new EndlessStream();

        var caught = async ? await Caught(() => QRCodeDecoder.DecodeAsync(stream)) : Caught(() => QRCodeDecoder.Decode(stream));

        // One byte past the documented 256 MiB is what shows the stream goes over it
        await Assert.That(caught is InvalidDataException).IsTrue();
        await Assert.That(stream.Served).IsEqualTo(256L * 1024 * 1024 + 1);
    }

    [Test]
    public async Task NullOrUnreadableStream_ThrowsBeforeReturningATask()
    {
        var unreadable = new ReadStream([], canRead: false);

        // Actions that drop the task: an exception stored in it would not reach Assert.Throws
        Assert.Throws<ArgumentNullException>(() => { _ = QRCodeDecoder.DecodeAsync(null!); });
        Assert.Throws<ArgumentNullException>(() => { _ = QRCodeDecoder.DecodeSymbolsAsync(null!); });
        Assert.Throws<ArgumentNullException>(() => { _ = MicroQRCodeDecoder.DecodeAsync(null!); });
        Assert.Throws<ArgumentNullException>(() => { _ = RmQRCodeDecoder.DecodeAsync(null!); });
        Assert.Throws<ArgumentNullException>(() => QRCodeDecoder.Decode((Stream)null!));
        Assert.Throws<ArgumentNullException>(() => QRCodeDecoder.DecodeSymbols((Stream)null!));
        Assert.Throws<ArgumentException>(() => { _ = QRCodeDecoder.DecodeAsync(unreadable); });
        Assert.Throws<ArgumentException>(() => { _ = QRCodeDecoder.DecodeSymbolsAsync(unreadable); });
        Assert.Throws<ArgumentException>(() => { _ = MicroQRCodeDecoder.DecodeAsync(unreadable); });
        Assert.Throws<ArgumentException>(() => { _ = RmQRCodeDecoder.DecodeAsync(unreadable); });
        Assert.Throws<ArgumentException>(() => MicroQRCodeDecoder.Decode(unreadable));
        Assert.Throws<ArgumentException>(() => RmQRCodeDecoder.Decode(unreadable));
    }

    [Test]
    [MethodDataSource(nameof(ReadCases))]
    public async Task Async_CanceledBeforeTheCall_ReturnsACanceledTaskAndReadsNothing(ReadCase c)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var stream = new ReadStream(c.Image);

        var task = c.Async(stream, cancellation.Token);
        await Caught(() => task);

        await Assert.That(task.IsCanceled).IsTrue();
        await Assert.That(stream.ReadCalls).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Async_CanceledDuringARead_StopsTheRead(bool everySymbol)
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new ReadStream(QRCodeImageBuilder.GetPngBytes(Content))
        {
            OnRead = async token =>
            {
                entered.TrySetResult();
                // Bounded, so a read that never sees the token fails the test instead of hanging it
                await Task.Delay(TimeSpan.FromSeconds(10), token);
            },
        };

        Task task = everySymbol ? QRCodeDecoder.DecodeSymbolsAsync(stream, cancellation.Token) : QRCodeDecoder.DecodeAsync(stream, cancellation.Token);
        // A call that fails before its first read never enters it, and then the task is what ends the wait
        await Task.WhenAny(entered.Task, task);
        cancellation.Cancel();
        var caught = await Caught(() => task);

        await Assert.That(caught is OperationCanceledException).IsTrue();
        await Assert.That(stream.FirstToken).IsEqualTo(cancellation.Token);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Async_UnderASynchronizationContext_DecodesOffIt(bool everySymbol)
    {
        // An exposed MemoryStream is read with no await, so a task still running when the call returns has moved off the context
        using var context = new SingleThreadSynchronizationContext();
        var image = QRCodeImageBuilder.GetPngBytes(Content);
        var completedOnReturn = true;
        SynchronizationContext? caller = null;
        Task? task = null;

        await context.Run(async () =>
        {
            caller = SynchronizationContext.Current;
            task = Start(Exposed(image), everySymbol);
            completedOnReturn = task.IsCompleted;
            await task;
        });

        await Assert.That(caller).IsSameReferenceAs(context);
        await Assert.That(completedOnReturn).IsFalse();
        await Assert.That(task!.IsCompletedSuccessfully).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Async_UnderATaskScheduler_DecodesOffIt(bool everySymbol)
    {
        var scheduler = new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler;
        var image = QRCodeImageBuilder.GetPngBytes(Content);
        var completedOnReturn = true;
        TaskScheduler? caller = null;

        await Task.Factory.StartNew(
            () =>
            {
                caller = TaskScheduler.Current;
                var task = Start(Exposed(image), everySymbol);
                completedOnReturn = task.IsCompleted;
                return task;
            },
            CancellationToken.None,
            TaskCreationOptions.None,
            scheduler).Unwrap();

        await Assert.That(caller).IsSameReferenceAs(scheduler);
        await Assert.That(completedOnReturn).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Async_WithNoContext_DecodesInTheCall(bool everySymbol)
    {
        var image = QRCodeImageBuilder.GetPngBytes(Content);

        var (premise, completedOnReturn) = await Task.Run(async () =>
        {
            var premise = SynchronizationContext.Current is null && TaskScheduler.Current == TaskScheduler.Default;
            var task = Start(Exposed(image), everySymbol);
            var completedOnReturn = task.IsCompleted;
            await task;
            return (premise, completedOnReturn);
        });

        await Assert.That(premise).IsTrue();
        await Assert.That(completedOnReturn).IsTrue();
    }

    [Test]
    public async Task DecodeSymbols_Bitmap_ReadsEverySymbol()
    {
        var (sheet, texts) = DecodeImageSymbolsTest.Sheet(3, 2);
        using var _ = sheet;

        var results = QRCodeDecoder.DecodeSymbols(sheet);

        await Assert.That(results.Select(static r => r.Text)).IsEquivalentTo(texts, CollectionOrdering.Any);
        await Assert.That(results).IsEquivalentTo(QRCodeDecoder.DecodeImageSymbols(DecodeImageSymbolsTest.Luminance(sheet), sheet.Width, sheet.Height), CollectionOrdering.Matching);
    }

    [Test]
    public async Task DecodeSymbols_Stream_ReadsWhatTheBitmapFormReads()
    {
        var (sheet, _) = DecodeImageSymbolsTest.Sheet(3, 2);
        using var __ = sheet;
        using var data = sheet.Encode(SKEncodedImageFormat.Png, 100);
        var png = data.ToArray();
        var expected = QRCodeDecoder.DecodeSymbols(sheet);

        var viaAsync = await QRCodeDecoder.DecodeSymbolsAsync(new ReadStream(png, perRead: 4096, refuseSync: true));
        var viaSync = QRCodeDecoder.DecodeSymbols(new ReadStream(png, perRead: 4096, refuseAsync: true));

        await Assert.That(viaAsync).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(viaSync).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task DecodeSymbols_BitmapTooSmallForASymbol_ReadsNone()
    {
        using var tiny = new SKBitmap(10, 10);
        tiny.Erase(SKColors.White);

        await Assert.That(QRCodeDecoder.DecodeSymbols(tiny)).IsEmpty();
    }

    private static Task Start(Stream stream, bool everySymbol)
        => everySymbol ? QRCodeDecoder.DecodeSymbolsAsync(stream) : QRCodeDecoder.DecodeAsync(stream);

    private static MemoryStream Exposed(byte[] image)
    {
        var stream = new MemoryStream();
        stream.Write(image);
        stream.Position = 0;
        return stream;
    }

    private static async Task<Exception?> Caught(Func<Task> call)
    {
        try
        {
            await call();
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    private static Exception? Caught(Action call)
    {
        try
        {
            call();
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    private static byte[] PngDeclaring(int width, int height)
    {
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        // One bit a pixel, grey, and no interlace
        header[8] = 1;
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", Deflated(new byte[1 + (width + 7) / 8]));
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static byte[] Deflated(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(data);
        return output.ToArray();
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        png.Write(length);
        var typed = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        png.Write(typed);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
        png.Write(crc);
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc >> 1) ^ (0xEDB88320u & (0u - (crc & 1)));
        }
        return ~crc;
    }

    /// <summary>
    /// Hands its bytes over as few at a time as asked, through either kind of read or only one, and records what its async reads saw.
    /// </summary>
    private sealed class ReadStream(byte[] data, int perRead = int.MaxValue, bool seekable = false, bool refuseSync = false, bool refuseAsync = false, bool canRead = true) : Stream
    {
        private int _position;

        public Func<CancellationToken, Task>? OnRead { get; init; }
        public int ReadCalls { get; private set; }
        public CancellationToken FirstToken { get; private set; }

        public override bool CanRead => canRead;
        public override bool CanSeek => seekable;
        public override bool CanWrite => false;
        public override long Length => seekable ? data.Length : throw new NotSupportedException();
        public override long Position
        {
            get => seekable ? _position : throw new NotSupportedException();
            set => _position = seekable ? (int)value : throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (refuseSync)
                throw new InvalidOperationException("Synchronous reads are refused.");
            ReadCalls++;
            return Copy(buffer);
        }

        public override int ReadByte() => refuseSync ? throw new InvalidOperationException("Synchronous reads are refused.") : base.ReadByte();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (refuseAsync)
                throw new InvalidOperationException("Asynchronous reads are refused.");
            if (ReadCalls++ == 0)
                FirstToken = cancellationToken;
            if (OnRead is not null)
                await OnRead(cancellationToken);
            return Copy(buffer.Span);
        }

        private int Copy(Span<byte> buffer)
        {
            var count = Math.Min(Math.Min(buffer.Length, perRead), data.Length - _position);
            data.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (!seekable)
                throw new NotSupportedException();
            _position = (int)(origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, _ => data.Length + offset });
            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A seekable stream whose length is all it has, and which counts any read made of it.</summary>
    private sealed class OverLengthStream(long length) : Stream
    {
        public int Reads { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count) => Reads++ * 0;
        public override int Read(Span<byte> buffer) => Reads++ * 0;
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromResult(Reads++ * 0);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Reads++ * 0);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A stream that never ends and cannot seek, and that counts the bytes it claims to have handed over.</summary>
    private sealed class EndlessStream : Stream
    {
        public long Served { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Serve(count);
        public override int Read(Span<byte> buffer) => Serve(buffer.Length);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromResult(Serve(count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Serve(buffer.Length));

        private int Serve(int count)
        {
            Served += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
