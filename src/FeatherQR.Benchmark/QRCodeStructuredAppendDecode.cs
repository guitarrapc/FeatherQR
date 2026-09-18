using System.Text;

/// <summary>
/// What reading a Structured Append set costs over reading the same chunks as plain symbols.
///
/// The baseline arm decodes one plain symbol per chunk, each made by <c>Create</c> at the set's version and level with no header, so the Ratio column is the header's cost on the decode path: the 20 header bits and reporting them on <c>QRCodeDecodeInfo</c>.
/// Both arms decode <c>QRCodeData</c> with the default quiet zone and allocate one string per symbol.
///
///   byte-45k-any    : ASCII prose, 16 symbols at version 40
///   numeric-100k-any: digits, 15 symbols at version 40
///   mixed-40k-opt   : order lines under Optimal, 14 symbols; every symbol carries several segments
///   utf8-15k-any    : Japanese prose behind a UTF-8 ECI, 15 symbols
///   byte-4k-max10   : ASCII prose capped at version 10, the label-sized set
/// </summary>
public class QRCodeStructuredAppendDecode
{
    private static readonly string[] shapeKeys =
    [
        "byte-45k-any", "numeric-100k-any", "mixed-40k-opt", "utf8-15k-any", "byte-4k-max10",
    ];

    private QRCodeData[] _set = default!;
    private QRCodeData[] _plain = default!;

    [ParamsSource(nameof(Shapes))]
    public string Shape { get; set; } = default!;

    public static IEnumerable<string> Shapes() => shapeKeys;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var (content, range, segmentation) = Shape switch
        {
            "byte-45k-any" => (Repeat("The quick brown fox jumps over the lazy dog. ", 45_000), QRVersionRange.Any, QRSegmentation.Single),
            "numeric-100k-any" => (Repeat("0123456789", 100_000), QRVersionRange.Any, QRSegmentation.Single),
            "mixed-40k-opt" => (Repeat("order 20260915 item 0000123456 qty 42 ", 40_000), QRVersionRange.Any, QRSegmentation.Optimal),
            "utf8-15k-any" => (Repeat("こんにちは世界、QRコードの分割テストです。", 15_000), QRVersionRange.Any, QRSegmentation.Single),
            "byte-4k-max10" => (Repeat("The quick brown fox jumps over the lazy dog. ", 4_000), QRVersionRange.AtMost(10), QRSegmentation.Single),
            _ => throw new ArgumentOutOfRangeException(nameof(Shape), Shape, "unknown shape"),
        };

        _set = QRCodeGenerator.CreateStructuredAppend(content.AsSpan(), QREccLevel.L, new QRCodeGeneratorOptions { Version = range, Segmentation = segmentation });
        _plain = new QRCodeData[_set.Length];
        var joined = new StringBuilder(content.Length);
        for (var i = 0; i < _set.Length; i++)
        {
            if (!QRCodeDecoder.TryDecode(_set[i], out var chunk, out var info))
                throw new InvalidOperationException($"symbol {i} of shape {Shape} did not decode: {info.Status}");
            joined.Append(chunk);
            _plain[i] = QRCodeGenerator.Create(chunk.AsSpan(), info.EccLevel, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(info.Version), Segmentation = segmentation });
        }
        if (joined.ToString() != content)
            throw new InvalidOperationException($"shape {Shape} did not read back as its content");
    }

    [Benchmark(Baseline = true, Description = "Plain symbols")]
    public int PlainSymbols() => DecodeAll(_plain);

    [Benchmark(Description = "Structured Append set")]
    public int StructuredAppendSet() => DecodeAll(_set);

    private static int DecodeAll(QRCodeData[] symbols)
    {
        var length = 0;
        foreach (var symbol in symbols)
        {
            QRCodeDecoder.TryDecode(symbol, out var text, out _);
            length += text.Length;
        }
        return length;
    }

    private static string Repeat(string text, int length)
    {
        var sb = new StringBuilder(length + text.Length);
        while (sb.Length < length)
            sb.Append(text);
        return sb.ToString(0, length);
    }
}
