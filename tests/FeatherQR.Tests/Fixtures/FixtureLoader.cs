using System.Text.Json;

namespace FeatherQR.Tests;

/// <summary>
/// Manifest for one committed fixture case (case-name.json), produced by
/// tools/QRInteropFixtures. Schema is documented in
/// .github/docs/specs/qrcode-test-fixtures.md.
/// </summary>
public sealed record FixtureManifest
{
    public required string Id { get; init; }
    public required string Generator { get; init; }
    public required string GeneratorVersion { get; init; }
    public required string SymbolType { get; init; }
    public required int Version { get; init; }
    /// <summary>Human-readable version name where the version is not a plain integer (rMQR: "R7x43"); null otherwise.</summary>
    public string? VersionName { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required string ErrorCorrectionLevel { get; init; }
    public required string Mode { get; init; }
    public int MaskPattern { get; init; } = -1;
    public required string PayloadText { get; init; }
    public required string PayloadUtf8Hex { get; init; }
    public string? EciCharset { get; init; }
    public int QuietZoneModules { get; init; }
    public int PixelsPerModule { get; init; }
    /// <summary>Structured Append header of this symbol; null when the symbol carries none.</summary>
    public StructuredAppendManifest? StructuredAppend { get; init; }
}

/// <summary>The Structured Append header as on the wire: 0-based index, count 1..16, parity byte; <c>SetId</c> groups one set.</summary>
public sealed record StructuredAppendManifest
{
    public required string SetId { get; init; }
    public required int Index { get; init; }
    public required int Count { get; init; }
    public required int Parity { get; init; }
}

/// <summary>One loadable fixture case: manifest plus sibling matrix/PNG files.</summary>
public sealed record FixtureCase(FixtureManifest Manifest, string MatrixPath, string PngPath);

/// <summary>
/// Loads committed fixtures from the Fixtures/ corpus (copied to the test output
/// directory). Fixture ids are "generator/case-name" relative paths without extension.
/// </summary>
public static class FixtureLoader
{
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    public static string FixtureRoot => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static IEnumerable<string> EnumerateFixtureIds(string symbology)
    {
        var root = Path.Combine(FixtureRoot, symbology);
        if (!Directory.Exists(root))
            yield break;

        foreach (var json in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, json).Replace('\\', '/');
            yield return relative.Substring(0, relative.Length - ".json".Length);
        }
    }

    public static FixtureCase Load(string symbology, string fixtureId)
    {
        var basePath = Path.Combine(FixtureRoot, symbology, fixtureId.Replace('/', Path.DirectorySeparatorChar));
        var manifestPath = basePath + ".json";
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"Fixture manifest not found: {manifestPath}");

        var manifest = JsonSerializer.Deserialize<FixtureManifest>(File.ReadAllText(manifestPath), jsonOptions)
            ?? throw new InvalidDataException($"Fixture manifest deserialized to null: {manifestPath}");

        return new FixtureCase(manifest, basePath + ".matrix.txt", basePath + ".png");
    }

    /// <summary>
    /// Parses a matrix fixture ('1' = dark, '0' = light, row-major, no quiet zone)
    /// into a byte-per-module buffer compatible with <see cref="QRCodeDecoder.TryDecode(ReadOnlySpan{byte}, int, out string, out QRCodeDecodeInfo)"/>.
    /// </summary>
    public static (byte[] Modules, int Size) ReadMatrix(string matrixPath)
    {
        var (modules, width, height) = ReadRectangularMatrix(matrixPath);
        if (width != height)
            throw new InvalidDataException($"Matrix fixture is not square: {height} rows x {width} columns ({matrixPath})");

        return (modules, width);
    }

    /// <summary>
    /// Parses a rectangular matrix fixture ('1' = dark, '0' = light, row-major, no
    /// quiet zone) into a byte-per-module buffer; every row must have the same width.
    /// </summary>
    public static (byte[] Modules, int Width, int Height) ReadRectangularMatrix(string matrixPath)
    {
        var lines = File.ReadAllLines(matrixPath).Where(static l => l.Length > 0).ToArray();
        var height = lines.Length;
        if (height == 0)
            throw new InvalidDataException($"Matrix fixture is empty ({matrixPath})");

        var width = lines[0].Length;
        var modules = new byte[width * height];
        for (var row = 0; row < height; row++)
        {
            var line = lines[row];
            if (line.Length != width)
                throw new InvalidDataException($"Matrix fixture is ragged: row {row} has {line.Length} columns, expected {width} ({matrixPath})");

            for (var col = 0; col < width; col++)
            {
                modules[row * width + col] = line[col] switch
                {
                    '1' => 1,
                    '0' => 0,
                    _ => throw new InvalidDataException($"Matrix fixture contains invalid character '{line[col]}' at ({row},{col}) ({matrixPath})"),
                };
            }
        }

        return (modules, width, height);
    }
}
