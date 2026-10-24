#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
using System.Text;

namespace FeatherQR.Internals;

/// <summary>
/// An encoder fallback that hands an <see cref="Encoding"/> the same buffer of another fallback's on a thread from call to call.
/// </summary>
/// <remarks>
/// An <see cref="Encoding"/> makes a fallback buffer in each call that meets a char it replaces. An <see cref="Encoder"/> keeps its
/// buffer, but a thread makes one on its first call whatever the text, and each call costs more than the encoding's. Here a thread makes
/// a buffer only the first time it replaces a char, and the buffer is reset for each call, so a call that threw leaves nothing behind.
/// Only the encoding's own calls may take the buffer. An encoder from its GetEncoder would keep one thread's buffer and could use it on
/// another thread, so none is taken.
/// </remarks>
internal sealed class ThreadBufferFallback : EncoderFallback
{
    // Each thread keeps its own buffer, since a buffer is not safe to share.
    [ThreadStatic]
    private static EncoderFallbackBuffer? buffer;

    private readonly EncoderFallback inner;

    private ThreadBufferFallback(EncoderFallback inner) => this.inner = inner;

    /// <summary>UTF-8 as <see cref="Encoding.UTF8"/> writes it, a lone surrogate as U+FFFD.</summary>
    /// <remarks>
    /// A clone rather than <see cref="Encoding.GetEncoding(int, EncoderFallback, DecoderFallback)"/>, which asks the process's encoding
    /// providers first. A provider may answer the code page with another charset.
    /// </remarks>
    internal static readonly Encoding Utf8 = WithFallback(Encoding.UTF8);

    private static Encoding WithFallback(Encoding encoding)
    {
        var clone = (Encoding)encoding.Clone();
        clone.EncoderFallback = new ThreadBufferFallback(encoding.EncoderFallback);
        return clone;
    }

    public override int MaxCharCount => inner.MaxCharCount;

    public override EncoderFallbackBuffer CreateFallbackBuffer()
    {
        buffer ??= inner.CreateFallbackBuffer();
        buffer.Reset();
        return buffer;
    }
}
#endif
