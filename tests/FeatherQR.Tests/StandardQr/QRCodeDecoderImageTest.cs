using FeatherQR.SkiaSharp;
using SkiaSharp;
namespace FeatherQR.Tests;

/// <summary>
/// Image-level decode tests (Tier 1): rendered bitmaps, scaling, rotation,
/// mirroring and negative cases.
/// </summary>
public class QRCodeDecoderImageTest
{
    [Test]
    [Arguments("Hello, World!", QREccLevel.M)]
    [Arguments("0123456789", QREccLevel.L)]
    [Arguments("HELLO WORLD $%*+-./:", QREccLevel.Q)]
    [Arguments("https://example.com/path?query=value", QREccLevel.H)]
    public async Task Decode_RenderedBitmap(string content, QREccLevel eccLevel)
    {
        using var bitmap = RenderQr(content, eccLevel, pixelsPerModule: 8);

        await Assert.That(QRCodeDecoder.TryDecode(bitmap, out var decoded, out var info)).IsTrue().Because($"status={info.Status}");
        await Assert.That(decoded).IsEqualTo(content);
        await Assert.That(info.EccLevel).IsEqualTo(eccLevel);
    }

    [Test]
    [Arguments("縺薙ｓ縺ｫ縺｡縺ｯ荳也阜")]
    [Arguments("脂 emoji 至")]
    public async Task Decode_RenderedBitmap_Utf8(string content)
    {
        using var bitmap = RenderQr(content, QREccLevel.M, pixelsPerModule: 8, eciMode: EciMode.Utf8);

        await Assert.That(QRCodeDecoder.TryDecode(bitmap, out var decoded, out var info)).IsTrue().Because($"status={info.Status}");
        await Assert.That(decoded).IsEqualTo(content);
    }

    [Test]
    [Arguments(3)]
    [Arguments(5)]
    [Arguments(11)]
    public async Task Decode_VariousModuleSizes(int pixelsPerModule)
    {
        var content = "module size test";
        using var bitmap = RenderQr(content, QREccLevel.M, pixelsPerModule);

        await Assert.That(QRCodeDecoder.TryDecode(bitmap, out var decoded, out var info)).IsTrue().Because($"ppm={pixelsPerModule}, status={info.Status}");
        await Assert.That(decoded).IsEqualTo(content);
    }

    [Test]
    [Arguments(12)]
    [Arguments(13)]
    [Arguments(14)] // 512px / 81 modules = 6.32 px/module 遯ｶ繝ｻsnapped one version low before the run-boundary fix
    [Arguments(15)]
    [Arguments(16)]
    [Arguments(17)] // 5.51 px/module 遯ｶ繝ｻsame regression
    [Arguments(18)]
    [Arguments(20)]
    [Arguments(25)]
    public async Task Decode_FixedCanvas_NonIntegerModuleSize(int version)
    {
        // A fixed 512px canvas gives fractional pixels-per-module at most versions;
        // the pixel-quantized module-size measurement must not snap the dimension
        // estimate to a neighboring version (regression: v14/v17/v18/v20 read as
        // one version lower and failed with DataUncorrectable).
        var content = "https://github.com/guitarrapc/FeatherQR";
        var qr = QRCodeGenerator.Create(content, QREccLevel.H, new QRCodeGeneratorOptions { Version = version, QuietZoneSize = 4 });
        using var bitmap = new SKBitmap(new SKImageInfo(512, 512, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            SymbolRenderer.Render(canvas, SKRect.Create(0, 0, 512, 512), qr, SKColors.Black, SKColors.White);
            canvas.Flush();
        }

        await Assert.That(QRCodeDecoder.TryDecode(bitmap, out var decoded, out var info)).IsTrue().Because($"version={version}, status={info.Status}, detected version={info.Version}");
        await Assert.That(decoded).IsEqualTo(content);
        await Assert.That(info.Version).IsEqualTo(version);
    }

    [Test]
    public async Task Decode_LargerVersion()
    {
        var content = string.Join(";", Enumerable.Range(0, 40).Select(i => $"item{i:D4}"));
        using var bitmap = RenderQr(content, QREccLevel.M, pixelsPerModule: 6);

        await Assert.That(QRCodeDecoder.TryDecode(bitmap, out var decoded, out var info)).IsTrue().Because($"status={info.Status}");
        await Assert.That(decoded).IsEqualTo(content);
        await Assert.That(info.Version >= 10).IsTrue().Because($"expected a large version, got {info.Version}");
    }

    [Test]
    [Arguments(90)]
    [Arguments(180)]
    [Arguments(270)]
    public async Task Decode_RightAngleRotations(int degrees)
    {
        var content = $"rotation {degrees}";
        using var bitmap = RenderRotatedQr(content, QREccLevel.M, pixelsPerModule: 8, degrees);

        await Assert.That(QRCodeDecoder.TryDecode(bitmap, out var decoded, out var info)).IsTrue().Because($"degrees={degrees}, status={info.Status}");
        await Assert.That(decoded).IsEqualTo(content);
    }

    [Test]
    [Arguments(5)]
    [Arguments(-7)]
    [Arguments(30)]
    [Arguments(45)]
    public async Task Decode_ArbitraryRotations(int degrees)
    {
        var content = $"tilt {degrees}";
        using var bitmap = RenderRotatedQr(content, QREccLevel.M, pixelsPerModule: 8, degrees);

        await Assert.That(QRCodeDecoder.TryDecode(bitmap, out var decoded, out var info)).IsTrue().Because($"degrees={degrees}, status={info.Status}");
        await Assert.That(decoded).IsEqualTo(content);
    }

    [Test]
    public async Task Decode_InvertedImage_LightOnDark()
    {
        // Reflectance-reversed rendering (dark-mode style): white modules on black
        var content = "inverted palette";
        var qr = QRCodeGenerator.Create(content, QREccLevel.M);
        var sizePx = qr.Size * 8;
        using var bitmap = new SKBitmap(new SKImageInfo(sizePx, sizePx, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            SymbolRenderer.Render(canvas, SKRect.Create(0, 0, sizePx, sizePx), qr, SKColors.White, SKColors.Black);
            canvas.Flush();
        }

        await Assert.That(QRCodeDecoder.TryDecode(bitmap, out var decoded, out var info)).IsTrue().Because($"status={info.Status}");
        await Assert.That(decoded).IsEqualTo(content);
    }

    [Test]
    public async Task Decode_MirroredImage()
    {
        var content = "mirrored capture";
        using var source = RenderQr(content, QREccLevel.M, pixelsPerModule: 8);
        using var mirrored = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(mirrored))
        {
            canvas.Clear(SKColors.White);
            canvas.Scale(-1, 1, source.Width / 2f, 0);
            canvas.DrawBitmap(source, 0, 0, SKSamplingOptions.Default);
        }

        await Assert.That(QRCodeDecoder.TryDecode(mirrored, out var decoded, out var info)).IsTrue().Because($"status={info.Status}");
        await Assert.That(decoded).IsEqualTo(content);
    }

    [Test]
    public async Task Decode_LuminanceSpan()
    {
        var content = "luminance span";
        var qr = QRCodeGenerator.Create(content, QREccLevel.M);
        var size = qr.Size;
        const int Scale = 6;
        var width = size * Scale;
        var luminance = new byte[width * width];
        luminance.AsSpan().Fill(255);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (!qr[y, x])
                    continue;
                for (var dy = 0; dy < Scale; dy++)
                {
                    luminance.AsSpan(((y * Scale + dy) * width) + x * Scale, Scale).Clear();
                }
            }
        }

        await Assert.That(QRCodeDecoder.TryDecodeImage(luminance, width, width, out var decoded, out var info)).IsTrue().Because($"status={info.Status}");
        await Assert.That(decoded).IsEqualTo(content);

        // Char-span destination path
        Span<char> destination = stackalloc char[QRCodeDecoder.GetMaxDecodedLength(info.Version)];
        var ok = QRCodeDecoder.TryDecodeImage(luminance, width, width, destination, out var charsWritten, out _);
        var decodedString = destination.Slice(0, charsWritten).ToString();
        await Assert.That(ok).IsTrue();
        await Assert.That(decodedString).IsEqualTo(content);
    }

    [Test]
    public async Task Decode_GrayBitmap_ColorTypeVariants()
    {
        var content = "gray8 bitmap";
        using var source = RenderQr(content, QREccLevel.M, pixelsPerModule: 8);
        using var gray = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.Gray8));
        using (var canvas = new SKCanvas(gray))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(source, 0, 0, SKSamplingOptions.Default);
        }

        await Assert.That(QRCodeDecoder.TryDecode(gray, out var decoded, out var info)).IsTrue().Because($"status={info.Status}");
        await Assert.That(decoded).IsEqualTo(content);
    }

    [Test]
    public async Task Decode_BlankBitmap_ReturnsNotDetected()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(200, 200, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
        }

        await Assert.That(QRCodeDecoder.TryDecode(bitmap, out var text, out var info)).IsFalse();
        await Assert.That(text).IsEqualTo(string.Empty);
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.NotDetected);
    }

    [Test]
    public async Task Decode_NoiseBitmap_ReturnsNotDetected()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(200, 200, SKColorType.Bgra8888, SKAlphaType.Premul));
        var random = new Random(42);
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var v = (byte)random.Next(256);
                bitmap.SetPixel(x, y, new SKColor(v, v, v));
            }
        }

        await Assert.That(QRCodeDecoder.TryDecode(bitmap, out _, out var info)).IsFalse();
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.NotDetected);
    }

    [Test]
    public async Task Decode_TinyBitmap_ReturnsNotDetected()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(10, 10, SKColorType.Bgra8888, SKAlphaType.Premul));

        await Assert.That(QRCodeDecoder.TryDecode(bitmap, out _, out var info)).IsFalse();
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.NotDetected);
    }

    [Test]
    public async Task Decode_NullBitmap_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => QRCodeDecoder.TryDecode((SKBitmap)null!, out _));
    }

    private static SKBitmap RenderQr(string content, QREccLevel eccLevel, int pixelsPerModule, EciMode eciMode = EciMode.Default)
    {
        var qr = QRCodeGenerator.Create(content, eccLevel, new QRCodeGeneratorOptions { EciMode = eciMode });
        var sizePx = qr.Size * pixelsPerModule;
        var bitmap = new SKBitmap(new SKImageInfo(sizePx, sizePx, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        SymbolRenderer.Render(canvas, SKRect.Create(0, 0, sizePx, sizePx), qr, SKColors.Black, SKColors.White);
        canvas.Flush();
        return bitmap;
    }

    private static SKBitmap RenderRotatedQr(string content, QREccLevel eccLevel, int pixelsPerModule, float degrees)
    {
        var qr = QRCodeGenerator.Create(content, eccLevel, new QRCodeGeneratorOptions { EciMode = EciMode.Default });
        var qrPx = qr.Size * pixelsPerModule;
        // Room for the rotated square plus margin
        var canvasPx = (int)(qrPx * 1.5f) + 16;
        var bitmap = new SKBitmap(new SKImageInfo(canvasPx, canvasPx, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        canvas.Translate(canvasPx / 2f, canvasPx / 2f);
        canvas.RotateDegrees(degrees);
        canvas.Translate(-qrPx / 2f, -qrPx / 2f);
        SymbolRenderer.Render(canvas, SKRect.Create(0, 0, qrPx, qrPx), qr, SKColors.Black, SKColors.White);
        canvas.Flush();
        return bitmap;
    }
}
