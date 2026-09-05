/// <summary>
/// What <c>MicroQRSegmentation.Optimal</c> costs against <c>Single</c>, encoding the
/// same content through the same zero-allocation span path.
///
/// Micro QR content never exceeds 35 characters and there are at most three
/// candidate versions below the single-mode fit, so planning is a handful of tiny
/// dynamic-program passes; the shapes separate the scan's paths:
///
///   numeric-20 : all digits, one mode is provably optimal, no cost run at all
///   alnum-15   : single-mode content the trivial bound rules out without a cost run
///   byte-12    : the same, in Byte mode (M2 additionally lacks Byte entirely)
///   mixed-8    : "A" + 7 digits, wins M3 -> M2 (the version without Byte mode)
///   mixed-19   : "AB" + 17 digits, wins M4 -> M3
///
/// Every fixture fits under Single too, so no row compares a success with a throw.
/// ECC L only.
/// </summary>
public class MicroQRSegmentationEncode
{
    private static readonly string[] shapeKeys =
    [
        "numeric-20", "alnum-15", "byte-12", "mixed-8", "mixed-19",
    ];

    private Dictionary<string, string> _contents = default!;
    private string _content = default!;
    private byte[] _spanDestination = default!;

    [ParamsSource(nameof(Shapes))]
    public string Shape { get; set; } = default!;

    public static IEnumerable<string> Shapes() => shapeKeys;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _contents = new Dictionary<string, string>
        {
            ["numeric-20"] = new string('7', 20),
            ["alnum-15"] = new string('A', 15),
            ["byte-12"] = new string('a', 12),
            ["mixed-8"] = "A1234567",
            ["mixed-19"] = "AB" + new string('1', 17),
        };

        _content = _contents[Shape];

        // Sized for the Single arm, which never selects a smaller version than Optimal.
        MicroQRCodeGenerator.TryGetRequiredBufferSize(_content.AsSpan(), MicroQREccLevel.L, out var size);
        _spanDestination = new byte[size.BufferSize];
    }

    [Benchmark(Baseline = true, Description = "Single")]
    public int SingleEncodeSpan()
    {
        return MicroQRCodeGenerator.Create(_content.AsSpan(), MicroQREccLevel.L, _spanDestination);
    }

    [Benchmark(Description = "Optimal")]
    public int OptimalEncodeSpan()
    {
        return MicroQRCodeGenerator.Create(_content.AsSpan(), MicroQREccLevel.L, _spanDestination, new MicroQRCodeGeneratorOptions { Segmentation = MicroQRSegmentation.Optimal });
    }
}
