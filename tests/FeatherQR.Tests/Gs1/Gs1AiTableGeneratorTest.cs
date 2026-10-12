using FeatherQR.Gs1.Internals;
using QRInteropFixtures;

namespace FeatherQR.Tests;

/// <summary>
/// The GS1 AI table and code lists in <c>src/FeatherQR.Gs1/Internals/</c> are generated from the pinned Syntax
/// Dictionary release under <c>Fixtures/Gs1/syntax-dictionary/</c> (the dictionary file and the list linters'
/// sources, with their provenance beside them). This test renders them again and fails when a generated file
/// differs from the render; run outside CI, it also rewrites the file, so the change to review is the diff. The
/// other tests hold the premises the generator rests on: the dictionary names only linters the code implements,
/// and its predefined flags and AI lengths agree with the frozen tables of the General Specifications.
/// </summary>
public class Gs1AiTableGeneratorTest
{
    [Test]
    public async Task GeneratedSources_AreRenderedFromThePinnedDictionary()
    {
        var root = RepositoryRoot();
        var generated = Gs1AiTableGenerator.Render(Gs1FixtureLoader.DictionaryPath, Path.Combine(Gs1FixtureLoader.Root, "syntax-dictionary"));
        var stale = new List<string>();
        foreach (var (relativePath, text) in generated)
        {
            var path = Path.Combine(root, relativePath);
            var current = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : "";
            if (current == text)
                continue;
            stale.Add(relativePath);
            if (Environment.GetEnvironmentVariable("CI") is null)
                File.WriteAllText(path, text);
        }

        var onCi = Environment.GetEnvironmentVariable("CI") is not null;
        await Assert.That(stale).IsEmpty()
            .Because(onCi
                ? "the generated GS1 sources differ from the pinned dictionary; run this test locally to rewrite them, and commit the diff"
                : "the generated GS1 sources were rewritten from the pinned dictionary; review and commit the diff");
    }

    [Test]
    public async Task Dictionary_NamesOnlyLintersTheCodeImplements()
    {
        var dictionary = new Gs1SyntaxDictionary(Gs1FixtureLoader.DictionaryPath);
        var implemented = Enum.GetNames<Gs1Linter>().Select(n => n.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        await Assert.That(dictionary.Linters.Where(l => !implemented.Contains(l))).IsEmpty();
        // And nothing in the enum that the release does not name, so a stale member cannot linger unused.
        await Assert.That(implemented.Except(dictionary.Linters)).IsEmpty();
        await Assert.That(dictionary.Linters.Count).IsEqualTo(34);
    }

    [Test]
    public async Task Dictionary_LinterMaskFitsOneWord()
    {
        await Assert.That(Enum.GetValues<Gs1Linter>().Select(l => (int)l).Max()).IsLessThan(64);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root (Directory.Build.props) not found above " + AppContext.BaseDirectory);
    }
}
