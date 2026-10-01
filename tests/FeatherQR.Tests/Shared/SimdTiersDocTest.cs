using System.Text;
using FeatherQR.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// .github/docs/specs/qrcode-simd-tiers.md shows <see cref="SimdTiers"/> as tables, rendered here into
/// the file's generated regions. A region that differs from the code fails the test; run outside CI, the
/// test also rewrites it, so the change to review is the file's diff.
/// </summary>
public class SimdTiersDocTest
{
    private const string DocPath = ".github/docs/specs/qrcode-simd-tiers.md";

    /// <summary>Each generated region of the document and what renders it.</summary>
    private static readonly (string Name, Func<string> Render)[] Regions =
    [
        ("build-classes", RenderBuildClasses),
        ("kernels", RenderKernels),
    ];

    [Test]
    public async Task Regions_AreRenderedFromTheTable()
    {
        var path = Path.Combine(RepositoryRoot(), DocPath);
        var text = File.ReadAllText(path);
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var stale = new List<string>();
        foreach (var (name, render) in Regions)
        {
            var begin = $"<!-- BEGIN GENERATED {name}: SimdTiersDocTest renders it from SimdTiers.cs -->";
            var end = $"<!-- END GENERATED {name} -->";
            var start = text.IndexOf(begin, StringComparison.Ordinal);
            var stop = text.IndexOf(end, StringComparison.Ordinal);
            await Assert.That(start >= 0 && stop > start).IsTrue().Because($"{DocPath} has the markers of region {name}");

            start = text.IndexOf('\n', start) + 1;
            var expected = render();
            if (text[start..stop].Replace("\r\n", "\n") == expected)
                continue;
            stale.Add(name);
            text = text[..start] + expected.Replace("\n", newline) + text[stop..];
        }

        // One write for every region, so a rewrite cannot undo another.
        var onCi = Environment.GetEnvironmentVariable("CI") is not null;
        if (stale.Count > 0 && !onCi)
            File.WriteAllText(path, text);

        await Assert.That(stale).IsEmpty()
            .Because(onCi
                ? $"{DocPath} is SimdTiers.cs rendered; run this test locally to rewrite it, and commit the diff"
                : $"{DocPath} was rewritten from SimdTiers.cs; review and commit the diff");
    }

    [Test]
    public async Task Key_NamesEveryTier()
    {
        var lines = File.ReadAllLines(Path.Combine(RepositoryRoot(), DocPath));
        foreach (var tier in Enum.GetValues<SimdTier>())
            await Assert.That(lines.Count(l => l.StartsWith($"| {Code(tier)} |", StringComparison.Ordinal))).IsEqualTo(1).Because($"one key row for {tier}");
    }

    private static string RenderBuildClasses()
    {
        var sb = new StringBuilder();
        sb.Append("| Build class | Always available | Depends on the CPU |\n");
        sb.Append("|---|---|---|\n");
        foreach (var buildClass in Enum.GetValues<SimdBuildClass>())
        {
            var (present, _, leftToCpu) = SimdTiers.Definition(buildClass);
            var left = leftToCpu.Length == 0 ? "None" : string.Join(", ", leftToCpu.Select(group => string.Join(" + ", group.Select(Code))));
            sb.Append($"| {Name(buildClass)} | {string.Join(", ", present.Select(Code))} | {left} |\n");
        }
        return sb.ToString();
    }

    private static string RenderKernels()
    {
        var classes = Enum.GetValues<SimdBuildClass>();
        var kernels = SimdTiers.Report().ToDictionary(k => k.Name);
        var sb = new StringBuilder();
        sb.Append("| Kernel | Tiers, in dispatch order |");
        foreach (var buildClass in classes)
            sb.Append($" {Name(buildClass)} |");
        sb.Append("\n|---|---|");
        foreach (var _ in classes)
            sb.Append("---|");
        sb.Append('\n');
        foreach (var row in SimdTiers.Expected())
        {
            sb.Append($"| `{row.Kernel}` | {string.Join(", ", kernels[row.Kernel].Tiers.Select(t => Code(t.Tier)))} |");
            foreach (var buildClass in classes)
                sb.Append($" {string.Join(" or ", row.For(buildClass).Select(Code))} |");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string Code(SimdTier tier) => $"`{tier}`";

    private static string Name(SimdBuildClass buildClass) => buildClass switch
    {
        SimdBuildClass.X64Sse => "x64 without AVX",
        SimdBuildClass.X64Avx2 => "x64 with AVX2",
        SimdBuildClass.Arm64 => "ARM64",
        SimdBuildClass.Wasm => "WebAssembly",
        _ => throw new ArgumentOutOfRangeException(nameof(buildClass), buildClass, "Name the new build class here and in the document's build table."),
    };

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root (Directory.Build.props) not found above " + AppContext.BaseDirectory);
    }
}
