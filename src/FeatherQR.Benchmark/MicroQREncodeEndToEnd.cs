/// <summary>
/// End-to-end Micro QR matrix encoding through the public API (MicroQRCodeGenerator).
/// Used to measure the user-visible impact of internal kernel changes such as the Reed-Solomon ECC encoder optimization.
///
/// Scenarios:
///   Numeric_M2_L : M2-L (numeric capacity boundary)
///   Alphanumeric_M3_L : M3-L (alphanumeric capacity boundary)
///   Byte_M4_M : M4-M (byte capacity boundary)
///   Kanji_M4_M : M4-M, Kanji mode (capacity boundary, 8 characters)
///
/// Byte_M4_M and Kanji_M4_M share version and level, so they differ in mode, not in symbol size.
/// </summary>
public class MicroQREncodeEndToEnd
{
    // Representative payloads: numeric M2-L, alphanumeric M3-L, byte M4-M, Kanji M4-M.
    private string _numeric = default!;
    private string _alphanumeric = default!;
    private string _byte = default!;
    private string _kanji = default!;
    private byte[] _spanDestination = default!;

    private static readonly MicroQRCodeGeneratorOptions KanjiVersionM4 = new() { AllowKanji = true, Version = MicroQRVersion.M4 };

    [GlobalSetup]
    public void GlobalSetup()
    {
        _numeric = "0123456789";        // M2-L (numeric capacity boundary)
        _alphanumeric = "HELLO WORLD 14"; // M3-L (alphanumeric capacity boundary)
        _byte = "bytes m4 mode";        // M4-M (byte capacity boundary)
        _kanji = "吾輩は猫である。";      // M4-M (Kanji capacity boundary)
        _spanDestination = new byte[Sizing.Required(_byte.AsSpan(), MicroQREccLevel.M).BufferSize];
    }

    // Class API (allocates the result object only)

    [Benchmark(Baseline = true)]
    public MicroQRCodeData MicroQR_Numeric_M2_Encode()
    {
        return MicroQRCodeGenerator.Create(_numeric.AsSpan(), MicroQREccLevel.L);
    }

    [Benchmark]
    public MicroQRCodeData MicroQR_Alphanumeric_M3_Encode()
    {
        return MicroQRCodeGenerator.Create(_alphanumeric.AsSpan(), MicroQREccLevel.L);
    }

    [Benchmark]
    public MicroQRCodeData MicroQR_Byte_M4_Encode()
    {
        return MicroQRCodeGenerator.Create(_byte.AsSpan(), MicroQREccLevel.M);
    }

    [Benchmark]
    public MicroQRCodeData MicroQR_Kanji_M4_Encode()
    {
        return MicroQRCodeGenerator.Create(_kanji.AsSpan(), MicroQREccLevel.M, new MicroQRCodeGeneratorOptions { AllowKanji = true });
    }

    // Span destination (zero-allocation) variants

    [Benchmark(Description = "MicroQR_Numeric_M2_Encode (Span)")]
    public int MicroQR_Numeric_M2_EncodeSpan()
    {
        return MicroQRCodeGenerator.Create(_numeric.AsSpan(), MicroQREccLevel.L, _spanDestination);
    }

    [Benchmark(Description = "MicroQR_Alphanumeric_M3_Encode (Span)")]
    public int MicroQR_Alphanumeric_M3_EncodeSpan()
    {
        return MicroQRCodeGenerator.Create(_alphanumeric.AsSpan(), MicroQREccLevel.L, _spanDestination);
    }

    [Benchmark(Description = "MicroQR_Byte_M4_Encode (Span)")]
    public int MicroQR_Byte_M4_EncodeSpan()
    {
        return MicroQRCodeGenerator.Create(_byte.AsSpan(), MicroQREccLevel.M, _spanDestination);
    }

    [Benchmark(Description = "MicroQR_Kanji_M4_Encode (Span)")]
    public int MicroQR_Kanji_M4_EncodeSpan()
    {
        return MicroQRCodeGenerator.Create(_kanji.AsSpan(), MicroQREccLevel.M, _spanDestination, new MicroQRCodeGeneratorOptions { AllowKanji = true });
    }

    // Version pinned: the same symbol, through the path that resolves the version first (the
    // cross-language benchmark's call). Kanji is the shape whose analysis costs the most.

    [Benchmark(Description = "MicroQR_Kanji_M4_Encode (Pinned)")]
    public MicroQRCodeData MicroQR_Kanji_M4_EncodePinned()
    {
        return MicroQRCodeGenerator.Create(_kanji.AsSpan(), MicroQREccLevel.M, KanjiVersionM4);
    }
}
