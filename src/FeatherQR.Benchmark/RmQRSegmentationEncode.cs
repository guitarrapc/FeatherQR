/// <summary>
/// What <c>RmQRSegmentation.Optimal</c> costs against <c>Single</c>, encoding the same content through the same zero-allocation span path.
///
/// The Ratio column is the end-to-end multiplier a caller actually pays, which is the number worth quoting — but note it is not the planning cost in isolation.
/// On the rows where the split wins, the Optimal arm lands on a smaller version and therefore also does less ECC, placement and module writing, so the ratio nets planning against a cheaper encode and understates the planning overhead.
/// Six of the twelve shapes below change version between the two arms.
///
/// This is a separate class from <see cref="RmQREncodeEndToEnd"/> on purpose.
/// That one varies version and mode with the version pinned, and ranks everything against one baseline row; this one varies content shape with the version free, and every row is meaningless without its same-run partner.
/// Mixing them would make both tables harder to read and both filters slower to run.
///
/// The shapes are chosen to separate the two things that drive planning cost:
///
///   Length, shape held fixed   : mixed-20 / -60 / -120 / -150 (half letters, half digits)
///   Shape, length held at 120  : numeric / alnum / byte / mixed / alt1 / alt10
///
/// and to cover every filter the scan can terminate in (measured DP passes per encode in brackets — the scan is bounded before it is priced, so most shapes never build a table at all):
///
///   numeric-*  : all digits, so one mode is provably optimal and the short-circuit
///                returns before any bound is computed [0 passes]
///   alnum-120  : the trivial O(n) bound rejects every better-ranked version, so no
///                cost run happens [0 passes]
///   byte-150   : the same, at the byte capacity boundary [0 passes]
///   alt1-120   : alternating every character. The trivial bound cannot rule this out
///                (it prices each digit at the numeric rate, ignoring that switching
///                every character never pays), so the floor runs and then rejects
///                everything — it plans and gains nothing [1 pass]
///   alt10-120  : alternating every ten, where a per-version cost run is what decides
///                the version rather than a bound [3 passes]
///   mixed-*    : the common shape; the upper bound accepts without a per-version run
///                [2 passes: floor + building the chosen plan]
///   url-38     : a realistic payload (R11x77 as one run, R15x43 split) [2 passes]
///   utf8-60    : multi-byte runs behind an ECI prefix; like alt1-120 it plans and
///                gains nothing, landing on the same version in both arms [2 passes]
///
/// Every fixture fits under Single too, so no row is comparing a success with a throw.
/// ECC M only: the H capacities shift which version wins but not the shape of the work.
/// </summary>
public class RmQRSegmentationEncode
{
    private static readonly string[] shapeKeys =
    [
        "numeric-120", "numeric-361",
        "alnum-120", "byte-150",
        "mixed-20", "mixed-60", "mixed-120", "mixed-150",
        "alt1-120", "alt10-120",
        "url-38", "utf8-60",
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
        };

        _content = _contents[Shape];

        // Sized from the largest-area version rather than from any shape's content: with
        // the version pinned, the reported buffer size depends only on that version and the
        // quiet zone, not on the payload, so this is the maximum every shape can need
        // (3,003 bytes at R17x139).
        _spanDestination = new byte[Sizing.Required("0".AsSpan(), RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R17x139 }).BufferSize];
    }

    [Benchmark(Baseline = true, Description = "Single")]
    public int SingleEncodeSpan()
    {
        return RmQRCodeGenerator.Create(_content.AsSpan(), RmQREccLevel.M, _spanDestination);
    }

    [Benchmark(Description = "Optimal")]
    public int OptimalEncodeSpan()
    {
        return RmQRCodeGenerator.Create(_content.AsSpan(), RmQREccLevel.M, _spanDestination, new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal });
    }
}
