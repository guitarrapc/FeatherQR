using System.Buffers;

namespace FeatherQR.SkiaSharp.Internals;

/// <summary>
/// A buffer rented from the pool that grows as it is written, for an image that has to be whole before an async write sends it.
/// </summary>
/// <remarks>
/// The rental is held across the write's await, so this class owns it. <see cref="Dispose"/> returns it, and a use after that throws instead of reaching an array the pool has handed on.
/// </remarks>
internal sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
{
    private const int DefaultCapacity = 16 * 1024;

    private byte[]? _buffer;
    private int _written;

    public PooledBufferWriter(int capacity = DefaultCapacity)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(capacity, 1));
    }

    public int WrittenCount => _written;

    public void Advance(int count)
    {
        var buffer = Buffer;
        if (count < 0 || count > buffer.Length - _written)
            throw new ArgumentOutOfRangeException(nameof(count));
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0) => Reserve(sizeHint).AsMemory(_written);

    public Span<byte> GetSpan(int sizeHint = 0) => Reserve(sizeHint).AsSpan(_written);

#if NETSTANDARD2_0
    public Task WriteToAsync(Stream output, CancellationToken cancellationToken)
        => output.WriteAsync(Buffer, 0, _written, cancellationToken);
#else
    // The memory overload, since a stream that overrides only it serves the array overload with a synchronous write
    public ValueTask WriteToAsync(Stream output, CancellationToken cancellationToken)
        => output.WriteAsync(Buffer.AsMemory(0, _written), cancellationToken);
#endif

    public void Dispose()
    {
        var buffer = _buffer;
        if (buffer is null)
            return;
        _buffer = null;
        ArrayPool<byte>.Shared.Return(buffer);
    }

    private byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(PooledBufferWriter));

    private byte[] Reserve(int sizeHint)
    {
        var buffer = Buffer;
        var needed = (long)_written + Math.Max(sizeHint, 1);
        if (needed <= buffer.Length)
            return buffer;

        // Doubling holds the copying of a document written a little at a time to less than twice its size
        var size = Math.Max(needed, 2L * buffer.Length);
        var grown = ArrayPool<byte>.Shared.Rent((int)Math.Min(size, int.MaxValue));
        buffer.AsSpan(0, _written).CopyTo(grown);
        _buffer = grown;
        ArrayPool<byte>.Shared.Return(buffer);
        return grown;
    }
}
