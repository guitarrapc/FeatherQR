/// <summary>
/// End-to-end rMQR matrix decoding through the public API (RmQRCodeDecoder): module matrix (no quiet zone) → text.
/// Baseline for the reference-shaped decoder; span-destination variants must stay allocation-free.
///
/// Scenarios (same payloads as RmQREncodeEndToEnd):
///   Numeric_R7x43_M      : smallest symbol, single RS block
///   Alphanumeric_R11x59_M: mid symbol, single block
///   Byte_R17x139_M       : largest symbol, 4 RS blocks
///   Kanji_R17x139_M      : largest symbol, Kanji mode (capacity boundary, 92 characters)
///   *_Corrected         : Numeric_R7x43 and Byte_R17x139 with damage the decoder
///                          confirms as exactly N corrected errors, so the
///                          Berlekamp-Massey/Chien/Forney correction path runs rather
///                          than syndrome generation alone (the clean cases exit early)
/// </summary>
public class RmQRDecodeEndToEnd
{
    private byte[] _numericModules = default!;
    private byte[] _alphanumericModules = default!;
    private byte[] _byteModules = default!;
    private byte[] _kanjiModules = default!;
    private (int Width, int Height) _numericSize;
    private (int Width, int Height) _alphanumericSize;
    private (int Width, int Height) _byteSize;
    private (int Width, int Height) _kanjiSize;
    private byte[] _numericDamagedModules = default!;
    private byte[] _byteDamagedModules = default!;
    private byte[] _standardModules = default!;
    private int _standardSize;
    private char[] _chars = default!;
    private char[] _standardChars = default!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        (_numericModules, _numericSize) = Build("012345678901", RmQREccLevel.M, RmQRVersion.R7x43);
        (_alphanumericModules, _alphanumericSize) = Build("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 $%*+-.", RmQREccLevel.M, RmQRVersion.R11x59);
        (_byteModules, _byteSize) = Build(string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog?! ", 4)).Substring(0, 150), RmQREccLevel.M, RmQRVersion.R17x139);
        (_kanjiModules, _kanjiSize) = Build(string.Concat(Enumerable.Repeat("吾輩は猫である。名前はまだ無い。", 6)).Substring(0, 92), RmQREccLevel.M, RmQRVersion.R17x139, allowKanji: true);
        _chars = new char[RmQRCodeDecoder.GetMaxDecodedLength(RmQRVersion.R17x139)];

        // Correctable damage: flip a few modules and keep only a corruption the decoder
        // still recovers, so the measurement covers correction rather than failure.
        _numericDamagedModules = CorrectableDamage.Flip(_numericModules, flips: 2, seed: 17, m => Decode(m, _numericSize));
        _byteDamagedModules = CorrectableDamage.Flip(_byteModules, flips: 6, seed: 23, m => Decode(m, _byteSize));

        var calculated = Sizing.Required("012345678901", QREccLevel.L, 0);
        _standardModules = new byte[calculated.BufferSize];
        FeatherQR.QRCodeGenerator.Create("012345678901", QREccLevel.L, _standardModules, new QRCodeGeneratorOptions { QuietZoneSize = 0 });
        _standardSize = calculated.Size;
        _standardChars = new char[QRCodeDecoder.GetMaxDecodedLength(1)];
    }

    // String-returning variants (allocate the result string only)

    [Benchmark]
    public string RmQR_Numeric_R7x43_Decode()
    {
        RmQRCodeDecoder.TryDecode(_numericModules, _numericSize.Width, _numericSize.Height, out var text, out _);
        return text;
    }

    [Benchmark]
    public string RmQR_Alphanumeric_R11x59_Decode()
    {
        RmQRCodeDecoder.TryDecode(_alphanumericModules, _alphanumericSize.Width, _alphanumericSize.Height, out var text, out _);
        return text;
    }

    [Benchmark]
    public string RmQR_Byte_R17x139_Decode()
    {
        RmQRCodeDecoder.TryDecode(_byteModules, _byteSize.Width, _byteSize.Height, out var text, out _);
        return text;
    }

    [Benchmark]
    public string RmQR_Kanji_R17x139_Decode()
    {
        RmQRCodeDecoder.TryDecode(_kanjiModules, _kanjiSize.Width, _kanjiSize.Height, out var text, out _);
        return text;
    }

    [Benchmark]
    public string RmQR_Numeric_R7x43_CorrectedDecode()
    {
        RmQRCodeDecoder.TryDecode(_numericDamagedModules, _numericSize.Width, _numericSize.Height, out var text, out _);
        return text;
    }

    [Benchmark]
    public string RmQR_Byte_R17x139_CorrectedDecode()
    {
        RmQRCodeDecoder.TryDecode(_byteDamagedModules, _byteSize.Width, _byteSize.Height, out var text, out _);
        return text;
    }

    // Span destination (zero-allocation) variants

    [Benchmark(Baseline = true, Description = "RmQR_Numeric_R7x43_Decode (Span)")]
    public int RmQR_Numeric_R7x43_DecodeSpan()
    {
        RmQRCodeDecoder.TryDecode(_numericModules, _numericSize.Width, _numericSize.Height, _chars, out var written, out _);
        return written;
    }

    [Benchmark(Description = "RmQR_Alphanumeric_R11x59_Decode (Span)")]
    public int RmQR_Alphanumeric_R11x59_DecodeSpan()
    {
        RmQRCodeDecoder.TryDecode(_alphanumericModules, _alphanumericSize.Width, _alphanumericSize.Height, _chars, out var written, out _);
        return written;
    }

    [Benchmark(Description = "RmQR_Byte_R17x139_Decode (Span)")]
    public int RmQR_Byte_R17x139_DecodeSpan()
    {
        RmQRCodeDecoder.TryDecode(_byteModules, _byteSize.Width, _byteSize.Height, _chars, out var written, out _);
        return written;
    }

    [Benchmark(Description = "RmQR_Kanji_R17x139_Decode (Span)")]
    public int RmQR_Kanji_R17x139_DecodeSpan()
    {
        RmQRCodeDecoder.TryDecode(_kanjiModules, _kanjiSize.Width, _kanjiSize.Height, _chars, out var written, out _);
        return written;
    }

    // Correctable damage: same symbols, modules flipped within RS capacity.

    [Benchmark(Description = "RmQR_Numeric_R7x43_Corrected_Decode (Span)")]
    public int RmQR_Numeric_R7x43_CorrectedDecodeSpan()
    {
        RmQRCodeDecoder.TryDecode(_numericDamagedModules, _numericSize.Width, _numericSize.Height, _chars, out var written, out _);
        return written;
    }

    [Benchmark(Description = "RmQR_Byte_R17x139_Corrected_Decode (Span)")]
    public int RmQR_Byte_R17x139_CorrectedDecodeSpan()
    {
        RmQRCodeDecoder.TryDecode(_byteDamagedModules, _byteSize.Width, _byteSize.Height, _chars, out var written, out _);
        return written;
    }

    // Standard QR version 1 with the same numeric payload, for scale reference.

    [Benchmark(Description = "StandardQr_Numeric_V1_Decode (Span)")]
    public int StandardQr_Numeric_V1_DecodeSpan()
    {
        QRCodeDecoder.TryDecode(_standardModules, _standardSize, _standardChars, out var written, out _);
        return written;
    }

    private static (bool, string, int) Decode(byte[] modules, (int Width, int Height) size)
        => (RmQRCodeDecoder.TryDecode(modules, size.Width, size.Height, out var text, out var info), text, info.ErrorsCorrected);

    private static (byte[] modules, (int Width, int Height) size) Build(string content, RmQREccLevel eccLevel, RmQRVersion version, bool allowKanji = false)
    {
        var options = new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = 0, AllowKanji = allowKanji };
        var calculated = Sizing.Required(content.AsSpan(), eccLevel, options);
        var buffer = new byte[calculated.BufferSize];
        RmQRCodeGenerator.Create(content.AsSpan(), eccLevel, buffer, options);
        return (buffer, (calculated.Width, calculated.Height));
    }
}
