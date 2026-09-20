using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace FeatherQR.Internals.BinaryEncoders;

/// <summary>
/// Reads bits from a byte buffer with precise bit-level control.
/// Used for QR code data decoding/placement.
/// </summary>
internal ref struct BitReader
{
    private ReadOnlySpan<byte> _data;
    private int _bitPosition;

    /// <summary>
    /// Current bit position in the buffer.
    /// </summary>
    public int BitPosition => _bitPosition;

    /// <summary>
    /// Check if there are more bits to read
    /// </summary>
    public bool HasBits => _bitPosition < _data.Length * 8;

    public BitReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _bitPosition = 0;
    }

    /// <summary>
    /// Reads a single bit from the buffer, advancing the position by one.
    /// </summary>
    /// <returns><c>true</c> when the bit is set.</returns>
    /// <exception cref="InvalidOperationException">Thrown when there are no bits left to read.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Read()
    {
        // --------------------------------------------
        // if data is [0b10110000] & bit position is 0
        // --------------------------------------------
        // position => 0
        // byte => 0b10110000
        // bitoffset => 7
        // 1 << bitOffset(7) => 0b10000000
        // AND => 0b10000000 != 0 => true
        // --------------------------------------------
        // if data is [0b10110000] & bit position is 1
        // --------------------------------------------
        // position => 1
        // byte => 0b10110000
        // bitoffset => 6
        // 1 << bitOffset(6) => 0b01000000
        // AND => 0b00000000 != 0 => false
        // --------------------------------------------

        if (_bitPosition >= _data.Length * 8)
            throw new InvalidOperationException($"Buffer underflow: trying to read beyond data length ({_data.Length * 8} bits)");

        var byteIndex = _bitPosition / 8;
        var bitOffset = 7 - _bitPosition % 8; // 7 - (...) because we read MSB first
        var bit = (_data[byteIndex] & 1 << bitOffset) != 0;
        _bitPosition++;
        return bit;
    }

    /// <summary>
    /// Reads <paramref name="bitCount"/> bits, MSB first, as an integer.
    /// </summary>
    /// <remarks>
    /// One field out of a 64-bit window rather than a loop over bits: a field starts at most 7 bits into the window and is at most 32 wide.
    /// A read that does not fit throws before it consumes anything.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="bitCount"/> is not 1-32.</exception>
    /// <exception cref="InvalidOperationException">Thrown when fewer than <paramref name="bitCount"/> bits are left.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Reads(int bitCount)
    {
        if ((uint)(bitCount - 1) > 31)
            throw new ArgumentOutOfRangeException(nameof(bitCount), "bitCount must be between 1 and 32");

        var position = _bitPosition;
        if (bitCount > _data.Length * 8 - position)
            ThrowUnderflow();

        var window = LoadWindow(position >> 3);
        _bitPosition = position + bitCount;
        return (int)(window << (position & 7) >> (64 - bitCount));
    }

    /// <summary>
    /// Reads <c>destination.Length</c> whole bytes from the current position, which need not be byte aligned.
    /// A read that does not fit throws before it consumes anything.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when fewer than <c>destination.Length</c> × 8 bits are left.</exception>
    public void ReadBytes(Span<byte> destination)
    {
        var position = _bitPosition;
        var count = destination.Length;
        if (count > (_data.Length * 8 - position) >> 3)
            ThrowUnderflow();

        var start = position >> 3;
        var shift = position & 7;
        if (shift == 0)
        {
            _data.Slice(start, count).CopyTo(destination);
        }
        else
        {
            // Unaligned, every output byte straddles two input bytes, so the byte after the last one read exists.
            var i = 0;
            for (; i + 8 <= count; i += 8)
            {
                var word = BinaryPrimitives.ReadUInt64BigEndian(_data.Slice(start + i)) << shift | (uint)(_data[start + i + 8] >> (8 - shift));
                BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(i), word);
            }
            for (; i < count; i++)
                destination[i] = (byte)(_data[start + i] << shift | _data[start + i + 1] >> (8 - shift));
        }
        _bitPosition = position + count * 8;
    }

    /// <summary>The 64 bits that start at <paramref name="byteIndex"/>, big-endian, zero past the end of the data.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly ulong LoadWindow(int byteIndex)
    {
        var data = _data;
        if (byteIndex + 8 <= data.Length)
            return BinaryPrimitives.ReadUInt64BigEndian(data.Slice(byteIndex));

        ulong window = 0;
        for (var i = byteIndex; i < data.Length; i++)
            window |= (ulong)data[i] << (56 - 8 * (i - byteIndex));
        return window;
    }

    private readonly void ThrowUnderflow()
        => throw new InvalidOperationException($"Buffer underflow: trying to read beyond data length ({_data.Length * 8} bits)");
}
