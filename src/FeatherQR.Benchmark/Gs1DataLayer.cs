using CodeGlyphX.Gs1Data;
using FeatherQR.Gs1;

/// <summary>
/// What the GS1 data layer costs on three typical element strings: every check (<c>TryValidate</c>), the (AI) text
/// in both directions, the Digital Link URI in both directions and the decode-side <c>TryRead</c> of the URI, each
/// through its span form into a buffer sized by the length twin, so the Allocated column is the layer's own.
/// CodeGlyphX's validator on the same data as (AI) text stands beside <c>TryValidate</c> for scale, not as a baseline:
/// it also builds its element list, so the two do not do the same work.
///
///   retail    : (01) GTIN, (17) expiry, (10) lot, the point-of-sale label
///   logistics : (00) SSCC, (02) content GTIN, (11) production date, (37) count, (10) lot, a logistic label
///   weighed   : (01) GTIN, (3103) net weight, (3922) price, (17) expiry, the Digital Link standard's variable-measure example
/// </summary>
public class Gs1DataLayer
{
    private static readonly string[] shapeKeys = ["retail", "logistics", "weighed"];

    private string _elementString = default!;
    private string _hri = default!;
    private string _uri = default!;
    private char[] _hriBuffer = default!;
    private char[] _elementStringBuffer = default!;
    private char[] _uriBuffer = default!;

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
        if (!Gs1ElementString.TryValidate(_elementString, out var error))
            throw new InvalidOperationException($"shape {Shape} is not valid GS1 data: {error}");
        if (!Gs1ElementString.TryToHri(_elementString, out _hri, out error))
            throw new InvalidOperationException($"shape {Shape} has no (AI) text: {error}");
        if (!Gs1ElementString.TryToDigitalLink(_elementString, out _uri, out error))
            throw new InvalidOperationException($"shape {Shape} has no Digital Link URI: {error}");
        if (!Gs1Validator.Validate(_hri).IsValid)
            throw new InvalidOperationException($"CodeGlyphX refuses shape {Shape}");

        _hriBuffer = new char[_hri.Length];
        _elementStringBuffer = new char[_elementString.Length];
        _uriBuffer = new char[_uri.Length];
    }

    [Benchmark(Description = "TryValidate")]
    public bool Validate() => Gs1ElementString.TryValidate(_elementString.AsSpan(), out _);

    [Benchmark(Description = "CodeGlyphX Gs1Validator.Validate on the (AI) text")]
    public bool CodeGlyphXValidate() => Gs1Validator.Validate(_hri).IsValid;

    [Benchmark(Description = "TryToHri (span)")]
    public int ToHri() => Gs1ElementString.TryToHri(_elementString.AsSpan(), _hriBuffer, out var written, out _) ? written : -1;

    [Benchmark(Description = "TryFromHri (span)")]
    public int FromHri() => Gs1ElementString.TryFromHri(_hri.AsSpan(), _elementStringBuffer, out var written, out _) ? written : -1;

    [Benchmark(Description = "TryToDigitalLink (span)")]
    public int ToDigitalLink() => Gs1ElementString.TryToDigitalLink(_elementString.AsSpan(), _uriBuffer, out var written, out _) ? written : -1;

    [Benchmark(Description = "TryFromDigitalLink (span)")]
    public int FromDigitalLink() => Gs1ElementString.TryFromDigitalLink(_uri.AsSpan(), _elementStringBuffer, out var written, out _) ? written : -1;

    [Benchmark(Description = "TryRead of the URI (span)")]
    public int ReadUri() => Gs1ElementString.TryRead(_uri.AsSpan(), default, _hriBuffer, out var written, out _) ? written : -1;
}
