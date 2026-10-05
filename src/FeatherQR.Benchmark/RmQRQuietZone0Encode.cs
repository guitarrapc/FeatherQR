/// <summary>
/// Quiet-zone-free span encodes of the <see cref="RmQREncodeEndToEnd"/> shapes, for performance work rather than the default path a caller takes.
/// The matrix is the one <see cref="RmQRDecodeEndToEnd"/> decodes, and the quiet zone's cost is the gap to the "(Span)" row of the same name there.
/// </summary>
public class RmQRQuietZone0Encode
{
    private string _numeric = default!;
    private string _alphanumeric = default!;
    private string _byte = default!;
    private string _latin1 = default!;
    private string _utf8 = default!;
    private string _kanji = default!;
    private byte[] _spanDestination = default!;

    private static readonly RmQRCodeGeneratorOptions NoQuietZone = new() { QuietZoneSize = 0 };
    private static readonly RmQRCodeGeneratorOptions Latin1NoQuietZone = new() { EciMode = EciMode.Iso8859_1, QuietZoneSize = 0 };
    private static readonly RmQRCodeGeneratorOptions Utf8NoQuietZone = new() { EciMode = EciMode.Utf8, QuietZoneSize = 0 };
    private static readonly RmQRCodeGeneratorOptions KanjiNoQuietZone = new() { AllowKanji = true, QuietZoneSize = 0 };

    [GlobalSetup]
    public void GlobalSetup()
    {
        _numeric = "012345678901";                                     // R11x27-M
        _alphanumeric = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 $%*+-.";   // R15x43-M
        _byte = string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog?! ", 4)).Substring(0, 150); // R17x139-M
        _latin1 = string.Concat(Enumerable.Repeat("Café déjà vu. ", 8));       // R15x139-M
        _utf8 = string.Concat(Enumerable.Repeat("日本語QRコード", 5));         // R13x139-M
        _kanji = string.Concat(Enumerable.Repeat("吾輩は猫である。名前はまだ無い。", 6)).Substring(0, 92); // R17x139-M
        _spanDestination = new byte[Sizing.Required(_byte.AsSpan(), RmQREccLevel.M, NoQuietZone).BufferSize];
    }

    [Benchmark(Baseline = true, Description = "RmQR_Numeric_R11x27_Encode (Span, QZ0)")]
    public int RmQR_Numeric_R11x27_EncodeSpanNoQuietZone()
    {
        return RmQRCodeGenerator.Create(_numeric.AsSpan(), RmQREccLevel.M, _spanDestination, NoQuietZone);
    }

    [Benchmark(Description = "RmQR_Alphanumeric_R15x43_Encode (Span, QZ0)")]
    public int RmQR_Alphanumeric_R15x43_EncodeSpanNoQuietZone()
    {
        return RmQRCodeGenerator.Create(_alphanumeric.AsSpan(), RmQREccLevel.M, _spanDestination, NoQuietZone);
    }

    [Benchmark(Description = "RmQR_Byte_R17x139_Encode (Span, QZ0)")]
    public int RmQR_Byte_R17x139_EncodeSpanNoQuietZone()
    {
        return RmQRCodeGenerator.Create(_byte.AsSpan(), RmQREccLevel.M, _spanDestination, NoQuietZone);
    }

    [Benchmark(Description = "RmQR_Latin1Eci_R15x139_Encode (Span, QZ0)")]
    public int RmQR_Latin1Eci_R15x139_EncodeSpanNoQuietZone()
    {
        return RmQRCodeGenerator.Create(_latin1.AsSpan(), RmQREccLevel.M, _spanDestination, Latin1NoQuietZone);
    }

    [Benchmark(Description = "RmQR_Utf8Eci_R13x139_Encode (Span, QZ0)")]
    public int RmQR_Utf8Eci_R13x139_EncodeSpanNoQuietZone()
    {
        return RmQRCodeGenerator.Create(_utf8.AsSpan(), RmQREccLevel.M, _spanDestination, Utf8NoQuietZone);
    }

    [Benchmark(Description = "RmQR_Kanji_R17x139_Encode (Span, QZ0)")]
    public int RmQR_Kanji_R17x139_EncodeSpanNoQuietZone()
    {
        return RmQRCodeGenerator.Create(_kanji.AsSpan(), RmQREccLevel.M, _spanDestination, KanjiNoQuietZone);
    }
}
