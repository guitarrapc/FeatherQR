using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QRInteropFixtures;

/// <summary>The form of a GS1 fixture's input, which decides which oracles can take it.</summary>
public enum Gs1InputKind
{
    /// <summary><c>(01)09521234567891(10)ABC</c>: the tool convention, with <c>\(</c> for a literal parenthesis.</summary>
    Bracketed,
    /// <summary>The element string as a symbol carries it, separators as U+001D and no leading FNC1.</summary>
    ElementString,
    /// <summary>A GS1 Digital Link URI.</summary>
    DigitalLink,
    /// <summary>Scanner transmission data starting with an AIM symbology identifier (<c>]Q3…</c>, <c>]Q1…</c>).</summary>
    ScanData,
}

/// <summary>One GS1 fixture file: the oracle versions it was made with and its cases. Schema in .github/docs/specs/qrcode-test-fixtures.md.</summary>
public sealed record Gs1FixtureFile
{
    public required Gs1OracleVersions Oracles { get; init; }
    public required string Source { get; init; }
    public required Gs1FixtureCase[] Cases { get; init; }
}

/// <summary>Each oracle's version and the Syntax Dictionary release its verdicts come from.</summary>
public sealed record Gs1OracleVersions
{
    public required Gs1OracleVersion SyntaxEngine { get; init; }
    public required Gs1OracleVersion CodeGlyphX { get; init; }
    public required Gs1OracleVersion Libzint { get; init; }
    public required Gs1OracleVersion ZxingCpp { get; init; }
}

public sealed record Gs1OracleVersion(string Version, string Dictionary);

/// <summary>One GS1 data case: the input, where it came from, what its source expects of it (imported vectors only), and each oracle's verdict.</summary>
public sealed record Gs1FixtureCase
{
    public required string Id { get; init; }
    public required string Group { get; init; }
    public required string Source { get; init; }
    public required Gs1InputKind InputKind { get; init; }
    public required string Input { get; init; }
    public string? Note { get; init; }
    /// <summary>The expectation the vector's own source states, for vectors imported from the Syntax Engine's unit tests. Null for this repository's corpus.</summary>
    public Gs1ExpectedVerdict? Expected { get; init; }
    public required Gs1SyntaxEngineVerdict SyntaxEngine { get; init; }
    public Gs1CodeGlyphXVerdict? CodeGlyphX { get; init; }
    public Gs1LibzintVerdict? Libzint { get; init; }
    public Gs1ReaderVerdict? Reader { get; init; }
}

/// <summary>What the imported vector's source asserts: whether the input is accepted and, where it states one, the output in the source's own notation (<c>^</c> for FNC1 and the separator).</summary>
public sealed record Gs1ExpectedVerdict(bool Valid, string? Output);

/// <summary>The Syntax Engine console app's reading of the input, with the pinned dictionary loaded through <c>--syndict</c>.</summary>
public sealed record Gs1SyntaxEngineVerdict
{
    public required bool Valid { get; init; }
    /// <summary>The engine's barcode message as it shows it: AI data in its <c>^</c> notation, a Digital Link URI, or plain text (with a literal <c>^</c> escaped as <c>\^</c>).</summary>
    public string? DataString { get; init; }
    /// <summary>The element string with separators as U+001D and no leading FNC1, null when the input is not AI data. For a URI it is the engine's own bracketed reading run back through the engine.</summary>
    public string? ElementString { get; init; }
    /// <summary>The engine's bracketed AI element string.</summary>
    public string? Bracketed { get; init; }
    /// <summary>The engine's HRI lines, in its own style (<c>(01) 09521234567891</c>).</summary>
    public string[]? Hri { get; init; }
    /// <summary>The canonical Digital Link URI the engine generates for the data, or null with <see cref="DigitalLinkError"/> set.</summary>
    public string? DigitalLink { get; init; }
    public string? DigitalLinkError { get; init; }
    public string? Error { get; init; }
    /// <summary>The engine's error markup: the input with the offending part between <c>|</c>.</summary>
    public string? ErrorMarkup { get; init; }
}

/// <summary>CodeGlyphX's verdict (its validator for AI data, its Digital Link parser for a URI).</summary>
public sealed record Gs1CodeGlyphXVerdict
{
    public required bool Valid { get; init; }
    public string? ElementString { get; init; }
    /// <summary>The parsed elements as <c>(AI)value</c> text, concatenated.</summary>
    public string? Hri { get; init; }
    public string? DigitalLink { get; init; }
    public string? DigitalLinkError { get; init; }
    public required string[] Issues { get; init; }
}

/// <summary>
/// libzint's verdict on bracketed input through its <c>gs1</c> option (it refuses any other form). It refuses an
/// unknown or malformed AI, a wrong length, an empty field and a control character, and writes the symbol for every
/// other defect its linters find, reporting a non-compliance warning where the wrapper passes one on.
/// </summary>
public sealed record Gs1LibzintVerdict
{
    public required bool Valid { get; init; }
    public string? Error { get; init; }
    public string? Warning { get; init; }
}

/// <summary>The pinned zxing-cpp reader over a symbol holding the data: the element string (<c>TextMode.Plain</c>) and its <c>(AI)</c> text (<c>TextMode.HRI</c>).</summary>
public sealed record Gs1ReaderVerdict
{
    /// <summary>Who wrote the symbol: <c>zint-libzint</c> from the bracketed input, or <c>zxing-net</c> from the element string.</summary>
    public required string Writer { get; init; }
    public required string SymbologyIdentifier { get; init; }
    public required string ElementString { get; init; }
    public required string Hri { get; init; }
}

/// <summary>One linter unit-test vector imported from the Syntax Dictionary's reference linter source.</summary>
public sealed record Gs1LinterVector
{
    public required string Value { get; init; }
    public required bool Pass { get; init; }
    public string? Error { get; init; }
    /// <summary>The source's error markup: the value with the offending part between <c>*</c>.</summary>
    public string? Markup { get; init; }
}

public sealed record Gs1LinterVectorFile
{
    public required string Linter { get; init; }
    public required string Source { get; init; }
    public required Gs1LinterVector[] Cases { get; init; }
}

public static class Gs1FixtureWriter
{
    public static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions compact = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Write(string path, Gs1FixtureFile file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(file, Indented) + "\n");
    }

    /// <summary>Linter vector files hold thousands of short cases, so each case is one line.</summary>
    public static void Write(string path, Gs1LinterVectorFile file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = file.Cases.Select(c => "    " + JsonSerializer.Serialize(c, compact));
        var text = "{\n  \"linter\": " + JsonSerializer.Serialize(file.Linter, compact) + ",\n  \"source\": " + JsonSerializer.Serialize(file.Source, compact) + ",\n  \"cases\": [\n" + string.Join(",\n", lines) + "\n  ]\n}\n";
        File.WriteAllText(path, text);
    }
}
