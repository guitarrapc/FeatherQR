using SkiaSharp;
using System.Text;

/// <summary>
/// Measures the bytes calls of the three decoders beside the text calls on the same symbols, so each pair reads as the cost of giving bytes instead of text.
/// </summary>
/// <remarks>
/// The symbols are those of <see cref="QRCodeDecodeEndToEnd"/>, <see cref="MicroQRDecodeEndToEnd"/> and <see cref="RmQRDecodeEndToEnd"/>, matrices without a quiet zone,
/// plus 2,900 bytes of every value under ECI 899, which only the bytes calls read. Setup checks that every symbol reads, so no row measures a failure.
/// Span destinations must stay allocation-free.
/// </remarks>
public class BytesDecodeEndToEnd
{
    private byte[] _qrUrlModules = default!;
    private int _qrUrlSize;
    private byte[] _qrLongModules = default!;
    private int _qrLongSize;
    private byte[] _qrKanjiModules = default!;
    private int _qrKanjiSize;
    private byte[] _qrBinaryModules = default!;
    private int _qrBinarySize;
    private byte[] _qrUrlLuminance = default!;
    private int _qrUrlImageSize;
    private byte[] _microModules = default!;
    private int _microSize;
    private byte[] _rmqrModules = default!;
    private (int Width, int Height) _rmqrSize;

    private char[] _qrChars = default!;
    private byte[] _qrBytes = default!;
    private char[] _microChars = default!;
    private byte[] _microBytes = default!;
    private char[] _rmqrChars = default!;
    private byte[] _rmqrBytes = default!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var url = "https://github.com/guitarrapc/FeatherQR/blob/main/README.md?foo=sample&bar=dummy&baz=42";
        (_qrUrlModules, _qrUrlSize) = BuildQR(url, QREccLevel.M);                                       // V6-M
        (_qrLongModules, _qrLongSize) = BuildQR(BuildDeterministicText(2900), QREccLevel.L);            // V40-L
        var japanese = string.Concat(Enumerable.Repeat("吾輩は猫である。名前はまだ無い。", 20));
        (_qrKanjiModules, _qrKanjiSize) = BuildQR(japanese, QREccLevel.L, new QRCodeGeneratorOptions { AllowKanji = true }); // V15-L
        (_qrBinaryModules, _qrBinarySize) = BuildQRBinary(2900, eciAssignment: 899);                    // V40-L
        (_qrUrlLuminance, _qrUrlImageSize) = RenderLuminance(url, QREccLevel.M, pixelsPerModule: 8);
        (_microModules, _microSize) = BuildMicro("bytes m4 mode", MicroQREccLevel.M);                   // M4-M
        (_rmqrModules, _rmqrSize) = BuildRmQR(string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog?! ", 4)).Substring(0, 150), RmQREccLevel.M); // R17x139

        _qrChars = new char[QRCodeDecoder.GetMaxDecodedLength(40)];
        _qrBytes = new byte[QRCodeDecoder.GetMaxDecodedLength(40)];
        _microChars = new char[MicroQRCodeDecoder.GetMaxDecodedLength(MicroQRVersion.M4)];
        _microBytes = new byte[MicroQRCodeDecoder.GetMaxDecodedLength(MicroQRVersion.M4)];
        _rmqrChars = new char[RmQRCodeDecoder.GetMaxDecodedLength(RmQRVersion.R17x139)];
        _rmqrBytes = new byte[RmQRCodeDecoder.GetMaxDecodedLength(RmQRVersion.R17x139)];

        Require(QRCodeDecoder.TryDecodeBytes(_qrUrlModules, _qrUrlSize, _qrBytes, out _, out _), "Standard QR URL");
        Require(QRCodeDecoder.TryDecodeBytes(_qrLongModules, _qrLongSize, _qrBytes, out _, out _), "Standard QR V40-L");
        Require(QRCodeDecoder.TryDecodeBytes(_qrKanjiModules, _qrKanjiSize, _qrBytes, out _, out _), "Standard QR Kanji");
        Require(QRCodeDecoder.TryDecodeBytes(_qrBinaryModules, _qrBinarySize, _qrBytes, out _, out _), "Standard QR binary");
        Require(QRCodeDecoder.TryDecodeImageBytes(_qrUrlLuminance, _qrUrlImageSize, _qrUrlImageSize, _qrBytes, out _, out _), "Standard QR image");
        Require(MicroQRCodeDecoder.TryDecodeBytes(_microModules, _microSize, _microBytes, out _, out _), "Micro QR");
        Require(RmQRCodeDecoder.TryDecodeBytes(_rmqrModules, _rmqrSize.Width, _rmqrSize.Height, _rmqrBytes, out _, out _), "rMQR");
    }

    // Standard QR, the URL at V6-M

    [Benchmark(Description = "QR_Byte_Url_V6_M_Decode (Span)")]
    public int QR_Byte_Url_V6_M_DecodeSpan()
    {
        QRCodeDecoder.TryDecode(_qrUrlModules, _qrUrlSize, _qrChars, out var written, out _);
        return written;
    }

    [Benchmark(Description = "QR_Byte_Url_V6_M_DecodeBytes (Span)")]
    public int QR_Byte_Url_V6_M_DecodeBytesSpan()
    {
        QRCodeDecoder.TryDecodeBytes(_qrUrlModules, _qrUrlSize, _qrBytes, out var written, out _);
        return written;
    }

    // Standard QR, 2,900 characters at V40-L

    [Benchmark]
    public string QR_Byte_V40_L_Decode()
    {
        QRCodeDecoder.TryDecode(_qrLongModules, _qrLongSize, out var text, out _);
        return text;
    }

    [Benchmark]
    public byte[] QR_Byte_V40_L_DecodeBytes()
    {
        QRCodeDecoder.TryDecodeBytes(_qrLongModules, _qrLongSize, out var bytes, out _);
        return bytes;
    }

    [Benchmark(Description = "QR_Byte_V40_L_Decode (Span)")]
    public int QR_Byte_V40_L_DecodeSpan()
    {
        QRCodeDecoder.TryDecode(_qrLongModules, _qrLongSize, _qrChars, out var written, out _);
        return written;
    }

    [Benchmark(Description = "QR_Byte_V40_L_DecodeBytes (Span)")]
    public int QR_Byte_V40_L_DecodeBytesSpan()
    {
        QRCodeDecoder.TryDecodeBytes(_qrLongModules, _qrLongSize, _qrBytes, out var written, out _);
        return written;
    }

    // Standard QR, Kanji at V15-L: the text is a char a character, the bytes two

    [Benchmark(Description = "QR_Kanji_Long_V15_L_Decode (Span)")]
    public int QR_Kanji_Long_V15_L_DecodeSpan()
    {
        QRCodeDecoder.TryDecode(_qrKanjiModules, _qrKanjiSize, _qrChars, out var written, out _);
        return written;
    }

    [Benchmark(Description = "QR_Kanji_Long_V15_L_DecodeBytes (Span)")]
    public int QR_Kanji_Long_V15_L_DecodeBytesSpan()
    {
        QRCodeDecoder.TryDecodeBytes(_qrKanjiModules, _qrKanjiSize, _qrBytes, out var written, out _);
        return written;
    }

    // Standard QR, 2,900 bytes of every value under ECI 899 at V40-L, which the text calls refuse

    [Benchmark(Description = "QR_Binary_Eci899_V40_L_DecodeBytes (Span)")]
    public int QR_Binary_Eci899_V40_L_DecodeBytesSpan()
    {
        QRCodeDecoder.TryDecodeBytes(_qrBinaryModules, _qrBinarySize, _qrBytes, out var written, out _);
        return written;
    }

    // Standard QR, the URL rendered at 8 pixels a module

    [Benchmark(Description = "Image_Byte_Url_V6_M_Decode (Span)")]
    public int Image_Byte_Url_V6_M_DecodeSpan()
    {
        QRCodeDecoder.TryDecodeImage(_qrUrlLuminance, _qrUrlImageSize, _qrUrlImageSize, _qrChars, out var written, out _);
        return written;
    }

    [Benchmark(Description = "Image_Byte_Url_V6_M_DecodeBytes (Span)")]
    public int Image_Byte_Url_V6_M_DecodeBytesSpan()
    {
        QRCodeDecoder.TryDecodeImageBytes(_qrUrlLuminance, _qrUrlImageSize, _qrUrlImageSize, _qrBytes, out var written, out _);
        return written;
    }

    // Micro QR, M4-M

    [Benchmark(Description = "MicroQR_Byte_M4_Decode (Span)")]
    public int MicroQR_Byte_M4_DecodeSpan()
    {
        MicroQRCodeDecoder.TryDecode(_microModules, _microSize, _microChars, out var written, out _);
        return written;
    }

    [Benchmark(Description = "MicroQR_Byte_M4_DecodeBytes (Span)")]
    public int MicroQR_Byte_M4_DecodeBytesSpan()
    {
        MicroQRCodeDecoder.TryDecodeBytes(_microModules, _microSize, _microBytes, out var written, out _);
        return written;
    }

    // rMQR, R17x139-M

    [Benchmark(Description = "RmQR_Byte_R17x139_Decode (Span)")]
    public int RmQR_Byte_R17x139_DecodeSpan()
    {
        RmQRCodeDecoder.TryDecode(_rmqrModules, _rmqrSize.Width, _rmqrSize.Height, _rmqrChars, out var written, out _);
        return written;
    }

    [Benchmark(Description = "RmQR_Byte_R17x139_DecodeBytes (Span)")]
    public int RmQR_Byte_R17x139_DecodeBytesSpan()
    {
        RmQRCodeDecoder.TryDecodeBytes(_rmqrModules, _rmqrSize.Width, _rmqrSize.Height, _rmqrBytes, out var written, out _);
        return written;
    }

    private static void Require(bool read, string symbol)
    {
        if (!read)
            throw new InvalidOperationException($"The bytes call did not read the {symbol} symbol, so its row would measure a failure.");
    }

    private static (byte[] modules, int size) BuildQR(string content, QREccLevel eccLevel, QRCodeGeneratorOptions options = default)
    {
        options = options with { QuietZoneSize = 0 };
        var calculated = Sizing.Required(content.AsSpan(), eccLevel, options);
        var buffer = new byte[calculated.BufferSize];
        QRCodeGenerator.Create(content.AsSpan(), eccLevel, buffer, options);
        return (buffer, calculated.Size);
    }

    private static (byte[] modules, int size) BuildQRBinary(int length, int eciAssignment)
    {
        var payload = new byte[length];
        for (var i = 0; i < length; i++)
            payload[i] = (byte)(i * 151 + 7);
        var options = new QRCodeGeneratorOptions { QuietZoneSize = 0 };
        if (!QRCodeGenerator.TryGetRequiredBufferSize(payload, QREccLevel.L, out var calculated, options, eciAssignment))
            throw new InvalidOperationException($"{length} bytes under ECI {eciAssignment} do not fit Standard QR at L.");
        var buffer = new byte[calculated.BufferSize];
        QRCodeGenerator.Create(payload, QREccLevel.L, buffer, options, eciAssignment);
        return (buffer, calculated.Size);
    }

    private static (byte[] modules, int size) BuildMicro(string content, MicroQREccLevel eccLevel)
    {
        var options = new MicroQRCodeGeneratorOptions { QuietZoneSize = 0 };
        var calculated = Sizing.Required(content.AsSpan(), eccLevel, options);
        var buffer = new byte[calculated.BufferSize];
        MicroQRCodeGenerator.Create(content.AsSpan(), eccLevel, buffer, options);
        return (buffer, calculated.Size);
    }

    private static (byte[] modules, (int Width, int Height) size) BuildRmQR(string content, RmQREccLevel eccLevel)
    {
        var options = new RmQRCodeGeneratorOptions { QuietZoneSize = 0 };
        var calculated = Sizing.Required(content.AsSpan(), eccLevel, options);
        var buffer = new byte[calculated.BufferSize];
        RmQRCodeGenerator.Create(content.AsSpan(), eccLevel, buffer, options);
        return (buffer, (calculated.Width, calculated.Height));
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
            pixels.Slice(y * pixmap.RowBytes, sizePx).CopyTo(luminance.AsSpan(y * sizePx, sizePx));
        return (luminance, sizePx);
    }

    private static string BuildDeterministicText(int length)
    {
        var sb = new StringBuilder(length);
        var rng = new Random(42);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 .,:/?&=-_";
        for (var i = 0; i < length; i++)
            sb.Append(alphabet[rng.Next(alphabet.Length)]);
        return sb.ToString();
    }
}
