using System.Text.Json;
using System.Text.Json.Serialization;

namespace FeatherQR.Tests;

/// <summary>
/// The GS1 data fixtures under Fixtures/Gs1/, produced by <c>tools/QRInteropFixtures -- regenerate-gs1</c>:
/// each case is an input (bracketed text, an element string, a Digital Link URI or scan data) with the verdict
/// of every pinned oracle beside it. Schema in .github/docs/specs/qrcode-test-fixtures.md. The records mirror
/// the tool's, so the two must change together.
/// </summary>
public static class Gs1FixtureLoader
{
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static string Root => Path.Combine(FixtureLoader.FixtureRoot, "Gs1");

    /// <summary>The case files: <c>corpus/*.json</c> and <c>syntax-engine/*.json</c>, as "dir/name" without the extension.</summary>
    public static IEnumerable<string> EnumerateFileIds()
    {
        foreach (var dir in new[] { "corpus", "syntax-engine" })
        {
            var path = Path.Combine(Root, dir);
            if (!Directory.Exists(path))
                continue;
            foreach (var json in Directory.EnumerateFiles(path, "*.json").OrderBy(x => x, StringComparer.Ordinal))
                yield return dir + "/" + Path.GetFileNameWithoutExtension(json);
        }
    }

    public static Gs1FixtureFile LoadFile(string fileId)
    {
        var path = Path.Combine(Root, fileId.Replace('/', Path.DirectorySeparatorChar) + ".json");
        return JsonSerializer.Deserialize<Gs1FixtureFile>(File.ReadAllText(path), jsonOptions)
            ?? throw new InvalidDataException($"GS1 fixture file deserialized to null: {path}");
    }

    public static IEnumerable<Gs1FixtureCase> EnumerateCases() => EnumerateFileIds().SelectMany(id => LoadFile(id).Cases);

    /// <summary>The linter vector files under <c>syntax-dictionary/linters/</c>, by linter name.</summary>
    public static IEnumerable<string> EnumerateLinters()
    {
        var path = Path.Combine(Root, "syntax-dictionary", "linters");
        if (!Directory.Exists(path))
            yield break;
        foreach (var json in Directory.EnumerateFiles(path, "*.json").OrderBy(x => x, StringComparer.Ordinal))
            yield return Path.GetFileNameWithoutExtension(json);
    }

    public static Gs1LinterVectorFile LoadLinter(string linter)
    {
        var path = Path.Combine(Root, "syntax-dictionary", "linters", linter + ".json");
        return JsonSerializer.Deserialize<Gs1LinterVectorFile>(File.ReadAllText(path), jsonOptions)
            ?? throw new InvalidDataException($"GS1 linter vector file deserialized to null: {path}");
    }

    public static string DictionaryPath => Path.Combine(Root, "syntax-dictionary", "gs1-syntax-dictionary.txt");
}

public enum Gs1InputKind
{
    Bracketed,
    ElementString,
    DigitalLink,
    ScanData,
}

public sealed record Gs1FixtureFile
{
    public required Gs1OracleVersions Oracles { get; init; }
    public required string Source { get; init; }
    public required Gs1FixtureCase[] Cases { get; init; }
}

public sealed record Gs1OracleVersions
{
    public required Gs1OracleVersion SyntaxEngine { get; init; }
    public required Gs1OracleVersion CodeGlyphX { get; init; }
    public required Gs1OracleVersion Libzint { get; init; }
    public required Gs1OracleVersion ZxingCpp { get; init; }
}

public sealed record Gs1OracleVersion(string Version, string Dictionary);

public sealed record Gs1FixtureCase
{
    public required string Id { get; init; }
    public required string Group { get; init; }
    public required string Source { get; init; }
    public required Gs1InputKind InputKind { get; init; }
    public required string Input { get; init; }
    public string? Note { get; init; }
    public Gs1ExpectedVerdict? Expected { get; init; }
    public required Gs1SyntaxEngineVerdict SyntaxEngine { get; init; }
    public Gs1CodeGlyphXVerdict? CodeGlyphX { get; init; }
    public Gs1LibzintVerdict? Libzint { get; init; }
    public Gs1ReaderVerdict? Reader { get; init; }
}

public sealed record Gs1ExpectedVerdict(bool Valid, string? Output);

public sealed record Gs1SyntaxEngineVerdict
{
    public required bool Valid { get; init; }
    public string? DataString { get; init; }
    public string? ElementString { get; init; }
    public string? Bracketed { get; init; }
    public string[]? Hri { get; init; }
    public string? DigitalLink { get; init; }
    public string? DigitalLinkError { get; init; }
    public string? Error { get; init; }
    public string? ErrorMarkup { get; init; }
}

public sealed record Gs1CodeGlyphXVerdict
{
    public required bool Valid { get; init; }
    public string? ElementString { get; init; }
    public string? Hri { get; init; }
    public string? DigitalLink { get; init; }
    public string? DigitalLinkError { get; init; }
    public required string[] Issues { get; init; }
}

public sealed record Gs1LibzintVerdict
{
    public required bool Valid { get; init; }
    public string? Error { get; init; }
    public string? Warning { get; init; }
}

public sealed record Gs1ReaderVerdict
{
    public required string Writer { get; init; }
    public required string SymbologyIdentifier { get; init; }
    public required string ElementString { get; init; }
    public required string Hri { get; init; }
}

public sealed record Gs1LinterVector
{
    public required string Value { get; init; }
    public required bool Pass { get; init; }
    public string? Error { get; init; }
    public string? Markup { get; init; }
}

public sealed record Gs1LinterVectorFile
{
    public required string Linter { get; init; }
    public required string Source { get; init; }
    public required Gs1LinterVector[] Cases { get; init; }
}
