/// <summary>
/// Measures Micro QR matrix decoding on the payloads of <see cref="MicroQREncodeEndToEnd"/>.
/// </summary>
/// <remarks>
/// Matrices have no quiet zone. Corrected rows verify the intended error count at the symbol's correction capacity in setup.
/// Byte_M4_M and Kanji_M4_M compare modes at the same version and ECC level.
/// </remarks>
public class MicroQRDecodeEndToEnd
{
    private byte[] _numericModules = default!;
    private int _numericSize;
    private byte[] _alphanumericModules = default!;
    private int _alphanumericSize;
    private byte[] _byteModules = default!;
    private int _byteSize;
    private byte[] _kanjiModules = default!;
    private int _kanjiSize;
    private byte[] _numericDamagedModules = default!;
    private byte[] _byteDamagedModules = default!;
    private char[] _chars = default!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        (_numericModules, _numericSize) = BuildMicro("0123456789", MicroQREccLevel.L);          // M2-L
        (_alphanumericModules, _alphanumericSize) = BuildMicro("HELLO WORLD 14", MicroQREccLevel.L); // M3-L
        (_byteModules, _byteSize) = BuildMicro("bytes m4 mode", MicroQREccLevel.M);             // M4-M
        (_kanjiModules, _kanjiSize) = BuildMicro("吾輩は猫である。", MicroQREccLevel.M, allowKanji: true);  // M4-M
        _chars = new char[MicroQRCodeDecoder.GetMaxDecodedLength(MicroQRVersion.M4)];

        _numericDamagedModules = CorrectableDamage.Flip(_numericModules, flips: 1, seed: 17, m => Decode(m, _numericSize));
        _byteDamagedModules = CorrectableDamage.Flip(_byteModules, flips: 5, seed: 23, m => Decode(m, _byteSize));
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

    [Benchmark]
    public string MicroQR_Kanji_M4_Decode()
    {
        MicroQRCodeDecoder.TryDecode(_kanjiModules, _kanjiSize, out var text, out _);
        return text;
    }

    [Benchmark]
    public string MicroQR_Numeric_M2_CorrectedDecode()
    {
        MicroQRCodeDecoder.TryDecode(_numericDamagedModules, _numericSize, out var text, out _);
        return text;
    }

    [Benchmark]
    public string MicroQR_Byte_M4_CorrectedDecode()
    {
        MicroQRCodeDecoder.TryDecode(_byteDamagedModules, _byteSize, out var text, out _);
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

    [Benchmark(Description = "MicroQR_Kanji_M4_Decode (Span)")]
    public int MicroQR_Kanji_M4_DecodeSpan()
    {
        MicroQRCodeDecoder.TryDecode(_kanjiModules, _kanjiSize, _chars, out var written, out _);
        return written;
    }

    // Correctable damage: same symbols, modules flipped within RS capacity.

    [Benchmark(Description = "MicroQR_Numeric_M2_Corrected_Decode (Span)")]
    public int MicroQR_Numeric_M2_CorrectedDecodeSpan()
    {
        MicroQRCodeDecoder.TryDecode(_numericDamagedModules, _numericSize, _chars, out var written, out _);
        return written;
    }

    [Benchmark(Description = "MicroQR_Byte_M4_Corrected_Decode (Span)")]
    public int MicroQR_Byte_M4_CorrectedDecodeSpan()
    {
        MicroQRCodeDecoder.TryDecode(_byteDamagedModules, _byteSize, _chars, out var written, out _);
        return written;
    }

    private static (bool, string, int) Decode(byte[] modules, int size)
        => (MicroQRCodeDecoder.TryDecode(modules, size, out var text, out var info), text, info.ErrorsCorrected);

    private static (byte[] modules, int size) BuildMicro(string content, MicroQREccLevel eccLevel, bool allowKanji = false)
    {
        var options = new MicroQRCodeGeneratorOptions { QuietZoneSize = 0, AllowKanji = allowKanji };
        var calculated = Sizing.Required(content.AsSpan(), eccLevel, options);
        var buffer = new byte[calculated.BufferSize];
        MicroQRCodeGenerator.Create(content.AsSpan(), eccLevel, buffer, options);
        return (buffer, calculated.Size);
    }
}
