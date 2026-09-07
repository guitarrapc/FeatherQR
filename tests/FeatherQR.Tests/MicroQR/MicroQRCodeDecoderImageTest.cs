using SkiaSharp;
using FeatherQR.SkiaSharp;
using FeatherQR.Internals.MicroQR;

namespace FeatherQR.Tests;

/// <summary>
/// Image-path decoding tests for <see cref="MicroQRCodeDecoder"/>: clean rendered
/// images (all versions × ECC), the supported geometric transforms (90° rotations,
/// mirroring, reflectance reversal, scaling, translation, quiet zone variants) and
/// a representative degradation subset per the test strategy §7. Small-angle
/// rotation and perspective are documented out of scope for the single-finder
/// Micro QR tier-1 detector.
/// </summary>
public class MicroQRCodeDecoderImageTest
{
    public static IEnumerable<(MicroQRVersion version, MicroQREccLevel eccLevel, string content)> AllVersionEccCombinations()
    {
        yield return (MicroQRVersion.M1, MicroQREccLevel.ErrorDetectionOnly, "123");
        yield return (MicroQRVersion.M2, MicroQREccLevel.L, "12345");
        yield return (MicroQRVersion.M2, MicroQREccLevel.M, "12345");
        yield return (MicroQRVersion.M3, MicroQREccLevel.L, "HELLO WORLD");
        yield return (MicroQRVersion.M3, MicroQREccLevel.M, "1234567");
        yield return (MicroQRVersion.M4, MicroQREccLevel.L, "micro qr bytes"); // lowercase forces Byte mode (M4-L capacity 15)
        yield return (MicroQRVersion.M4, MicroQREccLevel.M, "MICRO QR M4 TEST");
        yield return (MicroQRVersion.M4, MicroQREccLevel.Q, "123456789");
    }

    private static SKBitmap RenderBitmap(MicroQRCodeData data, int modulePixelSize)
    {
        return new MicroQRCodeImageBuilder(data)
            .WithModulePixelSize(modulePixelSize)
            .ToBitmap();
    }

    #region Clean images

    [Test]
    [MethodDataSource(nameof(AllVersionEccCombinations))]
    public async Task Decode_CleanRender_AllVersionsAndEccLevels(MicroQRVersion version, MicroQREccLevel eccLevel, string content)
    {
        var data = MicroQRCodeGenerator.Create(content, eccLevel, new MicroQRCodeGeneratorOptions { Version = version });
        using var bitmap = RenderBitmap(data, modulePixelSize: 8);

        var success = MicroQRCodeDecoder.TryDecode(bitmap, out var text, out var info);

        await Assert.That(success).IsTrue();
        await Assert.That(text).IsEqualTo(content);
        await Assert.That(info.Version).IsEqualTo(version);
        await Assert.That(info.EccLevel).IsEqualTo(eccLevel);
    }

    [Test]
    [Arguments(3)]
    [Arguments(5)]
    [Arguments(8)]
    [Arguments(13)]
    public async Task Decode_VariousModulePixelSizes(int modulePixelSize)
    {
        const string content = "12345";
        var data = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L);
        using var bitmap = RenderBitmap(data, modulePixelSize);

        var success = MicroQRCodeDecoder.TryDecode(bitmap, out var text, out _);

        await Assert.That(success).IsTrue();
        await Assert.That(text).IsEqualTo(content);
    }

    [Test]
    public async Task Decode_NonIntegerModuleScale()
    {
        // 300px canvas over a 21-module matrix → 14.28 px/module
        const string content = "MICRO QR M4 TEST";
        var data = MicroQRCodeGenerator.Create(content, MicroQREccLevel.M);
        using var bitmap = new MicroQRCodeImageBuilder(data).WithSize(300, 300).ToBitmap();

        var success = MicroQRCodeDecoder.TryDecode(bitmap, out var text, out _);

        await Assert.That(success).IsTrue();
        await Assert.That(text).IsEqualTo(content);
    }

    [Test]
    [Arguments(0)]
    [Arguments(90)]
    [Arguments(180)]
    [Arguments(270)]
    public async Task Decode_NonSquareRender(int degrees)
    {
        // The image builder supports independent width and height, so the image
        // decoder must preserve the finder pattern's horizontal and vertical
        // module scales instead of collapsing them to one square-module estimate.
        const string content = "MICRO QR M4 TEST";
        var data = MicroQRCodeGenerator.Create(content, MicroQREccLevel.M);
        using var rendered = new MicroQRCodeImageBuilder(data).WithSize(300, 400).ToBitmap();
        using var bitmap = Rotate(rendered, degrees);

        var success = MicroQRCodeDecoder.TryDecode(bitmap, out var text, out _);

        await Assert.That(success).IsTrue();
        await Assert.That(text).IsEqualTo(content);
    }

    [Test]
    public async Task Decode_TranslatedOnLargerCanvas()
    {
        const string content = "12345";
        var data = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L);
        using var content_bitmap = RenderBitmap(data, modulePixelSize: 8);

        // Paste off-center on a larger white canvas
        using var canvasBitmap = new SKBitmap(400, 300);
        using (var canvas = new SKCanvas(canvasBitmap))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(content_bitmap, 231, 87, SKSamplingOptions.Default);
        }

        var success = MicroQRCodeDecoder.TryDecode(canvasBitmap, out var text, out _);

        await Assert.That(success).IsTrue();
        await Assert.That(text).IsEqualTo(content);
    }

    [Test]
    [Arguments(1)]
    [Arguments(4)]
    public async Task Decode_QuietZoneVariants(int quietZoneModules)
    {
        const string content = "12345";
        var data = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { QuietZoneSize = quietZoneModules });
        using var bitmap = RenderBitmap(data, modulePixelSize: 8);

        var success = MicroQRCodeDecoder.TryDecode(bitmap, out var text, out _);

        await Assert.That(success).IsTrue();
        await Assert.That(text).IsEqualTo(content);
    }

    #endregion

    #region Geometric transforms

    private static SKBitmap Rotate(SKBitmap source, int degrees)
    {
        var swap = degrees is 90 or 270;
        var rotated = new SKBitmap(swap ? source.Height : source.Width, swap ? source.Width : source.Height);
        using var canvas = new SKCanvas(rotated);
        canvas.Clear(SKColors.White);
        canvas.Translate(rotated.Width / 2f, rotated.Height / 2f);
        canvas.RotateDegrees(degrees);
        canvas.Translate(-source.Width / 2f, -source.Height / 2f);
        canvas.DrawBitmap(source, 0, 0, SKSamplingOptions.Default);
        return rotated;
    }

    [Test]
    [Arguments(90)]
    [Arguments(180)]
    [Arguments(270)]
    public async Task Decode_RightAngleRotations(int degrees)
    {
        const string content = "MICRO QR M4 TEST";
        var data = MicroQRCodeGenerator.Create(content, MicroQREccLevel.M);
        using var bitmap = RenderBitmap(data, modulePixelSize: 8);
        using var rotated = Rotate(bitmap, degrees);

        var success = MicroQRCodeDecoder.TryDecode(rotated, out var text, out _);

        await Assert.That(success).IsTrue();
        await Assert.That(text).IsEqualTo(content);
    }

    [Test]
    [Arguments(MicroQRVersion.M1, MicroQREccLevel.ErrorDetectionOnly, "123", 30)]
    [Arguments(MicroQRVersion.M2, MicroQREccLevel.L, "12345", -7)]
    [Arguments(MicroQRVersion.M3, MicroQREccLevel.L, "HELLO WORLD", 45)]
    [Arguments(MicroQRVersion.M4, MicroQREccLevel.M, "MICRO QR M4 TEST", 5)]
    [Arguments(MicroQRVersion.M4, MicroQREccLevel.M, "MICRO QR M4 TEST", 30)]
    public async Task Decode_ArbitraryRotations(MicroQRVersion version, MicroQREccLevel eccLevel, string content, int degrees)
    {
        using var bitmap = RenderRotated(content, version, eccLevel, degrees);

        var success = MicroQRCodeDecoder.TryDecode(bitmap, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"version={version}, degrees={degrees}, status={info.Status}");
        await Assert.That(text).IsEqualTo(content);
    }

    [Test]
    [Arguments(MicroQRVersion.M1, MicroQREccLevel.ErrorDetectionOnly, "123")]
    [Arguments(MicroQRVersion.M4, MicroQREccLevel.M, "MICRO QR M4 TEST")]
    public async Task Decode_EveryIntegerRotation(MicroQRVersion version, MicroQREccLevel eccLevel, string content)
    {
        for (var degrees = 0; degrees < 90; degrees++)
        {
            using var bitmap = RenderRotated(content, version, eccLevel, degrees);

            var success = MicroQRCodeDecoder.TryDecode(bitmap, out var text, out var info);

            await Assert.That(success).IsTrue().Because($"version={version}, degrees={degrees}, status={info.Status}");
            await Assert.That(text).IsEqualTo(content).Because($"version={version}, degrees={degrees}");
        }
    }

    private static SKBitmap RenderRotated(string content, MicroQRVersion version, MicroQREccLevel eccLevel, float degrees)
    {
        var data = MicroQRCodeGenerator.Create(content, eccLevel, new MicroQRCodeGeneratorOptions { Version = version });
        var qrPx = data.Size * 8;
        var canvasPx = (int)(qrPx * 1.5f) + 16;
        var bitmap = new SKBitmap(new SKImageInfo(canvasPx, canvasPx, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        canvas.Translate(canvasPx / 2f, canvasPx / 2f);
        canvas.RotateDegrees(degrees);
        canvas.Translate(-qrPx / 2f, -qrPx / 2f);
        SymbolRenderer.Render(canvas, SKRect.Create(0, 0, qrPx, qrPx), data, SKColors.Black, SKColors.White);
        canvas.Flush();
        return bitmap;
    }

    [Test]
    public async Task Decode_MirroredImage()
    {
        const string content = "12345";
        var data = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L);
        using var bitmap = RenderBitmap(data, modulePixelSize: 8);

        using var mirrored = new SKBitmap(bitmap.Width, bitmap.Height);
        using (var canvas = new SKCanvas(mirrored))
        {
            canvas.Clear(SKColors.White);
            canvas.Scale(-1, 1, bitmap.Width / 2f, 0);
            canvas.DrawBitmap(bitmap, 0, 0, SKSamplingOptions.Default);
        }

        var success = MicroQRCodeDecoder.TryDecode(mirrored, out var text, out _);

        await Assert.That(success).IsTrue();
        await Assert.That(text).IsEqualTo(content);
    }

    [Test]
    public async Task Decode_InvertedColors()
    {
        const string content = "12345";
        var data = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L);
        using var bitmap = new MicroQRCodeImageBuilder(data)
            .WithModulePixelSize(8)
            .WithColors(codeColor: SKColors.White, backgroundColor: SKColors.Black)
            .ToBitmap();

        var success = MicroQRCodeDecoder.TryDecode(bitmap, out var text, out _);

        await Assert.That(success).IsTrue();
        await Assert.That(text).IsEqualTo(content);
    }

    #endregion

    #region Degradation subset (deterministic, per test strategy §7)

    [Test]
    public async Task Decode_JpegCompressionArtifacts()
    {
        const string content = "MICRO QR M4 TEST";
        var data = MicroQRCodeGenerator.Create(content, MicroQREccLevel.M);
        using var bitmap = RenderBitmap(data, modulePixelSize: 8);

        using var image = SKImage.FromBitmap(bitmap);
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, quality: 60);
        using var reloaded = SKBitmap.Decode(jpeg.AsSpan());

        var success = MicroQRCodeDecoder.TryDecode(reloaded, out var text, out _);

        await Assert.That(success).IsTrue();
        await Assert.That(text).IsEqualTo(content);
    }

    [Test]
    public async Task Decode_LowContrast()
    {
        const string content = "12345";
        var data = MicroQRCodeGenerator.Create(content, MicroQREccLevel.M);
        using var bitmap = RenderBitmap(data, modulePixelSize: 8);

        // Compress the dynamic range: black → 100, white → 170
        using var low = new SKBitmap(bitmap.Width, bitmap.Height);
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var p = bitmap.GetPixel(x, y);
                var v = (byte)(100 + (p.Red * 70 / 255));
                low.SetPixel(x, y, new SKColor(v, v, v));
            }
        }

        var success = MicroQRCodeDecoder.TryDecode(low, out var text, out _);

        await Assert.That(success).IsTrue();
        await Assert.That(text).IsEqualTo(content);
    }

    [Test]
    public async Task Decode_AdditiveNoise_Deterministic()
    {
        const string content = "1234567";
        var data = MicroQRCodeGenerator.Create(content, MicroQREccLevel.M);
        using var bitmap = RenderBitmap(data, modulePixelSize: 8);

        // ±24 uniform noise, fixed seed
        var rng = new Random(42);
        using var noisy = new SKBitmap(bitmap.Width, bitmap.Height);
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var p = bitmap.GetPixel(x, y);
                var v = Math.Clamp(p.Red + rng.Next(-24, 25), 0, 255);
                noisy.SetPixel(x, y, new SKColor((byte)v, (byte)v, (byte)v));
            }
        }

        var success = MicroQRCodeDecoder.TryDecode(noisy, out var text, out _);

        await Assert.That(success).IsTrue();
        await Assert.That(text).IsEqualTo(content);
    }

    #endregion

    #region Luminance / span overloads

    [Test]
    public async Task DecodeImage_LuminanceSpan_MatchesBitmapPath()
    {
        const string content = "MICRO QR M4 TEST";
        var data = MicroQRCodeGenerator.Create(content, MicroQREccLevel.M);
        using var bitmap = RenderBitmap(data, modulePixelSize: 8);

        // Grayscale conversion by hand (rendered image is already black/white)
        var luminance = new byte[bitmap.Width * bitmap.Height];
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                luminance[y * bitmap.Width + x] = bitmap.GetPixel(x, y).Red;
            }
        }

        var success = MicroQRCodeDecoder.TryDecodeImage(luminance, bitmap.Width, bitmap.Height, out var text, out var info);
        await Assert.That(success).IsTrue();
        await Assert.That(text).IsEqualTo(content);

        // Caller-provided destination overload decodes identically
        var destination = new char[MicroQRCodeDecoder.GetMaxDecodedLength(MicroQRVersion.M4)];
        var spanSuccess = MicroQRCodeDecoder.TryDecodeImage(luminance, bitmap.Width, bitmap.Height, destination, out var charsWritten, out var spanInfo);
        await Assert.That(spanSuccess).IsTrue();
        await Assert.That(new string(destination, 0, charsWritten)).IsEqualTo(content);
        await Assert.That(spanInfo.Version).IsEqualTo(info.Version);
    }

    #endregion

    #region Negative cases

    [Test]
    public async Task Decode_StandardQrImage_IsRejected()
    {
        // A Standard QR symbol must not decode as Micro QR
        using var bitmap = new QRCodeImageBuilder("HELLO WORLD").WithModulePixelSize(8).ToBitmap();

        var success = MicroQRCodeDecoder.TryDecode(bitmap, out _, out var info);

        await Assert.That(success).IsFalse();
        await Assert.That(info.Status).IsNotEqualTo(DecodeStatus.Success);
    }

    [Test]
    public async Task StandardDecoder_MicroQRImage_IsRejected()
    {
        // The Standard QR image decoder must not decode a Micro QR symbol
        var data = MicroQRCodeGenerator.Create("12345", MicroQREccLevel.L);
        using var bitmap = RenderBitmap(data, modulePixelSize: 8);

        var success = QRCodeDecoder.TryDecode(bitmap, out _, out _);

        await Assert.That(success).IsFalse();
    }

    [Test]
    public async Task Decode_BlankImage_ReturnsNotDetected()
    {
        using var bitmap = new SKBitmap(128, 128);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
        }

        var success = MicroQRCodeDecoder.TryDecode(bitmap, out var text, out var info);

        await Assert.That(success).IsFalse();
        await Assert.That(text).IsEqualTo(string.Empty);
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.NotDetected);
    }

    [Test]
    public async Task Decode_TooSmallImage_ReturnsNotDetected()
    {
        using var bitmap = new SKBitmap(8, 8);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Black);
        }

        var success = MicroQRCodeDecoder.TryDecode(bitmap, out _, out var info);

        await Assert.That(success).IsFalse();
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.NotDetected);
    }

    [Test]
    public async Task Decode_NullBitmap_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => MicroQRCodeDecoder.TryDecode((SKBitmap)null!, out _, out _));
    }

    [Test]
    public async Task DecodeLuminance_DimensionsOverflowInt_ReturnsNotDetected()
    {
        var status = MicroQRImageDecoder.DecodeLuminance(
            ReadOnlySpan<byte>.Empty,
            65_536,
            65_536,
            Span<char>.Empty,
            out var charsWritten,
            out var info);

        await Assert.That(status).IsEqualTo(DecodeStatus.NotDetected);
        await Assert.That(charsWritten).IsEqualTo(0);
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.NotDetected);
    }

    #endregion
}
