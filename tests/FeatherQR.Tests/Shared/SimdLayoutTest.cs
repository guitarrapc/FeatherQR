using System.Text.RegularExpressions;

namespace FeatherQR.Tests;

/// <summary>
/// Where SIMD code lives: a file's name says which instruction families it may use, so what a kernel
/// implements for which platform reads off the file list.
/// </summary>
/// <remarks>
/// <para>
/// <c>{stem}.X86.cs</c> holds x86 intrinsics (with the portable vectors they work on), <c>{stem}.Arm64.cs</c>
/// ARM intrinsics (with 128-bit vectors), <c>{stem}.Vector256.cs</c> and <c>{stem}.Vector128.cs</c> portable
/// vectors of that width or narrower, where <c>{stem}</c> is the type, or the type and a feature
/// (<c>ModulePlacer.Masking</c>). Every other file, the stem file among them, holds the entry, the dispatch
/// and the scalar tier, and uses no vector instruction at all.
/// </para>
/// <para>
/// Reading an instruction-set flag (<c>IsSupported</c>, <c>IsHardwareAccelerated</c>) or a vector's lane
/// count is not using the instruction set: the dispatch reads flags where it branches, which the JIT needs
/// (see the remarks on <c>SimdTiers</c>).
/// </para>
/// </remarks>
public class SimdLayoutTest
{
    /// <summary>
    /// Files whose code still mixes families inside a method, paths under src/FeatherQR/Internals. Laying one
    /// out removes it here; the list is empty when every file follows the rule.
    /// </summary>
    private static readonly string[] NotYetLaidOut =
    [
        "ModuleBitPacker.cs",
        "ImageDecoders/FinderPatternFinder.cs",
        "ImageDecoders/FinderPatternFinder.RowEdges.cs",
        "ImageDecoders/LuminanceInverter.cs",
        "MicroQR/MicroQRBinaryEncoder.cs",
        "MicroQR/MicroQRModulePlacer.PlaceSymbol.cs",
        "RmQR/RmQRBinaryEncoder.cs",
        "RmQR/RmQRModulePlacer.cs",
        "StandardQR/AlignmentPatternFinder.cs",
        "StandardQR/ModulePlacer.ExpandBits.cs",
    ];

    [Test]
    public async Task EveryFile_UsesOnlyTheFamiliesItsNameAllows()
    {
        var violations = SourceFiles()
            .Where(f => !NotYetLaidOut.Contains(f.Relative))
            .SelectMany(f => Violations(f.Relative, File.ReadAllText(f.Path)))
            .ToArray();

        await Assert.That(violations).IsEmpty().Because(string.Join("; ", violations));
    }

    [Test]
    public async Task NotYetLaidOut_ListsOnlyFilesThatStillBreakTheRule()
    {
        var files = SourceFiles().ToDictionary(f => f.Relative, f => f.Path);
        foreach (var relative in NotYetLaidOut)
        {
            await Assert.That(files.ContainsKey(relative)).IsTrue().Because($"{relative} exists");
            await Assert.That(Violations(relative, File.ReadAllText(files[relative])).Any()).IsTrue()
                .Because($"{relative} follows the rule now; take it off {nameof(NotYetLaidOut)}");
        }
    }

    [Test]
    [Arguments("Avx2.Shuffle(v, sel)", "X86")]
    [Arguments("System.Runtime.Intrinsics.X86.Sse2.PackUnsignedSaturate(a, b)", "X86")]
    [Arguments("Sse2.X64.ConvertToInt64(v)", "X86")]
    [Arguments("X86Base.CpuId(0, 0)", "X86")]
    [Arguments("Gfni.V256.GaloisFieldAffineTransform(x, m, 0)", "X86")]
    [Arguments("AdvSimd.Arm64.AddPairwise(a, b)", "Arm")]
    [Arguments("Dp.DotProduct(acc, a, b)", "Arm")]
    [Arguments("Vector256.Create((byte)1)", "Vector256")]
    [Arguments("Vector256<byte> v = default;", "Vector256")]
    [Arguments("var m = Vector128<byte>.Zero;", "Vector128")]
    [Arguments("Vector64.Create((byte)1)", "Vector128")]
    [Arguments("if (Avx2.IsSupported && AdvSimd.Arm64.IsSupported)", "")]
    [Arguments("if (Vector256.IsHardwareAccelerated && n >= Vector256<byte>.Count)", "")]
    [Arguments("// Avx2.Shuffle(v, sel) in a comment", "")]
    [Arguments("var s = \"Vector128.Create\";", "")]
    [Arguments("using System.Runtime.Intrinsics.X86;", "")]
    public async Task Families_AreTheInstructionsUsed_NotTheFlagsRead(string source, string expected)
    {
        await Assert.That(string.Join(",", Families(source))).IsEqualTo(expected);
    }

    [Test]
    [Arguments("TextAnalyzer.cs", "if (Avx2.IsSupported) return AnalyzeAvx2(text);", true)]
    [Arguments("TextAnalyzer.cs", "var v = Vector128.Create((byte)1);", false)]
    [Arguments("TextAnalyzer.X86.cs", "var v = Avx2.Shuffle(a, b); var w = Vector128.Create((byte)1);", true)]
    [Arguments("TextAnalyzer.X86.cs", "var v = AdvSimd.Arm64.AddPairwise(a, b);", false)]
    [Arguments("TextAnalyzer.X86.cs", "var v = Vector128.Create((byte)1);", false)]
    [Arguments("TextAnalyzer.Arm64.cs", "var v = AdvSimd.Arm64.AddPairwise(a, b); var w = Vector128.Create((byte)1);", true)]
    [Arguments("TextAnalyzer.Arm64.cs", "var v = Vector256.Create((byte)1); var w = AdvSimd.Add(a, b);", false)]
    [Arguments("Binarizer.Vector256.cs", "var v = Vector256.Create((byte)1); var w = Vector128.Create((byte)1);", true)]
    [Arguments("Binarizer.Vector256.cs", "var v = Avx2.Shuffle(a, b); var w = Vector256.Create((byte)1);", false)]
    [Arguments("LocalBinarizer.Vector128.cs", "var v = Vector128.Create((byte)1); var w = Vector64.Create((byte)1);", true)]
    [Arguments("LocalBinarizer.Vector128.cs", "var v = Vector256.Create((byte)1);", false)]
    public async Task Violations_FollowTheFileName(string file, string source, bool follows)
    {
        await Assert.That(!Violations(file, source).Any()).IsEqualTo(follows);
    }

    /// <summary>What the file breaks: a family its name does not allow, or, for a family file, none of its own family.</summary>
    private static IEnumerable<string> Violations(string relative, string source)
    {
        var name = Path.GetFileNameWithoutExtension(relative);
        var suffix = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : "";
        (string[] Allowed, string? Own) rule = suffix switch
        {
            "X86" => (["X86", "Vector256", "Vector128"], "X86"),
            "Arm64" => (["Arm", "Vector128"], "Arm"),
            "Vector256" => (["Vector256", "Vector128"], "Vector256"),
            "Vector128" => (["Vector128"], "Vector128"),
            _ => ([], null),
        };
        var families = Families(source).ToArray();
        foreach (var family in families.Where(f => !rule.Allowed.Contains(f)))
            yield return $"{relative} uses {family}";
        if (rule.Own is { } own && !families.Contains(own))
            yield return $"{relative} is named for {own} and uses none";
    }

    /// <summary>The instruction families the source uses, in a fixed order: X86, Arm, Vector256, Vector128 (which covers Vector64).</summary>
    private static IEnumerable<string> Families(string source)
    {
        var code = LaneCount.Replace(StripCommentsAndStrings(source), "");
        if (X86Use.IsMatch(code))
            yield return "X86";
        if (ArmUse.IsMatch(code))
            yield return "Arm";
        if (Vector256Use.IsMatch(code))
            yield return "Vector256";
        if (Vector128Use.IsMatch(code))
            yield return "Vector128";
    }

    private static readonly Regex X86Use = new(@"\b(?:Avx\w*|Sse\w*|Ssse3|Bmi[12]|Lzcnt|Popcnt|Gfni|Pclmulqdq|X86Base|X86Serialize|Fma)(?:\.(?:X64|V128|V256|V512))?\.(?!IsSupported\b)[A-Z]\w*\s*[(<]", RegexOptions.CultureInvariant);
    private static readonly Regex ArmUse = new(@"\b(?:AdvSimd|Dp|ArmBase|Crc32|Rdm|Sha1|Sha256|Sve\w*)(?:\.Arm64)?\.(?!IsSupported\b)[A-Z]\w*\s*[(<]", RegexOptions.CultureInvariant);
    private static readonly Regex Vector256Use = new(@"\bVector(?:256|512)(?:<[^>]*>)?\.(?!IsHardwareAccelerated\b)[A-Z]\w*|\bVector(?:256|512)<", RegexOptions.CultureInvariant);
    private static readonly Regex Vector128Use = new(@"\bVector(?:128|64)(?:<[^>]*>)?\.(?!IsHardwareAccelerated\b)[A-Z]\w*|\bVector(?:128|64)<", RegexOptions.CultureInvariant);
    private static readonly Regex LaneCount = new(@"\bVector(?:64|128|256|512)<\w+>\.Count\b", RegexOptions.CultureInvariant);

    /// <summary>The source with comments and string literals blanked; line breaks are kept.</summary>
    private static string StripCommentsAndStrings(string source)
    {
        var chars = source.ToCharArray();
        var i = 0;
        while (i < chars.Length)
        {
            if (chars[i] == '/' && i + 1 < chars.Length && chars[i + 1] == '/')
            {
                while (i < chars.Length && chars[i] != '\n')
                    chars[i++] = ' ';
            }
            else if (chars[i] == '/' && i + 1 < chars.Length && chars[i + 1] == '*')
            {
                while (i < chars.Length && !(chars[i] == '*' && i + 1 < chars.Length && chars[i + 1] == '/'))
                {
                    if (chars[i] != '\n')
                        chars[i] = ' ';
                    i++;
                }
                for (var k = 0; k < 2 && i < chars.Length; k++)
                    chars[i++] = ' ';
            }
            else if (chars[i] == '"')
            {
                chars[i++] = ' ';
                while (i < chars.Length && chars[i] != '"' && chars[i] != '\n')
                {
                    if (chars[i] == '\\' && i + 1 < chars.Length)
                        chars[i++] = ' ';
                    chars[i++] = ' ';
                }
                if (i < chars.Length && chars[i] == '"')
                    chars[i++] = ' ';
            }
            else
            {
                i++;
            }
        }
        return new string(chars);
    }

    private static IEnumerable<(string Relative, string Path)> SourceFiles()
    {
        var root = Path.Combine(RepositoryRoot(), "src", "FeatherQR");
        var internals = Path.Combine(root, "Internals");
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var fromRoot = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (fromRoot.StartsWith("obj/", StringComparison.Ordinal) || fromRoot.StartsWith("bin/", StringComparison.Ordinal))
                continue;
            yield return (Path.GetRelativePath(internals, file).Replace('\\', '/'), file);
        }
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root (Directory.Build.props) not found above " + AppContext.BaseDirectory);
    }
}
