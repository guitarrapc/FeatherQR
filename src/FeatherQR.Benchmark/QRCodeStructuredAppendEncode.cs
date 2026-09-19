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
///                     planner also has to find the smallest version that still holds 16
///   byte-45k-v40    : the same text pinned to version 40; the scan is gone, what remains is
///                     the fewest-symbols walk, the balancing search and the symbols
///   byte-4k-max10   : ASCII prose capped at version 10 (a label-sized symbol), 15 to 16 symbols;
///                     the realistic request the version cap exists for
///   numeric-100k-any: digits, 15 symbols at version 40; all-Numeric content has no better
///                     plan than one run, so the Optimal arm should cost what Single does
///   alnum-65k-any  : the full QR alphanumeric alphabet, exercising long alphanumeric prefixes
///   mixed-40k-any   : order lines, letters and digit runs, 14 symbols; the content a mixed
///                     plan is for, so the Optimal arm both plans and pays for it
///   uneven-40k-any  : 20k digits then 20k of the same order lines, 10 symbols; content whose density
///                     changes along the text, where a split even in characters is far from even in cost
///   utf8-15k-any    : Japanese prose behind a UTF-8 ECI, 15 symbols; multi-byte cost model
///   utf8-15k-mixed  : Japanese sentences carrying order numbers, behind a UTF-8 ECI; three-byte characters
///                     with digit runs worth a Numeric segment, so the searches run the program under UTF-8
///   marked-40k-any  : the order lines as concatenated files leave them, a U+FEFF after every line feed; no symbol
///                     may begin with one, so cuts are moved off it, and it should cost what mixed-40k-any does
///   *-boost         : the same prose and digits with BoostEccLevel, which re-costs every
///                     chunk once per level it tries; the baseline encodes its symbols at
///                     the level the boost reached, so the Ratio stays the planning overhead
///
/// ECC L is requested throughout: the largest chunks a version holds, so every search runs over the longest prefixes.
/// Symbol counts are the ones the planner picks; they move if the capacity tables or the split rule change, and the doc comment is not a pin.
/// </summary>
public class QRCodeStructuredAppendEncode
{
    private static readonly string[] shapeKeys =
    [
        "byte-45k-any", "byte-45k-v40", "byte-4k-max10",
        "numeric-100k-any", "alnum-65k-any", "mixed-40k-any", "uneven-40k-any", "utf8-15k-any", "utf8-15k-mixed", "marked-40k-any",
        "byte-45k-boost", "numeric-100k-boost",
    ];

    private string _content = default!;
    private QRCodeGeneratorOptions _single;
    private QRCodeGeneratorOptions _optimal;
    private QRCodeGeneratorOptions _chunkOptions;
    private QREccLevel _chunkLevel;
    private string[] _chunks = default!;

    [ParamsSource(nameof(Shapes))]
    public string Shape { get; set; } = default!;

    public static IEnumerable<string> Shapes() => shapeKeys;

    [GlobalSetup]
    public void GlobalSetup()
    {
        (_content, var range, var boost) = Shape switch
        {
            "byte-45k-any" => (Repeat("The quick brown fox jumps over the lazy dog. ", 45_000), QRVersionRange.Any, false),
            "byte-45k-v40" => (Repeat("The quick brown fox jumps over the lazy dog. ", 45_000), QRVersionRange.Exactly(40), false),
            "byte-4k-max10" => (Repeat("The quick brown fox jumps over the lazy dog. ", 4_000), QRVersionRange.AtMost(10), false),
            "numeric-100k-any" => (Repeat("0123456789", 100_000), QRVersionRange.Any, false),
            "alnum-65k-any" => (Repeat("HELLO WORLD 12345 $%*+-./: ", 65_000), QRVersionRange.Any, false),
            "mixed-40k-any" => (Repeat("order 20260915 item 0000123456 qty 42 ", 40_000), QRVersionRange.Any, false),
            "uneven-40k-any" => (Repeat("0123456789", 20_000) + Repeat("order 20260915 item 0000123456 qty 42 ", 20_000), QRVersionRange.Any, false),
            "utf8-15k-any" => (Repeat("こんにちは世界、QRコードの分割テストです。", 15_000), QRVersionRange.Any, false),
            "utf8-15k-mixed" => (Repeat("ご注文番号 20260915-0000123456 の商品を 42 個、本日発送いたしました。", 15_000), QRVersionRange.Any, false),
            "marked-40k-any" => (Repeat("order 20260915 item 0000123456 qty 42" + (char)0x0A + (char)0xFEFF, 40_000), QRVersionRange.Any, false),
            "byte-45k-boost" => (Repeat("The quick brown fox jumps over the lazy dog. ", 45_000), QRVersionRange.Any, true),
            "numeric-100k-boost" => (Repeat("0123456789", 100_000), QRVersionRange.Any, true),
            _ => throw new ArgumentOutOfRangeException(nameof(Shape), Shape, "unknown shape"),
        };

        _single = new QRCodeGeneratorOptions { Version = range, BoostEccLevel = boost };
        _optimal = new QRCodeGeneratorOptions { Version = range, BoostEccLevel = boost, Segmentation = QRSegmentation.Optimal };

        // The chunks of the Single set, read back through the decoder, with the version and
        // level every symbol of the set shares; the baseline encodes exactly those symbols,
        // so a boosted set is compared against symbols at the level the boost reached.
        var set = QRCodeGenerator.CreateStructuredAppend(_content.AsSpan(), QREccLevel.L, _single);
        _chunks = new string[set.Length];
        for (var i = 0; i < set.Length; i++)
        {
            if (!QRCodeDecoder.TryDecode(set[i], out _chunks[i], out var info))
                throw new InvalidOperationException($"symbol {i} of shape {Shape} did not decode: {info.Status}");
            _chunkLevel = info.EccLevel;
        }
        _chunkOptions = new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(set[0].Version) };
    }

    [Benchmark(Baseline = true, Description = "Symbols")]
    public int SymbolsOnly()
    {
        var modules = 0;
        foreach (var chunk in _chunks)
            modules += QRCodeGenerator.Create(chunk.AsSpan(), _chunkLevel, _chunkOptions).Size;
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
