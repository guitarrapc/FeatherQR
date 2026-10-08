using TUnit.Assertions.Enums;
using FeatherQR.Internals;
using FeatherQR.Internals.BinaryEncoders;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// Placement straight from the interleaved stream into the transposed scorer's column planes (AVX2, versions 12-40): the chosen pattern
/// and the written symbol against the template copy, <see cref="ModulePlacer.PlaceDataWords(Span{byte}, ModulePlacer.PlacementLayout, ReadOnlySpan{byte})"/>
/// and <see cref="ModulePlacer.MaskCodeTransposed"/>, at every version and ECC level.
/// </summary>
/// <remarks>
/// What the stream form can get wrong is where a bit lands: a run's two columns swapped, a downward run's rows reversed, a run straddling a
/// 64-row word, a scattered module beside an alignment pattern, the stream ending inside a run row. The streams are random, all light, all
/// dark and alternating, at the length the encoder passes and cut short and run long, and the output buffer starts dirty, so a module the
/// stream form does not write shows.
/// </remarks>
public class ModulePlacerStreamPlacementParityTest
{
    public static IEnumerable<int> Versions() => Enumerable.Range(12, 29);

    private static readonly QREccLevel[] EccLevels = [QREccLevel.L, QREccLevel.M, QREccLevel.Q, QREccLevel.H];

    [Test]
    [MethodDataSource(nameof(Versions))]
    public async Task FromStream_MatchesPlacedBufferThenMaskCode(int version)
    {
        if (!System.Runtime.Intrinsics.X86.Avx2.IsSupported)
        {
            Skip.Test("AVX2 not supported on this machine");
            return;
        }

        var layout = ModulePlacer.GetLayout(version);
        var size = layout.Size;
        foreach (var ecc in EccLevels)
        {
            var info = QRCodeConstants.GetEccInfo(version, ecc);
            var length = BinaryInterleaver.CalculateInterleavedSize(info, QRCodeConstants.GetRemainderBits(version));
            foreach (var (name, stream) in Streams(length, version * 4 + (int)ecc))
            {
                var expected = (byte[])layout.Template.Clone();
                ModulePlacer.PlaceDataWords(expected, layout, stream);
                var expectedPattern = ModulePlacer.MaskCodeTransposed(expected, size, version, layout.BlockedMask, ecc);

                var actual = new byte[size * size];
                actual.AsSpan().Fill(0xEE);
                // The planes come from the shared pool, which hands back what others left: a dirty array, so a padding row the stream form
                // does not clear shows.
                var junk = System.Buffers.ArrayPool<ulong>.Shared.Rent(ModulePlacer.TransposedScratchLength(version));
                junk.AsSpan().Fill(0xA5A5A5A5A5A5A5A5ul);
                System.Buffers.ArrayPool<ulong>.Shared.Return(junk);
                var pattern = ModulePlacer.MaskCodeTransposedFromStream(actual, version, stream, ecc);

                // The stream form writes the version information with the rest; the placed path gets it from PlaceVersion. Only the
                // expected matrix takes it, so the comparison holds the stream form's own version modules.
                ModulePlacer.PlaceVersion(expected, size, QRCodeConstants.GetVersionBits(version));

                var first = actual.AsSpan().CommonPrefixLength(expected);
                var where = $"version {version} {ecc} {name}, first difference at row {first / size}, column {first % size}";
                await Assert.That(pattern).IsEqualTo(expectedPattern).Because(where);
                await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching).Because(where);
            }
        }
    }

    [Test]
    public async Task TryMaskCodeFromStream_TakesTheStreamFormFromVersion12OnAvx2()
    {
        var stream = new byte[3706];
        var buffer = new byte[177 * 177];
        var avx2 = System.Runtime.Intrinsics.X86.Avx2.IsSupported;
        foreach (var version in new[] { 1, 11 })
            await Assert.That(ModulePlacer.TryMaskCodeFromStream(buffer, version, stream, QREccLevel.M, out _)).IsFalse().Because($"version {version}");
        foreach (var version in new[] { 12, 40 })
            await Assert.That(ModulePlacer.TryMaskCodeFromStream(buffer, version, stream, QREccLevel.M, out _)).IsEqualTo(avx2).Because($"version {version}");
    }

    [Test]
    public async Task ReadStreamBits_MatchesABitByBitRead()
    {
        // Every bit offset of short streams, so the last nine bytes, where the window reads past the stream, are read at every shift.
        var random = new Random(97);
        var mismatches = new List<string>();
        for (var length = 1; length <= 24; length++)
        {
            var stream = new byte[length];
            random.NextBytes(stream);
            for (var bit = 0; bit < length * 8; bit++)
            {
                ulong expected = 0;
                for (var k = 0; k < 64; k++)
                {
                    var b = bit + k;
                    var value = b < length * 8 ? (stream[b >> 3] >> (7 - (b & 7))) & 1 : 0;
                    expected = (expected << 1) | (ulong)value;
                }
                if (ModulePlacer.ReadStreamBits(stream, bit) != expected)
                    mismatches.Add($"length {length}, bit {bit}");
            }
        }
        await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    public async Task Tables_StayUnderTheirBudget()
    {
        // Printed so a growth shows in the log: each version's column template, its walk segments and its scattered modules' places.
        foreach (var version in Versions())
        {
            var bytes = ModulePlacer.StreamPlacementBytes(version);
            Console.WriteLine($"stream placement tables, version {version}: {bytes} bytes");
            await Assert.That(bytes).IsLessThanOrEqualTo(16 * 1024).Because($"version {version}");
        }
    }

    private static IEnumerable<(string Name, byte[] Stream)> Streams(int length, int seed)
    {
        var random = new byte[length];
        new Random(seed).NextBytes(random);
        yield return ("random", random);
        var random2 = new byte[length];
        new Random(seed + 1000).NextBytes(random2);
        yield return ("random 2", random2);
        yield return ("all light", new byte[length]);
        yield return ("all dark", Filled(length, 0xFF));
        yield return ("alternating", Filled(length, 0xAA));
        yield return ("cut short", random.AsSpan(0, length - 7).ToArray());
        yield return ("cut inside a byte", random.AsSpan(0, length / 3).ToArray());
        var longer = new byte[length + 5];
        random.CopyTo(longer, 0);
        new Random(seed + 2000).NextBytes(longer.AsSpan(length));
        yield return ("run long", longer);
    }

    private static byte[] Filled(int length, byte value)
    {
        var bytes = new byte[length];
        bytes.AsSpan().Fill(value);
        return bytes;
    }
}
