using FeatherQR.Internals.StandardQR;
using FeatherQR.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// Verifies that the ARM64 NEON mask selection tiers (ModulePlacer.Masking.Simd.Arm.cs:
/// single-word, two-word SoA, three-word SoA) select the same pattern and produce a
/// byte-identical matrix as the scalar bit-packed kernels. MaskCode dispatches by
/// hardware capability, so these tests pin BOTH sides explicitly: the scalar kernels
/// are called directly, and the NEON entry point is called directly (skipped on
/// machines without AdvSimd.Arm64).
/// </summary>
public class ModulePlacerMaskAdvSimdParityTest
{
    // Versions covering every SIMD tier and its boundaries:
    // 1/5/7/10/11 -> single-word (11 = size 61, last one-word version),
    // 12 -> first two-word (size 65), 20/27 -> two-word interior/last (27 = size 125),
    // 28 -> first three-word (size 129), 34/40 -> three-word interior/max.
    public static IEnumerable<int> Versions => [1, 5, 7, 10, 11, 12, 20, 27, 28, 34, 40];

    [Test]
    [MethodDataSource(nameof(Versions))]
    public async Task MaskCodeAdvSimd_MatchesScalarKernels(int version)
    {
        if (!System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported)
        {
            Skip.Test("AdvSimd.Arm64 not supported on this machine");
            return;
        }

        QREccLevel[] eccLevels = [QREccLevel.L, QREccLevel.M, QREccLevel.Q, QREccLevel.H];

        foreach (var eccLevel in eccLevels)
        {
            for (var seed = 0; seed < 3; seed++)
            {
                var (buffer, blockedMask, size) = BuildFixture(version, seed);

                var expectedBuffer = (byte[])buffer.Clone();
                var expectedBest = size <= 64
                    ? ModulePlacer.MaskCode64(expectedBuffer, size, version, blockedMask, eccLevel)
                    : ModulePlacer.MaskCode192(expectedBuffer, size, version, blockedMask, eccLevel);

                var actualBuffer = (byte[])buffer.Clone();
                var actualBest = ModulePlacer.MaskCodeAdvSimd(actualBuffer, size, version, blockedMask, eccLevel);

                await Assert.That(actualBest).IsEquivalentTo(expectedBest);
                await Assert.That(actualBuffer).IsEquivalentTo(expectedBuffer);
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(Versions))]
    public async Task MaskCodeAdvSimd_AllZeroAndAllOneData_MatchesScalarKernels(int version)
    {
        if (!System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported)
        {
            Skip.Test("AdvSimd.Arm64 not supported on this machine");
            return;
        }

        // Degenerate fills maximize penalty-rule hits (long runs, uniform 2x2
        // blocks, extreme balance) and exercise every scoring branch, including
        // the vector loops' scalar tails.
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

            var expectedBuffer = (byte[])buffer.Clone();
            var expectedBest = size <= 64
                ? ModulePlacer.MaskCode64(expectedBuffer, size, version, blockedMask, QREccLevel.M)
                : ModulePlacer.MaskCode192(expectedBuffer, size, version, blockedMask, QREccLevel.M);

            var actualBuffer = (byte[])buffer.Clone();
            var actualBest = ModulePlacer.MaskCodeAdvSimd(actualBuffer, size, version, blockedMask, QREccLevel.M);

            await Assert.That(actualBest).IsEquivalentTo(expectedBest);
            await Assert.That(actualBuffer).IsEquivalentTo(expectedBuffer);
        }
    }

    /// <summary>
    /// Builds realistic MaskCode inputs the same way QRCodeGenerator.WriteQRMatrix
    /// does (same construction as ModulePlacerMaskPackedParityTest).
    /// </summary>
    private static (byte[] Buffer, byte[] BlockedMask, int Size) BuildFixture(int version, int seed)
    {
        var size = 21 + (version - 1) * 4;
        var buffer = new byte[size * size];

        var blockedModules = new Rectangle[128];
        var blockedCount = 0;
        var alignmentPatternLocations = GetAlignmentPatternPositions(version);

        ModulePlacer.PlaceFinderPatterns(buffer, size, blockedModules, ref blockedCount);
        ModulePlacer.ReserveSeparatorAreas(size, blockedModules, ref blockedCount);
        ModulePlacer.PlaceAlignmentPatterns(buffer, size, alignmentPatternLocations, blockedModules, ref blockedCount);
        ModulePlacer.PlaceTimingPatterns(buffer, size, blockedModules, ref blockedCount);
        ModulePlacer.PlaceDarkModule(buffer, size, version, blockedModules, ref blockedCount);
        ModulePlacer.ReserveVersionAreas(size, version, blockedModules, ref blockedCount);

        var blockedMask = new byte[(size * size + 7) / 8];
        QRCodeGenerator.BuildBlockedMask(blockedMask, size, blockedModules.AsSpan(0, blockedCount));

        var freeModules = 0;
        for (var i = 0; i < size * size; i++)
        {
            if ((blockedMask[i >> 3] & (1 << (i & 7))) == 0) freeModules++;
        }
        var data = new byte[(freeModules + 7) / 8];
        new Random(seed).NextBytes(data);
        ModulePlacer.PlaceDataWords(buffer, size, data, blockedMask);

        return (buffer, blockedMask, size);
    }

    private static List<Point> GetAlignmentPatternPositions(int version)
    {
        var table = QRCodeConstants.AlignmentPatternTable;
        for (var i = 0; i < table.Count; i++)
        {
            if (table[i].Version == version)
                return table[i].PatternPositions;
        }
        throw new InvalidOperationException($"Alignment pattern positions not found for version {version}");
    }
}
