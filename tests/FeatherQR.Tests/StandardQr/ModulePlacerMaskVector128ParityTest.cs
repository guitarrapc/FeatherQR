using TUnit.Assertions.Enums;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// The 128-bit mask selection tier (ModulePlacer.Masking.Simd.cs) against the scalar bit-packed kernels, entered directly: the
/// dispatch takes it only on x64 without AVX2 and on WebAssembly, so this is where an x64 machine with AVX2 runs it at all. Same pattern,
/// byte-identical matrix.
/// </summary>
public class ModulePlacerMaskVector128ParityTest
{
    // 1..11 the lane-per-pattern tier (11 = size 61, the last one-word version); 12, 27, 28 and 40 the transposed tier at each end of its
    // two-word and three-word sizes (ModulePlacerMaskTransposedParityTest has every version)
    public static IEnumerable<int> Versions => [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 27, 28, 40];

    [Test]
    [MethodDataSource(nameof(Versions))]
    public async Task MaskCodeVector128_MatchesScalarKernels(int version)
    {
        if (!System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated)
        {
            Skip.Test("Vector128 not accelerated on this machine");
            return;
        }

        foreach (var eccLevel in new[] { QREccLevel.L, QREccLevel.M, QREccLevel.Q, QREccLevel.H })
        {
            for (var seed = 0; seed < 8; seed++)
            {
                var (buffer, blockedMask, size) = BuildFixture(version, seed);
                await AssertMatches(buffer, blockedMask, size, version, eccLevel);
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(Versions))]
    public async Task MaskCodeVector128_AllZeroAndAllOneData_MatchesScalarKernels(int version)
    {
        if (!System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated)
        {
            Skip.Test("Vector128 not accelerated on this machine");
            return;
        }

        // Long runs, uniform blocks and an extreme balance: every rule scores high, and later groups meet the checkpoint
        foreach (var fill in new byte[] { 0, 1 })
        {
            var (buffer, blockedMask, size) = BuildFixture(version, seed: 0);
            for (var i = 0; i < buffer.Length; i++)
            {
                if ((blockedMask[i >> 3] & (1 << (i & 7))) == 0)
                {
                    buffer[i] = fill;
                }
            }
            await AssertMatches(buffer, blockedMask, size, version, QREccLevel.M);
        }
    }

    [Test]
    public async Task MaskCode64Vector128_RejectsSizeVersionMismatchAndShortBuffer()
    {
        if (!System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated)
        {
            Skip.Test("Vector128 not accelerated on this machine");
            return;
        }

        // The tier caches its tables per version, so a size that does not belong to the version must be rejected before the cache is
        // touched; a short buffer before the wide row loads.
        var (buffer, blockedMask, size) = BuildFixture(1, seed: 0);
        await Assert.That(() => ModulePlacer.MaskCode64Vector128(buffer, 25, 1, blockedMask, QREccLevel.M)).Throws<ArgumentException>();
        await Assert.That(() => ModulePlacer.MaskCode64Vector128(buffer, size, 12, blockedMask, QREccLevel.M)).Throws<ArgumentException>();
        await Assert.That(() => ModulePlacer.MaskCode64Vector128(buffer.AsSpan(0, size * size - 1), size, 1, blockedMask, QREccLevel.M)).Throws<ArgumentException>();
        await Assert.That(() => ModulePlacer.MaskCode64Vector128(buffer, size, 1, blockedMask, (QREccLevel)4)).Throws<ArgumentOutOfRangeException>();
        var ok = ModulePlacer.MaskCode64Vector128((byte[])buffer.Clone(), size, 1, blockedMask, QREccLevel.M);
        await Assert.That(ok).IsBetween(0, 7);
    }

    private static async Task AssertMatches(byte[] buffer, byte[] blockedMask, int size, int version, QREccLevel eccLevel)
    {
        var expectedBuffer = (byte[])buffer.Clone();
        var expectedBest = size <= 64
            ? ModulePlacer.MaskCode64(expectedBuffer, size, version, blockedMask, eccLevel)
            : ModulePlacer.MaskCode192(expectedBuffer, size, version, blockedMask, eccLevel);

        var actualBuffer = (byte[])buffer.Clone();
        var actualBest = ModulePlacer.MaskCodeVector128(actualBuffer, size, version, blockedMask, eccLevel);

        await Assert.That(actualBest).IsEquivalentTo(expectedBest);
        await Assert.That(actualBuffer).IsEquivalentTo(expectedBuffer, CollectionOrdering.Matching);
    }

    /// <summary>A version's template with random data placed, as the generator hands it to the mask selection.</summary>
    private static (byte[] Buffer, byte[] BlockedMask, int Size) BuildFixture(int version, int seed)
    {
        var layout = ModulePlacer.GetLayout(version);
        var buffer = new byte[layout.Size * layout.Size];
        layout.Template.AsSpan().CopyTo(buffer);
        var codewords = new byte[layout.FreeModules / 8];
        new Random(seed * 41 + version).NextBytes(codewords);
        ModulePlacer.PlaceDataWords(buffer, layout, codewords);
        return (buffer, layout.BlockedMask, layout.Size);
    }
}
