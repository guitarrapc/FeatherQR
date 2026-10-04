using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// A pinned mask (<see cref="QRCodeGeneratorOptions.MaskPattern"/>) as <see cref="ModulePlacer.ApplyMaskPattern"/> applies it, and
/// each route it dispatches to, against the ISO/IEC 18004 predicates applied module by module: every version, every pattern, byte for
/// byte.
/// </summary>
/// <remarks>
/// The routes apply a pattern's 12-row template to the unblocked modules of each row as packed bits, so what they can get wrong is a
/// template row or column off, a blocked row read at the wrong offset (the last rows read up to the bitmask's end), and a row unpacked
/// at the wrong width. Each version has its own blocked layout and row width, so all 40 run, on placed random data, on all-light data
/// (the template's own data area) and on all-dark data, where a data module the mask flips twice, or misses, shows against its
/// neighbours.
/// </remarks>
public class ModulePlacerMaskApplyParityTest
{
    private delegate void Apply(Span<byte> buffer, int size, ReadOnlySpan<byte> blockedMask, int patternIndex);

    public static IEnumerable<int> Versions() => Enumerable.Range(1, 40);

    [Test]
    [MethodDataSource(nameof(Versions))]
    public async Task ApplyMaskPattern_MatchesPredicates(int version)
        => await AssertRoute(version, ModulePlacer.ApplyMaskPattern);

    [Test]
    [MethodDataSource(nameof(Versions))]
    public async Task ApplyMaskPatternScalar_MatchesPredicates(int version)
        => await AssertRoute(version, ModulePlacer.ApplyMaskPatternScalar);

    [Test]
    [MethodDataSource(nameof(Versions))]
    public async Task ApplyMaskPatternSimd_MatchesPredicates(int version)
    {
        if (!System.Runtime.Intrinsics.X86.Avx2.IsSupported)
        {
            Skip.Test("AVX2 not supported on this machine");
            return;
        }
        await AssertRoute(version, ModulePlacer.ApplyMaskPatternSimd);
    }

    [Test]
    [MethodDataSource(nameof(Versions))]
    public async Task ApplyMaskPatternAdvSimd_MatchesPredicates(int version)
    {
        if (!System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported)
        {
            Skip.Test("AdvSimd.Arm64 not supported on this machine");
            return;
        }
        await AssertRoute(version, ModulePlacer.ApplyMaskPatternAdvSimd);
    }

    [Test]
    public async Task ApplyMaskPattern_ShortBuffer_Throws()
    {
        var layout = ModulePlacer.GetLayout(2);
        var buffer = new byte[layout.Size * layout.Size - 1];
        await Assert.That(() => ModulePlacer.ApplyMaskPattern(buffer, layout.Size, layout.BlockedMask, 0)).ThrowsExactly<ArgumentException>();
    }

    private static async Task AssertRoute(int version, Apply apply)
    {
        var layout = ModulePlacer.GetLayout(version);
        var size = layout.Size;
        foreach (var (name, data) in Fixtures(layout))
        {
            for (var pattern = 0; pattern < 8; pattern++)
            {
                var expected = (byte[])data.Clone();
                ReferenceApply(expected, size, layout.BlockedMask, pattern);
                var actual = (byte[])data.Clone();
                apply(actual, size, layout.BlockedMask, pattern);

                var same = actual.AsSpan().CommonPrefixLength(expected);
                await Assert.That(same).IsEqualTo(expected.Length)
                    .Because($"version {version}, pattern {pattern}, {name}: first difference at row {same / size}, column {same % size}");
            }
        }
    }

    private static IEnumerable<(string Name, byte[] Data)> Fixtures(ModulePlacer.PlacementLayout layout)
    {
        var size = layout.Size;
        for (var seed = 0; seed < 2; seed++)
        {
            var random = layout.Template.ToArray();
            var codewords = new byte[layout.FreeModules / 8];
            new Random(seed * 41 + size).NextBytes(codewords);
            ModulePlacer.PlaceDataWords(random, layout, codewords);
            yield return ($"random #{seed}", random);
        }

        yield return ("all light", layout.Template.ToArray());

        var dark = layout.Template.ToArray();
        for (var i = 0; i < dark.Length; i++)
            if (!Blocked(layout.BlockedMask, i)) dark[i] = 1;
        yield return ("all dark", dark);
    }

    /// <summary>ISO/IEC 18004 Table 10, row i and column j, flipping each module the blocked bitmask leaves to data.</summary>
    private static void ReferenceApply(byte[] matrix, int size, ReadOnlySpan<byte> blockedMask, int pattern)
    {
        for (var i = 0; i < size; i++)
        {
            for (var j = 0; j < size; j++)
            {
                var flip = pattern switch
                {
                    0 => (i + j) % 2 == 0,
                    1 => i % 2 == 0,
                    2 => j % 3 == 0,
                    3 => (i + j) % 3 == 0,
                    4 => (i / 2 + j / 3) % 2 == 0,
                    5 => i * j % 2 + i * j % 3 == 0,
                    6 => (i * j % 2 + i * j % 3) % 2 == 0,
                    7 => ((i + j) % 2 + i * j % 3) % 2 == 0,
                    _ => throw new ArgumentOutOfRangeException(nameof(pattern)),
                };
                if (flip && !Blocked(blockedMask, i * size + j))
                    matrix[i * size + j] ^= 1;
            }
        }
    }

    private static bool Blocked(ReadOnlySpan<byte> blockedMask, int index) => (blockedMask[index >> 3] & (1 << (index & 7))) != 0;
}
