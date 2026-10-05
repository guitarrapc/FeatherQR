using TUnit.Assertions.Enums;
using System.Text.RegularExpressions;
using FeatherQR.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// <see cref="SimdTiers"/> is the one place that says which SIMD tiers each kernel has, and the report
/// built from it is what a build is asked to show. The kernels keep their own <c>IsSupported</c> reads
/// (see the remarks on <see cref="SimdTiers"/>), so these tests hold the table and the code together:
/// every instruction-set flag a kernel's files read is the condition of a tier the table declares for
/// that kernel, every declared tier is read there, and no other file reads a flag at all.
/// </summary>
/// <remarks>
/// The table's values are checked in whatever process runs the tests, so each CI leg (x64, ARM64, and
/// the knob runs that emulate a default NativeAOT publish) checks the combination of values it has.
/// </remarks>
public class SimdTiersTest
{
    /// <summary>
    /// The files whose flag reads are each kernel's dispatch, paths under src/FeatherQR/Internals.
    /// A kernel added to <see cref="SimdTiers.Report"/> needs its files here. Two kernels that dispatch in one file can each name their
    /// dispatch method (<c>file#Method</c>), so a read there belongs to that kernel alone.
    /// </summary>
    private static readonly Dictionary<string, string[]> KernelFiles = new()
    {
        ["TextAnalyzer"] = ["TextAnalyzer.cs"],
        ["ModuleBitPacker"] = ["ModuleBitPacker.cs"],
        ["ModeSegmenterLanes"] = ["ModeSegmenter.Lanes.cs", "ModeSegmenter.Lanes.Arm64.cs", "ModeSegmenter.Lanes.Simd.cs"],
        ["EccBinaryEncoder"] = ["BinaryEncoders/EccBinaryEncoder.cs", "BinaryEncoders/EccBinaryEncoder.X86.cs", "BinaryEncoders/EccBinaryEncoder.Wasm.cs"],
        ["EccBinaryDecoder"] = ["BinaryDecoders/EccBinaryDecoder.cs", "BinaryDecoders/EccBinaryDecoder.Arm64.cs", "BinaryDecoders/EccBinaryDecoder.Vector128.cs"],
        ["LuminanceConverter"] = ["ImageDecoders/LuminanceConverter.cs", "ImageDecoders/LuminanceConverter.Simd.cs"],
        ["LuminanceInverter"] = ["ImageDecoders/LuminanceInverter.cs"],
        ["Binarizer"] = ["ImageDecoders/Binarizer.cs", "ImageDecoders/Binarizer.Vector128.cs"],
        ["LocalBinarizer"] = ["ImageDecoders/LocalBinarizer.Vector128.cs"],
        ["FinderRowMask"] = ["ImageDecoders/FinderPatternFinder.cs", "ImageDecoders/FinderPatternFinder.Simd.cs"],
        ["FinderRowEdges"] = ["ImageDecoders/FinderPatternFinder.RowEdges.cs", "ImageDecoders/FinderPatternFinder.RowEdges.Simd.cs"],
        ["PerspectiveGridSampler"] = ["ImageDecoders/PerspectiveGridSampler.cs", "ImageDecoders/VectorCast.Simd.cs"],
        ["ModulePlacerExpandBits"] = ["StandardQR/ModulePlacer.ExpandBits.cs"],
        ["ModulePlacerMaskCode"] = ["StandardQR/ModulePlacer.Masking.cs", "StandardQR/ModulePlacer.Masking.Simd.cs"],
        ["QRAlphanumericWriter"] = ["StandardQR/QRBinaryEncoder.cs#WriteAlphanumericData"],
        ["QRNumericWriter"] = ["StandardQR/QRBinaryEncoder.cs#WriteNumericData"],
        ["AlignmentRowMask"] = ["StandardQR/AlignmentPatternFinder.cs", "StandardQR/AlignmentPatternFinder.Simd.cs"],
        ["QRSampleGridPiecewise"] = ["StandardQR/QRImageDecoder.PiecewiseSampling.cs", "StandardQR/QRImageDecoder.PiecewiseSampling.X86.cs", "StandardQR/QRImageDecoder.PiecewiseSampling.Arm64.cs", "StandardQR/QRImageDecoder.PiecewiseSampling.Vector128.cs", "ImageDecoders/VectorCast.Simd.cs"],
        ["StructuredAppendLanes"] = ["StandardQR/StructuredAppendPlanner.Lanes.cs", "StandardQR/StructuredAppendPlanner.Lanes.Arm64.cs", "StandardQR/StructuredAppendPlanner.Lanes.Simd.cs", "StandardQR/StructuredAppendPlanner.Lanes.Vector256.cs"],
        ["StructuredAppendParity"] = ["StandardQR/StructuredAppendPlanner.Parity.cs"],
        ["StructuredAppendScanner"] = ["StandardQR/StructuredAppendScanner.cs"],
        ["MicroQRByteSegment"] = ["MicroQR/MicroQRBinaryEncoder.cs"],
        ["MicroQRModulePlacer"] = ["MicroQR/MicroQRModulePlacer.PlaceSymbol.cs", "MicroQR/MicroQRModulePlacer.PlaceSymbol.Simd.cs"],
        ["MicroQRSampleGrid"] = ["MicroQR/MicroQRImageDecoder.cs", "ImageDecoders/VectorCast.Simd.cs"],
        ["RmQRValueSegments"] = ["RmQR/RmQRBinaryEncoder.cs"],
        ["RmQRLatin1Segment"] = ["RmQR/RmQRBinaryEncoder.cs"],
        ["RmQRModulePlacer"] = ["RmQR/RmQRModulePlacer.cs", "RmQR/RmQRModulePlacer.Arm64.cs"],
        ["RmQRExtractCodewords"] = ["RmQR/RmQRMatrixDecoder.cs", "RmQR/RmQRMatrixDecoder.X86.cs", "RmQR/RmQRMatrixDecoder.Arm64.cs", "RmQR/RmQRMatrixDecoder.Vector128.cs"],
        ["RmQRSubFinderLattice"] = ["RmQR/RmQRImageDecoder.cs", "ImageDecoders/VectorCast.Simd.cs"],
        ["RmQRSampleGrid"] = ["RmQR/RmQRImageDecoder.cs", "RmQR/RmQRImageDecoder.Vector128.cs", "ImageDecoders/VectorCast.Simd.cs"],
    };

    /// <summary>
    /// The flag reads each tier's condition is made of: all of <c>Required</c> must be read in the kernel's files, and a read in them must belong to one of its tiers' <c>Allowed</c>.
    /// </summary>
    private static readonly Dictionary<SimdTier, (string[] Required, string[] Allowed)> TierReads = new()
    {
        [SimdTier.Vector128] = (["Vector128"], ["Vector128"]),
        [SimdTier.Vector256] = (["Vector256"], ["Vector256"]),
        [SimdTier.Sse2] = (["Sse2"], ["Sse2"]),
        [SimdTier.Ssse3] = (["Ssse3"], ["Ssse3"]),
        [SimdTier.Sse41] = (["Sse41"], ["Sse41", "Ssse3"]),
        [SimdTier.Avx2] = (["Avx2"], ["Avx2"]),
        [SimdTier.Avx2Pext] = (["HasFastPext"], ["HasFastPext", "Avx2"]),
        [SimdTier.Gfni] = (["Gfni"], ["Gfni"]),
        [SimdTier.GfniV256] = (["Gfni.V256"], ["Gfni.V256", "Avx2"]),
        [SimdTier.AdvSimd] = (["AdvSimd.Arm64"], ["AdvSimd.Arm64"]),
        [SimdTier.AdvSimdDp] = (["Dp"], ["Dp", "AdvSimd.Arm64"]),
        [SimdTier.PackedSimd] = (["PackedSimd"], ["PackedSimd"]),
    };

    /// <summary>The files that read flags without being a kernel: the table itself, and the CPU facts <c>IsSupported</c> does not express.</summary>
    private static readonly string[] NonKernelFiles = ["SimdTiers.cs", "HardwareCapabilities.X86.cs"];

    [Test]
    public async Task EveryReportedKernel_HasItsFiles_AndNoOther()
    {
        var reported = SimdTiers.Report().Select(k => k.Name).Order(StringComparer.Ordinal).ToArray();

        await Assert.That(reported.Distinct().Count()).IsEqualTo(reported.Length);
        await Assert.That(KernelFiles.Keys.Order(StringComparer.Ordinal).ToArray()).IsEquivalentTo(reported, CollectionOrdering.Matching);
        foreach (var spec in KernelFiles.Values.SelectMany(f => f).Distinct())
        {
            var (file, method) = ParseSpec(spec);
            await Assert.That(File.Exists(Path.Combine(InternalsRoot(), file))).IsTrue().Because(spec);
            if (method is not null)
                await Assert.That(MethodLines(File.ReadAllText(Path.Combine(InternalsRoot(), file)), method)).IsNotNull().Because(spec);
        }
    }

    [Test]
    public async Task EveryTier_HasItsReads()
    {
        await Assert.That(TierReads.Keys.Order().ToArray()).IsEquivalentTo(Enum.GetValues<SimdTier>().Where(t => t != SimdTier.Scalar).Order().ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task KernelFiles_ReadOnlyTheFlagsOfTheirDeclaredTiers()
    {
        var root = InternalsRoot();
        // per file, the reads each kernel's tiers allow, over the whole file or over its dispatch method's lines
        var scopesByFile = SimdTiers.Report()
            .SelectMany(k => KernelFiles[k.Name].Select(spec => (Spec: ParseSpec(spec), Allowed: k.Tiers.SelectMany(t => TierReads[t.Tier].Allowed))))
            .GroupBy(x => x.Spec.File)
            .ToDictionary(g => g.Key, g => g.ToList());
        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, ".."), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.StartsWith("../obj/", StringComparison.Ordinal) || relative.StartsWith("../bin/", StringComparison.Ordinal) || NonKernelFiles.Contains(relative))
                continue;
            var source = File.ReadAllText(file);
            var scopes = scopesByFile.TryGetValue(relative, out var list)
                ? list.Select(s => (Lines: s.Spec.Method is null ? (0, int.MaxValue) : MethodLines(source, s.Spec.Method) ?? (0, -1), s.Allowed)).ToList()
                : [];
            foreach (var (line, read) in FindIsaReads(source))
            {
                if (!scopes.Any(s => line >= s.Lines.Item1 && line <= s.Lines.Item2 && s.Allowed.Contains(read)))
                    violations.Add($"{relative}:{line} reads {read}");
            }
        }

        await Assert.That(violations).IsEmpty()
            .Because("a flag a kernel reads is the condition of a tier SimdTiers declares for it, or the report misses a tier the code has: " + string.Join("; ", violations));
    }

    [Test]
    public async Task EveryDeclaredTier_IsReadInItsKernelsFiles()
    {
        var root = InternalsRoot();
        var missing = new List<string>();
        foreach (var kernel in SimdTiers.Report())
        {
            var reads = KernelFiles[kernel.Name].SelectMany(spec => ReadsIn(root, spec)).Select(r => r.Read).ToHashSet();
            foreach (var (tier, _) in kernel.Tiers)
            {
                foreach (var read in TierReads[tier].Required.Where(r => !reads.Contains(r)))
                    missing.Add($"{kernel.Name}.{tier} needs {read}");
            }
        }

        await Assert.That(missing).IsEmpty()
            .Because("a tier SimdTiers declares is one the kernel's dispatch reads, or the report claims a tier the code does not have: " + string.Join("; ", missing));
    }

    /// <summary>The other half of the table: each tier's own condition in <c>SimdTiers.Isa</c> reads that tier's flags, so a tier name means the same instruction set in the report as in the code.</summary>
    [Test]
    public async Task IsaConditions_ReadTheirTiersFlags()
    {
        var conditions = new Dictionary<SimdTier, List<HashSet<string>>>();
        foreach (var line in File.ReadAllLines(Path.Combine(InternalsRoot(), "SimdTiers.cs")))
        {
            var match = IsaCondition.Match(line);
            if (!match.Success || !Enum.TryParse<SimdTier>(match.Groups["tier"].Value, out var tier))
                continue;
            var reads = FindIsaReads(match.Groups["condition"].Value).Select(r => r.Read).ToHashSet();
            if (reads.Count == 0)
                continue; // the "=> false" of a target without the instruction set
            if (!conditions.TryGetValue(tier, out var list))
                conditions[tier] = list = [];
            list.Add(reads);
        }

        await Assert.That(conditions.Keys.Order().ToArray()).IsEquivalentTo(TierReads.Keys.Order().ToArray(), CollectionOrdering.Matching);
        foreach (var (tier, list) in conditions)
        {
            foreach (var reads in list)
            {
                await Assert.That(TierReads[tier].Required.All(reads.Contains) && reads.All(TierReads[tier].Allowed.Contains)).IsTrue()
                    .Because($"Isa.{tier} reads {string.Join(", ", reads)}");
            }
        }
    }

    private static readonly Regex IsaCondition = new(@"^\s*internal static bool (?<tier>\w+) => (?<condition>.+);\s*$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    [Test]
    public async Task RunnableTiers_HaveTheirInstructionSet()
    {
        var isa = SimdTiers.IsaReport().ToDictionary(t => t.Tier, t => t.Available);
        foreach (var kernel in SimdTiers.Report())
        {
            foreach (var (tier, _) in kernel.Tiers.Where(t => t.Runs))
            {
                await Assert.That(isa[tier]).IsTrue().Because($"{kernel.Name} runs {tier}, so the {tier} instruction set is there");
            }
        }
    }

    [Test]
    public async Task Active_IsTheFirstTierThatRuns()
    {
        foreach (var kernel in SimdTiers.Report())
        {
            var expected = kernel.Tiers.Where(t => t.Runs).Select(t => t.Tier).DefaultIfEmpty(SimdTier.Scalar).First();

            await Assert.That(kernel.Active).IsEqualTo(expected).Because(kernel.Name);
        }
    }

    [Test]
    public async Task IsaReport_CoversEveryTierButScalar()
    {
        var reported = SimdTiers.IsaReport().Select(t => t.Tier).ToArray();

        await Assert.That(reported).IsEquivalentTo(Enum.GetValues<SimdTier>().Where(t => t != SimdTier.Scalar).ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("if (Avx2.IsSupported)", "Avx2")]
    [Arguments("if (System.Runtime.Intrinsics.X86.Ssse3.IsSupported)", "Ssse3")]
    [Arguments("        else if (Gfni.V256.IsSupported && Avx2.IsSupported)", "Gfni.V256,Avx2")]
    [Arguments("        => System.Runtime.Intrinsics.Arm.Dp.IsSupported && System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported;", "Dp,AdvSimd.Arm64")]
    [Arguments("        var lanes = Vector256.IsHardwareAccelerated ? 16 : 8;", "Vector256")]
    [Arguments("if (Avx2.IsSupported && HardwareCapabilities.HasFastPext)", "Avx2,HasFastPext")]
    [Arguments("Debug.Assert(AdvSimd.Arm64.IsSupported);", "AdvSimd.Arm64")]
    [Arguments("var x = 1; // Avx2.IsSupported is read in the dispatch", "")]
    [Arguments("/// Caller guarantees <see cref=\"Ssse3.IsSupported\"/>.", "")]
    [Arguments("throw new PlatformNotSupportedException(nameof(IsBitPlaneTierSupported));", "")]
    public async Task FindIsaReads_NamesCodeReadsAndSkipsComments(string source, string expected)
    {
        var reads = string.Join(",", FindIsaReads(source).Select(r => r.Read));

        await Assert.That(reads).IsEqualTo(expected);
    }

    [Test]
    public async Task FindIsaReads_SkipsBlockComments()
    {
        var source = "/* if (Avx2.IsSupported) */ var a = 1;\n/*\n Vector128.IsHardwareAccelerated\n*/\nif (Sse2.IsSupported) { }";

        await Assert.That(FindIsaReads(source).ToArray()).IsEquivalentTo(new[] { (5, "Sse2") }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task MethodLines_SpanTheDeclarationAndItsBody()
    {
        // Other has an expression body and a block-bodied member after it, so its declaration is passed over rather than read through
        // to Next's braces.
        var source = "class C\n{\n    // void Run() in a comment\n    internal static void Run() => Run(0);\n    private void Run(int a)\n    {\n        if (a > 0) { a--; }\n    }\n\n    internal static int Other() => 1;\n    private void Next()\n    {\n    }\n}";

        await Assert.That(MethodLines(source, "Run")).IsEqualTo((5, 8));
        await Assert.That(MethodLines(source, "Other")).IsNull();
        await Assert.That(MethodLines(source, "Next")).IsEqualTo((11, 13));
        await Assert.That(MethodLines(source, "Missing")).IsNull();
    }

    /// <summary>The flag reads of a <see cref="KernelFiles"/> entry: the whole file, or the lines of its dispatch method.</summary>
    private static IEnumerable<(int Line, string Read)> ReadsIn(string root, string spec)
    {
        var (file, method) = ParseSpec(spec);
        var source = File.ReadAllText(Path.Combine(root, file));
        var (start, end) = method is null ? (0, int.MaxValue) : MethodLines(source, method) ?? throw new InvalidOperationException($"{spec}: no such method");
        return FindIsaReads(source).Where(r => r.Line >= start && r.Line <= end);
    }

    private static (string File, string? Method) ParseSpec(string spec)
    {
        var hash = spec.IndexOf('#');
        return hash < 0 ? (spec, null) : (spec[..hash], spec[(hash + 1)..]);
    }

    /// <summary>
    /// The 1-based first and last line of the first member that declares <paramref name="method"/> on a line without <c>=&gt;</c>, through
    /// the brace that closes its body, or null. Braces are counted as written, so a dispatch method named here has none in a string or
    /// char literal.
    /// </summary>
    private static (int Start, int End)? MethodLines(string source, string method)
    {
        var lines = StripComments(source).Split('\n');
        var declaration = new Regex($@"^\s*(?:(?:private|internal|public|protected|static|unsafe)\s+)+[\w<>\[\],? ]+\s{Regex.Escape(method)}\s*\(", RegexOptions.CultureInvariant);
        var start = Array.FindIndex(lines, l => declaration.IsMatch(l) && !l.Contains("=>", StringComparison.Ordinal));
        if (start < 0)
            return null;
        var depth = 0;
        var opened = false;
        for (var i = start; i < lines.Length; i++)
        {
            foreach (var c in lines[i])
            {
                if (c == '{')
                {
                    depth++;
                    opened = true;
                }
                else if (c == '}')
                {
                    depth--;
                }
            }
            if (opened && depth == 0)
                return (start + 1, i + 1);
        }
        return null;
    }

    /// <summary>1-based line and flag of every instruction-set read outside a comment: <c>Avx2</c>, <c>Gfni.V256</c>, <c>AdvSimd.Arm64</c>, <c>Vector128</c>, <c>HasFastPext</c>.</summary>
    private static IEnumerable<(int Line, string Read)> FindIsaReads(string source)
    {
        var code = StripComments(source).Split('\n');
        for (var i = 0; i < code.Length; i++)
        {
            foreach (Match match in IsaRead.Matches(code[i]))
                yield return (i + 1, match.Groups["isa"].Success ? match.Groups["isa"].Value : match.Groups["vector"].Success ? match.Groups["vector"].Value : "HasFastPext");
        }
    }

    private static readonly Regex IsaRead = new(
        @"\b(?:System\.Runtime\.Intrinsics\.(?:X86|Arm)\.)?(?<isa>[A-Z]\w*(?:\.(?:V128|V256|V512|X64|Arm64))?)\.IsSupported\b"
        + @"|\b(?<vector>Vector(?:64|128|256|512))\.IsHardwareAccelerated\b"
        + @"|\bHasFastPext\b",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    /// <summary>The source with line and block comments blanked, line breaks kept so line numbers still match; string literals are not parsed, which no flag name appears in.</summary>
    private static string StripComments(string source)
    {
        var chars = source.ToCharArray();
        var i = 0;
        while (i < chars.Length - 1)
        {
            if (chars[i] == '/' && chars[i + 1] == '/')
            {
                while (i < chars.Length && chars[i] != '\n')
                    chars[i++] = ' ';
            }
            else if (chars[i] == '/' && chars[i + 1] == '*')
            {
                chars[i++] = ' ';
                chars[i++] = ' ';
                while (i < chars.Length && !(chars[i] == '*' && i + 1 < chars.Length && chars[i + 1] == '/'))
                {
                    if (chars[i] != '\n')
                        chars[i] = ' ';
                    i++;
                }
                if (i < chars.Length)
                {
                    chars[i++] = ' ';
                    chars[i++] = ' ';
                }
            }
            else
            {
                i++;
            }
        }
        return new string(chars);
    }

    private static string InternalsRoot() => Path.Combine(RepositoryRoot(), "src", "FeatherQR", "Internals");

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root (Directory.Build.props) not found above " + AppContext.BaseDirectory);
    }
}
