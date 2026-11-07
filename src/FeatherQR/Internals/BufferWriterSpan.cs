using System.Buffers;

namespace FeatherQR.Internals;

/// <summary>
/// The span a caller's <see cref="IBufferWriter{T}"/> hands out for an output of known length.
/// </summary>
internal static class BufferWriterSpan
{
    /// <summary>
    /// Asks <paramref name="writer"/> for <paramref name="length"/> bytes and returns exactly that many.
    /// A shorter span breaks the writer's contract, and it is refused before anything is written into it.
    /// </summary>
    public static Span<byte> Get(IBufferWriter<byte> writer, int length)
    {
        var span = writer.GetSpan(length);
        if (span.Length < length)
            ThrowShort(span.Length, length);
        return span.Slice(0, length);
    }

    // In a method of its own, so a call that does not throw sets up nothing for the message.
    private static void ThrowShort(int length, int asked)
        => throw new InvalidOperationException($"The writer handed out a span of {length} bytes where {asked} were asked for.");
}
