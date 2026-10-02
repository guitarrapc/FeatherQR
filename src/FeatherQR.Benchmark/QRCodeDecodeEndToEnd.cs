using SkiaSharp;
using System.Text;

/// <summary>
/// End-to-end QR matrix decoding through the public API (QRCodeDecoder).
/// Payloads mirror QRCodeEncodeEndToEnd so encode and decode costs are directly comparable; a Micro QR M2-L decode of the same numeric payload gives the scale reference.
/// Matrices are quiet-zone-free (the decoder's in-place fast path).
///
/// Scenarios:
///   Numeric_V1_L : version 1, numeric mode (digits only)
///   Alphanumeric_V1_M : version 1, alphanumeric mode (uppercase / punctuation subset)
///   Byte_Url_V6_M : version 6, byte mode (typical URL with lowercase)
///   Kanji_V6_M : version 6-M, Kanji mode (capacity boundary, 65 characters)
///   Byte_V40_L : version 40-L, byte mode (largest data volume)
///   Byte_V40_H : version 39-H, byte mode (77 blocks x 30 ecc). Named V40 before 1,200 bytes was found to fit version 39
///   Kanji_Long_V15_L : version 15-L, Kanji mode (capacity boundary, 320 characters)
///   Image_Byte_Url_V6_M : rendered bitmap luminance -> text (binarize + finder detection + sampling)
///   *_Corrected : Numeric_V1_L (2 errors) and Byte_V40_H (81 errors, one a block on average) with damage the decoder confirms as exactly that many corrected errors, so the correction path runs rather than syndrome generation alone (the clean cases exit early)
///
/// Byte_Url_V6_M and Kanji_V6_M share version and level, so they differ in mode, not in symbol size.
/// </summary>
public class QRCodeDecodeEndToEnd
{
    private byte[] _numericModules = default!;
    private int _numericSize;
    private byte[] _alphanumericModules = default!;
    private int _alphanumericSize;
    private byte[] _byteUrlModules = default!;
    private int _byteUrlSize;
    private byte[] _byteLongLModules = default!;
    private int _byteLongLSize;
    private byte[] _byteLongHModules = default!;
    private int _byteLongHSize;
    private byte[] _kanjiModules = default!;
    private int _kanjiSize;
    private byte[] _kanjiLongModules = default!;
    private int _kanjiLongSize;
    private byte[] _numericDamagedModules = default!;
    private byte[] _byteLongHDamagedModules = default!;
    private byte[] _microNumericModules = default!;
    private int _microNumericSize;
    private char[] _chars = default!;
    private char[] _microChars = default!;

    private byte[] _byteUrlLuminance = default!;
    private int _byteUrlImageSize;

    private string _byteUrl = default!;
    private string _byteLongL = default!;
    private string _byteLongH = default!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        (_numericModules, _numericSize) = BuildModules("0123456789", QREccLevel.L);
        (_alphanumericModules, _alphanumericSize) = BuildModules("HELLO WORLD 2026", QREccLevel.M);
        _byteUrl = "https://github.com/guitarrapc/FeatherQR/blob/main/README.md?foo=sample&bar=dummy&baz=42";
        (_byteUrlModules, _byteUrlSize) = BuildModules(_byteUrl, QREccLevel.M);
        _byteLongL = BuildDeterministicText(2900);
        _byteLongH = BuildDeterministicText(1200);
        (_byteLongLModules, _byteLongLSize) = BuildModules(_byteLongL, QREccLevel.L);
        (_byteLongHModules, _byteLongHSize) = BuildModules(_byteLongH, QREccLevel.H);
        var japanese = string.Concat(Enumerable.Repeat("吾輩は猫である。名前はまだ無い。", 20));
        (_kanjiModules, _kanjiSize) = BuildModules(japanese.Substring(0, 65), QREccLevel.M, new QRCodeGeneratorOptions { AllowKanji = true });
        (_kanjiLongModules, _kanjiLongSize) = BuildModules(japanese, QREccLevel.L, new QRCodeGeneratorOptions { AllowKanji = true });
        _chars = new char[QRCodeDecoder.GetMaxDecodedLength(40)];

        _numericDamagedModules = CorrectableDamage.Flip(_numericModules, flips: 2, seed: 17, m => Decode(m, _numericSize));
        // About one error a block, so correction is a measurable share of an 81-block decode.
        _byteLongHDamagedModules = CorrectableDamage.Flip(_byteLongHModules, flips: 81, seed: 23, m => Decode(m, _byteLongHSize));

        (_microNumericModules, _microNumericSize) = BuildMicro("0123456789", MicroQREccLevel.L);
        _microChars = new char[MicroQRCodeDecoder.GetMaxDecodedLength(MicroQRVersion.M2)];

        (_byteUrlLuminance, _byteUrlImageSize) = RenderLuminance(_byteUrl, QREccLevel.M, pixelsPerModule: 8);
    }

    // String path (allocates the result string only)

    [Benchmark]
    public string QR_Numeric_V1_L_Decode()
    {
        QRCodeDecoder.TryDecode(_numericModules, _numericSize, out var text, out _);
        return text;
    }

    [Benchmark]
    public string QR_Alphanumeric_V1_M_Decode()
    {
        QRCodeDecoder.TryDecode(_alphanumericModules, _alphanumericSize, out var text, out _);
        return text;
    }

    [Benchmark]
    public string QR_Byte_Url_V6_M_Decode()
    {
        QRCodeDecoder.TryDecode(_byteUrlModules, _byteUrlSize, out var text, out _);
        return text;
    }

    [Benchmark]
    public string QR_Kanji_V6_M_Decode()
    {
        QRCodeDecoder.TryDecode(_kanjiModules, _kanjiSize, out var text, out _);
        return text;
    }

    [Benchmark]
    public string QR_Byte_V40_L_Decode()
    {
        QRCodeDecoder.TryDecode(_byteLongLModules, _byteLongLSize, out var text, out _);
        return text;
    }

    [Benchmark]
    public string QR_Byte_V40_H_Decode()
    {
        QRCodeDecoder.TryDecode(_byteLongHModules, _byteLongHSize, out var text, out _);
        return text;
    }

    [Benchmark]
    public string QR_Kanji_Long_V15_L_Decode()
    {
        QRCodeDecoder.TryDecode(_kanjiLongModules, _kanjiLongSize, out var text, out _);
        return text;
    }

    [Benchmark]
    public string QR_Numeric_V1_L_CorrectedDecode()
    {
        QRCodeDecoder.TryDecode(_numericDamagedModules, _numericSize, out var text, out _);
        return text;
    }

    [Benchmark]
    public string QR_Byte_V40_H_CorrectedDecode()
    {
        QRCodeDecoder.TryDecode(_byteLongHDamagedModules, _byteLongHSize, out var text, out _);
        return text;
    }

    [Benchmark]
    public string Image_Byte_Url_V6_M_Decode()
    {
        QRCodeDecoder.TryDecodeImage(_byteUrlLuminance, _byteUrlImageSize, _byteUrlImageSize, out var text, out _);
        return text;
    }

    // Span destination (zero-allocation) path

    [Benchmark(Baseline = true, Description = "QR_Numeric_V1_L_Decode (Span)")]
    public int QR_Numeric_V1_L_DecodeSpan()
    {
        QRCodeDecoder.TryDecode(_numericModules, _numericSize, _chars, out var written, out _);
        return written;
    }

    [Benchmark(Description = "QR_Alphanumeric_V1_M_Decode (Span)")]
    public int QR_Alphanumeric_V1_M_DecodeSpan()
    {
        QRCodeDecoder.TryDecode(_alphanumericModules, _alphanumericSize, _chars, out var written, out _);
        return written;
    }

    [Benchmark(Description = "QR_Byte_Url_V6_M_Decode (Span)")]
    public int QR_Byte_Url_V6_M_DecodeSpan()
    {
        QRCodeDecoder.TryDecode(_byteUrlModules, _byteUrlSize, _chars, out var written, out _);
        return written;
    }

    [Benchmark(Description = "QR_Kanji_V6_M_Decode (Span)")]
    public int QR_Kanji_V6_M_DecodeSpan()
    {
        QRCodeDecoder.TryDecode(_kanjiModules, _kanjiSize, _chars, out var written, out _);
        return written;
    }

    [Benchmark(Description = "QR_Byte_V40_L_Decode (Span)")]
    public int QR_Byte_V40_L_DecodeSpan()
    {
        QRCodeDecoder.TryDecode(_byteLongLModules, _byteLongLSize, _chars, out var written, out _);
        return written;
    }

    [Benchmark(Description = "QR_Byte_V40_H_Decode (Span)")]
    public int QR_Byte_V40_H_DecodeSpan()
    {
        QRCodeDecoder.TryDecode(_byteLongHModules, _byteLongHSize, _chars, out var written, out _);
        return written;
    }

    [Benchmark(Description = "QR_Kanji_Long_V15_L_Decode (Span)")]
    public int QR_Kanji_Long_V15_L_DecodeSpan()
    {
        QRCodeDecoder.TryDecode(_kanjiLongModules, _kanjiLongSize, _chars, out var written, out _);
        return written;
    }

    // Correctable damage: same symbols, modules flipped within RS capacity.

    [Benchmark(Description = "QR_Numeric_V1_L_Corrected_Decode (Span)")]
    public int QR_Numeric_V1_L_CorrectedDecodeSpan()
    {
        QRCodeDecoder.TryDecode(_numericDamagedModules, _numericSize, _chars, out var written, out _);
        return written;
    }

    [Benchmark(Description = "QR_Byte_V40_H_Corrected_Decode (Span)")]
    public int QR_Byte_V40_H_CorrectedDecodeSpan()
    {
        QRCodeDecoder.TryDecode(_byteLongHDamagedModules, _byteLongHSize, _chars, out var written, out _);
        return written;
    }

    [Benchmark(Description = "Image_Byte_Url_V6_M_Decode (Span)")]
    public int Image_Byte_Url_V6_M_DecodeSpan()
    {
        QRCodeDecoder.TryDecodeImage(_byteUrlLuminance, _byteUrlImageSize, _byteUrlImageSize, _chars, out var written, out _);
        return written;
    }

    // Micro QR M2-L with the same numeric payload, for scale reference.

    [Benchmark(Description = "MicroQR_Numeric_M2_Decode (Span)")]
    public int MicroQR_Numeric_M2_DecodeSpan()
    {
        MicroQRCodeDecoder.TryDecode(_microNumericModules, _microNumericSize, _microChars, out var written, out _);
        return written;
    }

    private static (bool, string, int) Decode(byte[] modules, int size)
        => (QRCodeDecoder.TryDecode(modules, size, out var text, out var info), text, info.ErrorsCorrected);

    private static (byte[] modules, int size) BuildModules(string content, QREccLevel eccLevel, QRCodeGeneratorOptions options = default)
    {
        options = options with { QuietZoneSize = 0 };
        var calculated = Sizing.Required(content.AsSpan(), eccLevel, options);
        var buffer = new byte[calculated.BufferSize];
        QRCodeGenerator.Create(content.AsSpan(), eccLevel, buffer, options);
        return (buffer, calculated.Size);
    }

    private static (byte[] modules, int size) BuildMicro(string content, MicroQREccLevel eccLevel)
    {
        var calculated = Sizing.Required(content.AsSpan(), eccLevel, 0);
        var buffer = new byte[calculated.BufferSize];
        MicroQRCodeGenerator.Create(content.AsSpan(), eccLevel, buffer, new MicroQRCodeGeneratorOptions { QuietZoneSize = 0 });
        return (buffer, calculated.Size);
    }

    private static (byte[] luminance, int size) RenderLuminance(string content, QREccLevel eccLevel, int pixelsPerModule)
    {
        var qr = QRCodeGenerator.Create(content.AsSpan(), eccLevel);
        var sizePx = qr.Size * pixelsPerModule;
        using var bitmap = new SKBitmap(new SKImageInfo(sizePx, sizePx, SKColorType.Gray8));
        using (var canvas = new SKCanvas(bitmap))
        {
            SymbolRenderer.Render(canvas, SKRect.Create(0, 0, sizePx, sizePx), qr, SKColors.Black, SKColors.White);
            canvas.Flush();
        }

        var luminance = new byte[sizePx * sizePx];
        using var pixmap = bitmap.PeekPixels();
        var pixels = pixmap.GetPixelSpan();
        for (var y = 0; y < sizePx; y++)
        {
            pixels.Slice(y * pixmap.RowBytes, sizePx).CopyTo(luminance.AsSpan(y * sizePx, sizePx));
        }
        return (luminance, sizePx);
    }

    private static string BuildDeterministicText(int length)
    {
        var sb = new StringBuilder(length);
        var rng = new Random(42);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 .,:/?&=-_";
        for (var i = 0; i < length; i++)
        {
            sb.Append(alphabet[rng.Next(alphabet.Length)]);
        }
        return sb.ToString();
    }
}
