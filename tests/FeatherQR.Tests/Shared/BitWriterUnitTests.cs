using FeatherQR.Internals.BinaryEncoders;

namespace FeatherQR.Tests;

public class BitWriterUnitTests
{
    [Test]
    public async Task WriteBits_Singlebyte_CorrectPlacement()
    {
        byte[] buffer = new byte[1];
        var writer = new BitWriter(buffer);

        writer.Write(0b_10101010, 8);
        var data = writer.GetData();

        await Assert.That(data[0]).IsEqualTo((byte)0xAA);
    }

    [Test]
    public async Task WriteBits_AccessByteBoundary_CorrectPlacement()
    {
        byte[] buffer = new byte[2];
        var writer = new BitWriter(buffer);

        writer.Write(0b1111, 4);
        writer.Write(0b0000, 4);
        writer.Write(0b1010, 4);
        var data = writer.GetData().ToArray();

        await Assert.That(data[0]).IsEqualTo((byte)0xF0);
        await Assert.That(data[1]).IsEqualTo((byte)0xA0);
    }

    [Test]
    [Arguments(0b_1, 1, new byte[] { 0b_10000000 })]
    [Arguments(0b_11, 2, new byte[] { 0b_11000000 })]
    [Arguments(0b_111, 3, new byte[] { 0b_11100000 })]
    [Arguments(0b_1111, 4, new byte[] { 0b_11110000 })]
    [Arguments(0b_11111, 5, new byte[] { 0b_11111000 })]
    [Arguments(0b_111111, 6, new byte[] { 0b_11111100 })]
    [Arguments(0b_1111111, 7, new byte[] { 0b_11111110 })]
    [Arguments(0b_11111111, 8, new byte[] { 0b_11111111 })]
    public async Task WriteBits_VariableBitCounts_CorrectAlignment(int value, int bits, byte[] expected)
    {
        byte[] buffer = new byte[1];
        var writer = new BitWriter(buffer);
        writer.Write(value, bits);
        var data = writer.GetData();

        await Assert.That(data[0]).IsEqualTo(expected[0]);
    }

    // Edge cases

    [Test]
    public async Task RoundTrip_MaxBitCount_IdenticalData()
    {
        byte[] buffer = new byte[4];
        var writer = new BitWriter(buffer);

        writer.Write(int.MaxValue, 32); // 0x7FFFFFFF

        var reader = new BitReader(writer.GetData());
        var result = reader.Reads(32);

        await Assert.That(result).IsEqualTo(int.MaxValue);
    }

    // multiple writes with different bit counts
    [Test]
    public async Task RoundTrip_MultipleBitCounts_IdenticalData()
    {
        byte[] buffer = new byte[10];
        var writer = new BitWriter(buffer);

        writer.Write(0b101, 3);
        writer.Write(0b11111111, 8);
        writer.Write(0b1010101010101010, 16);

        var reader = new BitReader(writer.GetData());

        var read3 = reader.Reads(3);
        var read8 = reader.Reads(8);
        var read16 = reader.Reads(16);

        await Assert.That(read3).IsEqualTo(0b101);
        await Assert.That(read8).IsEqualTo(0b11111111);
        await Assert.That(read16).IsEqualTo(0b1010101010101010);
    }

    // hasBits for last bit
    [Test]
    public async Task BitReader_HasBits_CorrectlyIndicatesEndOfData()
    {
        var data = new byte[] { 0xFF };
        var reader = new BitReader(data);
        var hasBitsWhileReading = new bool[8];
        for (var i = 0; i < 8; i++)
        {
            hasBitsWhileReading[i] = reader.HasBits;
            reader.Read();
        }

        var hasBitsAfterReads = reader.HasBits;

        foreach (var hasBits in hasBitsWhileReading)
        {
            await Assert.That(hasBits).IsTrue();
        }

        await Assert.That(hasBitsAfterReads).IsFalse();
    }

    // WriteWide: 1 to 56 bits in one append, a 32-bit store below 64 pending bits and an 8-byte store from 64 up

    /// <summary>
    /// Every width 1-56 after every count of pending bits 0-31, behind no stored word and behind one, so both stores run from every
    /// alignment: the value's every bit set, alternate bits, the top bit alone and the bottom bit alone.
    /// </summary>
    [Test]
    public async Task WriteWide_EveryWidthAfterEveryPendingCount_MatchesBitByBitInExactBuffer()
    {
        var mismatches = new List<string>();
        foreach (var lead in new[] { 0, 32 })
        {
            for (var pending = 0; pending < 32; pending++)
            {
                for (var width = 1; width <= 56; width++)
                {
                    var mask = (1UL << width) - 1;
                    foreach (var value in new[] { mask, 0xAAAA_AAAA_AAAA_AAAAUL & mask, 0x5555_5555_5555_5555UL & mask, 1UL << (width - 1), 1UL })
                    {
                        var appends = new List<(ulong Value, int Width, bool Wide)>();
                        if (lead > 0)
                            appends.Add((0xA5C3_9E71UL, 32, false));
                        if (pending > 0)
                            appends.Add((0x6B2D_F48EUL >> (32 - pending), pending, false));
                        appends.Add((value, width, true));
                        if (Run(appends) is { } failure)
                            mismatches.Add($"lead {lead}, pending {pending}, width {width}, value 0x{value:X}: {failure}");
                    }
                }
            }
        }

        await Assert.That(mismatches).IsEmpty().Because(string.Join("; ", mismatches.Take(8)));
    }

    /// <summary>
    /// Random runs of WriteWide and Write, so an append starts from what any earlier one left pending, each run in a buffer exactly as
    /// long as its bits.
    /// </summary>
    [Test]
    public async Task WriteWide_MixedWithWrite_MatchesBitByBitInExactBuffer()
    {
        var mismatches = new List<string>();
        for (var seed = 0; seed < 500; seed++)
        {
            var random = new Random(seed);
            var appends = new List<(ulong Value, int Width, bool Wide)>();
            for (var count = random.Next(1, 49); count > 0; count--)
            {
                var wide = random.Next(4) != 0;
                var width = wide ? random.Next(1, 57) : random.Next(1, 33);
                appends.Add(((ulong)random.NextInt64() & ((1UL << width) - 1), width, wide));
            }
            if (Run(appends) is { } failure)
                mismatches.Add($"seed {seed}: {failure}");
        }

        await Assert.That(mismatches).IsEmpty().Because(string.Join("; ", mismatches.Take(8)));
    }

    private const byte Untouched = 0xCC;

    /// <summary>
    /// The appends into a buffer exactly as long as their bits, filled with <see cref="Untouched"/>. After each append the bit position is
    /// the bits so far and no byte from the one the next bit lands in has been stored (a store holds written bits only); a store past the
    /// buffer throws. Flushed, the buffer must be the bits MSB first, zero past them. Returns what went wrong, or null.
    /// </summary>
    private static string? Run(List<(ulong Value, int Width, bool Wide)> appends)
    {
        var buffer = new byte[(appends.Sum(a => a.Width) + 7) / 8];
        buffer.AsSpan().Fill(Untouched);
        var writer = new BitWriter(buffer);
        var written = 0;
        foreach (var (value, width, wide) in appends)
        {
            var call = $"{(wide ? "WriteWide" : "Write")}({width}) after {written} bits";
            try
            {
                if (wide)
                    writer.WriteWide(value, width);
                else
                    writer.Write((int)value, width);
            }
            catch (ArgumentOutOfRangeException)
            {
                return $"{call} stored past the {buffer.Length}-byte buffer";
            }
            written += width;
            if (writer.BitPosition != written)
                return $"{call} left bit position {writer.BitPosition}";
            for (var b = written / 8; b < buffer.Length; b++)
            {
                if (buffer[b] != Untouched)
                    return $"{call} stored byte {b}, which holds bits not written yet";
            }
        }

        writer.Flush();
        var expected = new byte[buffer.Length];
        var position = 0;
        foreach (var (value, width, _) in appends)
        {
            for (var b = width - 1; b >= 0; b--, position++)
            {
                if (((value >> b) & 1) != 0)
                    expected[position >> 3] |= (byte)(0x80 >> (position & 7));
            }
        }
        return buffer.AsSpan().SequenceEqual(expected) ? null : $"flushed {Convert.ToHexString(buffer)}, expected {Convert.ToHexString(expected)}";
    }
}
