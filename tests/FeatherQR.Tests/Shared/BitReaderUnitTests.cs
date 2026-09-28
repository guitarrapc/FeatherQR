using FeatherQR.Internals.BinaryEncoders;

namespace FeatherQR.Tests;

public class BitReaderUnitTests
{
    [Test]
    public async Task ReadBits_Singlebyte_CorrectPlacement()
    {
        Span<byte> data = [0b_10101011];
        var reader = new BitReader(data);

        var bits = new bool[8];
        for (var i = 0; i < bits.Length; i++)
        {
            bits[i] = reader.Read();
        }

        await Assert.That(bits[0]).IsTrue();
        await Assert.That(bits[1]).IsFalse();
        await Assert.That(bits[2]).IsTrue();
        await Assert.That(bits[3]).IsFalse();
        await Assert.That(bits[4]).IsTrue();
        await Assert.That(bits[5]).IsFalse();
        await Assert.That(bits[6]).IsTrue();
        await Assert.That(bits[7]).IsTrue();
    }

    [Test]
    public async Task ReadBits_AccessByteBoundary_CorrectPlacement()
    {
        Span<byte> data = [0b11110010];
        var reader = new BitReader(data);

        var first = reader.Reads(4);
        var second = reader.Reads(4);

        await Assert.That(first).IsEquivalentTo(0b_1111);
        await Assert.That(second).IsEquivalentTo(0b_0010);
    }

    [Test]
    public async Task RoundTrip_WriteAndRead_IdenticalData()
    {
        Span<byte> buffer = stackalloc byte[10];
        var writer = new BitWriter(buffer);

        writer.Write(0b_1010, 4);
        writer.Write(0b_1100, 4);
        writer.Write(0b_1111, 4);

        var reader = new BitReader(writer.GetData());
        var first = reader.Reads(4);
        var second = reader.Reads(4);
        var third = reader.Reads(4);

        await Assert.That(first).IsEquivalentTo(0b_1010);
        await Assert.That(second).IsEquivalentTo(0b_1100);
        await Assert.That(third).IsEquivalentTo(0b_1111);
    }

    // parameter check
    [Test]
    [Arguments(0)]    // bitCount = 0
    [Arguments(-1)]   // bitCount < 0
    [Arguments(33)]   // bitCount > 32
    public async Task Reads_InvalidBitCount_ThrowsArgumentOutOfRangeException(int bitCount)
    {
        var data = new byte[] { 0xFF };

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            var reader = new BitReader(data);
            reader.Reads(bitCount);
        });
        await Assert.That(exception.ParamName).IsEqualTo(nameof(bitCount));
    }

    // 32-bit checks
    [Test]
    public async Task Reads_32Bits_ReturnsCorrectValue()
    {
        ReadOnlySpan<byte> data = [0xFF, 0xFF, 0xFF, 0xFF]; // all 1s
        var reader = new BitReader(data);
        var result = reader.Reads(32);

        await Assert.That(result).IsEquivalentTo(-1); // 0xFFFFFFFF as signed int
    }

    // hasBits
    [Test]
    public async Task Reads_1Bit_ReturnsCorrectValue()
    {
        ReadOnlySpan<byte> data = [0b10000000];
        var reader = new BitReader(data);
        var result = reader.Reads(1);

        await Assert.That(result).IsEquivalentTo(1);
    }
}
