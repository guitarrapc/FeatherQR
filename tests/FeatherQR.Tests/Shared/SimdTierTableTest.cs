using FeatherQR.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// <see cref="SimdTiers.Expected"/> says which tier each kernel takes per build class, and CI holds every
/// build to it (tests/FeatherQR.AotAnalysis --simd-class). These tests check the table itself, on any
/// machine: that each cell is written in the kernel's own tiers, that the table agrees with the declared
/// tiers on every state a build class allows, and that a kernel losing the tier the table expects fails
/// the check in every build class and state where it was the one taken.
/// </summary>
/// <remarks>
/// A state is one assignment of the instruction sets a build class leaves to the CPU (GFNI, fast PEXT,
/// the ARM64 dot product). The kernels are simulated in a state by giving each tier its instruction set's
/// value, which is each kernel's own condition wherever a build class can hold it.
/// </remarks>
public class SimdTierTableTest
{
    public static IEnumerable<Func<string>> BuildClasses()
        => Enum.GetNames<SimdBuildClass>().Select(name => (Func<string>)(() => name));

    [Test]
    public async Task Table_HasEveryKernelOnce()
    {
        var reported = SimdTiers.Report().Select(k => k.Name).Order(StringComparer.Ordinal).ToArray();
        var rows = SimdTiers.Expected().Select(e => e.Kernel).ToArray();

        await Assert.That(rows.Distinct().Count()).IsEqualTo(rows.Length);
        await Assert.That(rows.Order(StringComparer.Ordinal).ToArray()).IsEquivalentTo(reported);
    }

    [Test]
    public async Task Definitions_SplitTheTiersIntoPresentAbsentAndLeftToTheCpu()
    {
        foreach (var buildClass in Enum.GetValues<SimdBuildClass>())
        {
            var (present, absent, leftToCpu) = SimdTiers.Definition(buildClass);
            var all = present.Concat(absent).Concat(leftToCpu.SelectMany(g => g)).ToArray();

            await Assert.That(all.Distinct().Count()).IsEqualTo(all.Length).Because($"each tier is in one part: {buildClass}");
            await Assert.That(all.Order().ToArray()).IsEquivalentTo(Enum.GetValues<SimdTier>().Where(t => t != SimdTier.Scalar).Order().ToArray()).Because($"{buildClass}");
            await Assert.That(leftToCpu.All(g => g.Length > 0)).IsTrue().Because($"{buildClass}");
            await Assert.That(present).Contains(SimdTier.Vector128).Because($"every build class runs 128-bit vectors: {buildClass}");
        }
    }

    [Test]
    [MethodDataSource(nameof(BuildClasses))]
    public async Task Cells_AreTheKernelsOwnTiers_MostPreferredFirst(string buildClassName)
    {
        var buildClass = Enum.Parse<SimdBuildClass>(buildClassName);
        var kernels = SimdTiers.Report().ToDictionary(k => k.Name);
        var (present, absent, _) = SimdTiers.Definition(buildClass);
        foreach (var row in SimdTiers.Expected())
        {
            var cell = row.For(buildClass);
            var order = kernels[row.Kernel].Tiers.Select(t => t.Tier).Append(SimdTier.Scalar).ToList();
            var because = $"{row.Kernel} on {buildClass}: {string.Join(", ", cell)}";

            await Assert.That(cell.Length).IsGreaterThan(0).Because(because);
            await Assert.That(cell.All(order.Contains)).IsTrue().Because(because + " names only the kernel's tiers");
            var indexes = cell.Select(t => order.IndexOf(t)).ToArray();
            await Assert.That(indexes.Zip(indexes.Skip(1), (a, b) => a < b).All(x => x)).IsTrue().Because(because + " follows the kernel's preference");
            await Assert.That(cell.Any(absent.Contains)).IsFalse().Because(because + " names no tier the build class lacks");
            // Every entry but the last is one the CPU decides: one the build class always has leaves the rest unreachable
            await Assert.That(cell.SkipLast(1).Any(t => t == SimdTier.Scalar || present.Contains(t))).IsFalse().Because(because);
        }
    }

    [Test]
    [MethodDataSource(nameof(BuildClasses))]
    public async Task Table_AgreesWithTheDeclaredTiers_InEveryStateOfTheClass(string buildClassName)
    {
        var buildClass = Enum.Parse<SimdBuildClass>(buildClassName);
        foreach (var isa in States(buildClass))
        {
            var problems = SimdTiers.Check(buildClass, Simulate(SimdTiers.Report(), isa), isa);

            await Assert.That(problems).IsEmpty().Because($"{Describe(isa)}: {string.Join("; ", problems)}");
        }
    }

    [Test]
    [MethodDataSource(nameof(BuildClasses))]
    public async Task LosingTheTakenTier_FailsTheCheck(string buildClassName)
    {
        var buildClass = Enum.Parse<SimdBuildClass>(buildClassName);
        var planted = 0;
        foreach (var isa in States(buildClass))
        {
            var kernels = Simulate(SimdTiers.Report(), isa);
            for (var k = 0; k < kernels.Length; k++)
            {
                var taken = kernels[k].Active;
                if (taken == SimdTier.Scalar)
                    continue;
                // The gate removed from the code and the tier from SimdTiers together, which the source check lets through
                var lowered = kernels.ToArray();
                lowered[k] = new SimdKernel(kernels[k].Name, kernels[k].Tiers.Where(t => t.Tier != taken).ToArray());
                var problems = SimdTiers.Check(buildClass, lowered, isa);

                await Assert.That(problems.Any(p => p.StartsWith(kernels[k].Name + " ", StringComparison.Ordinal))).IsTrue()
                    .Because($"{kernels[k].Name} without its {taken} tier, {Describe(isa)}");
                planted++;
            }
        }

        await Assert.That(planted).IsGreaterThan(0);
    }

    [Test]
    [MethodDataSource(nameof(BuildClasses))]
    public async Task Check_RefusesAProcessOutsideTheClass(string buildClassName)
    {
        var buildClass = Enum.Parse<SimdBuildClass>(buildClassName);
        var (present, absent, _) = SimdTiers.Definition(buildClass);
        foreach (var missing in present)
        {
            var isa = States(buildClass).First().Select(t => t.Tier == missing ? (t.Tier, false) : t).ToArray();

            await Assert.That(SimdTiers.Check(buildClass, [], isa)).IsNotEmpty().Because($"{buildClass} without {missing}");
        }
        foreach (var extra in absent)
        {
            var isa = States(buildClass).First().Select(t => t.Tier == extra ? (t.Tier, true) : t).ToArray();

            await Assert.That(SimdTiers.Check(buildClass, [], isa)).IsNotEmpty().Because($"{buildClass} with {extra}");
        }
    }

    /// <summary>The live check: the build class this process is, held to the table. CI runs the same check on each NativeAOT build.</summary>
    [Test]
    public async Task ThisProcess_TakesWhatTheTableExpects()
    {
        var isa = SimdTiers.IsaReport();
        var matching = Enum.GetValues<SimdBuildClass>().Where(c => SimdTiers.Check(c, [], isa).Count == 0).ToArray();
        if (matching.Length == 0)
            Skip.Test("This process is no build class the table covers (DOTNET_EnableHWIntrinsic=0, or AVX without AVX2).");

        var problems = SimdTiers.Check(matching.Single());

        await Assert.That(problems).IsEmpty().Because($"{matching.Single()}: {string.Join("; ", problems)}");
    }

    /// <summary>Every CPU the build class allows: each group it leaves to the CPU there or not; the other tiers as the class fixes them.</summary>
    private static IEnumerable<(SimdTier Tier, bool Available)[]> States(SimdBuildClass buildClass)
    {
        var (present, _, leftToCpu) = SimdTiers.Definition(buildClass);
        var tiers = Enum.GetValues<SimdTier>().Where(t => t != SimdTier.Scalar).ToArray();
        for (var mask = 0; mask < 1 << leftToCpu.Length; mask++)
        {
            var has = present.Concat(leftToCpu.Where((_, g) => (mask & (1 << g)) != 0).SelectMany(g => g)).ToHashSet();
            yield return tiers.Select(t => (t, has.Contains(t))).ToArray();
        }
    }

    private static SimdKernel[] Simulate(SimdKernel[] kernels, (SimdTier Tier, bool Available)[] isa)
    {
        var available = isa.ToDictionary(t => t.Tier, t => t.Available);
        return kernels.Select(k => new SimdKernel(k.Name, k.Tiers.Select(t => (t.Tier, available[t.Tier])).ToArray())).ToArray();
    }

    private static string Describe((SimdTier Tier, bool Available)[] isa)
        => "with " + string.Join(", ", isa.Where(t => t.Available).Select(t => t.Tier));
}
