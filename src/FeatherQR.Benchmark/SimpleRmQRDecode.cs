using BenchmarkDotNet.Configs;

/// <summary>
/// Cross-library rMQR decoding, over the same three symbols as
/// <see cref="SimpleRmQREncode"/>. CodeGlyphX is the only other library compared in this
/// project that reads rMQR at all.
///
/// Comparability notes:
///
///   Matrix decoding only. CodeGlyphX does not decode rMQR from an image, so there is no
///   image row to compare.
///   Both libraries start from the same quiet-zone-free modules.
///   The rows use this library's string-returning overload, not the caller-buffer one, so
///   both libraries allocate their result the same way.
///   Every decoder is verified in setup to return the original text, so no row can win by
///   failing fast.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class SimpleRmQRDecode
{
    private byte[] _numericModules = default!;
    private byte[] _alphanumericModules = default!;
    private byte[] _byteModules = default!;
    private (int width, int height) _numericExtent;
    private (int width, int height) _alphanumericExtent;
    private (int width, int height) _byteExtent;
    private CodeGlyphX.BitMatrix _numericMatrix = default!;
    private CodeGlyphX.BitMatrix _alphanumericMatrix = default!;
    private CodeGlyphX.BitMatrix _byteMatrix = default!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        (_numericModules, _numericExtent) = BuildModules(RmQRPayloads.Numeric, RmQRVersion.R7x43);
        (_alphanumericModules, _alphanumericExtent) = BuildModules(RmQRPayloads.Alphanumeric, RmQRVersion.R11x59);
        (_byteModules, _byteExtent) = BuildModules(RmQRPayloads.Byte, RmQRVersion.R17x139);

        _numericMatrix = GlyphBitMatrix.From(_numericModules, _numericExtent.width, _numericExtent.height);
        _alphanumericMatrix = GlyphBitMatrix.From(_alphanumericModules, _alphanumericExtent.width, _alphanumericExtent.height);
        _byteMatrix = GlyphBitMatrix.From(_byteModules, _byteExtent.width, _byteExtent.height);

        VerifyEveryDecoderSucceeds();
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("FeatherQR")]
    public string SkiaSharpQrCode_Numeric_R7x43_Decode()
    {
        RmQRCodeDecoder.TryDecode(_numericModules, _numericExtent.width, _numericExtent.height, out var text, out _);
        return text;
    }

    [Benchmark]
    [BenchmarkCategory("FeatherQR")]
    public string SkiaSharpQrCode_Alphanumeric_R11x59_Decode()
    {
        RmQRCodeDecoder.TryDecode(_alphanumericModules, _alphanumericExtent.width, _alphanumericExtent.height, out var text, out _);
        return text;
    }

    [Benchmark]
    [BenchmarkCategory("FeatherQR")]
    public string SkiaSharpQrCode_Byte_R17x139_Decode()
    {
        RmQRCodeDecoder.TryDecode(_byteModules, _byteExtent.width, _byteExtent.height, out var text, out _);
        return text;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("CodeGlyphX")]
    public string CodeGlyphX_Numeric_R7x43_Decode()
    {
        CodeGlyphX.RmQrDecoder.TryDecode(_numericMatrix, out var decoded);
        return decoded.Text;
    }

    [Benchmark]
    [BenchmarkCategory("CodeGlyphX")]
    public string CodeGlyphX_Alphanumeric_R11x59_Decode()
    {
        CodeGlyphX.RmQrDecoder.TryDecode(_alphanumericMatrix, out var decoded);
        return decoded.Text;
    }

    [Benchmark]
    [BenchmarkCategory("CodeGlyphX")]
    public string CodeGlyphX_Byte_R17x139_Decode()
    {
        CodeGlyphX.RmQrDecoder.TryDecode(_byteMatrix, out var decoded);
        return decoded.Text;
    }

    private void VerifyEveryDecoderSucceeds()
    {
        Check(nameof(SkiaSharpQrCode_Numeric_R7x43_Decode), RmQRPayloads.Numeric, SkiaSharpQrCode_Numeric_R7x43_Decode());
        Check(nameof(SkiaSharpQrCode_Alphanumeric_R11x59_Decode), RmQRPayloads.Alphanumeric, SkiaSharpQrCode_Alphanumeric_R11x59_Decode());
        Check(nameof(SkiaSharpQrCode_Byte_R17x139_Decode), RmQRPayloads.Byte, SkiaSharpQrCode_Byte_R17x139_Decode());
        Check(nameof(CodeGlyphX_Numeric_R7x43_Decode), RmQRPayloads.Numeric, CodeGlyphX_Numeric_R7x43_Decode());
        Check(nameof(CodeGlyphX_Alphanumeric_R11x59_Decode), RmQRPayloads.Alphanumeric, CodeGlyphX_Alphanumeric_R11x59_Decode());
        Check(nameof(CodeGlyphX_Byte_R17x139_Decode), RmQRPayloads.Byte, CodeGlyphX_Byte_R17x139_Decode());

        static void Check(string path, string expected, string? decoded)
        {
            if (decoded != expected)
            {
                throw new InvalidOperationException(
                    $"{path} did not round-trip: expected \"{expected}\", got \"{decoded ?? "<null>"}\".");
            }
        }
    }

    private static (byte[] modules, (int width, int height) extent) BuildModules(string content, RmQRVersion version)
    {
        var options = new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = 0 };
        var calculated = Sizing.Required(content.AsSpan(), RmQREccLevel.M, options);
        var buffer = new byte[calculated.BufferSize];
        RmQRCodeGenerator.Create(content.AsSpan(), RmQREccLevel.M, buffer, options);
        return (buffer, (calculated.Width, calculated.Height));
    }
}
