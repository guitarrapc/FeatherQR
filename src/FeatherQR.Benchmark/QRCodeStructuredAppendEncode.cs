using System.Text;

/// <summary>
/// What <c>QRCodeGenerator.CreateStructuredAppend</c> costs over the symbols it produces.
///
/// The baseline arm encodes the same chunks the balanced split produces, one <c>Create</c> per chunk at the set's version, so the Ratio column of the Single and Optimal arms is the planning overhead directly: the split search, the charset decision and the parity, net of the symbol work every path pays.
/// Allocations are the same on every arm but the result array, because every symbol is one <c>QRCodeData</c> either way.
///
/// The shapes separate the costs the planner can pay:
///
///   byte-45k-any    : ASCII prose, 16 symbols at version 40; the version range is open, so the
///                     planner scans versions 1 to 39 for the smallest that still holds 16
///   byte-45k-v40    : the same text pinned to version 40; the scan is gone, what remains is
///                     the fewest-symbols walk, the balancing search and the symbols
///   byte-4k-max10   : ASCII prose capped at version 10 (a label-sized symbol), 15 to 16 symbols;
///                     the realistic request the version cap exists for
///   numeric-100k-any: digits, 15 symbols at version 40; all-Numeric content has no better
///                     plan than one run, so the Optimal arm should cost what Single does
///   mixed-40k-any   : order lines, letters and digit runs, 14 symbols; the content a mixed
///                     plan is for, so the Optimal arm both plans and pays for it
///   utf8-15k-any    : Japanese prose behind a UTF-8 ECI, 15 symbols; multi-byte cost model
///
/// ECC L throughout: the largest chunks a version holds, so every search runs over the longest prefixes.
/// Symbol counts are the ones the planner picks; they move if the capacity tables or the split rule change, and the doc comment is not a pin.
/// </summary>
public class QRCodeStructuredAppendEncode
{
    private static readonly string[] shapeKeys =
    [
        "byte-45k-any", "byte-45k-v40", "byte-4k-max10",
        "numeric-100k-any", "mixed-40k-any", "utf8-15k-any",
    ];

    private string _content = default!;
    private QRCodeGeneratorOptions _single;
    private QRCodeGeneratorOptions _optimal;
    private QRCodeGeneratorOptions _chunkOptions;
    private string[] _chunks = default!;

    [ParamsSource(nameof(Shapes))]
    public string Shape { get; set; } = default!;

    public static IEnumerable<string> Shapes() => shapeKeys;

    [GlobalSetup]
    public void GlobalSetup()
    {
        (_content, var range) = Shape switch
        {
            "byte-45k-any" => (Repeat("The quick brown fox jumps over the lazy dog. ", 45_000), QRVersionRange.Any),
            "byte-45k-v40" => (Repeat("The quick brown fox jumps over the lazy dog. ", 45_000), QRVersionRange.Exactly(40)),
            "byte-4k-max10" => (Repeat("The quick brown fox jumps over the lazy dog. ", 4_000), QRVersionRange.AtMost(10)),
            "numeric-100k-any" => (Repeat("0123456789", 100_000), QRVersionRange.Any),
            "mixed-40k-any" => (Repeat("order 20260915 item 0000123456 qty 42 ", 40_000), QRVersionRange.Any),
            "utf8-15k-any" => (Repeat("こんにちは世界、QRコードの分割テストです。", 15_000), QRVersionRange.Any),
            _ => throw new ArgumentOutOfRangeException(nameof(Shape), Shape, "unknown shape"),
        };

        _single = new QRCodeGeneratorOptions { Version = range };
        _optimal = new QRCodeGeneratorOptions { Version = range, Segmentation = QRSegmentation.Optimal };

        // The chunks of the Single set, read back through the decoder, and the version
        // every symbol of the set shares; the baseline encodes exactly these.
        var set = QRCodeGenerator.CreateStructuredAppend(_content.AsSpan(), QREccLevel.L, _single);
        _chunks = new string[set.Length];
        for (var i = 0; i < set.Length; i++)
        {
            if (!QRCodeDecoder.TryDecode(set[i], out _chunks[i], out var info))
                throw new InvalidOperationException($"symbol {i} of shape {Shape} did not decode: {info.Status}");
        }
        _chunkOptions = new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(set[0].Version) };
    }

    [Benchmark(Baseline = true, Description = "Symbols")]
    public int SymbolsOnly()
    {
        var modules = 0;
        foreach (var chunk in _chunks)
            modules += QRCodeGenerator.Create(chunk.AsSpan(), QREccLevel.L, _chunkOptions).Size;
        return modules;
    }

    [Benchmark(Description = "Single")]
    public QRCodeData[] SingleSet()
    {
        return QRCodeGenerator.CreateStructuredAppend(_content.AsSpan(), QREccLevel.L, _single);
    }

    [Benchmark(Description = "Optimal")]
    public QRCodeData[] OptimalSet()
    {
        return QRCodeGenerator.CreateStructuredAppend(_content.AsSpan(), QREccLevel.L, _optimal);
    }

    private static string Repeat(string text, int length)
    {
        var sb = new StringBuilder(length + text.Length);
        while (sb.Length < length)
            sb.Append(text);
        return sb.ToString(0, length);
    }
}
