using System.Buffers.Binary;

namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// Halves a luminance image: each pixel the rounded mean of the two by two block above it.
/// </summary>
/// <remarks>
/// The step of the reduced-scale search, which reads an image again at half, a quarter and so on when nothing reads at full size.
/// The mean takes out what is finer than the block (sensor noise, a screen's pixel grid, halftone) and keeps a module's level, so a finder whose runs that texture broke up reads as runs again.
/// Paid only by an image nothing read: a fixed cost of the failure path, a quarter of the pixels a level.
/// Four pixels a step in one 64-bit word, with no vector instruction: the sums of a block fit sixteen bits, so eight bytes of each row add as four lanes.
/// </remarks>
internal static class LuminanceHalver
{
    private const ulong LowBytes = 0x00FF00FF00FF00FF;
    private const ulong Round = 0x0002000200020002;

    /// <summary>Writes the image halved: <paramref name="width"/> / 2 by <paramref name="height"/> / 2 pixels, an odd last column or row dropped.</summary>
    /// <param name="source">Luminance, row-major, <paramref name="width"/> × <paramref name="height"/> bytes read.</param>
    /// <param name="width">Width of <paramref name="source"/> in pixels.</param>
    /// <param name="height">Height of <paramref name="source"/> in pixels.</param>
    /// <param name="destination">Receives the halved image, row-major. May start where <paramref name="source"/> starts: each step writes after it has read, and no later step reads what it wrote over.</param>
    /// <exception cref="ArgumentException"><paramref name="source"/> is shorter than its dimensions, or <paramref name="destination"/> than the halved image.</exception>
    internal static void Halve(ReadOnlySpan<byte> source, int width, int height, Span<byte> destination)
    {
        int halfWidth = width / 2, halfHeight = height / 2;
        if (halfWidth < 1 || halfHeight < 1)
            return;
        if (source.Length < (long)width * height)
            throw new ArgumentException("Source is shorter than its dimensions.", nameof(source));
        if (destination.Length < (long)halfWidth * halfHeight)
            throw new ArgumentException("Destination is shorter than the halved image.", nameof(destination));

        for (var y = 0; y < halfHeight; y++)
        {
            var top = source.Slice(2 * y * width, 2 * halfWidth);
            var bottom = source.Slice((2 * y + 1) * width, 2 * halfWidth);
            var row = destination.Slice(y * halfWidth, halfWidth);

            var x = 0;
            for (; x <= row.Length - 4; x += 4)
            {
                var upper = BinaryPrimitives.ReadUInt64LittleEndian(top.Slice(2 * x));
                var lower = BinaryPrimitives.ReadUInt64LittleEndian(bottom.Slice(2 * x));
                // Each sixteen-bit lane: the block's four pixels and the rounding, 1,022 at most
                var sums = (upper & LowBytes) + ((upper >> 8) & LowBytes) + (lower & LowBytes) + ((lower >> 8) & LowBytes) + Round;
                var means = (sums >> 2) & LowBytes;
                // The four means from every other byte into four adjacent ones
                means = (means | (means >> 8)) & 0x0000FFFF0000FFFF;
                BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(x), (uint)(means | (means >> 16)));
            }
            for (; x < row.Length; x++)
                row[x] = (byte)((top[2 * x] + top[2 * x + 1] + bottom[2 * x] + bottom[2 * x + 1] + 2) >> 2);
        }
    }
}
