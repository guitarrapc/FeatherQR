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
/// Only the encodings' own calls may take the buffer. An encoder from their GetEncoder would keep one thread's buffer and could use it
/// on another thread, so none is taken.
/// </remarks>
internal sealed class ThreadBufferFallback : EncoderFallback
{
    // Each thread keeps one buffer for each encoding, since a buffer is not safe to share.
    [ThreadStatic]
    private static EncoderFallbackBuffer? utf8Buffer;
    [ThreadStatic]
    private static EncoderFallbackBuffer? latin1Buffer;

    private readonly EncoderFallback inner;
    private readonly bool latin1;

    private ThreadBufferFallback(EncoderFallback inner, bool latin1)
    {
        this.inner = inner;
        this.latin1 = latin1;
    }

    /// <summary>UTF-8 as <see cref="Encoding.UTF8"/> writes it, a lone surrogate as U+FFFD.</summary>
    internal static readonly Encoding Utf8 = WithFallback(Encoding.UTF8, new ThreadBufferFallback(Encoding.UTF8.EncoderFallback, latin1: false));

#if NET5_0_OR_GREATER
    /// <summary>
    /// ISO-8859-1 with '?' for each char above U+00FF, which the Structured Append parity counts. <see cref="Encoding.Latin1"/> writes a
    /// letter with an ASCII look-alike as that letter, 'ž' as 'z'.
    /// </summary>
    internal static readonly Encoding Latin1 = WithFallback(Encoding.Latin1, new ThreadBufferFallback(new EncoderReplacementFallback("?"), latin1: true));
#endif

    // The encoding is cloned rather than taken from Encoding.GetEncoding, which asks the process's encoding providers first. A provider
    // may answer the code page with another charset.
    private static Encoding WithFallback(Encoding encoding, EncoderFallback fallback)
    {
        var clone = (Encoding)encoding.Clone();
        clone.EncoderFallback = fallback;
        return clone;
    }

    public override int MaxCharCount => inner.MaxCharCount;

    public override EncoderFallbackBuffer CreateFallbackBuffer()
    {
        ref var buffer = ref latin1 ? ref latin1Buffer : ref utf8Buffer;
        buffer ??= inner.CreateFallbackBuffer();
        buffer.Reset();
        return buffer;
    }
}
#endif
