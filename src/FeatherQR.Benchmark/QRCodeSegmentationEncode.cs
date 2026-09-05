/// <summary>
/// What <c>QRSegmentation.Optimal</c> costs against <c>Single</c>, encoding the same
/// content through the same zero-allocation span path.
///
/// The Ratio column is the end-to-end multiplier a caller actually pays — but note it is
/// not the planning cost in isolation. On the rows where the split wins, the Optimal arm
/// lands on a smaller version and therefore also does less ECC, placement and module
/// writing, so the ratio nets planning against a cheaper encode.
///
/// Count indicator widths are constant within the three version bands (1-9 / 10-26 /
/// 27-40), so the planner runs at most three O(n) cost passes plus one reconstruction
/// pass, and a trivial single-pass bound rejects candidates no split could fit before
/// any cost pass runs. The shapes separate the paths the scan can take:
///
///   numeric-*  : all digits, one mode is provably optimal, no cost run at all
///   alnum-120  : single-mode content the trivial bound rules out without a cost run
///   byte-120   : the same, in Byte mode
///   alt1-120   : alternating every character — the bound's blind spot: it pays for
///                a cost run (one, within the 1-9 band its scan window spans) and
///                gains nothing
///   mixed-*    : half letters half digits; 120/1000 win a version, mixed-20 does
///                not (its 140-bit optimum misses v1-M's 128 bits) and emits the
///                Single stream — the priced-but-no-candidate-fits path
///   url-58     : a realistic payload (version 4-M as one run, version 3-M split)
///   utf8-60    : multi-byte runs behind an ECI prefix
///
/// Every fixture fits under Single too, so no row compares a success with a throw.
/// ECC M only: other levels shift which version wins but not the shape of the work.
/// </summary>
public class QRCodeSegmentationEncode
{
    private static readonly string[] shapeKeys =
    [
        "numeric-120", "numeric-3000",
        "alnum-120", "byte-120",
        "mixed-20", "mixed-120", "mixed-1000",
        "alt1-120",
        "url-58", "utf8-60",
    ];

    private Dictionary<string, string> _contents = default!;
    private string _content = default!;
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
        };

        _content = _contents[Shape];

        // Sized for the Single arm, which never selects a smaller version than Optimal.
        _spanDestination = new byte[Sizing.Required(_content.AsSpan(), QREccLevel.M).BufferSize];
    }

    [Benchmark(Baseline = true, Description = "Single")]
    public int SingleEncodeSpan()
    {
        return FeatherQR.QRCodeGenerator.Create(_content.AsSpan(), QREccLevel.M, _spanDestination);
    }

    [Benchmark(Description = "Optimal")]
    public int OptimalEncodeSpan()
    {
        return FeatherQR.QRCodeGenerator.Create(_content.AsSpan(), QREccLevel.M, _spanDestination, new QRCodeGeneratorOptions { Segmentation = QRSegmentation.Optimal });
    }
}
