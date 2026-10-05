using TUnit.Assertions.Enums;
using FeatherQR.Internals;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// The transposed mask tiers (versions 12-40), on AVX2, on 128-bit vectors and on ARM64: every candidate's score against the textbook score, the
/// early abort's contract, and the chosen pattern and masked matrix against the scalar kernel, at every version the tiers serve and every
/// ECC level.
/// </summary>
/// <remarks>
/// <para>
/// The tiers hold each candidate twice, as row words and as column words (the transpose), and score every rule along the word index, so
/// what they can get wrong is per version and per word: a block transposed wrongly, a padding row or column read as modules, a format or
/// version module overlaid in one orientation and not the other, the 2x2 rule's one-bit carry between words. All 29 versions run, on placed
/// random data, all-light and all-dark data, and data striped along rows and along columns, which put long runs and finder-like windows
/// across the 64-bit word boundaries in both orientations.
/// </para>
/// <para>
/// Each route is entered directly, so an x64 machine with AVX2 runs the AVX2 and 128-bit routes, and an ARM64 machine the 128-bit and ARM64
/// routes: the dispatch takes the 128-bit tier only on x64 without AVX2 and on WebAssembly, where no test runs (the timing mode's parity
/// check holds those builds). The ARM64 route scores with the 128-bit tier's rules and its own row packing and winner unpack, and on ARM64
/// the 128-bit route's popcount is NEON's too.
/// </para>
/// </remarks>
public class ModulePlacerMaskTransposedParityTest
{
    public enum Route
    {
        Avx2,
        Vector128,
        AdvSimd,
    }

    public static IEnumerable<int> Versions() => Enumerable.Range(12, 29);

    public static IEnumerable<(int Version, Route Route)> VersionsAndRoutes()
        => from route in Enum.GetValues<Route>() from version in Versions() select (version, route);

    public static IEnumerable<Route> Routes() => Enum.GetValues<Route>();

    private static readonly QREccLevel[] EccLevels = [QREccLevel.L, QREccLevel.M, QREccLevel.Q, QREccLevel.H];

    [Test]
    [MethodDataSource(nameof(VersionsAndRoutes))]
    public async Task Scores_MatchTextbook(int version, Route route)
    {
        if (!Available(route))
        {
            Skip.Test($"{route} not run on this machine");
            return;
        }

        var layout = ModulePlacer.GetLayout(version);
        var scores = new int[8];
        foreach (var ecc in EccLevels)
        {
            foreach (var (name, data) in Fixtures(layout))
            {
                Score(route, data, version, ecc, int.MaxValue, scores);
                for (var pattern = 0; pattern < 8; pattern++)
                {
                    var expected = ModulePlacerMaskPackedParityTest.ReferenceScore(Candidate(data, layout, version, ecc, pattern), layout.Size);
                    await Assert.That(scores[pattern]).IsEqualTo(expected).Because($"version {version}, {ecc}, {name}, pattern {pattern}");
                }
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(VersionsAndRoutes))]
    public async Task AbortBound_GivesTheScoreOrMaxValue(int version, Route route)
    {
        if (!Available(route))
        {
            Skip.Test($"{route} not run on this machine");
            return;
        }

        var layout = ModulePlacer.GetLayout(version);
        var exact = new int[8];
        var scores = new int[8];
        foreach (var (name, data) in Fixtures(layout))
        {
            Score(route, data, version, QREccLevel.M, int.MaxValue, exact);

            // A bound at a candidate's own score must not abort it, even where the part left after the checkpoint is zero (a candidate
            // with no finder-like window along its rows), and below a score a candidate reads its score or int.MaxValue and nothing else.
            foreach (var bound in exact.Concat(exact.Select(s => s - 1)).Append(0).Distinct())
            {
                Score(route, data, version, QREccLevel.M, bound, scores);
                for (var pattern = 0; pattern < 8; pattern++)
                {
                    if (bound >= exact[pattern] || scores[pattern] != int.MaxValue)
                        await Assert.That(scores[pattern]).IsEqualTo(exact[pattern]).Because($"version {version}, {name}, bound {bound}, pattern {pattern}");
                }
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(VersionsAndRoutes))]
    public async Task Selection_MatchesScalarKernel(int version, Route route)
    {
        if (!Available(route))
        {
            Skip.Test($"{route} not run on this machine");
            return;
        }

        var layout = ModulePlacer.GetLayout(version);
        foreach (var ecc in EccLevels)
        {
            foreach (var (name, data) in Fixtures(layout))
            {
                var expected = (byte[])data.Clone();
                var expectedBest = ModulePlacer.MaskCode192(expected, layout.Size, version, layout.BlockedMask, ecc);
                var actual = (byte[])data.Clone();
                var actualBest = Select(route, actual, layout.Size, version, layout.BlockedMask, ecc);

                await Assert.That(actualBest).IsEqualTo(expectedBest).Because($"version {version}, {ecc}, {name}");
                await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching).Because($"version {version}, {ecc}, {name}");
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(Routes))]
    public async Task Selection_RejectsMismatchedInput(Route route)
    {
        if (!Available(route))
        {
            Skip.Test($"{route} not run on this machine");
            return;
        }

        // The tiers cache their tables per version, so a size that does not belong to the version, or a version outside the tiers, must
        // be refused before the cache is touched, and a short buffer before the rows are read.
        var layout = ModulePlacer.GetLayout(20);
        var data = Fixtures(layout).First().Data;
        await Assert.That(() => Select(route, (byte[])data.Clone(), layout.Size - 4, 20, layout.BlockedMask, QREccLevel.M)).Throws<ArgumentException>();
        await Assert.That(() => Select(route, (byte[])data.Clone(), layout.Size, 11, layout.BlockedMask, QREccLevel.M)).Throws<ArgumentException>();
        await Assert.That(() => Select(route, (byte[])data.Clone(), layout.Size, 41, layout.BlockedMask, QREccLevel.M)).Throws<ArgumentException>();
        await Assert.That(() => Select(route, data.AsSpan(0, data.Length - 1).ToArray(), layout.Size, 20, layout.BlockedMask, QREccLevel.M)).Throws<ArgumentException>();
        await Assert.That(() => Select(route, (byte[])data.Clone(), layout.Size, 20, layout.BlockedMask, (QREccLevel)4)).Throws<ArgumentOutOfRangeException>();

        // and a valid call afterwards still matches the scalar kernel (the refused calls did not poison the cache)
        var expected = (byte[])data.Clone();
        var expectedBest = ModulePlacer.MaskCode192(expected, layout.Size, 20, layout.BlockedMask, QREccLevel.M);
        var actual = (byte[])data.Clone();
        await Assert.That(Select(route, actual, layout.Size, 20, layout.BlockedMask, QREccLevel.M)).IsEqualTo(expectedBest);
        await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Tables_StayUnderTheirBudget()
    {
        // Printed so a growth shows in the log: each version's tables are its unblocked rows and columns, two or three words each, shared by
        // both tiers.
        foreach (var version in Versions())
        {
            var bytes = ModulePlacer.TransposedTableBytes(version);
            Console.WriteLine($"transposed mask tables, version {version}: {bytes} bytes");
            await Assert.That(bytes).IsLessThanOrEqualTo(10 * 1024).Because($"version {version}");
        }
    }

    private static bool Available(Route route) => route switch
    {
        Route.Avx2 => System.Runtime.Intrinsics.X86.Avx2.IsSupported,
        Route.Vector128 => System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated,
        Route.AdvSimd => System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported,
        _ => false,
    };

    private static void Score(Route route, byte[] data, int version, QREccLevel ecc, int abortAbove, int[] scores)
    {
        switch (route)
        {
            case Route.Avx2: ModulePlacer.ScoreCandidatesTransposed(data, version, ecc, abortAbove, scores); break;
            case Route.Vector128: ModulePlacer.ScoreCandidatesTransposedVector128(data, version, ecc, abortAbove, scores); break;
            default: ModulePlacer.ScoreCandidatesTransposedAdvSimd(data, version, ecc, abortAbove, scores); break;
        }
    }

    private static int Select(Route route, byte[] buffer, int size, int version, byte[] blockedMask, QREccLevel ecc) => route switch
    {
        Route.Avx2 => ModulePlacer.MaskCodeTransposed(buffer, size, version, blockedMask, ecc),
        Route.Vector128 => ModulePlacer.MaskCodeTransposedVector128(buffer, size, version, blockedMask, ecc),
        _ => ModulePlacer.MaskCodeTransposedAdvSimd(buffer, size, version, blockedMask, ecc),
    };

    /// <summary>Candidate <paramref name="pattern"/> as the final symbol: the mask on the data area, its format information, the version information.</summary>
    private static byte[] Candidate(byte[] data, ModulePlacer.PlacementLayout layout, int version, QREccLevel ecc, int pattern)
    {
        var candidate = (byte[])data.Clone();
        ModulePlacer.ApplyMaskPattern(candidate, layout.Size, layout.BlockedMask, pattern);
        ModulePlacer.PlaceFormat(candidate, layout.Size, QRCodeConstants.GetFormatBits(ecc, pattern));
        if (version >= 7)
            ModulePlacer.PlaceVersion(candidate, layout.Size, QRCodeConstants.GetVersionBits(version));
        return candidate;
    }

    private static IEnumerable<(string Name, byte[] Data)> Fixtures(ModulePlacer.PlacementLayout layout)
    {
        var size = layout.Size;
        for (var seed = 0; seed < 2; seed++)
        {
            var random = layout.Template.ToArray();
            var codewords = new byte[layout.FreeModules / 8];
            new Random(seed * 37 + size).NextBytes(codewords);
            ModulePlacer.PlaceDataWords(random, layout, codewords);
            yield return ($"random #{seed}", random);
        }

        yield return ("all light", layout.Template.ToArray());
        yield return ("all dark", Fill(layout, (_, _) => true));
        yield return ("row stripes", Fill(layout, (y, _) => y % 4 < 2));
        yield return ("column stripes", Fill(layout, (_, x) => x % 6 < 3));
    }

    private static byte[] Fill(ModulePlacer.PlacementLayout layout, Func<int, int, bool> dark)
    {
        var size = layout.Size;
        var data = layout.Template.ToArray();
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var index = y * size + x;
                if ((layout.BlockedMask[index >> 3] & (1 << (index & 7))) == 0)
                    data[index] = dark(y, x) ? (byte)1 : (byte)0;
            }
        }
        return data;
    }
}
