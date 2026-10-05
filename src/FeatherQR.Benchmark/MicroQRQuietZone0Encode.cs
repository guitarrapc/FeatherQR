/// <summary>
/// Quiet-zone-free span encodes of the <see cref="MicroQREncodeEndToEnd"/> shapes, for performance work rather than the default path a caller takes.
/// The matrix is the one <see cref="MicroQRDecodeEndToEnd"/> decodes, and the quiet zone's cost is the gap to the "(Span)" row of the same name there.
/// </summary>
public class MicroQRQuietZone0Encode
{
    private string _numeric = default!;
    private string _alphanumeric = default!;
    private string _byte = default!;
    private string _kanji = default!;
    private byte[] _spanDestination = default!;

    private static readonly MicroQRCodeGeneratorOptions NoQuietZone = new() { QuietZoneSize = 0 };
    private static readonly MicroQRCodeGeneratorOptions KanjiNoQuietZone = new() { AllowKanji = true, QuietZoneSize = 0 };

    [GlobalSetup]
    public void GlobalSetup()
    {
        _numeric = "0123456789";        // M2-L
        _alphanumeric = "HELLO WORLD 14"; // M3-L
        _byte = "bytes m4 mode";        // M4-M
        _kanji = "吾輩は猫である。";      // M4-M
        _spanDestination = new byte[Sizing.Required(_byte.AsSpan(), MicroQREccLevel.M, NoQuietZone).BufferSize];
    }

    [Benchmark(Baseline = true, Description = "MicroQR_Numeric_M2_Encode (Span, QZ0)")]
    public int MicroQR_Numeric_M2_EncodeSpanNoQuietZone()
    {
        return MicroQRCodeGenerator.Create(_numeric.AsSpan(), MicroQREccLevel.L, _spanDestination, NoQuietZone);
    }

    [Benchmark(Description = "MicroQR_Alphanumeric_M3_Encode (Span, QZ0)")]
    public int MicroQR_Alphanumeric_M3_EncodeSpanNoQuietZone()
    {
        return MicroQRCodeGenerator.Create(_alphanumeric.AsSpan(), MicroQREccLevel.L, _spanDestination, NoQuietZone);
    }

    [Benchmark(Description = "MicroQR_Byte_M4_Encode (Span, QZ0)")]
    public int MicroQR_Byte_M4_EncodeSpanNoQuietZone()
    {
        return MicroQRCodeGenerator.Create(_byte.AsSpan(), MicroQREccLevel.M, _spanDestination, NoQuietZone);
    }

    [Benchmark(Description = "MicroQR_Kanji_M4_Encode (Span, QZ0)")]
    public int MicroQR_Kanji_M4_EncodeSpanNoQuietZone()
    {
        return MicroQRCodeGenerator.Create(_kanji.AsSpan(), MicroQREccLevel.M, _spanDestination, KanjiNoQuietZone);
    }
}
