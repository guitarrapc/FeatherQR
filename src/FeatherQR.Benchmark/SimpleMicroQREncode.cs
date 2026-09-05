using BenchmarkDotNet.Configs;

/// <summary>
/// Cross-library Micro QR encoding. Micro QR narrows the field sharply: of the libraries
/// compared elsewhere in this project only QRCoder and CodeGlyphX support it at all.
///
/// Payloads sit on the capacity boundary of three versions, and every library selects the
/// same version for each (M2-L numeric, M3-L alphanumeric, M4-M byte), so the rows encode
/// the same symbol.
///
/// Comparability notes:
///
///   FeatherQR and QRCoder place a quiet zone in the returned matrix; CodeGlyphX
///   returns the bare symbol.
///   FeatherQR picks the encoding mode from the payload, while CodeGlyphX exposes
///   one entry point per mode, so each row calls the CodeGlyphX method for the mode this
///   library would have chosen.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class SimpleMicroQREncode
{
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
