using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using FeatherQR.SkiaSharp;
using FeatherQR.SkiaSharp.Internals;
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

    /// <param name="Start">The public async call itself, with no async wrapper that would turn an exception thrown at the call into a faulted task.</param>
    public sealed record ReadCase(string Name, byte[] Image, Func<SKBitmap, Read> FromBitmap, Func<Stream, CancellationToken, Task> Start, Func<Stream, CancellationToken, Task<Read>> Async, Func<Stream, Read> Sync)
    {
        public override string ToString() => Name;
    }

    // The big reads each hold a buffer of the byte limit or more, so they run one at a time
    private const string LargeReads = "Reads at the byte limit";

    // The documented default byte limit, 64 MiB
    private const int DefaultMaxBytes = 64 * 1024 * 1024;

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
        Func<Stream, CancellationToken, Task> qr = static (s, t) => QRCodeDecoder.DecodeAsync(s, t);
        Func<Stream, CancellationToken, Task> micro = static (s, t) => MicroQRCodeDecoder.DecodeAsync(s, t);
        Func<Stream, CancellationToken, Task> rmqr = static (s, t) => RmQRCodeDecoder.DecodeAsync(s, t);
        yield return () => new("QR PNG", QRCodeImageBuilder.GetPngBytes(Content), QRBitmap, qr, QRAsync, QRSync);
        yield return () => new("QR JPEG", QRCodeImageBuilder.GetImageBytes(Content, SKEncodedImageFormat.Jpeg, quality: 90), QRBitmap, qr, QRAsync, QRSync);
        yield return () => new("QR blank", Blank(), QRBitmap, qr, QRAsync, QRSync);
        yield return () => new("Micro PNG", MicroQRCodeImageBuilder.GetPngBytes("12345"), MicroBitmap, micro, MicroAsync, MicroSync);
        yield return () => new("Micro blank", Blank(), MicroBitmap, micro, MicroAsync, MicroSync);
        yield return () => new("RmQR PNG", RmQRCodeImageBuilder.GetPngBytes(Content), RmQRBitmap, rmqr, RmQRAsync, RmQRSync);
        yield return () => new("RmQR blank", Blank(), RmQRBitmap, rmqr, RmQRAsync, RmQRSync);
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
    public async Task ExposedMemoryStreamOverPartOfAnArray_ReadsItsOwnBytes(ReadCase c)
    {
        var array = new byte[3 + c.Image.Length];
        c.Image.CopyTo(array, 3);
        var stream = new MemoryStream(array, 3, c.Image.Length, writable: false, publiclyVisible: true);
        // Premise: the stream lends its array, and its first byte is not the array's
        await Assert.That(stream.TryGetBuffer(out var lent) && lent.Offset == 3).IsTrue();

        await Assert.That(await c.Async(stream, CancellationToken.None)).IsEqualTo(FromBitmap(c));
        stream.Position = 0;
        await Assert.That(c.Sync(stream)).IsEqualTo(FromBitmap(c));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MemoryStreamPositionedPastItsEnd_IsLeftWhereItWas(bool exposed)
    {
        var image = QRCodeImageBuilder.GetPngBytes(Content);
        var stream = exposed ? Exposed(image) : new MemoryStream(image);
        // A read at a position past the end reads nothing and leaves the position as it is
        stream.Position = stream.Length + 10;
        var past = stream.Position;

        var caught = await Caught(QRCodeDecoder.DecodeAsync(stream));

        await Assert.That(caught is InvalidDataException).IsTrue();
        await Assert.That(stream.Position).IsEqualTo(past);
    }

    [Test]
    [MethodDataSource(nameof(ReadCases))]
    public async Task StreamWithNothingLeftToRead_SaysSo(ReadCase c)
    {
        // A stream just written and not rewound, the common way to hand over nothing
        var written = new MemoryStream();
        written.Write(c.Image);
        Stream[] nothingLeft = [new ReadStream([]), written];

        foreach (var stream in nothingLeft)
        {
            var viaAsync = await Caught(c.Start(stream, CancellationToken.None));
            var viaSync = Caught(() => c.Sync(stream));
            await Assert.That(viaAsync is InvalidDataException && viaAsync.Message.Contains("nothing left to read")).IsTrue();
            await Assert.That(viaSync is InvalidDataException && viaSync.Message.Contains("nothing left to read")).IsTrue();
        }
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
    public async Task NotAnImage_FaultsTheTaskWithInvalidData(ReadCase c)
    {
        byte[][] notImages = [[], Encoding.UTF8.GetBytes("not an image, only text")];
        foreach (var bytes in notImages)
        {
            await Assert.That(await Caught(c.Start(new ReadStream(bytes), CancellationToken.None)) is InvalidDataException).IsTrue();
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
        var viaAsync = await Caught(c.Start(new ReadStream(png), CancellationToken.None));
        var viaSync = Caught(() => c.Sync(new ReadStream(png)));
        await Assert.That(viaAsync is InvalidDataException && viaAsync.Message.Contains("40000 × 40000")).IsTrue();
        await Assert.That(viaSync is InvalidDataException && viaSync.Message.Contains("40000 × 40000")).IsTrue();
    }

    [Test]
    [Arguments(4_096, 4_096, false)]
    [Arguments(4_096, 4_097, true)]
    [Arguments(2_048, 8_192, false)]
    [Arguments(2_048, 8_193, true)]
    public async Task PixelLimit_IsTheDocumentedCount(int width, int height, bool over)
    {
        // The documented limit is 16,777,216 pixels (4,096 × 4,096). A 1-bit grey image at it decodes to 16 MiB, so the read itself is held to the boundary
        var caught = Caught(() => QRCodeDecoder.Decode(new ReadStream(PngDeclaring(width, height))));

        var refused = caught is InvalidDataException && caught.Message.Contains($"{width} × {height}");
        await Assert.That(refused).IsEqualTo(over);
        if (!over)
            await Assert.That(caught).IsNull();
    }

    [Test]
    public async Task ImageOverThePixelLimit_SaysHowToReadIt()
    {
        var caught = await Caught(QRCodeDecoder.DecodeAsync(new ReadStream(PngDeclaring(4_096, 4_097))));

        await Assert.That(caught is InvalidDataException && caught.Message.Contains(nameof(ImageStreamReadLimits.MaxPixels))).IsTrue();
    }

    [Test]
    public async Task PixelCheck_TakesTheLimitGiven()
    {
        var raised = new ImageStreamReadLimits { MaxPixels = 16_384L * 16_385 };

        await Assert.That(EncodedImage.IsOverPixelLimit(16_384, 16_385, raised)).IsFalse();
        await Assert.That(EncodedImage.IsOverPixelLimit(16_384, 16_386, raised)).IsTrue();
        await Assert.That(EncodedImage.IsOverPixelLimit(int.MaxValue, int.MaxValue, raised)).IsTrue();
    }

    [Test]
    [MethodDataSource(nameof(LimitedCalls))]
    public async Task RaisedPixelLimit_ReachesTheBitmapCheck(LimitedCall c)
    {
        // 46,341 × 46,341 grey pixels are over the default limit and over the 2,147,483,647 bytes a bitmap holds, so the message says which refused them
        var raised = new ImageStreamReadLimits { MaxPixels = long.MaxValue };

        var caught = await c.Fault(new ReadStream(PngDeclaring(46_341, 46_341)), raised);

        await Assert.That(caught is InvalidDataException && caught.Message.Contains("more than a bitmap holds")).IsTrue();
    }

    [Test]
    [NotInParallel(LargeReads)]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RaisedByteLimit_ReadsAStreamOverTheDefault(bool async)
    {
        var limits = new ImageStreamReadLimits { MaxBytes = DefaultMaxBytes + 1 };
        var stream = new EndlessStream(DefaultMaxBytes + 1);

        var caught = async ? await Caught(QRCodeDecoder.DecodeAsync(stream, limits)) : Caught(() => QRCodeDecoder.Decode(stream, limits));

        await Assert.That(caught is InvalidDataException && caught.Message.Contains("does not hold an image")).IsTrue();
        await Assert.That(stream.Served).IsEqualTo(DefaultMaxBytes + 1);
    }

    [Test]
    public async Task Limits_Default_IsTheDocumentedOne()
    {
        await Assert.That(ImageStreamReadLimits.Default.MaxBytes).IsEqualTo(64 * 1024 * 1024);
        await Assert.That(ImageStreamReadLimits.Default.MaxPixels).IsEqualTo(4_096L * 4_096);
        await Assert.That(new ImageStreamReadLimits()).IsEqualTo(ImageStreamReadLimits.Default);
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task Limits_NotPositive_Throw(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new ImageStreamReadLimits { MaxBytes = value });
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new ImageStreamReadLimits { MaxPixels = value });
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new ImageStreamReadLimits(maxBytes: value));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new ImageStreamReadLimits(maxPixels: value));
    }

    [Test]
    public async Task Limits_MaxBytes_IsAtMostTheLongestArray()
    {
        // The bytes are held in one array, and no array is longer than 2,147,483,591
        await Assert.That(new ImageStreamReadLimits { MaxBytes = 2_147_483_591 }.MaxBytes).IsEqualTo(2_147_483_591);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new ImageStreamReadLimits { MaxBytes = 2_147_483_592 });
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new ImageStreamReadLimits { MaxBytes = int.MaxValue });
        await Assert.That(new ImageStreamReadLimits { MaxBytes = 1, MaxPixels = 1 }).IsEqualTo(new ImageStreamReadLimits(maxBytes: 1, maxPixels: 1));
    }

    [Test]
    public async Task Limits_NotPositive_NameTheValueTheCallerGave()
    {
        await Assert.That(Assert.Throws<ArgumentOutOfRangeException>(() => _ = new ImageStreamReadLimits(maxBytes: 0)).ParamName).IsEqualTo("maxBytes");
        await Assert.That(Assert.Throws<ArgumentOutOfRangeException>(() => _ = new ImageStreamReadLimits(maxPixels: 0)).ParamName).IsEqualTo("maxPixels");
        await Assert.That(Assert.Throws<ArgumentOutOfRangeException>(() => _ = new ImageStreamReadLimits { MaxBytes = 0 }).ParamName).IsEqualTo("MaxBytes");
        await Assert.That(Assert.Throws<ArgumentOutOfRangeException>(() => _ = new ImageStreamReadLimits { MaxPixels = 0 }).ParamName).IsEqualTo("MaxPixels");
    }

    [Test]
    public async Task Limits_HaveAConstructorWithNoParameters()
    {
        // An initializer that binds to it puts no default into the caller's code, where one bound to the optional parameters would keep the default it was compiled with
        await Assert.That(typeof(ImageStreamReadLimits).GetConstructor(Type.EmptyTypes)).IsNotNull();
    }

    [Test]
    public async Task Async_WithNoContext_ReadsAnExposedBufferInPlace()
    {
        // 4 MiB after the image is in the stream's bytes, so a copy of them allocates that much, where reading in place allocates far less
        var png = QRCodeImageBuilder.GetPngBytes(Content);
        var array = new byte[png.Length + 4 * 1024 * 1024];
        png.CopyTo(array, 0);

        var (premise, allocated, read) = await Task.Run(async () =>
        {
            var stream = new MemoryStream(array, 0, array.Length, writable: false, publiclyVisible: true);
            var premise = SynchronizationContext.Current is null && TaskScheduler.Current == TaskScheduler.Default;
            // A call that does not leave completes on this thread, so the thread's count is the call's
            var before = GC.GetAllocatedBytesForCurrentThread();
            var task = QRCodeDecoder.DecodeAsync(stream);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            return (premise && task.IsCompleted, allocated, await task);
        });

        await Assert.That(premise).IsTrue();
        await Assert.That(read.Success).IsTrue();
        await Assert.That(allocated).IsLessThan(1024 * 1024);
    }

    [Test]
    public async Task Limits_ConstructorAgreesWithTheObjectInitializer()
    {
        // Distinct values, so a crossing shows up as a wrong property
        await Assert.That(new ImageStreamReadLimits(maxBytes: 1000, maxPixels: 4000)).IsEqualTo(new ImageStreamReadLimits { MaxBytes = 1000, MaxPixels = 4000 });
        await Assert.That(new ImageStreamReadLimits(maxPixels: 4000)).IsEqualTo(new ImageStreamReadLimits { MaxPixels = 4000 });
        await Assert.That(new ImageStreamReadLimits(maxBytes: 1000)).IsEqualTo(new ImageStreamReadLimits { MaxBytes = 1000 });
    }

    public sealed record LimitedCall(string Name, Func<Stream, ImageStreamReadLimits, Task> Call)
    {
        public async Task<Exception?> Fault(Stream stream, ImageStreamReadLimits limits)
        {
            try
            {
                await Call(stream, limits);
                return null;
            }
            catch (Exception e)
            {
                return e;
            }
        }

        public override string ToString() => Name;
    }

    public static IEnumerable<Func<LimitedCall>> LimitedCalls()
    {
        yield return () => new("QR DecodeAsync", static (s, l) => QRCodeDecoder.DecodeAsync(s, l));
        yield return () => new("QR Decode", static (s, l) => Task.FromResult(QRCodeDecoder.Decode(s, l)));
        yield return () => new("QR DecodeSymbolsAsync", static (s, l) => QRCodeDecoder.DecodeSymbolsAsync(s, l));
        yield return () => new("QR DecodeSymbols", static (s, l) => Task.FromResult(QRCodeDecoder.DecodeSymbols(s, l)));
        yield return () => new("Micro DecodeAsync", static (s, l) => MicroQRCodeDecoder.DecodeAsync(s, l));
        yield return () => new("Micro Decode", static (s, l) => Task.FromResult(MicroQRCodeDecoder.Decode(s, l)));
        yield return () => new("RmQR DecodeAsync", static (s, l) => RmQRCodeDecoder.DecodeAsync(s, l));
        yield return () => new("RmQR Decode", static (s, l) => Task.FromResult(RmQRCodeDecoder.Decode(s, l)));
    }

    [Test]
    [MethodDataSource(nameof(LimitedCalls))]
    public async Task LowerPixelLimit_RefusesAnImageOverIt_AndTakesOneAtIt(LimitedCall c)
    {
        var limits = new ImageStreamReadLimits { MaxPixels = 300 * 300 };

        var over = await c.Fault(new ReadStream(PngDeclaring(300, 301)), limits);
        var at = await c.Fault(new ReadStream(PngDeclaring(300, 300)), limits);

        await Assert.That(over is InvalidDataException && over.Message.Contains("300 × 301")).IsTrue();
        await Assert.That(at).IsNull();
    }

    [Test]
    [MethodDataSource(nameof(LimitedCalls))]
    public async Task LowerByteLimit_RefusesAStreamOverIt_AndTakesOneAtIt(LimitedCall c)
    {
        var limits = new ImageStreamReadLimits { MaxBytes = 1000 };
        var seekable = new OverLengthStream(1001);

        // Zeros: one at the limit is read whole and refused as no image, which the message tells from the byte limit's refusal
        var at = await c.Fault(new EndlessStream(1000), limits);
        var seekableAt = await c.Fault(new MemoryStream(new byte[1000]), limits);
        var over = await c.Fault(new EndlessStream(1001), limits);
        var seekableOver = await c.Fault(seekable, limits);
        var exposedOver = await c.Fault(new MemoryStream(new byte[1001], 0, 1001, writable: false, publiclyVisible: true), limits);

        await Assert.That(at is InvalidDataException && at.Message.Contains("does not hold an image")).IsTrue();
        await Assert.That(seekableAt is InvalidDataException && seekableAt.Message.Contains("does not hold an image")).IsTrue();
        await Assert.That(over is InvalidDataException && over.Message.Contains("more than")).IsTrue();
        await Assert.That(seekableOver is InvalidDataException && seekableOver.Message.Contains("more than")).IsTrue();
        await Assert.That(seekable.Reads).IsEqualTo(0);
        await Assert.That(exposedOver is InvalidDataException && exposedOver.Message.Contains("more than")).IsTrue();
    }

    [Test]
    [MethodDataSource(nameof(LimitedCalls))]
    public async Task NullLimits_ReadUnderTheDefaultLimits(LimitedCall c)
    {
        // The default limits refuse this image, so a read that took null for no limits would decode it
        var overTheDefault = await c.Fault(new ReadStream(PngDeclaring(4_096, 4_097)), null!);
        var withinThem = await c.Fault(new ReadStream(QRCodeImageBuilder.GetPngBytes(Content)), null!);

        await Assert.That(overTheDefault is InvalidDataException && overTheDefault.Message.Contains("4096 × 4097")).IsTrue();
        await Assert.That(withinThem).IsNull();
    }

    [Test]
    public async Task DefaultInTheSecondPlace_ReadsAlikeOnTheSyncAndAsyncForms()
    {
        // default binds to the token on the async form and to the limits on the sync one
        var png = QRCodeImageBuilder.GetPngBytes(Content);

        var viaAsync = await QRCodeDecoder.DecodeAsync(new MemoryStream(png), default);
        var viaSync = QRCodeDecoder.Decode(new MemoryStream(png), default);

        await Assert.That(viaSync).IsEqualTo(viaAsync);
        await Assert.That(viaSync.Success).IsTrue();
    }

    [Test]
    public async Task SeekableStreamOverTheByteLimit_ThrowsInvalidDataBeforeReading()
    {
        var asyncStream = new OverLengthStream(DefaultMaxBytes + 1);
        var syncStream = new OverLengthStream(DefaultMaxBytes + 1);

        var viaAsync = await Caught(QRCodeDecoder.DecodeAsync(asyncStream));
        var viaSync = Caught(() => QRCodeDecoder.Decode(syncStream));

        // The message names the limit to raise
        await Assert.That(viaAsync is InvalidDataException && viaAsync.Message.Contains(nameof(ImageStreamReadLimits.MaxBytes))).IsTrue();
        await Assert.That(viaSync is InvalidDataException && viaSync.Message.Contains(nameof(ImageStreamReadLimits.MaxBytes))).IsTrue();
        await Assert.That(asyncStream.Reads + syncStream.Reads).IsEqualTo(0);
    }

    [Test]
    [NotInParallel(LargeReads)]
    [Arguments(false)]
    [Arguments(true)]
    public async Task StreamThatDoesNotEnd_ThrowsInvalidDataOneBytePastTheLimit(bool async)
    {
        // The stream serves zeros from no data of its own, so the test costs the read's buffer alone
        var stream = new EndlessStream();

        var caught = async ? await Caught(QRCodeDecoder.DecodeAsync(stream)) : Caught(() => QRCodeDecoder.Decode(stream));

        // One byte past the documented 64 MiB is what shows the stream goes over it
        await Assert.That(caught is InvalidDataException && caught.Message.Contains("more than")).IsTrue();
        await Assert.That(stream.Served).IsEqualTo(DefaultMaxBytes + 1);
    }

    [Test]
    [NotInParallel(LargeReads)]
    [Arguments(false)]
    [Arguments(true)]
    public async Task StreamOfExactlyTheByteLimit_IsReadWhole(bool async)
    {
        // 64 MiB of zeros: taken whole, then refused as no image, which the message tells from the byte limit's refusal
        var stream = new EndlessStream(DefaultMaxBytes);

        var caught = async ? await Caught(QRCodeDecoder.DecodeAsync(stream)) : Caught(() => QRCodeDecoder.Decode(stream));

        await Assert.That(caught is InvalidDataException && caught.Message.Contains("does not hold an image")).IsTrue();
        await Assert.That(stream.Served).IsEqualTo(DefaultMaxBytes);
    }

    [Test]
    [NotInParallel(LargeReads)]
    [Arguments(0, "does not hold an image")]
    [Arguments(1, "more than")]
    public async Task ExposedMemoryStreamAtTheByteLimit_IsTakenAndOneBytePastIsNot(int past, string message)
    {
        var length = DefaultMaxBytes + past;
        var stream = new MemoryStream(new byte[length], 0, length, writable: false, publiclyVisible: true);

        var caught = await Caught(QRCodeDecoder.DecodeAsync(stream));

        await Assert.That(caught is InvalidDataException && caught.Message.Contains(message)).IsTrue();
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

        // The public call itself, so an exception thrown at the call escapes and fails the test
        var task = c.Start(stream, cancellation.Token);
        await Caught(task);

        await Assert.That(task.IsCanceled).IsTrue();
        await Assert.That(stream.ReadCalls).IsEqualTo(0);
    }

    [Test]
    public async Task DecodeSymbolsAsync_CanceledOrNotAnImage_ComesThroughTheTask()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var stream = new ReadStream(QRCodeImageBuilder.GetPngBytes(Content));

        // The public call itself, so an exception thrown at the call escapes and fails the test
        var canceled = QRCodeDecoder.DecodeSymbolsAsync(stream, cancellation.Token);
        var notAnImage = QRCodeDecoder.DecodeSymbolsAsync(new ReadStream(Encoding.UTF8.GetBytes("not an image")));

        await Caught(canceled);
        await Assert.That(canceled.IsCanceled).IsTrue();
        await Assert.That(stream.ReadCalls).IsEqualTo(0);
        await Assert.That(await Caught(notAnImage) is InvalidDataException).IsTrue();
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
        var caught = await Caught(task);

        await Assert.That(caught is OperationCanceledException).IsTrue();
        await Assert.That(stream.FirstToken).IsEqualTo(cancellation.Token);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Async_UnderASynchronizationContext_DecodesOffIt(bool everySymbol)
    {
        // An exposed MemoryStream is read with no await, so a task still running when the call returns has moved off the context.
        // The caller then blocks the context's one thread on the task, so a decode or a continuation sent back to that thread would never run
        using var context = new SingleThreadSynchronizationContext();
        var image = QRCodeImageBuilder.GetPngBytes(Content);
        var completedOnReturn = true;
        var finished = false;
        SynchronizationContext? caller = null;

        await context.Run(() =>
        {
            caller = SynchronizationContext.Current;
            var task = Start(Exposed(image), everySymbol);
            completedOnReturn = task.IsCompleted;
            finished = task.Wait(TimeSpan.FromSeconds(10));
            return Task.CompletedTask;
        });

        await Assert.That(caller).IsSameReferenceAs(context);
        await Assert.That(completedOnReturn).IsFalse();
        await Assert.That(finished).IsTrue();
    }

    [Test]
    [Arguments(Caller.SynchronizationContext, false, false)]
    [Arguments(Caller.SynchronizationContext, true, false)]
    [Arguments(Caller.SynchronizationContext, false, true)]
    [Arguments(Caller.TaskScheduler, false, false)]
    [Arguments(Caller.TaskScheduler, true, true)]
    public async Task Async_LeavingTheCaller_ExposedBufferClearedOnceTheCallReturns_ReadsWhatItHeld(Caller caller, bool everySymbol, bool offset)
    {
        var png = QRCodeImageBuilder.GetPngBytes(Content);
        // The decode starts on the pool once the call returns, and a clear that comes later proves nothing, so the call is made many times
        const int Tries = 20;
        var cleared = 0;
        var read = 0;

        await Callers.RunUnder(caller, async () =>
        {
            for (var i = 0; i < Tries; i++)
            {
                // Bytes past the array's start and the stream's position, as a stream over part of an array lends them
                var array = new byte[(offset ? 5 : 0) + png.Length];
                png.CopyTo(array, offset ? 5 : 0);
                var stream = offset
                    ? new MemoryStream(array, 3, png.Length + 2, writable: true, publiclyVisible: true) { Position = 2 }
                    : new MemoryStream(array, 0, array.Length, writable: true, publiclyVisible: true);

                var decode = everySymbol ? (Task)QRCodeDecoder.DecodeSymbolsAsync(stream) : QRCodeDecoder.DecodeAsync(stream);
                if (!decode.IsCompleted)
                {
                    Array.Clear(array);
                    cleared++;
                }
                var readWhole = everySymbol
                    ? await (Task<QRCodeDecodeResult[]>)decode is [{ Success: true, Text: Content }]
                    : await (Task<QRCodeDecodeResult>)decode is { Success: true, Text: Content };
                if (readWhole)
                    read++;
            }
        });

        // Premise: some call left the caller before its buffer was cleared
        await Assert.That(cleared).IsGreaterThan(0);
        await Assert.That(read).IsEqualTo(Tries);
    }

    [Test]
    public async Task Async_CanceledOnceTheReadsEnd_StopsBeforeTheDecode()
    {
        // The token is canceled by the read that finds the end, so the check after the reads is what sees it
        using var cancellation = new CancellationTokenSource();
        var stream = new ReadStream(QRCodeImageBuilder.GetPngBytes(Content)) { OnEnd = cancellation.Cancel };

        var caught = await Caught(QRCodeDecoder.DecodeAsync(stream, cancellation.Token));

        await Assert.That(caught is OperationCanceledException).IsTrue();
        await Assert.That(stream.Ended).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Async_UnderATaskScheduler_DecodesOffIt(bool everySymbol)
    {
        // The exclusive scheduler runs one task at a time, and the caller's task blocks it, so work queued to it would never run
        var scheduler = new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler;
        var image = QRCodeImageBuilder.GetPngBytes(Content);
        var completedOnReturn = true;
        var finished = false;
        TaskScheduler? caller = null;

        await Task.Factory.StartNew(
            () =>
            {
                caller = TaskScheduler.Current;
                var task = Start(Exposed(image), everySymbol);
                completedOnReturn = task.IsCompleted;
                finished = task.Wait(TimeSpan.FromSeconds(10));
            },
            CancellationToken.None,
            TaskCreationOptions.None,
            scheduler);

        await Assert.That(caller).IsSameReferenceAs(scheduler);
        await Assert.That(completedOnReturn).IsFalse();
        await Assert.That(finished).IsTrue();
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

    // Takes the task, not a call that makes it, so an exception thrown at the call escapes and fails the test instead of passing for the task's
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
    /// Its async read is the memory overload alone, so an async read through the array overload falls to the synchronous read.
    /// </summary>
    private sealed class ReadStream(byte[] data, int perRead = int.MaxValue, bool seekable = false, bool refuseSync = false, bool refuseAsync = false, bool canRead = true) : Stream
    {
        private int _position;

        public Func<CancellationToken, Task>? OnRead { get; init; }
        public Action? OnEnd { get; init; }
        public bool Ended { get; private set; }
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

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (refuseAsync)
                throw new InvalidOperationException("Asynchronous reads are refused.");
            if (ReadCalls++ == 0)
                FirstToken = cancellationToken;
            if (OnRead is not null)
                await OnRead(cancellationToken);
            var copied = Copy(buffer.Span);
            if (copied == 0 && buffer.Length > 0)
            {
                Ended = true;
                OnEnd?.Invoke();
            }
            return copied;
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

    /// <summary>
    /// A stream of zeros that ends only at the length it is given, if any, cannot seek, and counts the bytes it served.
    /// It writes the zeros, since a read's buffer is rented and may hold the bytes of an earlier read.
    /// </summary>
    private sealed class EndlessStream(long length = long.MaxValue) : Stream
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

        public override int Read(byte[] buffer, int offset, int count) => Serve(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer) => Serve(buffer);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromResult(Serve(buffer.AsSpan(offset, count)));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Serve(buffer.Span));

        private int Serve(Span<byte> buffer)
        {
            var served = (int)Math.Min(buffer.Length, length - Served);
            buffer.Slice(0, served).Clear();
            Served += served;
            return served;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
