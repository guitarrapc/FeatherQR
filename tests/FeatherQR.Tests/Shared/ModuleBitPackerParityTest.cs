using TUnit.Assertions.Enums;
using FeatherQR.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// <see cref="ModuleBitPacker"/> (byte-per-module ↔ MSB-first bit-packed, the storage
/// conversion of the three data models) versus an independent naive reference: every
/// length to 80, which takes each vector step's tail, and eleven from 297 to 4,097, the
/// largest rMQR core (2,363) among them; all-light, all-dark, 0/1, any-byte and
/// high-bit-only contents, any non-zero byte dark for <see cref="ModuleBitPacker.Pack"/>;
/// the padding bits of the final byte zero and nothing written past it, and unpack
/// reproducing the 0/1 view exactly.
/// Standard QR's entry, <see cref="ModuleBitPacker.PackZeroOrOne"/>, is held to the same
/// reference over 0/1 modules on both its routes, and to the route the tier table gives
/// this process.
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
        await Assert.That(actual.AsSpan(length).ToArray()).IsEquivalentTo(new byte[] { 0xA5, 0xA5, 0xA5 }, CollectionOrdering.Matching);
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
            await Assert.That(back).IsEquivalentTo(modules, CollectionOrdering.Matching);
        }
    }

    // QRCodeData's entry: modules that are 0 or 1, through Pack where it takes a vector step and the scalar gather otherwise. The
    // scalar route is entered directly too, so it runs on a machine whose dispatch takes the vector step.
    [Test]
    [MethodDataSource(nameof(Lengths))]
    public async Task PackZeroOrOne_MatchesNaive_BothRoutes(int length)
    {
        var contents = new[]
        {
            new byte[length],
            Enumerable.Repeat((byte)1, length).ToArray(),
            PseudoRandom(length, length, 0x01),
            PseudoRandom(length, length + 11, 0x01),
        };
        foreach (var modules in contents)
        {
            var expected = NaivePack(modules);
            foreach (var scalar in new[] { false, true })
            {
                var actual = new byte[expected.Length + 2];
                actual.AsSpan().Fill(0xA5);
                if (scalar)
                    ModuleBitPacker.PackZeroOrOneScalar(modules, actual.AsSpan(0, expected.Length));
                else
                    ModuleBitPacker.PackZeroOrOne(modules, actual.AsSpan(0, expected.Length));
                if (!actual.AsSpan(0, expected.Length).SequenceEqual(expected))
                    Assert.Fail($"{(scalar ? "scalar" : "dispatch")} pack mismatch at length {length}: expected {Convert.ToHexString(expected)} actual {Convert.ToHexString(actual, 0, expected.Length)}");
                await Assert.That(actual[expected.Length]).IsEqualTo((byte)0xA5);
                await Assert.That(actual[expected.Length + 1]).IsEqualTo((byte)0xA5);
            }
        }
    }

    // The entry's route: Pack where the packer's row of the tier table has a tier that runs in this process, the gather otherwise. Both
    // pack modules of 0 and 1 alike, so the test above cannot tell which ran; a byte of 0xFF at module 0 can, since Pack makes it dark
    // and the gather darkens modules 2 to 7 with it.
    [Test]
    public async Task PackZeroOrOne_TakesPack_WhereTheTierTableGivesThePackerAVectorStep()
    {
        var vector = SimdTiers.Report().Single(k => k.Name == "ModuleBitPacker").Active != SimdTier.Scalar;
        var modules = new byte[32];
        modules[0] = 0xFF;
        modules[9] = 1;
        modules[17] = 1;
        var packed = new byte[4];
        ModuleBitPacker.Pack(modules, packed);
        var gathered = new byte[4];
        ModuleBitPacker.PackZeroOrOneScalar(modules, gathered);
        await Assert.That(gathered.AsSpan().SequenceEqual(packed)).IsFalse().Because("the two routes must pack this input differently");

        var actual = new byte[4];
        ModuleBitPacker.PackZeroOrOne(modules, actual);
        await Assert.That(actual).IsEquivalentTo(vector ? packed : gathered, CollectionOrdering.Matching)
            .Because($"the tier table gives the packer {(vector ? "a vector step" : "no vector step")} in this process");
    }

    [Test]
    public async Task Pack_RejectsShortDestination_Unpack_RejectsShortSource()
    {
        await Assert.That(() => ModuleBitPacker.Pack(new byte[9], new byte[1])).Throws<ArgumentException>();
        await Assert.That(() => ModuleBitPacker.PackZeroOrOne(new byte[9], new byte[1])).Throws<ArgumentException>();
        await Assert.That(() => ModuleBitPacker.PackZeroOrOneScalar(new byte[9], new byte[1])).Throws<ArgumentException>();
        await Assert.That(() => ModuleBitPacker.Unpack(new byte[1], new byte[9])).Throws<ArgumentException>();
    }
}
