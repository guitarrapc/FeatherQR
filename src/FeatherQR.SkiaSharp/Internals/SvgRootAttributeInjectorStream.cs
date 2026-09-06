using SkiaSharp;
using System.Text;

namespace FeatherQR.SkiaSharp.Internals;

/// <summary>
/// Write-only stream that forwards to an inner stream, injecting extra root element attributes immediately after the first <c>&lt;svg </c> marker.
/// </summary>
/// <remarks>
/// <see cref="SKSvgCanvas"/> writes <c>width</c>/<c>height</c> on the root element but no <c>viewBox</c>, and offers no hook to add attributes.
/// Only the document head is buffered, and only until the marker is complete (it appears within the first bytes of the document); everything after streams straight through, so the document is never held in memory as a whole.
/// If the marker does not appear within the first <see cref="MaxHeaderScan"/> bytes (unexpected upstream format change), the document is forwarded unmodified.
/// The inner stream is not disposed; dispose this wrapper (after the SVG canvas) to flush any pending header bytes.
/// </remarks>
internal sealed class SvgRootAttributeInjectorStream : Stream
{
    // The marker normally completes within the first ~50 bytes (XML declaration + "<svg ").
    private const int MaxHeaderScan = 512;

    private readonly Stream _inner;
    private byte[]? _attributeBytes;
    private byte[]? _header;
    private int _headerLength;

    public SvgRootAttributeInjectorStream(Stream inner, string rootAttributes)
    {
        _inner = inner;
        _attributeBytes = Encoding.UTF8.GetBytes(rootAttributes);
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
        // Injection already happened (or was abandoned): pure passthrough.
        if (_attributeBytes is null)
        {
            _inner.Write(buffer, offset, count);
            return;
        }

        // Accumulate into the header window; a write may split the marker at any
        // position, so the search always runs over the accumulated prefix.
        _header ??= new byte[MaxHeaderScan];
        while (count > 0)
        {
            var copyable = Math.Min(count, _header.Length - _headerLength);
            Buffer.BlockCopy(buffer, offset, _header, _headerLength, copyable);
            _headerLength += copyable;
            offset += copyable;
            count -= copyable;

            var index = _header.AsSpan(0, _headerLength).IndexOf("<svg "u8);
            if (index >= 0)
            {
                var insertAt = index + 5;
                _inner.Write(_header, 0, insertAt);
                _inner.Write(_attributeBytes, 0, _attributeBytes.Length);
                _inner.Write(_header, insertAt, _headerLength - insertAt);
                CompleteInjection();
                break;
            }

            if (_headerLength == _header.Length)
            {
                // No marker within the scan window, forward the document unmodified.
                _inner.Write(_header, 0, _headerLength);
                CompleteInjection();
                break;
            }
        }

        if (count > 0)
        {
            _inner.Write(buffer, offset, count);
        }
    }

    public override void Flush()
    {
        // Pending header bytes are intentionally held back: the marker may still
        // complete in a later write, and flushing them would forfeit the insertion point.
        _inner.Flush();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _header is not null && _headerLength > 0)
        {
            // The document ended inside the scan window without a marker, forward
            // it unmodified rather than dropping bytes.
            _inner.Write(_header, 0, _headerLength);
            CompleteInjection();
        }
        base.Dispose(disposing);
    }

    private void CompleteInjection()
    {
        _attributeBytes = null;
        _header = null;
        _headerLength = 0;
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
