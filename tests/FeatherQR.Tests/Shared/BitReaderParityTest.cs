using FeatherQR.Internals.BinaryEncoders;

namespace FeatherQR.Tests;

/// <summary>
/// <see cref="BitReader.Reads"/> and <see cref="BitReader.ReadBytes"/> against the definition
/// (bit i of the stream is bit 7 − i mod 8 of byte i / 8), read here one bit at a time with no
/// code shared with the reader.
/// </summary>
/// <remarks>
/// The reader takes a field out of a 64-bit window instead of looping over bits, and the window
/// has two forms: a whole load where eight bytes are left, a byte loop in the last seven. Every
/// width at every offset of short buffers is what walks both forms and the seam between them.
/// </remarks>
public class BitReaderParityTest
{
    private static byte[] PseudoRandom(int length, int seed)
    {
        var bytes = new byte[length];
        var state = (uint)seed * 2654435761u + 7u;
        for (var i = 0; i < length; i++)
        {
            state = state * 1664525u + 1013904223u;
            bytes[i] = (byte)(state >> 16);
        }
        return bytes;
    }

    private static int ReferenceBits(byte[] data, int position, int count)
    {
        var value = 0;
        for (var i = 0; i < count; i++)
        {
            var bit = position + i;
            value = value << 1 | (data[bit >> 3] >> (7 - (bit & 7)) & 1);
        }
        return value;
    }

    private static BitReader ReaderAt(byte[] data, int position)
    {
        var reader = new BitReader(data);
        for (var left = position; left > 0; left -= Math.Min(left, 32))
            reader.Reads(Math.Min(left, 32));
        return reader;
    }

    [Test]
    public async Task Reads_MatchesTheDefinition_EveryWidthAtEveryOffset()
    {
        var mismatches = new List<string>();
        for (var length = 1; length <= 20; length++)
        {
            var data = PseudoRandom(length, length);
            for (var position = 0; position < length * 8; position++)
            {
                for (var width = 1; width <= 32 && position + width <= length * 8; width++)
                {
                    var reader = ReaderAt(data, position);
                    var value = reader.Reads(width);
                    if (value != ReferenceBits(data, position, width) || reader.BitPosition != position + width)
                        mismatches.Add($"length {length} position {position} width {width}");
                }
            }
        }

        await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    public async Task Reads_MixedWidthsToTheLastBit_MatchTheDefinition()
    {
        var mismatches = new List<string>();
        for (var seed = 0; seed < 64; seed++)
        {
            var data = PseudoRandom(1 + seed * 7 % 61, seed + 100);
            var widths = PseudoRandom(data.Length * 8, seed + 200);
            var reader = new BitReader(data);
            var position = 0;
            for (var k = 0; position < data.Length * 8; k++)
            {
                var width = Math.Min(1 + widths[k] % 32, data.Length * 8 - position);
                if (reader.Reads(width) != ReferenceBits(data, position, width))
                    mismatches.Add($"seed {seed} position {position} width {width}");
                position += width;
            }
            if (reader.HasBits)
                mismatches.Add($"seed {seed}: bits left after the last read");
        }

        await Assert.That(mismatches).IsEmpty();
    }

    /// <summary>
    /// A read that does not fit throws before it consumes anything. The bit loop used to consume
    /// what was left and then throw, which left a caller that catches with a reader it cannot reason about.
    /// </summary>
    [Test]
    [Arguments(0, 9)]
    [Arguments(1, 8)]
    [Arguments(7, 2)]
    [Arguments(8, 1)]
    [Arguments(3, 32)]
    public async Task Reads_PastTheEnd_Throws_AndDoesNotAdvance(int position, int width)
    {
        var (threw, after) = ReadPastTheEnd(position, width);

        await Assert.That(threw).IsTrue();
        await Assert.That(after).IsEqualTo(position);
    }

    private static (bool Threw, int Position) ReadPastTheEnd(int position, int width)
    {
        var reader = ReaderAt([0xA5], position);
        try
        {
            reader.Reads(width);
            return (false, reader.BitPosition);
        }
        catch (InvalidOperationException)
        {
            return (true, reader.BitPosition);
        }
    }

    [Test]
    public async Task ReadBytes_MatchesTheDefinition_EveryAlignmentAndLength()
    {
        var mismatches = new List<string>();
        var data = PseudoRandom(48, 9);
        for (var position = 0; position < 24; position++)
        {
            // up to the last whole byte the stream still holds
            for (var count = 0; position + count * 8 <= data.Length * 8; count++)
            {
                var reader = ReaderAt(data, position);
                var backing = new byte[count + 8];
                backing.AsSpan().Fill(0xA5);
                reader.ReadBytes(backing.AsSpan(0, count));

                for (var i = 0; i < count; i++)
                {
                    if (backing[i] != ReferenceBits(data, position + i * 8, 8))
                    {
                        mismatches.Add($"position {position} count {count} byte {i}");
                        break;
                    }
                }
                if (backing.AsSpan(count).IndexOfAnyExcept((byte)0xA5) >= 0)
                    mismatches.Add($"position {position} count {count}: wrote past the destination");
                if (reader.BitPosition != position + count * 8)
                    mismatches.Add($"position {position} count {count}: position {reader.BitPosition}");
            }
        }

        await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    [Arguments(0, 3)]
    [Arguments(1, 2)]
    [Arguments(9, 1)]
    public async Task ReadBytes_PastTheEnd_Throws_AndDoesNotAdvance(int position, int count)
    {
        var (threw, after) = ReadBytesPastTheEnd(position, count);

        await Assert.That(threw).IsTrue();
        await Assert.That(after).IsEqualTo(position);
    }

    private static (bool Threw, int Position) ReadBytesPastTheEnd(int position, int count)
    {
        var reader = ReaderAt([0xA5, 0x5A], position);
        try
        {
            reader.ReadBytes(new byte[count]);
            return (false, reader.BitPosition);
        }
        catch (InvalidOperationException)
        {
            return (true, reader.BitPosition);
        }
    }
}
