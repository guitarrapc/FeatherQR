/// <summary>
/// Compares Micro QR Optimal and Single segmentation through the allocation-free span API.
/// </summary>
/// <remarks>
/// Cases cover single-mode shortcuts and mixed or Kanji plans that select a smaller version.
/// Both arms must encode every fixture successfully at ECC L. Ratio includes the change in symbol-generation cost.
/// </remarks>
public class MicroQRSegmentationEncode
{
    private static readonly string[] shapeKeys =
    [
        "numeric-20", "alnum-15", "byte-12", "mixed-8", "mixed-19", "kanji-8",
    ];

    private Dictionary<string, string> _contents = default!;
    private string _content = default!;
    private MicroQRCodeGeneratorOptions _single;
    private MicroQRCodeGeneratorOptions _optimal;
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
            ["kanji-8"] = "日本語12345",
        };

        _content = _contents[Shape];
        var allowKanji = Shape is "kanji-8";
        _single = new MicroQRCodeGeneratorOptions { Segmentation = MicroQRSegmentation.Single, AllowKanji = allowKanji };
        _optimal = new MicroQRCodeGeneratorOptions { Segmentation = MicroQRSegmentation.Optimal, AllowKanji = allowKanji };

        // Sized for the Single arm, which never selects a smaller version than Optimal.
        MicroQRCodeGenerator.TryGetRequiredBufferSize(_content.AsSpan(), MicroQREccLevel.L, out var size, _single);
        _spanDestination = new byte[size.BufferSize];
    }

    [Benchmark(Baseline = true, Description = "Single")]
    public int SingleEncodeSpan()
    {
        return MicroQRCodeGenerator.Create(_content.AsSpan(), MicroQREccLevel.L, _spanDestination, _single);
    }

    [Benchmark(Description = "Optimal")]
    public int OptimalEncodeSpan()
    {
        return MicroQRCodeGenerator.Create(_content.AsSpan(), MicroQREccLevel.L, _spanDestination, _optimal);
    }
}
