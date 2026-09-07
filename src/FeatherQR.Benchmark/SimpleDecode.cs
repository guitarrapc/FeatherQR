using BenchmarkDotNet.Configs;
using SkiaSharp;

/// <summary>
/// Cross-library Standard QR decoding over the same five payloads as <see cref="SimpleEncode"/>.
/// Only three of the compared libraries decode at all: QRCoder and Net.Codecrete.QrCodeGenerator are encoders only.
///
/// Two entry points are measured, because the libraries differ in what they accept:
///
///   matrix - a module grid that is already extracted, no image processing. Both
///     libraries take a quiet-zone-free symbol here. This is the path a caller uses
///     when the modules come from somewhere other than a camera.
///   image  - a rendered bitmap: binarize, locate finder patterns, sample, then decode.
///     Every library gets the same 8-pixel-per-module RGBA image and does its own
///     grayscale conversion.
///
/// Comparability notes:
///
///   The matrix rows use this library's string-returning overload, not the
///   caller-buffer one, so every library allocates its result the same way. The
///   allocation-free span overload is measured in <see cref="QRCodeDecodeEndToEnd"/>.
///   CodeGlyphX is fed 32-bit RGBA rather than 8-bit luminance because its pixel entry
///   point rejects any buffer narrower than 4 bytes per pixel, including the Gray8
///   format its own PixelFormat enum offers (QrPixelDecoder guards on stride &lt; width * 4).
///   Every decoder is verified in setup to actually succeed and return the original
///   text, so no row can win by failing fast.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class SimpleDecode
{
    private const string SkiaMatrix = "FeatherQR (matrix)";
    private const string GlyphMatrix = "CodeGlyphX (matrix)";
    private const string SkiaImage = "FeatherQR (image)";
    private const string GlyphImage = "CodeGlyphX (image)";
    private const string ZxingImage = "Zxing (image)";

    private const int Number = 0;
    private const int Alphanumeric = 1;
    private const int Url = 2;
    private const int Unicode = 3;
    private const int Wifi = 4;

    private const int PixelsPerModule = 8;

    private string[] _texts = default!;
    private byte[][] _modules = default!;
    private int[] _moduleSizes = default!;
    private CodeGlyphX.BitMatrix[] _glyphMatrices = default!;
    private SKBitmap[] _bitmaps = default!;
    private byte[][] _rgba = default!;
    private int[] _imageSizes = default!;
    private ZXing.SkiaSharp.BarcodeReader _zxingReader = default!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _texts =
        [
            "0123456789012345678901234567890123456789",
            "0123456789ABCDEFG0123456789HIJKLMN",
            "https://example.com/user/repo?foo=value&bar=piyo",
            "FooBar你好世界こんにちはПривет мир🎉🎊🎈Zürich",
            "WIFI:S:foobar-wifi;T:WPA;P:test123;H:false;;",
        ];

        _zxingReader = new ZXing.SkiaSharp.BarcodeReader
        {
            Options = new ZXing.Common.DecodingOptions
            {
                PossibleFormats = [ZXing.BarcodeFormat.QR_CODE],
            },
        };

        _modules = new byte[_texts.Length][];
        _moduleSizes = new int[_texts.Length];
        _glyphMatrices = new CodeGlyphX.BitMatrix[_texts.Length];
        _bitmaps = new SKBitmap[_texts.Length];
        _rgba = new byte[_texts.Length][];
        _imageSizes = new int[_texts.Length];

        for (var i = 0; i < _texts.Length; i++)
        {
            (_modules[i], _moduleSizes[i]) = BuildModules(_texts[i]);
            _glyphMatrices[i] = GlyphBitMatrix.From(_modules[i], _moduleSizes[i], _moduleSizes[i]);
            (_bitmaps[i], _rgba[i], _imageSizes[i]) = RenderImage(_texts[i]);
        }

        VerifyEveryDecoderSucceeds();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        foreach (var bitmap in _bitmaps)
        {
            bitmap.Dispose();
        }
    }

    // FeatherQR, matrix

    [Benchmark(Baseline = true)]
    [BenchmarkCategory(SkiaMatrix)]
    public string SkiaSharpQrCode_Number_MatrixDecode() => DecodeMatrix(Number);

    [Benchmark]
    [BenchmarkCategory(SkiaMatrix)]
    public string SkiaSharpQrCode_Alphanumeric_MatrixDecode() => DecodeMatrix(Alphanumeric);

    [Benchmark]
    [BenchmarkCategory(SkiaMatrix)]
    public string SkiaSharpQrCode_Url_MatrixDecode() => DecodeMatrix(Url);

    [Benchmark]
    [BenchmarkCategory(SkiaMatrix)]
    public string SkiaSharpQrCode_Unicode_MatrixDecode() => DecodeMatrix(Unicode);

    [Benchmark]
    [BenchmarkCategory(SkiaMatrix)]
    public string SkiaSharpQrCode_Wifi_MatrixDecode() => DecodeMatrix(Wifi);

    // CodeGlyphX, matrix

    [Benchmark(Baseline = true)]
    [BenchmarkCategory(GlyphMatrix)]
    public string CodeGlyphX_Number_MatrixDecode() => GlyphDecodeMatrix(Number);

    [Benchmark]
    [BenchmarkCategory(GlyphMatrix)]
    public string CodeGlyphX_Alphanumeric_MatrixDecode() => GlyphDecodeMatrix(Alphanumeric);

    [Benchmark]
    [BenchmarkCategory(GlyphMatrix)]
    public string CodeGlyphX_Url_MatrixDecode() => GlyphDecodeMatrix(Url);

    [Benchmark]
    [BenchmarkCategory(GlyphMatrix)]
    public string CodeGlyphX_Unicode_MatrixDecode() => GlyphDecodeMatrix(Unicode);

    [Benchmark]
    [BenchmarkCategory(GlyphMatrix)]
    public string CodeGlyphX_Wifi_MatrixDecode() => GlyphDecodeMatrix(Wifi);

    // FeatherQR, image

    [Benchmark(Baseline = true)]
    [BenchmarkCategory(SkiaImage)]
    public string SkiaSharpQrCode_Number_ImageDecode() => DecodeImage(Number);

    [Benchmark]
    [BenchmarkCategory(SkiaImage)]
    public string SkiaSharpQrCode_Alphanumeric_ImageDecode() => DecodeImage(Alphanumeric);

    [Benchmark]
    [BenchmarkCategory(SkiaImage)]
    public string SkiaSharpQrCode_Url_ImageDecode() => DecodeImage(Url);

    [Benchmark]
    [BenchmarkCategory(SkiaImage)]
    public string SkiaSharpQrCode_Unicode_ImageDecode() => DecodeImage(Unicode);

    [Benchmark]
    [BenchmarkCategory(SkiaImage)]
    public string SkiaSharpQrCode_Wifi_ImageDecode() => DecodeImage(Wifi);

    // CodeGlyphX, image

    [Benchmark(Baseline = true)]
    [BenchmarkCategory(GlyphImage)]
    public string CodeGlyphX_Number_ImageDecode() => GlyphDecodeImage(Number);

    [Benchmark]
    [BenchmarkCategory(GlyphImage)]
    public string CodeGlyphX_Alphanumeric_ImageDecode() => GlyphDecodeImage(Alphanumeric);

    [Benchmark]
    [BenchmarkCategory(GlyphImage)]
    public string CodeGlyphX_Url_ImageDecode() => GlyphDecodeImage(Url);

    [Benchmark]
    [BenchmarkCategory(GlyphImage)]
    public string CodeGlyphX_Unicode_ImageDecode() => GlyphDecodeImage(Unicode);

    [Benchmark]
    [BenchmarkCategory(GlyphImage)]
    public string CodeGlyphX_Wifi_ImageDecode() => GlyphDecodeImage(Wifi);

    // Zxing, image

    [Benchmark(Baseline = true)]
    [BenchmarkCategory(ZxingImage)]
    public string ZXing_Number_ImageDecode() => ZxingDecodeImage(Number);

    [Benchmark]
    [BenchmarkCategory(ZxingImage)]
    public string ZXing_Alphanumeric_ImageDecode() => ZxingDecodeImage(Alphanumeric);

    [Benchmark]
    [BenchmarkCategory(ZxingImage)]
    public string ZXing_Url_ImageDecode() => ZxingDecodeImage(Url);

    [Benchmark]
    [BenchmarkCategory(ZxingImage)]
    public string ZXing_Unicode_ImageDecode() => ZxingDecodeImage(Unicode);

    [Benchmark]
    [BenchmarkCategory(ZxingImage)]
    public string ZXing_Wifi_ImageDecode() => ZxingDecodeImage(Wifi);

    private string DecodeMatrix(int index)
    {
        QRCodeDecoder.TryDecode(_modules[index], _moduleSizes[index], out var text, out _);
        return text;
    }

    private string GlyphDecodeMatrix(int index)
    {
        CodeGlyphX.QrDecoder.TryDecode(_glyphMatrices[index], out var decoded);
        return decoded.Text;
    }

    private string DecodeImage(int index)
    {
        QRCodeDecoder.TryDecode(_bitmaps[index], out var text);
        return text;
    }

    private string GlyphDecodeImage(int index)
    {
        var size = _imageSizes[index];
        CodeGlyphX.QrDecoder.TryDecode(_rgba[index], size, size, size * 4, CodeGlyphX.PixelFormat.Rgba32, out var decoded);
        return decoded.Text;
    }

    private string ZxingDecodeImage(int index)
    {
        return _zxingReader.Decode(_bitmaps[index]).Text;
    }

    /// <summary>
    /// Fails the run if any library cannot decode any payload.
    /// A decoder that bails out early would otherwise post the fastest time in its category.
    /// </summary>
    private void VerifyEveryDecoderSucceeds()
    {
        for (var i = 0; i < _texts.Length; i++)
        {
            Check(nameof(DecodeMatrix), i, DecodeMatrix(i));
            Check(nameof(GlyphDecodeMatrix), i, GlyphDecodeMatrix(i));
            Check(nameof(DecodeImage), i, DecodeImage(i));
            Check(nameof(GlyphDecodeImage), i, GlyphDecodeImage(i));
            Check(nameof(ZxingDecodeImage), i, ZxingDecodeImage(i));
        }

        void Check(string path, int index, string? decoded)
        {
            if (decoded != _texts[index])
            {
                throw new InvalidOperationException(
                    $"{path} did not round-trip payload {index}: expected \"{_texts[index]}\", got \"{decoded ?? "<null>"}\".");
            }
        }
    }

    private static (byte[] modules, int size) BuildModules(string content)
    {
        var calculated = Sizing.Required(content.AsSpan(), QREccLevel.L, 0);
        var buffer = new byte[calculated.BufferSize];
        QRCodeGenerator.Create(content.AsSpan(), QREccLevel.L, buffer, new QRCodeGeneratorOptions { QuietZoneSize = 0 });
        return (buffer, calculated.Size);
    }

    private static (SKBitmap bitmap, byte[] rgba, int size) RenderImage(string content)
    {
        var qr = QRCodeGenerator.Create(content.AsSpan(), QREccLevel.L);
        var sizePx = qr.Size * PixelsPerModule;
        var bitmap = new SKBitmap(new SKImageInfo(sizePx, sizePx, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap))
        {
            SymbolRenderer.Render(canvas, SKRect.Create(0, 0, sizePx, sizePx), qr, SKColors.Black, SKColors.White);
            canvas.Flush();
        }

        var rgba = new byte[sizePx * sizePx * 4];
        using (var pixmap = bitmap.PeekPixels())
        {
            var pixels = pixmap.GetPixelSpan();
            for (var y = 0; y < sizePx; y++)
            {
                pixels.Slice(y * pixmap.RowBytes, sizePx * 4).CopyTo(rgba.AsSpan(y * sizePx * 4, sizePx * 4));
            }
        }
        return (bitmap, rgba, sizePx);
    }
}
