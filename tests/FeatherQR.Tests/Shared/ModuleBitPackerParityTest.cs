using FeatherQR.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// <see cref="ModuleBitPacker"/> (byte-per-module ↔ MSB-first bit-packed, the storage
/// conversion of the Micro QR / rMQR data models) versus an independent naive
/// reference: every length up to the largest rMQR core (2,363) plus a few larger,
/// 0/1 modules, arbitrary non-zero dark values, all-dark, all-light and pseudo-random
/// contents; the padding bits of the final byte must be zero, and unpack must
/// reproduce the 0/1 view exactly.
/// </summary>
public class ModuleBitPackerParityTest
{
    private static byte[] NaivePack(ReadOnlySpan<byte> modules)
    {
        var bits = new byte[(modules.Length + 7) / 8];
        for (var m = 0; m < modules.Length; m++)
            if (modules[m] != 0)
                bits[m >> 3] |= (byte)(1 << (7 - (m & 7)));
        return bits;
    }

    private static byte[] NaiveUnpack(ReadOnlySpan<byte> bits, int count)
    {
        var modules = new byte[count];
        for (var m = 0; m < count; m++)
            modules[m] = (byte)((bits[m >> 3] >> (7 - (m & 7))) & 1);
        return modules;
    }

    private static byte[] PseudoRandom(int length, int seed, byte mask)
    {
        var bytes = new byte[length];
        var state = (uint)seed * 2654435761u + 7u;
        for (var i = 0; i < length; i++)
        {
            state = state * 1664525u + 1013904223u;
            bytes[i] = (byte)((state >> 16) & mask);
        }
        return bytes;
    }

    public static IEnumerable<int> Lengths()
    {
        for (var n = 0; n <= 80; n++) yield return n;               // every vector-tail phase
        foreach (var n in new[] { 297, 301, 413, 649, 1287, 1683, 2079, 2363, 2400, 4096, 4097 }) yield return n;
    }

    [Test]
    [MethodDataSource(nameof(Lengths))]
    public async Task Pack_MatchesNaive_EveryContentShape(int length)
    {
        var contents = new[]
        {
            new byte[length],
            Enumerable.Repeat((byte)1, length).ToArray(),
            Enumerable.Repeat((byte)0xFF, length).ToArray(),
            PseudoRandom(length, length, 0x01),   // 0/1
            PseudoRandom(length, length + 3, 0xFF), // any non-zero is dark
            PseudoRandom(length, length + 7, 0x80), // only the high bit set
        };
        foreach (var modules in contents)
        {
            var expected = NaivePack(modules);
            var actual = new byte[expected.Length + 2];
            actual.AsSpan().Fill(0xA5);
            ModuleBitPacker.Pack(modules, actual.AsSpan(0, expected.Length));
            if (!actual.AsSpan(0, expected.Length).SequenceEqual(expected))
                Assert.Fail($"pack mismatch at length {length}: expected {Convert.ToHexString(expected)} actual {Convert.ToHexString(actual, 0, expected.Length)}");
            await Assert.That(actual[expected.Length]).IsEqualTo((byte)0xA5); // nothing written past the packed length
            await Assert.That(actual[expected.Length + 1]).IsEqualTo((byte)0xA5);
        }
    }

    [Test]
    [MethodDataSource(nameof(Lengths))]
    public async Task Unpack_MatchesNaive_AndWritesExactlyCountBytes(int length)
    {
        var bits = PseudoRandom((length + 7) / 8, length * 5, 0xFF);
        var expected = NaiveUnpack(bits, length);
        var actual = new byte[length + 3];
        actual.AsSpan().Fill(0xA5);
        ModuleBitPacker.Unpack(bits, actual.AsSpan(0, length));
        if (!actual.AsSpan(0, length).SequenceEqual(expected))
            Assert.Fail($"unpack mismatch at length {length}");
        await Assert.That(actual.AsSpan(length).ToArray()).IsEquivalentTo(new byte[] { 0xA5, 0xA5, 0xA5 });
    }

    [Test]
    public async Task Pack_Unpack_RoundTrip_AllRmqrCoreSizes()
    {
        foreach (var version in Enum.GetValues<RmQRVersion>())
        {
            var n = Internals.RmQR.RmQRConstants.GetWidth(version) * Internals.RmQR.RmQRConstants.GetHeight(version);
            var modules = PseudoRandom(n, (int)version, 0x01);
            var bits = new byte[(n + 7) / 8];
            ModuleBitPacker.Pack(modules, bits);
            var back = new byte[n];
            ModuleBitPacker.Unpack(bits, back);
            await Assert.That(back).IsEquivalentTo(modules);
        }
    }

    [Test]
    public async Task Pack_RejectsShortDestination_Unpack_RejectsShortSource()
    {
        await Assert.That(() => ModuleBitPacker.Pack(new byte[9], new byte[1])).Throws<ArgumentException>();
        await Assert.That(() => ModuleBitPacker.Unpack(new byte[1], new byte[9])).Throws<ArgumentException>();
    }
}
