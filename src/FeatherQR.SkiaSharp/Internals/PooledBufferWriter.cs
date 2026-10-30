using System.Buffers;

namespace FeatherQR.SkiaSharp.Internals;

/// <summary>
/// A buffer that grows as it is written, from the pool but for a buffer of the most room past 1 MiB, for an image that has to be whole before an async write sends it or a read decodes it.
/// </summary>
/// <remarks>
/// The buffer is held across an await, so this class owns it. <see cref="Dispose"/> returns a buffer the pool lent, and a use after that throws instead of reaching an array the pool has handed on.
/// </remarks>
internal sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
{
    private const int DefaultCapacity = 16 * 1024;

    // Past this a buffer of the most room is allocated at its size. The pool rounds a size up to a power of two, so a limit just past one would take twice its room
    private const int LargestRoundedUp = 1024 * 1024;

    private readonly int _maxCapacity;
    private byte[]? _buffer;
    private bool _pooled;
    private int _written;

    /// <param name="capacity">The room to start with.</param>
    /// <param name="maxCapacity">The most room growth asks for beyond what a write needs.</param>
    public PooledBufferWriter(int capacity = DefaultCapacity, int maxCapacity = int.MaxValue)
    {
        _maxCapacity = maxCapacity;
        _buffer = Allocate(Math.Max(capacity, 1), out _pooled);
    }

    public int WrittenCount => _written;

    public ReadOnlySpan<byte> WrittenSpan => Buffer.AsSpan(0, _written);

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
        if (_pooled)
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
        var size = Math.Max(needed, Math.Min(2L * buffer.Length, _maxCapacity));
        var grown = Allocate((int)Math.Min(size, int.MaxValue), out var pooled);
        buffer.AsSpan(0, _written).CopyTo(grown);
        if (_pooled)
            ArrayPool<byte>.Shared.Return(buffer);
        _buffer = grown;
        _pooled = pooled;
        return grown;
    }

    private byte[] Allocate(int size, out bool pooled)
    {
        pooled = size < _maxCapacity || size <= LargestRoundedUp;
        return pooled ? ArrayPool<byte>.Shared.Rent(size) : new byte[size];
    }
}
