using System.Buffers.Binary;
#if NET5_0_OR_GREATER
using System.Numerics;
#endif

namespace FeatherQR.Internals.ImageWriters;

/// <summary>
/// Run lengths in packed module bits, a 64-bit window at a time.
/// </summary>
internal static class PackedRuns
{
    /// <summary>
    /// How many modules from bit <paramref name="start"/> on, at most <paramref name="max"/>, are all dark or all light.
    /// </summary>
    public static int Length(ReadOnlySpan<byte> bits, long start, int max, bool dark)
    {
        var length = 0;
        while (length < max)
        {
            var at = start + length;
            var shift = (int)(at & 7);
            var window = Load(bits, (int)(at >> 3)) << shift;
            // Count the leading bits of the run's colour: for dark ones the window is inverted, so they become leading zeros.
            if (dark)
                window = ~window;
            var valid = 64 - shift;
            var same = LeadingZeros(window);
            if (same < valid)
                return Math.Min(length + same, max);
            length += valid;
        }
        return max;
    }

    /// <summary>Eight bytes from <paramref name="index"/> as a big-endian word, zeros past the end.</summary>
    private static ulong Load(ReadOnlySpan<byte> bits, int index)
    {
        if (index + 8 <= bits.Length)
            return BinaryPrimitives.ReadUInt64BigEndian(bits.Slice(index));
        ulong word = 0;
        for (var k = 0; k < 8; k++)
            word = (word << 8) | (index + k < bits.Length ? bits[index + k] : 0u);
        return word;
    }

    private static int LeadingZeros(ulong value)
    {
#if NET5_0_OR_GREATER
        return BitOperations.LeadingZeroCount(value);
#else
        if (value == 0)
            return 64;
        var count = 0;
        if (value >> 32 == 0) { count += 32; value <<= 32; }
        if (value >> 48 == 0) { count += 16; value <<= 16; }
        if (value >> 56 == 0) { count += 8; value <<= 8; }
        if (value >> 60 == 0) { count += 4; value <<= 4; }
        if (value >> 62 == 0) { count += 2; value <<= 2; }
        if (value >> 63 == 0) { count += 1; }
        return count;
#endif
    }
}
