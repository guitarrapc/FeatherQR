/// <summary>
/// End-to-end Micro QR matrix decoding through the public API (MicroQRCodeDecoder).
/// Payloads mirror MicroQREncode so encode and decode costs are directly comparable; a Standard QR v1 decode of the same numeric payload gives the scale reference.
/// Matrices are quiet-zone-free (the decoder's in-place fast path).
///
/// Scenarios:
///   Numeric_M2_L : M2-L (numeric capacity boundary)
///   Alphanumeric_M3_L : M3-L (alphanumeric capacity boundary)
///   Byte_M4_M : M4-M (byte capacity boundary)
/// </summary>
public class MicroQRDecodeEndToEnd
{
    private byte[] _numericModules = default!;
    private int _numericSize;
    private byte[] _alphanumericModules = default!;
    private int _alphanumericSize;
    private byte[] _byteModules = default!;
    private int _byteSize;
    private byte[] _standardModules = default!;
    private int _standardSize;
    private char[] _chars = default!;
    private char[] _standardChars = default!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        (_numericModules, _numericSize) = BuildMicro("0123456789", MicroQREccLevel.L);          // M2-L
        (_alphanumericModules, _alphanumericSize) = BuildMicro("HELLO WORLD 14", MicroQREccLevel.L); // M3-L
        (_byteModules, _byteSize) = BuildMicro("bytes m4 mode", MicroQREccLevel.M);             // M4-M
        _chars = new char[MicroQRCodeDecoder.GetMaxDecodedLength(MicroQRVersion.M4)];

        var calculated = Sizing.Required("0123456789", QREccLevel.L, 0);
        _standardModules = new byte[calculated.BufferSize];
        FeatherQR.QRCodeGenerator.Create("0123456789", QREccLevel.L, _standardModules, new QRCodeGeneratorOptions { QuietZoneSize = 0 });
        _standardSize = calculated.Size;
        _standardChars = new char[QRCodeDecoder.GetMaxDecodedLength(1)];
    }

    // String path (allocates the result string only)

    [Benchmark]
    public string MicroQR_Numeric_M2_Decode()
    {
        MicroQRCodeDecoder.TryDecode(_numericModules, _numericSize, out var text, out _);
        return text;
    }

    [Benchmark]
    public string MicroQR_Alphanumeric_M3_Decode()
    {
        MicroQRCodeDecoder.TryDecode(_alphanumericModules, _alphanumericSize, out var text, out _);
        return text;
    }

    [Benchmark]
    public string MicroQR_Byte_M4_Decode()
    {
        MicroQRCodeDecoder.TryDecode(_byteModules, _byteSize, out var text, out _);
        return text;
    }

    // Span destination (zero-allocation) path

    [Benchmark(Baseline = true, Description = "MicroQR_Numeric_M2_Decode (Span)")]
    public int MicroQR_Numeric_M2_DecodeSpan()
    {
        MicroQRCodeDecoder.TryDecode(_numericModules, _numericSize, _chars, out var written, out _);
        return written;
    }

    [Benchmark(Description = "MicroQR_Alphanumeric_M3_Decode (Span)")]
    public int MicroQR_Alphanumeric_M3_DecodeSpan()
    {
        MicroQRCodeDecoder.TryDecode(_alphanumericModules, _alphanumericSize, _chars, out var written, out _);
        return written;
    }

    [Benchmark(Description = "MicroQR_Byte_M4_Decode (Span)")]
    public int MicroQR_Byte_M4_DecodeSpan()
    {
        MicroQRCodeDecoder.TryDecode(_byteModules, _byteSize, _chars, out var written, out _);
        return written;
    }

    // Standard QR version 1 with the same numeric payload, for scale reference.

    [Benchmark(Description = "StandardQr_Numeric_V1_Decode (Span)")]
    public int StandardQr_Numeric_V1_DecodeSpan()
    {
        QRCodeDecoder.TryDecode(_standardModules, _standardSize, _standardChars, out var written, out _);
        return written;
    }

    private static (byte[] modules, int size) BuildMicro(string content, MicroQREccLevel eccLevel)
    {
        var calculated = Sizing.Required(content.AsSpan(), eccLevel, 0);
        var buffer = new byte[calculated.BufferSize];
        MicroQRCodeGenerator.Create(content.AsSpan(), eccLevel, buffer, new MicroQRCodeGeneratorOptions { QuietZoneSize = 0 });
        return (buffer, calculated.Size);
    }
}
