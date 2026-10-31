using BenchmarkDotNet.Configs;

/// <summary>
/// Compares Micro QR encoding with QRCoder and CodeGlyphX at matching symbol sizes.
/// </summary>
/// <remarks>
/// Every library selects the same version for each capacity-boundary payload.
/// FeatherQR and QRCoder include a quiet zone. CodeGlyphX returns the bare symbol and requires a mode-specific entry point.
/// Pinned rows measure the version-constrained calls used by tools/CrossLanguageBenchmark.
/// </remarks>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class SimpleMicroQREncode
{
    private static readonly MicroQRCodeGeneratorOptions VersionM2 = new() { Version = MicroQRVersion.M2 };
    private static readonly MicroQRCodeGeneratorOptions VersionM3 = new() { Version = MicroQRVersion.M3 };
    private static readonly MicroQRCodeGeneratorOptions VersionM4 = new() { Version = MicroQRVersion.M4 };

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("FeatherQR")]
    public MicroQRCodeData SkiaSharpQrCode_Numeric_M2_Encode()
        => MicroQRCodeGenerator.Create(MicroQRPayloads.Numeric.AsSpan(), MicroQREccLevel.L);

    [Benchmark]
    [BenchmarkCategory("FeatherQR")]
    public MicroQRCodeData SkiaSharpQrCode_Alphanumeric_M3_Encode()
        => MicroQRCodeGenerator.Create(MicroQRPayloads.Alphanumeric.AsSpan(), MicroQREccLevel.L);

    [Benchmark]
    [BenchmarkCategory("FeatherQR")]
    public MicroQRCodeData SkiaSharpQrCode_Byte_M4_Encode()
        => MicroQRCodeGenerator.Create(MicroQRPayloads.Byte.AsSpan(), MicroQREccLevel.M);

    [Benchmark(Description = "SkiaSharpQrCode_Numeric_M2_Encode (Pinned)")]
    [BenchmarkCategory("FeatherQR")]
    public MicroQRCodeData SkiaSharpQrCode_Numeric_M2_EncodePinned()
        => MicroQRCodeGenerator.Create(MicroQRPayloads.Numeric.AsSpan(), MicroQREccLevel.L, VersionM2);

    [Benchmark(Description = "SkiaSharpQrCode_Alphanumeric_M3_Encode (Pinned)")]
    [BenchmarkCategory("FeatherQR")]
    public MicroQRCodeData SkiaSharpQrCode_Alphanumeric_M3_EncodePinned()
        => MicroQRCodeGenerator.Create(MicroQRPayloads.Alphanumeric.AsSpan(), MicroQREccLevel.L, VersionM3);

    [Benchmark(Description = "SkiaSharpQrCode_Byte_M4_Encode (Pinned)")]
    [BenchmarkCategory("FeatherQR")]
    public MicroQRCodeData SkiaSharpQrCode_Byte_M4_EncodePinned()
        => MicroQRCodeGenerator.Create(MicroQRPayloads.Byte.AsSpan(), MicroQREccLevel.M, VersionM4);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("CodeGlyphX")]
    public CodeGlyphX.MicroQrCode CodeGlyphX_Numeric_M2_Encode()
        => CodeGlyphX.MicroQrCodeEncoder.EncodeNumeric(MicroQRPayloads.Numeric, CodeGlyphX.QrErrorCorrectionLevel.L);

    [Benchmark]
    [BenchmarkCategory("CodeGlyphX")]
    public CodeGlyphX.MicroQrCode CodeGlyphX_Alphanumeric_M3_Encode()
        => CodeGlyphX.MicroQrCodeEncoder.EncodeAlphanumeric(MicroQRPayloads.Alphanumeric, CodeGlyphX.QrErrorCorrectionLevel.L);

    [Benchmark]
    [BenchmarkCategory("CodeGlyphX")]
    public CodeGlyphX.MicroQrCode CodeGlyphX_Byte_M4_Encode()
        => CodeGlyphX.MicroQrCodeEncoder.EncodeText(MicroQRPayloads.Byte, CodeGlyphX.QrTextEncoding.Latin1, CodeGlyphX.QrErrorCorrectionLevel.M);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("QRCoder")]
    public QRCoder.QRCodeData QRCoder_Numeric_M2_Encode()
        => QRCoder.QRCodeGenerator.GenerateMicroQrCode(MicroQRPayloads.Numeric, QRCoder.QRCodeGenerator.ECCLevel.L);

    [Benchmark]
    [BenchmarkCategory("QRCoder")]
    public QRCoder.QRCodeData QRCoder_Alphanumeric_M3_Encode()
        => QRCoder.QRCodeGenerator.GenerateMicroQrCode(MicroQRPayloads.Alphanumeric, QRCoder.QRCodeGenerator.ECCLevel.L);

    [Benchmark]
    [BenchmarkCategory("QRCoder")]
    public QRCoder.QRCodeData QRCoder_Byte_M4_Encode()
        => QRCoder.QRCodeGenerator.GenerateMicroQrCode(MicroQRPayloads.Byte, QRCoder.QRCodeGenerator.ECCLevel.M);
}
