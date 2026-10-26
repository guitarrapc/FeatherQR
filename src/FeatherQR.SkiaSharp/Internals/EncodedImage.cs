using System.Diagnostics.CodeAnalysis;
using SkiaSharp;

namespace FeatherQR.SkiaSharp.Internals;

/// <summary>
/// The encoded image a stream read takes whole, and its decode, under the limits of the stream reads.
/// </summary>
/// <remarks>
/// The limits apply where a read takes the caller's I/O over. A bitmap or luminance the caller hands in is memory already allocated, and keeps only the limit the decoders have.
/// </remarks>
internal sealed class EncodedImage : IDisposable
{
    // Where the room starts for a stream that cannot say its length
    private const int UnknownLengthCapacity = 64 * 1024;

    private readonly PooledBufferWriter? _owned;
    private readonly ArraySegment<byte> _borrowed;

    private EncodedImage(PooledBufferWriter owned) => _owned = owned;

    private EncodedImage(ArraySegment<byte> borrowed) => _borrowed = borrowed;

    public ReadOnlySpan<byte> Bytes => _owned is not null ? _owned.WrittenSpan : _borrowed.AsSpan();

    public void Dispose() => _owned?.Dispose();

    public static void ThrowIfUnreadable(Stream image)
    {
        if (image is null)
            throw new ArgumentNullException(nameof(image));
        if (!image.CanRead)
            throw new ArgumentException("Image stream must be readable", nameof(image));
    }

    /// <summary>Reads the stream to its end, decodes the image and hands the bitmap to <paramref name="read"/>, with no token.</summary>
    public static TResult Read<TResult>(Stream image, ImageStreamReadLimits limits, Func<SKBitmap, CancellationToken, TResult> read)
    {
        SKBitmap bitmap;
        using (var encoded = ReadToEnd(image, limits))
            bitmap = Decode(encoded.Bytes, limits);
        using (bitmap)
            return read(bitmap, CancellationToken.None);
    }

    /// <summary>Reads the stream to its end through its async reads, then decodes the image and hands the bitmap and the token to <paramref name="read"/>, off the caller's context.</summary>
    public static async Task<TResult> ReadAsync<TResult>(Stream image, ImageStreamReadLimits limits, Func<SKBitmap, CancellationToken, TResult> read, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        SKBitmap bitmap;
        var encoded = await ReadToEndAsync(image, limits, cancellationToken).ConfigureAwait(false);
        try
        {
            // Reads that all completed at once leave the call on the caller's thread
            var leave = CallerContext.Leave();
            // A call that leaves the caller's context decodes after it has returned, so it decodes a copy of a buffer the caller lent
            if (!leave.IsCompleted)
                encoded = encoded.Owned();
            await leave;
            cancellationToken.ThrowIfCancellationRequested();
            bitmap = Decode(encoded.Bytes, limits);
        }
        finally
        {
            encoded.Dispose();
        }
        using (bitmap)
            return read(bitmap, cancellationToken);
    }

    /// <summary>The image in a buffer of its own: a borrowed buffer is copied into one of its size, and one already its own is kept.</summary>
    private EncodedImage Owned()
    {
        if (_owned is not null)
            return this;

        var buffer = new PooledBufferWriter(_borrowed.Count, _borrowed.Count);
        _borrowed.AsSpan().CopyTo(buffer.GetSpan(_borrowed.Count));
        buffer.Advance(_borrowed.Count);
        return new EncodedImage(buffer);
    }

    private static EncodedImage ReadToEnd(Stream stream, ImageStreamReadLimits limits)
    {
        if (TryBorrow(stream, limits, out var borrowed))
            return borrowed;

        var buffer = new PooledBufferWriter(InitialCapacity(stream, limits), limits.MaxBytes);
        try
        {
            while (true)
            {
                var room = Room(buffer, limits);
                if (room.IsEmpty)
                {
                    // At the limit, one byte more says whether the stream goes past it
                    if (Read(stream, new byte[1]) > 0)
                        throw TooManyBytes(limits);
                    return new EncodedImage(buffer);
                }

                var read = Read(stream, room);
                if (read == 0)
                    return new EncodedImage(buffer);
                buffer.Advance(read);
            }
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    private static async Task<EncodedImage> ReadToEndAsync(Stream stream, ImageStreamReadLimits limits, CancellationToken cancellationToken)
    {
        if (TryBorrow(stream, limits, out var borrowed))
            return borrowed;

        var buffer = new PooledBufferWriter(InitialCapacity(stream, limits), limits.MaxBytes);
        try
        {
            while (true)
            {
                var room = Room(buffer, limits);
                if (room.IsEmpty)
                {
                    // At the limit, one byte more says whether the stream goes past it
                    if (await ReadAsync(stream, new byte[1], cancellationToken).ConfigureAwait(false) > 0)
                        throw TooManyBytes(limits);
                    return new EncodedImage(buffer);
                }

                var read = await ReadAsync(stream, room, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    return new EncodedImage(buffer);
                buffer.Advance(read);
            }
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    /// <summary>
    /// An exposed <see cref="MemoryStream"/>'s bytes from its position on, read in place with no copy. The stream is left where a read to its end leaves it.
    /// </summary>
    private static bool TryBorrow(Stream stream, ImageStreamReadLimits limits, [NotNullWhen(true)] out EncodedImage? image)
    {
        image = null;
        if (stream is not MemoryStream memory || !memory.TryGetBuffer(out var whole))
            return false;

        var start = (int)Math.Min(memory.Position, whole.Count);
        var bytes = new ArraySegment<byte>(whole.Array!, whole.Offset + start, whole.Count - start);
        if (bytes.Count > limits.MaxBytes)
            throw TooManyBytes(limits);

        // A read at a position past the end reads nothing and leaves the position there
        if (memory.Position < memory.Length)
            memory.Position = memory.Length;
        image = new EncodedImage(bytes);
        return true;
    }

    private static int InitialCapacity(Stream stream, ImageStreamReadLimits limits)
    {
        if (!stream.CanSeek)
            return Math.Min(UnknownLengthCapacity, limits.MaxBytes);

        var remaining = Math.Max(stream.Length - stream.Position, 0);
        if (remaining > limits.MaxBytes)
            throw TooManyBytes(limits);

        // A byte over what is left, so the read that finds the end needs no room of its own
        return (int)Math.Min(remaining + 1, limits.MaxBytes);
    }

    /// <summary>The room for the next read, never past the limit. Empty once the bytes reach it.</summary>
    private static Memory<byte> Room(PooledBufferWriter buffer, ImageStreamReadLimits limits)
    {
        var allowed = limits.MaxBytes - buffer.WrittenCount;
        if (allowed == 0)
            return Memory<byte>.Empty;

        var room = buffer.GetMemory(1);
        return room.Length > allowed ? room.Slice(0, allowed) : room;
    }

#if NETSTANDARD2_0
    private static int Read(Stream stream, Memory<byte> room)
    {
        System.Runtime.InteropServices.MemoryMarshal.TryGetArray<byte>(room, out var segment);
        return stream.Read(segment.Array!, segment.Offset, segment.Count);
    }

    private static Task<int> ReadAsync(Stream stream, Memory<byte> room, CancellationToken cancellationToken)
    {
        System.Runtime.InteropServices.MemoryMarshal.TryGetArray<byte>(room, out var segment);
        return stream.ReadAsync(segment.Array!, segment.Offset, segment.Count, cancellationToken);
    }
#else
    private static int Read(Stream stream, Memory<byte> room) => stream.Read(room.Span);

    // The memory overload, since a stream that overrides only it serves the array overload with a synchronous read
    private static ValueTask<int> ReadAsync(Stream stream, Memory<byte> room, CancellationToken cancellationToken)
        => stream.ReadAsync(room, cancellationToken);
#endif

    /// <summary>Decodes the image as <see cref="SKBitmap.Decode(ReadOnlySpan{byte})"/> does, once its header shows it within the pixel limit.</summary>
    private static unsafe SKBitmap Decode(ReadOnlySpan<byte> encoded, ImageStreamReadLimits limits)
    {
        if (encoded.IsEmpty)
            throw NothingLeft();

        // One codec gives the bounds and the pixels. DecodeBounds and Decode would each parse the file, and for an icon file of many images the parse is most of the cost
        fixed (byte* bytes = encoded)
        {
            using var data = SKData.Create((IntPtr)bytes, encoded.Length);
            using var codec = SKCodec.Create(data) ?? throw NotAnImage();
            var info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0)
                throw NotAnImage();
            if (IsOverPixelLimit(info.Width, info.Height, limits))
                throw new InvalidDataException($"The image is {info.Width} × {info.Height} pixels, more than the {limits.MaxPixels:N0} the read decodes. Give the read an {nameof(ImageStreamReadLimits)} with a larger {nameof(ImageStreamReadLimits.MaxPixels)} to read it.");
            // Skia allocates no bitmap of more than int.MaxValue bytes, and past that throws an exception that names no reason
            if (info.BytesSize64 > int.MaxValue)
                throw new InvalidDataException($"The image is {info.Width} × {info.Height} pixels, more than a bitmap holds.");

            return SKBitmap.Decode(codec) ?? throw NotAnImage();
        }
    }

    internal static bool IsOverPixelLimit(int width, int height, ImageStreamReadLimits limits) => (long)width * height > limits.MaxPixels;

    private static InvalidDataException NothingLeft() => new("The stream has nothing left to read from its position.");

    private static InvalidDataException NotAnImage() => new("The stream does not hold an image that can be decoded.");

    private static InvalidDataException TooManyBytes(ImageStreamReadLimits limits) => new($"The stream holds more than {limits.MaxBytes:N0} bytes, the most the read takes. Give the read an {nameof(ImageStreamReadLimits)} with a larger {nameof(ImageStreamReadLimits.MaxBytes)} to read it.");
}
