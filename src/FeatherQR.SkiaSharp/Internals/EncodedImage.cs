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
    /// <summary>The most bytes a stream read takes: 256 MiB.</summary>
    public const int MaxEncodedBytes = 256 * 1024 * 1024;

    /// <summary>The most pixels a stream read decodes: 16,384 × 16,384.</summary>
    public const long MaxPixels = 16_384L * 16_384;

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

    /// <summary>Reads the stream to its end, decodes the image and hands the bitmap to <paramref name="read"/>.</summary>
    public static TResult Read<TResult>(Stream image, Func<SKBitmap, TResult> read)
    {
        SKBitmap bitmap;
        using (var encoded = ReadToEnd(image))
            bitmap = Decode(encoded.Bytes);
        using (bitmap)
            return read(bitmap);
    }

    /// <summary>Reads the stream to its end through its async reads, then decodes the image and hands the bitmap to <paramref name="read"/>, off the caller's context.</summary>
    public static async Task<TResult> ReadAsync<TResult>(Stream image, Func<SKBitmap, TResult> read, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        SKBitmap bitmap;
        using (var encoded = await ReadToEndAsync(image, cancellationToken).ConfigureAwait(false))
        {
            // Reads that all completed at once leave the call on the caller's thread
            await CallerContext.Leave();
            cancellationToken.ThrowIfCancellationRequested();
            bitmap = Decode(encoded.Bytes);
        }
        using (bitmap)
            return read(bitmap);
    }

    private static EncodedImage ReadToEnd(Stream stream)
    {
        if (TryBorrow(stream, out var borrowed))
            return borrowed;

        var buffer = new PooledBufferWriter(InitialCapacity(stream), MaxEncodedBytes);
        try
        {
            while (true)
            {
                var room = Room(buffer);
                if (room.IsEmpty)
                {
                    // At the limit, one byte more says whether the stream goes past it
                    if (Read(stream, new byte[1]) > 0)
                        throw TooManyBytes();
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

    private static async Task<EncodedImage> ReadToEndAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (TryBorrow(stream, out var borrowed))
            return borrowed;

        var buffer = new PooledBufferWriter(InitialCapacity(stream), MaxEncodedBytes);
        try
        {
            while (true)
            {
                var room = Room(buffer);
                if (room.IsEmpty)
                {
                    // At the limit, one byte more says whether the stream goes past it
                    if (await ReadAsync(stream, new byte[1], cancellationToken).ConfigureAwait(false) > 0)
                        throw TooManyBytes();
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
    /// An exposed <see cref="MemoryStream"/>'s bytes from its position on, read in place with no copy. The stream is left at its end, as a read to the end leaves it.
    /// </summary>
    private static bool TryBorrow(Stream stream, [NotNullWhen(true)] out EncodedImage? image)
    {
        image = null;
        if (stream is not MemoryStream memory || !memory.TryGetBuffer(out var whole))
            return false;

        var start = (int)Math.Min(memory.Position, whole.Count);
        var bytes = new ArraySegment<byte>(whole.Array!, whole.Offset + start, whole.Count - start);
        if (bytes.Count > MaxEncodedBytes)
            throw TooManyBytes();

        memory.Position = memory.Length;
        image = new EncodedImage(bytes);
        return true;
    }

    private static int InitialCapacity(Stream stream)
    {
        if (!stream.CanSeek)
            return UnknownLengthCapacity;

        var remaining = Math.Max(stream.Length - stream.Position, 0);
        if (remaining > MaxEncodedBytes)
            throw TooManyBytes();

        // A byte over what is left, so the read that finds the end needs no room of its own
        return (int)Math.Min(remaining + 1, MaxEncodedBytes);
    }

    /// <summary>The room for the next read, never past the limit. Empty once the bytes reach it.</summary>
    private static Memory<byte> Room(PooledBufferWriter buffer)
    {
        var allowed = MaxEncodedBytes - buffer.WrittenCount;
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
    private static SKBitmap Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.IsEmpty)
            throw NotAnImage();

        var info = SKBitmap.DecodeBounds(encoded);
        if (info.Width <= 0 || info.Height <= 0)
            throw NotAnImage();
        if ((long)info.Width * info.Height > MaxPixels)
            throw new InvalidDataException($"The image is {info.Width} × {info.Height} pixels, more than the {MaxPixels:N0} a stream read decodes.");

        return SKBitmap.Decode(encoded) ?? throw NotAnImage();
    }

    private static InvalidDataException NotAnImage() => new("The stream does not hold an image that can be decoded.");

    private static InvalidDataException TooManyBytes() => new($"The stream holds more than {MaxEncodedBytes:N0} bytes, the most a stream read takes.");
}
