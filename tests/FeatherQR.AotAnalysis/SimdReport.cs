using FeatherQR.Internals;

/// <summary>
/// Which SIMD tier each kernel runs in this build, printed from the build itself, and with <c>--simd-class</c> held to
/// <see cref="SimdTiers.Expected"/> for that build class. Shared by the NativeAOT gate and the WebAssembly report
/// (tests/FeatherQR.WasmReport links this file), so every build CI checks prints the same report.
/// </summary>
internal static class SimdReport
{
    /// <summary>The build class <c>--simd-class</c> names, or null without the argument; false when the argument names none.</summary>
    public static bool TryParseClass(string[] args, out SimdBuildClass? buildClass)
    {
        buildClass = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] != "--simd-class")
                continue;
            if (i + 1 >= args.Length || !Enum.TryParse<SimdBuildClass>(args[i + 1], ignoreCase: true, out var parsed))
            {
                Console.Error.WriteLine($"--simd-class takes one of {string.Join(", ", Enum.GetNames<SimdBuildClass>())}.");
                return false;
            }
            buildClass = parsed;
        }
        return true;
    }

    /// <summary>
    /// Prints the instruction sets the build and the CPU give, then every kernel's tiers, most preferred first, with the one
    /// it takes marked. Returns 1 when <paramref name="buildClass"/> is given and the build disagrees with the table for it, else 0.
    /// </summary>
    public static int PrintAndCheck(SimdBuildClass? buildClass)
    {
        Console.WriteLine("SIMD instruction sets:");
        foreach (var (tier, available) in SimdTiers.IsaReport())
            Console.WriteLine($"  {tier,-10} {(available ? "yes" : "no")}");
        Console.WriteLine("SIMD tiers per kernel (* = taken):");
        foreach (var kernel in SimdTiers.Report())
        {
            var active = kernel.Active;
            var tiers = string.Join(" > ", kernel.Tiers.Select(t => (t.Tier == active ? "*" : "") + t.Tier).Append((active == SimdTier.Scalar ? "*" : "") + nameof(SimdTier.Scalar)));
            Console.WriteLine($"  {kernel.Name,-24} {tiers}");
        }
        if (buildClass is not { } expected)
            return 0;

        var problems = SimdTiers.Check(expected);
        if (problems.Count > 0)
        {
            Console.Error.WriteLine($"SIMD tiers disagree with the table for {expected}:");
            foreach (var problem in problems)
                Console.Error.WriteLine($"  {problem}");
            return 1;
        }
        Console.WriteLine($"SIMD tiers match the table for {expected}.");
        return 0;
    }
}
