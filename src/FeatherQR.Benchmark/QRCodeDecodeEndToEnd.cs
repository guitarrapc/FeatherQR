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
///   Byte_V40_L : version 40-L, byte mode (largest data volume)
///   Byte_V40_H : version 40-H, byte mode (81 blocks, max ECC share)
///   Image_Byte_Url_V6_M : rendered bitmap luminance -> text (binarize + finder detection + sampling)
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
        _byteUrl = "https://github.com/guitarrapc/FeatherQR/blob/main/README.md?foo=sample&bar=dummy";
        (_byteUrlModules, _byteUrlSize) = BuildModules(_byteUrl, QREccLevel.M);
        _byteLongL = BuildDeterministicText(2900);
        _byteLongH = BuildDeterministicText(1200);
        (_byteLongLModules, _byteLongLSize) = BuildModules(_byteLongL, QREccLevel.L);
        (_byteLongHModules, _byteLongHSize) = BuildModules(_byteLongH, QREccLevel.H);
        _chars = new char[QRCodeDecoder.GetMaxDecodedLength(40)];

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

    private static (byte[] modules, int size) BuildModules(string content, QREccLevel eccLevel)
    {
        var calculated = Sizing.Required(content.AsSpan(), eccLevel, 0);
        var buffer = new byte[calculated.BufferSize];
        QRCodeGenerator.Create(content.AsSpan(), eccLevel, buffer, new QRCodeGeneratorOptions { QuietZoneSize = 0 });
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
