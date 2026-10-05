/// <summary>
/// End-to-end rMQR matrix encoding through the public API (RmQRCodeGenerator).
/// Used to measure the user-visible impact of internal kernel changes such as the Reed-Solomon ECC encoder optimization.
///
/// Scenarios (level M, version fitted by the default MinimizeArea strategy):
///   Numeric_R11x27 : smallest symbol, 12 digits (R7x43's capacity, which the area fit puts in R11x27: 297 modules against 301)
///   Alphanumeric_R15x43 : 43 characters (R11x59's capacity, which the area fit puts in R15x43: 645 modules against 649)
///   Byte_R17x139 : largest symbol, 150 bytes (capacity boundary, 4 RS blocks)
///   Latin1Eci_R15x139 : explicit ECI 3 Byte segment
///   Utf8Eci_R13x139 : explicit ECI 26 Byte segment
///   Kanji_R17x139 : largest symbol, Kanji mode (capacity boundary, 92 characters)
///
/// The "(Pinned)" rows resolve the version before the pipeline, the path tools/CrossLanguageBenchmark takes on every encode.
/// Mixed-mode segmentation has its own class (<see cref="RmQRSegmentationEncode"/>): it varies content shape rather than version, and every row needs a same-run Single pair, which does not belong in this table.
/// </summary>
public class RmQREncodeEndToEnd
{
    private string _numeric = default!;
    private string _alphanumeric = default!;
    private string _byte = default!;
    private string _latin1 = default!;
    private string _utf8 = default!;
    private string _kanji = default!;
    private byte[] _spanDestination = default!;

    private static readonly RmQRCodeGeneratorOptions VersionR11x27 = new() { Version = RmQRVersion.R11x27 };
    private static readonly RmQRCodeGeneratorOptions VersionR17x139 = new() { Version = RmQRVersion.R17x139 };
    private static readonly RmQRCodeGeneratorOptions KanjiVersionR17x139 = new() { AllowKanji = true, Version = RmQRVersion.R17x139 };

    [GlobalSetup]
    public void GlobalSetup()
    {
        _numeric = "012345678901";                                     // R11x27-M (R7x43-M numeric boundary)
        _alphanumeric = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 $%*+-.";   // 43 chars: R15x43-M (R11x59-M alphanumeric boundary)
        _byte = string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog?! ", 4)).Substring(0, 150); // R17x139-M byte boundary
        _latin1 = string.Concat(Enumerable.Repeat("Café déjà vu. ", 8));       // R15x139-M
        _utf8 = string.Concat(Enumerable.Repeat("日本語QRコード", 5));         // R13x139-M
        _kanji = string.Concat(Enumerable.Repeat("吾輩は猫である。名前はまだ無い。", 6)).Substring(0, 92); // R17x139-M Kanji boundary
        _spanDestination = new byte[Sizing.Required(_byte.AsSpan(), RmQREccLevel.M).BufferSize];
    }

    // Class API (allocates the result object only)

    [Benchmark(Baseline = true)]
    public RmQRCodeData RmQR_Numeric_R11x27_Encode()
    {
        return RmQRCodeGenerator.Create(_numeric.AsSpan(), RmQREccLevel.M);
    }

    [Benchmark]
    public RmQRCodeData RmQR_Alphanumeric_R15x43_Encode()
    {
        return RmQRCodeGenerator.Create(_alphanumeric.AsSpan(), RmQREccLevel.M);
    }

    [Benchmark]
    public RmQRCodeData RmQR_Byte_R17x139_Encode()
    {
        return RmQRCodeGenerator.Create(_byte.AsSpan(), RmQREccLevel.M);
    }

    [Benchmark]
    public RmQRCodeData RmQR_Latin1Eci_R15x139_Encode()
    {
        return RmQRCodeGenerator.Create(_latin1.AsSpan(), RmQREccLevel.M, new RmQRCodeGeneratorOptions { EciMode = EciMode.Iso8859_1 });
    }

    [Benchmark]
    public RmQRCodeData RmQR_Utf8Eci_R13x139_Encode()
    {
        return RmQRCodeGenerator.Create(_utf8.AsSpan(), RmQREccLevel.M, new RmQRCodeGeneratorOptions { EciMode = EciMode.Utf8 });
    }

    [Benchmark]
    public RmQRCodeData RmQR_Kanji_R17x139_Encode()
    {
        return RmQRCodeGenerator.Create(_kanji.AsSpan(), RmQREccLevel.M, new RmQRCodeGeneratorOptions { AllowKanji = true });
    }

    // Span destination (zero-allocation) variants

    [Benchmark(Description = "RmQR_Numeric_R11x27_Encode (Span)")]
    public int RmQR_Numeric_R11x27_EncodeSpan()
    {
        return RmQRCodeGenerator.Create(_numeric.AsSpan(), RmQREccLevel.M, _spanDestination);
    }

    [Benchmark(Description = "RmQR_Alphanumeric_R15x43_Encode (Span)")]
    public int RmQR_Alphanumeric_R15x43_EncodeSpan()
    {
        return RmQRCodeGenerator.Create(_alphanumeric.AsSpan(), RmQREccLevel.M, _spanDestination);
    }

    [Benchmark(Description = "RmQR_Byte_R17x139_Encode (Span)")]
    public int RmQR_Byte_R17x139_EncodeSpan()
    {
        return RmQRCodeGenerator.Create(_byte.AsSpan(), RmQREccLevel.M, _spanDestination);
    }

    [Benchmark(Description = "RmQR_Latin1Eci_R15x139_Encode (Span)")]
    public int RmQR_Latin1Eci_R15x139_EncodeSpan()
    {
        return RmQRCodeGenerator.Create(_latin1.AsSpan(), RmQREccLevel.M, _spanDestination, new RmQRCodeGeneratorOptions { EciMode = EciMode.Iso8859_1 });
    }

    [Benchmark(Description = "RmQR_Utf8Eci_R13x139_Encode (Span)")]
    public int RmQR_Utf8Eci_R13x139_EncodeSpan()
    {
        return RmQRCodeGenerator.Create(_utf8.AsSpan(), RmQREccLevel.M, _spanDestination, new RmQRCodeGeneratorOptions { EciMode = EciMode.Utf8 });
    }

    [Benchmark(Description = "RmQR_Kanji_R17x139_Encode (Span)")]
    public int RmQR_Kanji_R17x139_EncodeSpan()
    {
        return RmQRCodeGenerator.Create(_kanji.AsSpan(), RmQREccLevel.M, _spanDestination, new RmQRCodeGeneratorOptions { AllowKanji = true });
    }

    // Version pinned: the same symbol as the row above it with the same name, through the path that
    // resolves the version first. The smallest symbol, the largest, and Kanji as in the other symbologies.

    [Benchmark(Description = "RmQR_Numeric_R11x27_Encode (Pinned)")]
    public RmQRCodeData RmQR_Numeric_R11x27_EncodePinned()
    {
        return RmQRCodeGenerator.Create(_numeric.AsSpan(), RmQREccLevel.M, VersionR11x27);
    }

    [Benchmark(Description = "RmQR_Byte_R17x139_Encode (Pinned)")]
    public RmQRCodeData RmQR_Byte_R17x139_EncodePinned()
    {
        return RmQRCodeGenerator.Create(_byte.AsSpan(), RmQREccLevel.M, VersionR17x139);
    }

    [Benchmark(Description = "RmQR_Kanji_R17x139_Encode (Pinned)")]
    public RmQRCodeData RmQR_Kanji_R17x139_EncodePinned()
    {
        return RmQRCodeGenerator.Create(_kanji.AsSpan(), RmQREccLevel.M, KanjiVersionR17x139);
    }
}
