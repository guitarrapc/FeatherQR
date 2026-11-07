using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace FeatherQR.Internals.ImageWriters;

/// <summary>
/// The fixed parts of deflate (RFC 1951): the base and extra bits of each length and distance code, and canonical codes from code lengths.
/// </summary>
internal static class Deflate
{
    /// <summary>Lengths 3 to 258 take codes 257 to 285, distances 1 to 32,768 take codes 0 to 29.</summary>
    public const int LiteralLengthSymbols = 286;
    public const int DistanceSymbols = 30;
    public const int CodeLengthSymbols = 19;
    public const int EndOfBlock = 256;
    public const int MinMatch = 3;
    public const int MaxMatch = 258;
    public const int MaxDistance = 32_768;

    private static readonly ushort[] LengthBases = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
    private static readonly ushort[] DistanceBases = [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];

    public static ReadOnlySpan<byte> LengthExtraBits => [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];

    public static ReadOnlySpan<byte> DistanceExtraBits => [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];

    /// <summary>The order the code-length code's lengths are written in.</summary>
    public static ReadOnlySpan<byte> CodeLengthOrder => [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

    // The length code of each length 0 to 258, and the distance code of each distance - 1 below 256, then of (distance - 1) >> 7.
    private static readonly byte[] LengthCodes = BuildLengthCodes();
    private static readonly byte[] DistanceCodes = BuildDistanceCodes();

    /// <summary>The length code, 0 to 28, of a match length from 3 to 258.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LengthCode(int length) => LengthCodes[length];

    public static int LengthBase(int code) => LengthBases[code];

    /// <summary>The distance code, 0 to 29, of a distance from 1 to 32,768.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DistanceCode(int distance)
    {
        var d = distance - 1;
        return d < 256 ? DistanceCodes[d] : DistanceCodes[256 + (d >> 7)];
    }

    public static int DistanceBase(int code) => DistanceBases[code];

    /// <summary>
    /// The canonical code of each symbol from the code lengths, bit-reversed, since deflate writes a code from its first bit and the bit writer from the low end.
    /// Each entry holds the code in its low 16 bits and the length above, so writing a symbol reads one entry.
    /// </summary>
    public static void BuildCodes(ReadOnlySpan<byte> lengths, Span<uint> codes)
    {
        Span<int> next = stackalloc int[16];
        next.Clear();
        foreach (var length in lengths)
            next[length]++;
        next[0] = 0;
        var code = 0;
        var previous = 0;
        for (var bits = 1; bits < 16; bits++)
        {
            code = (code + previous) << 1;
            previous = next[bits];
            next[bits] = code;
        }
        for (var symbol = 0; symbol < lengths.Length; symbol++)
        {
            var length = lengths[symbol];
            codes[symbol] = length == 0 ? 0 : Reverse(next[length]++, length) | (uint)length << 16;
        }
    }

    private static uint Reverse(int code, int length)
    {
        var reversed = 0;
        for (var i = 0; i < length; i++)
        {
            reversed = (reversed << 1) | (code & 1);
            code >>= 1;
        }
        return (uint)reversed;
    }

    private static byte[] BuildLengthCodes()
    {
        var table = new byte[MaxMatch + 1];
        for (var code = 0; code < 28; code++)
        {
            for (var k = 0; k < 1 << LengthExtraBits[code]; k++)
                table[LengthBases[code] + k] = (byte)code;
        }
        // 258 has a code of its own with no extra bits, though 227 + 31 would reach it too.
        table[MaxMatch] = 28;
        return table;
    }

    private static byte[] BuildDistanceCodes()
    {
        var table = new byte[512];
        for (var code = 0; code < DistanceSymbols; code++)
        {
            for (var k = 0; k < 1 << DistanceExtraBits[code]; k++)
            {
                var d = DistanceBases[code] + k - 1;
                if (d < 256)
                    table[d] = (byte)code;
                else
                    table[256 + (d >> 7)] = (byte)code;
            }
        }
        return table;
    }
}

/// <summary>
/// Writes deflate's bits from the low end of each byte, as RFC 1951 orders them, into a span the caller sized exactly.
/// </summary>
internal ref struct DeflateBitWriter(Span<byte> destination)
{
    private readonly Span<byte> _destination = destination;
    private int _position;
    private ulong _bits;
    private int _count;

    /// <summary>Adds the <paramref name="count"/> bits of <paramref name="value"/>, at most 16. It is not masked, so it must fit in them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Put(uint value, int count)
    {
        _bits |= (ulong)value << _count;
        _count += count;
        if (_count >= 32)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_destination.Slice(_position), (uint)_bits);
            _position += 4;
            _bits >>= 32;
            _count -= 32;
        }
    }

    /// <summary>Adds an entry of a <see cref="Deflate.BuildCodes"/> table: the code in the low 16 bits, its length above.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Put(uint code) => Put(code & 0xFFFF, (int)(code >> 16));

    public readonly Span<byte> Destination => _destination;

    /// <summary>
    /// The position of the next four bytes, the bits held and how many, for a loop that keeps them in locals. It writes four bytes from
    /// the position whenever 32 bits are held, as <see cref="Put(uint, int)"/> does, and hands them back through <see cref="Resume"/>.
    /// </summary>
    public readonly void Save(out int position, out ulong bits, out int count)
    {
        position = _position;
        bits = _bits;
        count = _count;
    }

    public void Resume(int position, ulong bits, int count)
    {
        _position = position;
        _bits = bits;
        _count = count;
    }

    /// <summary>Writes the bits still held, the last byte padded with zeros, and returns the length written.</summary>
    public int Flush()
    {
        while (_count > 0)
        {
            _destination[_position++] = (byte)_bits;
            _bits >>= 8;
            _count -= 8;
        }
        _count = 0;
        return _position;
    }
}
