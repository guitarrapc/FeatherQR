#if NET5_0_OR_GREATER
using System.Text;
#endif

namespace FeatherQR.Internals.BinaryDecoders;

/// <summary>
/// Whether the decode on this thread writes the symbol's bytes rather than its text.
/// </summary>
/// <remarks>
/// <para>
/// The bytes calls carry their output through the char destination the text calls use, one byte a char, and narrow it at the end.
/// The image decoders pass that destination through every stage of their search, and only the bitstream decode at the end of it differs between the two outputs.
/// A flag the bytes calls set around the decode reaches that step without a parameter on each stage. Nothing in a decode leaves the calling thread.
/// </para>
/// <para>
/// The matrix decoders read it once, after Reed-Solomon has corrected the data, and hand it to their bitstream decoder.
/// </para>
/// </remarks>
internal static class PayloadOutput
{
    [ThreadStatic]
    private static bool bytesOnThisThread;

    /// <summary>Whether a bytes call is decoding on this thread.</summary>
    public static bool IsBytes => bytesOnThisThread;

    /// <summary>Marks this thread's decodes as bytes decodes until the scope is disposed.</summary>
    public static BytesScope Bytes()
    {
        bytesOnThisThread = true;
        return default;
    }

    /// <summary>Copies bytes to chars one to one, U+0000 to U+00FF, as a bytes decode writes them.</summary>
    public static void Widen(ReadOnlySpan<byte> bytes, Span<char> destination)
    {
#if NET5_0_OR_GREATER
        Encoding.Latin1.GetChars(bytes, destination);
#else
        for (var i = 0; i < bytes.Length; i++)
            destination[i] = (char)bytes[i];
#endif
    }

    /// <summary>Copies a bytes decode's chars to bytes. Every char of one is below 0x100.</summary>
    public static void Narrow(ReadOnlySpan<char> chars, Span<byte> destination)
    {
#if NET5_0_OR_GREATER
        // Latin-1 maps every char below 0x100 to itself, so its fallback for the others never runs.
        Encoding.Latin1.GetBytes(chars, destination);
#else
        for (var i = 0; i < chars.Length; i++)
            destination[i] = (byte)chars[i];
#endif
    }

    /// <summary>A bytes decode's chars as a new array.</summary>
    public static byte[] ToArray(ReadOnlySpan<char> chars)
    {
        if (chars.IsEmpty)
            return [];
        var bytes = new byte[chars.Length];
        Narrow(chars, bytes);
        return bytes;
    }

    /// <summary>Ends the bytes decodes <see cref="Bytes"/> began.</summary>
    public readonly ref struct BytesScope
    {
        public void Dispose() => bytesOnThisThread = false;
    }
}
