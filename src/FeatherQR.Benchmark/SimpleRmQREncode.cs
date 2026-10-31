using BenchmarkDotNet.Configs;

/// <summary>
/// Compares rMQR encoding with CodeGlyphX at matching pinned versions.
/// </summary>
/// <remarks>
/// Automatic fitting differs between libraries, so both encode the same pinned symbol size.
/// FeatherQR includes a quiet zone and mixed-mode planning. CodeGlyphX returns the bare symbol and selects one mode.
/// These payloads produce a single-mode stream under both encoders.
/// </remarks>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class SimpleRmQREncode
{
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("FeatherQR")]
    public RmQRCodeData SkiaSharpQrCode_Numeric_R7x43_Encode()
        => RmQRCodeGenerator.Create(RmQRPayloads.Numeric.AsSpan(), RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43 });

    [Benchmark]
    [BenchmarkCategory("FeatherQR")]
    public RmQRCodeData SkiaSharpQrCode_Alphanumeric_R11x59_Encode()
        => RmQRCodeGenerator.Create(RmQRPayloads.Alphanumeric.AsSpan(), RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R11x59 });

    [Benchmark]
    [BenchmarkCategory("FeatherQR")]
    public RmQRCodeData SkiaSharpQrCode_Byte_R17x139_Encode()
        => RmQRCodeGenerator.Create(RmQRPayloads.Byte.AsSpan(), RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R17x139 });

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("CodeGlyphX")]
    public CodeGlyphX.RmQrCode CodeGlyphX_Numeric_R7x43_Encode()
        => CodeGlyphX.RmQrCodeEncoder.EncodeText(RmQRPayloads.Numeric, GlyphOptions(RmQRVersion.R7x43));

    [Benchmark]
    [BenchmarkCategory("CodeGlyphX")]
    public CodeGlyphX.RmQrCode CodeGlyphX_Alphanumeric_R11x59_Encode()
        => CodeGlyphX.RmQrCodeEncoder.EncodeText(RmQRPayloads.Alphanumeric, GlyphOptions(RmQRVersion.R11x59));

    [Benchmark]
    [BenchmarkCategory("CodeGlyphX")]
    public CodeGlyphX.RmQrCode CodeGlyphX_Byte_R17x139_Encode()
        => CodeGlyphX.RmQrCodeEncoder.EncodeText(RmQRPayloads.Byte, GlyphOptions(RmQRVersion.R17x139));

    private static CodeGlyphX.RmQrEncodingOptions GlyphOptions(RmQRVersion version) => new()
    {
        ErrorCorrectionLevel = CodeGlyphX.QrErrorCorrectionLevel.M,
        MinimumVersion = (int)version,
        MaximumVersion = (int)version,
    };
}
