using CodeGlyphX.Gs1Data;
using FeatherQR.Gs1;

/// <summary>
/// Measures GS1 validation, HRI and Digital Link conversion, and decode-side interpretation.
/// </summary>
/// <remarks>
/// Span rows use destinations sized in advance, so their allocations belong to the data layer.
/// String rows also allocate the returned string. CodeGlyphX validation is a comparison, not a baseline: it additionally builds an element list.
/// Retail, logistics and variable-measure data exercise different AI combinations.
/// </remarks>
public class Gs1DataLayer
{
    private static readonly string[] shapeKeys = ["retail", "logistics", "weighed"];

    private string _elementString = default!;
    private string _hri = default!;
    private string _uri = default!;
    private char[] _hriBuffer = default!;
    private char[] _fromHriBuffer = default!;
    private char[] _uriBuffer = default!;
    private char[] _fromUriBuffer = default!;
    private char[] _readBuffer = default!;

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

        // Each call has its own buffer, of the length its twin gives. The setup's own texts cannot size them,
        // because the element string read back from a URI keeps the URI's element order. For the retail label
        // that order needs a separator the original element string does not.
        _hriBuffer = Sized(Gs1ElementString.TryGetToHriLength(_elementString, out var length, out _), length, "TryGetToHriLength");
        _fromHriBuffer = Sized(Gs1ElementString.TryGetFromHriLength(_hri, out length, out _), length, "TryGetFromHriLength");
        _uriBuffer = Sized(Gs1ElementString.TryGetToDigitalLinkLength(_elementString, out length, out _), length, "TryGetToDigitalLinkLength");
        _fromUriBuffer = Sized(Gs1ElementString.TryGetFromDigitalLinkLength(_uri, out length, out _), length, "TryGetFromDigitalLinkLength");
        _readBuffer = Sized(Gs1ElementString.TryGetReadLength(_uri, default, out length, out _), length, "TryGetReadLength");

        // A call that answers false would have its refusal timed, so each call that writes into a buffer is made
        // once here. TryValidate's row is the call checked above.
        Require(ToHri() >= 0, "TryToHri");
        Require(ToHriString() >= 0, "TryToHri (string)");
        Require(FromHri() >= 0, "TryFromHri");
        Require(ToDigitalLink() >= 0, "TryToDigitalLink");
        Require(ToDigitalLinkString() >= 0, "TryToDigitalLink (string)");
        Require(FromDigitalLink() >= 0, "TryFromDigitalLink");
        Require(ReadUri() >= 0, "TryRead of the URI");
    }

    private char[] Sized(bool answered, int length, string twin)
    {
        Require(answered, twin);
        return new char[length];
    }

    private void Require(bool succeeded, string call)
    {
        if (!succeeded)
            throw new InvalidOperationException($"{call} fails on shape {Shape}");
    }

    [Benchmark(Description = "TryValidate")]
    public bool Validate() => Gs1ElementString.TryValidate(_elementString.AsSpan(), out _);

    [Benchmark(Description = "CodeGlyphX Gs1Validator.Validate on the (AI) text")]
    public bool CodeGlyphXValidate() => Gs1Validator.Validate(_hri).IsValid;

    [Benchmark(Description = "TryToHri (span)")]
    public int ToHri() => Gs1ElementString.TryToHri(_elementString.AsSpan(), _hriBuffer, out var written, out _) ? written : -1;

    [Benchmark(Description = "TryToHri (string)")]
    public int ToHriString() => Gs1ElementString.TryToHri(_elementString.AsSpan(), out var hri, out _) ? hri.Length : -1;

    [Benchmark(Description = "TryFromHri (span)")]
    public int FromHri() => Gs1ElementString.TryFromHri(_hri.AsSpan(), _fromHriBuffer, out var written, out _) ? written : -1;

    [Benchmark(Description = "TryToDigitalLink (span)")]
    public int ToDigitalLink() => Gs1ElementString.TryToDigitalLink(_elementString.AsSpan(), _uriBuffer, out var written, out _) ? written : -1;

    [Benchmark(Description = "TryToDigitalLink (string)")]
    public int ToDigitalLinkString() => Gs1ElementString.TryToDigitalLink(_elementString.AsSpan(), out var uri, out _) ? uri.Length : -1;

    [Benchmark(Description = "TryFromDigitalLink (span)")]
    public int FromDigitalLink() => Gs1ElementString.TryFromDigitalLink(_uri.AsSpan(), _fromUriBuffer, out var written, out _) ? written : -1;

    [Benchmark(Description = "TryRead of the URI (span)")]
    public int ReadUri() => Gs1ElementString.TryRead(_uri.AsSpan(), default, _readBuffer, out var written, out _) ? written : -1;
}
