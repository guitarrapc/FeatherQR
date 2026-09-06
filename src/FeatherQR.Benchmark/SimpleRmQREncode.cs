using BenchmarkDotNet.Configs;

/// <summary>
/// Cross-library rMQR encoding.
/// CodeGlyphX is the only other library compared in this project that implements ISO/IEC 23941 at all, so these are the only rMQR rows with something to compare against.
///
/// Each row pins the same version on both libraries. rMQR versions are not ordered by size, and the two libraries pick differently when left to fit automatically, so pinning is what makes the rows measure the same symbol.
/// The version numbering is the same on both sides (1 is R7x43, 32 is R17x139), which is why the cast works.
///
/// Comparability notes:
///
///   FeatherQR places a quiet zone in the returned matrix; CodeGlyphX returns the
///   bare symbol.
///   Both libraries pick one mode for the whole payload by default, so the rows do the
///   same work.
/// </summary>
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
