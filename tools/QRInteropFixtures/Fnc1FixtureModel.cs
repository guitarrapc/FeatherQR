using System.Text;
using System.Text.Json.Serialization;

namespace QRInteropFixtures;

/// <summary>Which FNC1 mode indicator a symbol carries (ISO/IEC 18004 6.4.8, ISO/IEC 23941 7.4.8).</summary>
public enum Fnc1Position
{
    /// <summary>FNC1 in first position: GS1 data.</summary>
    First,
    /// <summary>FNC1 in second position: data in an industry format registered with AIM, with an 8-bit application indicator.</summary>
    Second,
}

/// <summary>
/// Input definition of one FNC1 corpus case. Each writer has its own case list, because the
/// writers take different input forms (libzint and CodeGlyphX's rMQR encoder take bracketed GS1
/// AI syntax, the others take the data itself with U+001D separators) and write different modes
/// (of these writers only CodeGlyphX writes second position, and none writes rMQR second position).
/// </summary>
/// <param name="Id">Case name, and the file stem of the three fixture files.</param>
/// <param name="Input">The text handed to the writer, in the writer's own input form.</param>
/// <param name="ErrorCorrectionLevel">"L", "M", "Q" or "H" (rMQR: "M" or "H"), requested and honored by the writer.</param>
/// <param name="SymbolType"><see cref="StandardQR"/> or <see cref="RmQR"/>, as the manifest spells them.</param>
/// <param name="Position">First position (GS1) or second position (an AIM application indicator).</param>
/// <param name="ApplicationIndicator">Second position only: the 8-bit codeword as on the wire, 0-99 as its value or a letter's ASCII value plus 100.</param>
/// <param name="ExpectedText">
/// The data the symbol means, an element string in first position, with U+001D separators and without any indicator: what the
/// reader must return, and what this library's decoder has to reproduce. The sanity gate checks the
/// zxing-cpp reading against it. Where a writer defect makes the symbol mean something other than its
/// input (see the fixture spec), this is the standard's reading of the symbol as written, and the
/// manifest records the input beside it.
/// </param>
/// <param name="Utf8">Ask the writer for an ECI 26 header over the whole text. Recorded as <c>eciCharset</c>.</param>
/// <param name="RmQRVersion">rMQR only: the ISO/IEC 23941 version index + 1 (1 = R7x43 ... 32 = R17x139), pinned on the writer.</param>
public sealed record Fnc1CaseDefinition(
    string Id,
    string Input,
    string ErrorCorrectionLevel,
    string SymbolType = Fnc1CaseDefinition.StandardQR,
    Fnc1Position Position = Fnc1Position.First,
    int ApplicationIndicator = -1,
    string ExpectedText = "",
    bool Utf8 = false,
    int RmQRVersion = 0)
{
    public const string StandardQR = "StandardQR";
    public const string RmQR = "rMQR";

    public bool IsRmQR => SymbolType == RmQR;

    /// <summary>The fixture root under Fixtures/ this case is written to.</summary>
    public string FixtureRoot => IsRmQR ? "RmQrFnc1" : "StandardQrFnc1";
}

/// <summary>
/// The FNC1 mode of a fixture as the manifest records it (<c>fnc1</c>, see the schema in
/// .github/docs/specs/qrcode-test-fixtures.md). The loader in the test project mirrors it.
/// </summary>
public sealed record Fnc1Manifest
{
    /// <summary>"First" (GS1) or "Second" (an AIM application indicator follows).</summary>
    public required string Position { get; init; }

    /// <summary>Second position only: the 8-bit codeword as on the wire (0-99, or the letter's ASCII value plus 100). Omitted in first position.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ApplicationIndicator { get; init; }

    /// <summary>The AIM symbology identifier the zxing-cpp reader reports: <c>]Q3</c> in first position, <c>]Q5</c> in second. Reader-sourced.</summary>
    public required string SymbologyIdentifier { get; init; }

    /// <summary>
    /// The text the writer was given, recorded only where it is not <c>payloadText</c>: libzint and CodeGlyphX's rMQR
    /// encoder take bracketed AI syntax, and two writers get the <c>%</c> rule wrong for some input (the fixture spec
    /// lists them), so the symbol means what the reader says, not what the writer was asked.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WriterInput { get; init; }
}

/// <summary>An FNC1 fixture generator backed by one external encoder implementation. Each has its own case list in <see cref="Fnc1Corpus"/>.</summary>
public interface IFnc1FixtureGenerator
{
    /// <summary>Directory name under Fixtures/StandardQrFnc1/ and Fixtures/RmQrFnc1/ (e.g. "zint-libzint").</summary>
    string Name { get; }

    /// <summary>False when the backing toolchain is not present on this machine.</summary>
    bool IsAvailable { get; }

    GeneratedFixture Generate(Fnc1CaseDefinition caseDefinition);
}

/// <summary>The manifest fields every FNC1 generator fills the same way. The sanity gate completes the reader-sourced ones.</summary>
public static class Fnc1Manifests
{
    public static FixtureManifest Build(string generator, string generatorVersion, Fnc1CaseDefinition caseDefinition, ModuleMatrix matrix, int version, string? versionName, string mode, int maskPattern)
        => new()
        {
            Id = caseDefinition.Id,
            Generator = generator,
            GeneratorVersion = generatorVersion,
            SymbolType = caseDefinition.SymbolType,
            Version = version,
            VersionName = versionName,
            Width = matrix.Width,
            Height = matrix.Height,
            ErrorCorrectionLevel = caseDefinition.ErrorCorrectionLevel,
            Mode = mode,
            MaskPattern = maskPattern,
            PayloadText = caseDefinition.ExpectedText,
            PayloadUtf8Hex = Convert.ToHexString(Encoding.UTF8.GetBytes(caseDefinition.ExpectedText)),
            EciCharset = caseDefinition.Utf8 ? "UTF-8" : null,
            QuietZoneModules = caseDefinition.IsRmQR ? FixtureWriter.RmQRQuietZoneModules : FixtureWriter.QuietZoneModules,
            PixelsPerModule = FixtureWriter.PixelsPerModule,
            Fnc1 = new Fnc1Manifest
            {
                Position = caseDefinition.Position.ToString(),
                ApplicationIndicator = caseDefinition.Position == Fnc1Position.Second ? caseDefinition.ApplicationIndicator : null,
                SymbologyIdentifier = "", // filled in by the sanity gate from the zxing-cpp reader
            },
        };
}
