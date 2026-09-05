using BenchmarkDotNet.Configs;

/// <summary>
/// Cross-library Micro QR decoding, over the same three symbols as
/// <see cref="SimpleMicroQREncode"/>. CodeGlyphX is the only other library compared in
/// this project that decodes Micro QR at all: QRCoder encodes it but does not read it.
///
/// Comparability notes:
///
///   Matrix decoding only. Neither library's Micro QR image path is measured here.
///   Both libraries start from the same quiet-zone-free modules.
///   The rows use this library's string-returning overload, not the caller-buffer one, so
///   both libraries allocate their result the same way.
///   Every decoder is verified in setup to return the original text, so no row can win by
///   failing fast.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class SimpleMicroQRDecode
{
    private byte[] _numericModules = default!;
    private byte[] _alphanumericModules = default!;
    private byte[] _byteModules = default!;
    private int _numericSize;
    private int _alphanumericSize;
    private int _byteSize;
    private CodeGlyphX.BitMatrix _numericMatrix = default!;
    private CodeGlyphX.BitMatrix _alphanumericMatrix = default!;
    private CodeGlyphX.BitMatrix _byteMatrix = default!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        (_numericModules, _numericSize) = BuildModules(MicroQRPayloads.Numeric, MicroQREccLevel.L);
        (_alphanumericModules, _alphanumericSize) = BuildModules(MicroQRPayloads.Alphanumeric, MicroQREccLevel.L);
        (_byteModules, _byteSize) = BuildModules(MicroQRPayloads.Byte, MicroQREccLevel.M);

        _numericMatrix = GlyphBitMatrix.From(_numericModules, _numericSize, _numericSize);
        _alphanumericMatrix = GlyphBitMatrix.From(_alphanumericModules, _alphanumericSize, _alphanumericSize);
        _byteMatrix = GlyphBitMatrix.From(_byteModules, _byteSize, _byteSize);

        VerifyEveryDecoderSucceeds();
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("FeatherQR")]
    public string SkiaSharpQrCode_Numeric_M2_Decode()
    {
        MicroQRCodeDecoder.TryDecode(_numericModules, _numericSize, out var text, out _);
        return text;
    }

    [Benchmark]
    [BenchmarkCategory("FeatherQR")]
    public string SkiaSharpQrCode_Alphanumeric_M3_Decode()
    {
        MicroQRCodeDecoder.TryDecode(_alphanumericModules, _alphanumericSize, out var text, out _);
        return text;
    }

    [Benchmark]
    [BenchmarkCategory("FeatherQR")]
    public string SkiaSharpQrCode_Byte_M4_Decode()
    {
        MicroQRCodeDecoder.TryDecode(_byteModules, _byteSize, out var text, out _);
        return text;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("CodeGlyphX")]
    public string CodeGlyphX_Numeric_M2_Decode()
    {
        CodeGlyphX.MicroQrDecoder.TryDecode(_numericMatrix, out var decoded);
        return decoded.Text;
    }

    [Benchmark]
    [BenchmarkCategory("CodeGlyphX")]
    public string CodeGlyphX_Alphanumeric_M3_Decode()
    {
        CodeGlyphX.MicroQrDecoder.TryDecode(_alphanumericMatrix, out var decoded);
        return decoded.Text;
    }

    [Benchmark]
    [BenchmarkCategory("CodeGlyphX")]
    public string CodeGlyphX_Byte_M4_Decode()
    {
        CodeGlyphX.MicroQrDecoder.TryDecode(_byteMatrix, out var decoded);
        return decoded.Text;
    }

    private void VerifyEveryDecoderSucceeds()
    {
        Check(nameof(SkiaSharpQrCode_Numeric_M2_Decode), MicroQRPayloads.Numeric, SkiaSharpQrCode_Numeric_M2_Decode());
        Check(nameof(SkiaSharpQrCode_Alphanumeric_M3_Decode), MicroQRPayloads.Alphanumeric, SkiaSharpQrCode_Alphanumeric_M3_Decode());
        Check(nameof(SkiaSharpQrCode_Byte_M4_Decode), MicroQRPayloads.Byte, SkiaSharpQrCode_Byte_M4_Decode());
        Check(nameof(CodeGlyphX_Numeric_M2_Decode), MicroQRPayloads.Numeric, CodeGlyphX_Numeric_M2_Decode());
        Check(nameof(CodeGlyphX_Alphanumeric_M3_Decode), MicroQRPayloads.Alphanumeric, CodeGlyphX_Alphanumeric_M3_Decode());
        Check(nameof(CodeGlyphX_Byte_M4_Decode), MicroQRPayloads.Byte, CodeGlyphX_Byte_M4_Decode());

        static void Check(string path, string expected, string? decoded)
        {
            if (decoded != expected)
            {
                throw new InvalidOperationException(
                    $"{path} did not round-trip: expected \"{expected}\", got \"{decoded ?? "<null>"}\".");
            }
        }
    }

    private static (byte[] modules, int size) BuildModules(string content, MicroQREccLevel eccLevel)
    {
        var calculated = Sizing.Required(content.AsSpan(), eccLevel, 0);
        var buffer = new byte[calculated.BufferSize];
        MicroQRCodeGenerator.Create(content.AsSpan(), eccLevel, buffer, new MicroQRCodeGeneratorOptions { QuietZoneSize = 0 });
        return (buffer, calculated.Size);
    }
}
