using FeatherQR.Gs1;

/// <summary>
/// What the GS1 entry points add to an encode, on the three element strings of <see cref="Gs1DataLayer"/>: each
/// entry point beside the <c>Create</c> call it ends in, all in the span form into one buffer, so the Allocated
/// column is the layer's own. The difference between a row and its <c>Create</c> row is the data layer's work for
/// the text: the checks, the separators of (AI) text, and for Digital Link the URI. <c>CreateGs1</c> has a row
/// under <c>Optimal</c> in each symbology too, where the element string is split into runs when that lowers the
/// version, so the cost of planning a GS1 label stands beside the default.
///
///   retail    : (01) GTIN, (17) expiry, (10) lot, the point-of-sale label
///   logistics : (00) SSCC, (02) content GTIN, (11) production date, (37) count, (10) lot, a logistic label
///   weighed   : (01) GTIN, (3103) net weight, (3922) price, (17) expiry, the Digital Link standard's variable-measure example
/// </summary>
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
