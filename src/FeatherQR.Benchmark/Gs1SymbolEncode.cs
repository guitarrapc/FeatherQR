using FeatherQR.Gs1;

/// <summary>
/// Measures the GS1 data layer overhead over the underlying QR and rMQR encodes.
/// </summary>
/// <remarks>
/// Each GS1 span call is paired with the plain Create call for its encoded data, using a shared destination buffer.
/// The difference includes validation, HRI parsing and URI construction as applicable. Optimal rows also include segmentation.
/// Payloads are shared with <see cref="Gs1DataLayer"/>.
/// </remarks>
public class Gs1SymbolEncode
{
    private static readonly string[] shapeKeys = ["retail", "logistics", "weighed"];
    private static readonly QRCodeGeneratorOptions firstPosition = new() { Fnc1 = Fnc1Mode.FirstPosition, Segmentation = QRSegmentation.Single };
    private static readonly RmQRCodeGeneratorOptions rmqrFirstPosition = new() { Fnc1 = Fnc1Mode.FirstPosition, Segmentation = RmQRSegmentation.Single };
    private static readonly QRCodeGeneratorOptions optimal = new() { Segmentation = QRSegmentation.Optimal };
    private static readonly RmQRCodeGeneratorOptions rmqrOptimal = new() { Segmentation = RmQRSegmentation.Optimal };

    private string _elementString = default!;
    private string _hri = default!;
    private string _uri = default!;
    private byte[] _destination = default!;

    [ParamsSource(nameof(Shapes))]
    public string Shape { get; set; } = default!;

    public static IEnumerable<string> Shapes() => shapeKeys;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _elementString = Shape switch
        {
            "retail" => "0109520123456788" + "17251231" + "10ABC123",
            "logistics" => "00195201234567891232" + "0209520123456788" + "11250504" + "3725\u001D" + "10ABC123",
            "weighed" => "0109520123456788" + "3103000195" + "39220299\u001D" + "17251231",
            _ => throw new ArgumentOutOfRangeException(nameof(Shape), Shape, "unknown shape"),
        };
        if (!Gs1ElementString.TryToHri(_elementString, out _hri, out var error))
            throw new InvalidOperationException($"shape {Shape} has no (AI) text: {error}");
        if (!Gs1ElementString.TryToDigitalLink(_elementString, out _uri, out error))
            throw new InvalidOperationException($"shape {Shape} has no Digital Link URI: {error}");
        if (!RmQRCodeGenerator.TryGetRequiredBufferSizeGs1(_hri, RmQREccLevel.M, out _))
            throw new InvalidOperationException($"shape {Shape} fits no rMQR symbol");

        _destination = new byte[40_000];
    }

    [Benchmark(Baseline = true, Description = "Create, the element string under FNC1")]
    public int Create() => QRCodeGenerator.Create(_elementString.AsSpan(), QREccLevel.M, _destination, firstPosition);

    [Benchmark(Description = "CreateGs1 from (AI) text")]
    public int CreateGs1FromAiText() => QRCodeGenerator.CreateGs1(_hri.AsSpan(), QREccLevel.M, _destination);

    [Benchmark(Description = "CreateGs1 from an element string")]
    public int CreateGs1FromElementString() => QRCodeGenerator.CreateGs1(_elementString.AsSpan(), QREccLevel.M, _destination);

    [Benchmark(Description = "CreateGs1 from an element string, Optimal")]
    public int CreateGs1FromElementStringOptimal() => QRCodeGenerator.CreateGs1(_elementString.AsSpan(), QREccLevel.M, _destination, optimal);

    [Benchmark(Description = "Create, the URI")]
    public int CreateUri() => QRCodeGenerator.Create(_uri.AsSpan(), QREccLevel.M, _destination);

    [Benchmark(Description = "CreateGs1DigitalLink from (AI) text")]
    public int CreateGs1DigitalLink() => QRCodeGenerator.CreateGs1DigitalLink(_hri.AsSpan(), QREccLevel.M, _destination);

    [Benchmark(Description = "rMQR Create, the element string under FNC1")]
    public int RmQrCreate() => RmQRCodeGenerator.Create(_elementString.AsSpan(), RmQREccLevel.M, _destination, rmqrFirstPosition);

    [Benchmark(Description = "rMQR CreateGs1 from (AI) text")]
    public int RmQrCreateGs1() => RmQRCodeGenerator.CreateGs1(_hri.AsSpan(), RmQREccLevel.M, _destination);

    [Benchmark(Description = "rMQR CreateGs1 from (AI) text, Optimal")]
    public int RmQrCreateGs1Optimal() => RmQRCodeGenerator.CreateGs1(_hri.AsSpan(), RmQREccLevel.M, _destination, rmqrOptimal);
}
