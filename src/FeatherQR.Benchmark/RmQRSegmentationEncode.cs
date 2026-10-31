/// <summary>
/// Compares rMQR Optimal and Single segmentation through the allocation-free span API.
/// </summary>
/// <remarks>
/// Ratio includes the whole encode: a smaller selected symbol can offset planning cost, so it is not an isolated planner measurement.
/// Cases vary length and character layout, including numeric shortcuts, unsuccessful splits, UTF-8 and Kanji plans.
/// Both arms must encode every fixture successfully at ECC M.
/// </remarks>
public class RmQRSegmentationEncode
{
    private static readonly string[] shapeKeys =
    [
        "numeric-120", "numeric-361",
        "alnum-120", "byte-150",
        "mixed-20", "mixed-60", "mixed-120", "mixed-150",
        "alt1-120", "alt10-120",
        "url-38", "utf8-60", "kanji-60",
    ];

    private Dictionary<string, string> _contents = default!;
    private string _content = default!;
    private RmQRCodeGeneratorOptions _single;
    private RmQRCodeGeneratorOptions _optimal;
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
            ["numeric-361"] = new string('7', 361),                                                     // R17x139-M numeric boundary
            ["alnum-120"] = new string('A', 120),
            ["byte-150"] = new string('a', 150),                                                        // R17x139-M byte boundary
            ["mixed-20"] = HalfAndHalf(20),
            ["mixed-60"] = HalfAndHalf(60),
            ["mixed-120"] = HalfAndHalf(120),
            ["mixed-150"] = HalfAndHalf(150),
            ["alt1-120"] = string.Concat(Enumerable.Repeat("a7", 60)),                                  // alternating every character
            ["alt10-120"] = string.Concat(Enumerable.Repeat(new string('a', 10) + new string('7', 10), 6)),
            ["url-38"] = "https://example.com/p/1234567890123456",
            ["utf8-60"] = string.Concat(Enumerable.Repeat("日本7777", 10)),
            ["kanji-60"] = string.Concat(Enumerable.Repeat("日本7777", 10)),
        };

        _content = _contents[Shape];
        var allowKanji = Shape is "kanji-60";
        _single = new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Single, AllowKanji = allowKanji };
        _optimal = new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal, AllowKanji = allowKanji };

        // Sized from the largest-area version rather than from any shape's content: with
        // the version pinned, the reported buffer size depends only on that version and the
        // quiet zone, not on the payload, so this is the maximum every shape can need
        // (3,003 bytes at R17x139).
        _spanDestination = new byte[Sizing.Required("0".AsSpan(), RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R17x139 }).BufferSize];
    }

    [Benchmark(Baseline = true, Description = "Single")]
    public int SingleEncodeSpan()
    {
        return RmQRCodeGenerator.Create(_content.AsSpan(), RmQREccLevel.M, _spanDestination, _single);
    }

    [Benchmark(Description = "Optimal")]
    public int OptimalEncodeSpan()
    {
        return RmQRCodeGenerator.Create(_content.AsSpan(), RmQREccLevel.M, _spanDestination, _optimal);
    }
}
