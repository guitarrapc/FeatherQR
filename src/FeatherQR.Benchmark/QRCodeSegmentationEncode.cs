/// <summary>
/// Compares Standard QR Optimal and Single segmentation through the allocation-free span API.
/// </summary>
/// <remarks>
/// Ratio measures the whole encode, including any reduction in ECC and placement work from selecting a smaller symbol.
/// Cases cover single-mode shortcuts, splits that do or do not shrink the symbol, UTF-8 and Kanji plans.
/// Both arms must encode every fixture successfully at ECC M. Planner details belong beside QRSegmentPlanner.
/// </remarks>
public class QRCodeSegmentationEncode
{
    private static readonly string[] shapeKeys =
    [
        "numeric-120", "numeric-3000",
        "alnum-120", "byte-120",
        "mixed-20", "mixed-120", "mixed-1000",
        "alt1-120",
        "url-58", "utf8-60", "kanji-60", "kanji-prose-66",
    ];

    private Dictionary<string, string> _contents = default!;
    private string _content = default!;
    private QRCodeGeneratorOptions _single;
    private QRCodeGeneratorOptions _optimal;
    private byte[] _spanDestination = default!;

    [ParamsSource(nameof(Shapes))]
    public string Shape { get; set; } = default!;

    public static IEnumerable<string> Shapes() => shapeKeys;

    private static string HalfAndHalf(int total) => new string('a', total / 2) + new string('7', total - total / 2);

    [GlobalSetup]
    public void GlobalSetup()
    {
        _contents = new Dictionary<string, string>
        {
            ["numeric-120"] = new string('7', 120),
            ["numeric-3000"] = new string('7', 3000),
            ["alnum-120"] = new string('A', 120),
            ["byte-120"] = new string('a', 120),
            ["mixed-20"] = HalfAndHalf(20),
            ["mixed-120"] = HalfAndHalf(120),
            ["mixed-1000"] = HalfAndHalf(1000),
            ["alt1-120"] = string.Concat(Enumerable.Repeat("a7", 60)),
            ["url-58"] = "https://example.com/item?id=123456789012345678901234567890",
            ["utf8-60"] = string.Concat(Enumerable.Repeat("日本7777", 10)),
            ["kanji-60"] = string.Concat(Enumerable.Repeat("日本7777", 10)),
            ["kanji-prose-66"] = string.Concat(Enumerable.Repeat("こんにちは世界、QRコードの分割テストです。", 3)),
        };

        _content = _contents[Shape];
        var allowKanji = Shape is "kanji-60" or "kanji-prose-66";
        _single = new QRCodeGeneratorOptions { Segmentation = QRSegmentation.Single, AllowKanji = allowKanji };
        _optimal = new QRCodeGeneratorOptions { Segmentation = QRSegmentation.Optimal, AllowKanji = allowKanji };

        // Sized for the Single arm, which never selects a smaller version than Optimal.
        _spanDestination = new byte[Sizing.Required(_content.AsSpan(), QREccLevel.M, _single).BufferSize];
    }

    [Benchmark(Baseline = true, Description = "Single")]
    public int SingleEncodeSpan()
    {
        return FeatherQR.QRCodeGenerator.Create(_content.AsSpan(), QREccLevel.M, _spanDestination, _single);
    }

    [Benchmark(Description = "Optimal")]
    public int OptimalEncodeSpan()
    {
        return FeatherQR.QRCodeGenerator.Create(_content.AsSpan(), QREccLevel.M, _spanDestination, _optimal);
    }
}
