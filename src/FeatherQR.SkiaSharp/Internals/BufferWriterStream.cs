using System.Buffers;

namespace FeatherQR.SkiaSharp.Internals;

/// <summary>
/// Write-only stream adapter over an <see cref="IBufferWriter{T}"/>.
/// Data is copied in writer-provided segments (via <see cref="BuffersExtensions.Write{T}"/>), so no single contiguous buffer is ever requested for the whole payload, segmented writers such as PipeWriter never receive an oversized GetSpan request.
/// </summary>
internal sealed class BufferWriterStream : Stream
{
    private readonly IBufferWriter<byte> _writer;

    public BufferWriterStream(IBufferWriter<byte> writer)
    {
        _writer = writer;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _writer.Write(buffer.AsSpan(offset, count));
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
