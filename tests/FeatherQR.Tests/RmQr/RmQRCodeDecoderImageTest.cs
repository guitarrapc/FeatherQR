using SkiaSharp;
using FeatherQR.SkiaSharp;
using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// Image-path decoding tests for <see cref="RmQRCodeDecoder"/>: clean rendered
/// images (all 32 versions × 2 ECC levels), the supported geometric transforms
/// (scaling, non-integer scale, letterboxing, translation, quiet zone variants,
/// right-angle and arbitrary rotation, mirroring, reflectance reversal), the
/// extreme aspect ratios, a deterministic degradation subset and negative cases.
/// </summary>
public class RmQRCodeDecoderImageTest
{
    public static IEnumerable<(RmQRVersion version, RmQREccLevel eccLevel, string content)> AllVersionEccCombinations()
    {
        foreach (var version in Enum.GetValues<RmQRVersion>())
        {
            // Short payloads fit every version at both levels ("R7x43-H" holds 3 alphanumerics
            // per the capacity tables; a numeric string of 4 digits fits every version).
            yield return (version, RmQREccLevel.M, "1234");
            yield return (version, RmQREccLevel.H, "1234");
        }
    }

    private static RmQRCodeData Create(string content, RmQREccLevel eccLevel, RmQRVersion? version = null, int quietZone = 2)
        => RmQRCodeGenerator.Create(content, eccLevel, new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = quietZone });

    private static SKBitmap RenderBitmap(RmQRCodeData data, int modulePixelSize)
        => new RmQRCodeImageBuilder(data).WithModulePixelSize(modulePixelSize).ToBitmap();

    /// <summary>R7x43-M holds 7 alphanumerics; every other version takes the longer payload.</summary>
    private static string ContentFor(RmQRVersion version)
        => version == RmQRVersion.R7x43 ? "RMQR 43" : "RMQR IMAGE 123";

    #region Clean images

    [Test]
    [MethodDataSource(nameof(AllVersionEccCombinations))]
    public async Task Decode_CleanRender_AllVersionsAndEccLevels(RmQRVersion version, RmQREccLevel eccLevel, string content)
    {
        var data = Create(content, eccLevel, version);
        using var bitmap = RenderBitmap(data, modulePixelSize: 6);

        var success = RmQRCodeDecoder.TryDecode(bitmap, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"version={version}, ecc={eccLevel}, status={info.Status}");
        await Assert.That(text).IsEqualTo(content);
        await Assert.That(info.Version).IsEqualTo(version);
        await Assert.That(info.EccLevel).IsEqualTo(eccLevel);
        await Assert.That(info.ErrorsCorrected).IsEqualTo(0);
    }

    [Test]
    [Arguments(RmQRVersion.R7x43, 3)]
    [Arguments(RmQRVersion.R7x43, 5)]
    [Arguments(RmQRVersion.R7x43, 8)]
    [Arguments(RmQRVersion.R7x43, 13)]
    [Arguments(RmQRVersion.R17x139, 3)]
    [Arguments(RmQRVersion.R17x139, 5)]
    [Arguments(RmQRVersion.R17x139, 8)]
    [Arguments(RmQRVersion.R17x139, 13)]
    public async Task Decode_VariousModulePixelSizes(RmQRVersion version, int modulePixelSize)
    {
        var content = ContentFor(version);
        var data = Create(content, RmQREccLevel.M, version);
        using var bitmap = RenderBitmap(data, modulePixelSize);

        var success = RmQRCodeDecoder.TryDecode(bitmap, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"version={version}, px={modulePixelSize}, status={info.Status}");
        await Assert.That(text).IsEqualTo(content);
    }

    [Test]
    [Arguments(RmQRVersion.R7x43, 300)]
    [Arguments(RmQRVersion.R11x77, 500)]
    [Arguments(RmQRVersion.R17x139, 1000)]
    [Arguments(RmQRVersion.R17x139, 730)]
    public async Task Decode_NonIntegerModuleScale(RmQRVersion version, int widthPx)
    {
        // WithSize with preserved aspect ratio: e.g. 300 px over 47 modules (43 + quiet zone) → 6.38 px/module
        var content = ContentFor(version);
        var data = Create(content, RmQREccLevel.M, version);
        using var bitmap = new RmQRCodeImageBuilder(data).WithSize(widthPx, widthPx).ToBitmap();

        var success = RmQRCodeDecoder.TryDecode(bitmap, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"version={version}, width={widthPx}, status={info.Status}");
        await Assert.That(text).IsEqualTo(content);
    }

    [Test]
    [Arguments(0)]
    [Arguments(90)]
    [Arguments(180)]
    [Arguments(270)]
    public async Task Decode_NonSquareModules(int degrees)
    {
        // Rendering into a rectangle without aspect preservation stretches modules
        // (here 8 × 12 px); the two finder axes must keep independent module scales.
        const string content = "RMQR IMAGE 123";
        var data = Create(content, RmQREccLevel.M, RmQRVersion.R11x59);
        using var rendered = new SKBitmap(data.Width * 8, data.Height * 12);
        using (var canvas = new SKCanvas(rendered))
        {
            SymbolRenderer.Render(canvas, SKRect.Create(0, 0, rendered.Width, rendered.Height), data, SKColors.Black, SKColors.White);
            canvas.Flush();
        }
        using var bitmap = Rotate(rendered, degrees);

        var success = RmQRCodeDecoder.TryDecode(bitmap, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"degrees={degrees}, status={info.Status}");
        await Assert.That(text).IsEqualTo(content);
    }

    [Test]
    public async Task Decode_TranslatedOnLargerCanvas()
    {
        const string content = "RMQR IMAGE 123";
        var data = Create(content, RmQREccLevel.M, RmQRVersion.R9x77);
        using var symbol = RenderBitmap(data, modulePixelSize: 6);

        using var canvasBitmap = new SKBitmap(1000, 400);
        using (var canvas = new SKCanvas(canvasBitmap))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(symbol, 331, 187, SKSamplingOptions.Default);
        }

        var success = RmQRCodeDecoder.TryDecode(canvasBitmap, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"status={info.Status}");
        await Assert.That(text).IsEqualTo(content);
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(4)]
    public async Task Decode_QuietZoneVariants(int quietZoneModules)
    {
        const string content = "RMQR IMAGE 123";
        var data = Create(content, RmQREccLevel.M, RmQRVersion.R13x77, quietZoneModules);
        using var bitmap = RenderBitmap(data, modulePixelSize: 6);

        var success = RmQRCodeDecoder.TryDecode(bitmap, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"quietZone={quietZoneModules}, status={info.Status}");
        await Assert.That(text).IsEqualTo(content);
    }

    [Test]
    [Arguments(RmQRVersion.R7x139)]
    [Arguments(RmQRVersion.R11x27)]
    [Arguments(RmQRVersion.R13x27)]
    public async Task Decode_ExtremeAspectRatios(RmQRVersion version)
    {
        const string content = "1234567";
        var data = Create(content, RmQREccLevel.H, version);
        using var bitmap = RenderBitmap(data, modulePixelSize: 7);

        var success = RmQRCodeDecoder.TryDecode(bitmap, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"version={version}, status={info.Status}");
        await Assert.That(text).IsEqualTo(content);
        await Assert.That(info.Version).IsEqualTo(version);
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
    [Arguments(RmQRVersion.R7x43, 90)]
    [Arguments(RmQRVersion.R7x43, 180)]
    [Arguments(RmQRVersion.R7x43, 270)]
    [Arguments(RmQRVersion.R15x99, 90)]
    [Arguments(RmQRVersion.R15x99, 180)]
    [Arguments(RmQRVersion.R15x99, 270)]
    public async Task Decode_RightAngleRotations(RmQRVersion version, int degrees)
    {
        var content = ContentFor(version);
        var data = Create(content, RmQREccLevel.M, version);
        using var bitmap = RenderBitmap(data, modulePixelSize: 6);
        using var rotated = Rotate(bitmap, degrees);

        var success = RmQRCodeDecoder.TryDecode(rotated, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"version={version}, degrees={degrees}, status={info.Status}");
        await Assert.That(text).IsEqualTo(content);
    }

    [Test]
    [Arguments(RmQRVersion.R7x43, 30)]
    [Arguments(RmQRVersion.R7x43, -7)]
    [Arguments(RmQRVersion.R11x59, 45)]
    [Arguments(RmQRVersion.R13x77, 5)]
    [Arguments(RmQRVersion.R17x139, 12)]
    [Arguments(RmQRVersion.R17x139, 30)]
    public async Task Decode_ArbitraryRotations(RmQRVersion version, int degrees)
    {
        var content = ContentFor(version);
        using var bitmap = RenderRotated(content, version, RmQREccLevel.M, degrees);

        var success = RmQRCodeDecoder.TryDecode(bitmap, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"version={version}, degrees={degrees}, status={info.Status}");
        await Assert.That(text).IsEqualTo(content);
    }

    [Test]
    [Arguments(RmQRVersion.R7x43)]
    [Arguments(RmQRVersion.R13x77)]
    public async Task Decode_EveryIntegerRotation(RmQRVersion version)
    {
        var content = ContentFor(version);
        for (var degrees = 0; degrees < 90; degrees++)
        {
            using var bitmap = RenderRotated(content, version, RmQREccLevel.M, degrees);

            var success = RmQRCodeDecoder.TryDecode(bitmap, out var text, out var info);

            await Assert.That(success).IsTrue().Because($"version={version}, degrees={degrees}, status={info.Status}");
            await Assert.That(text).IsEqualTo(content).Because($"version={version}, degrees={degrees}");
        }
    }

    private static SKBitmap RenderRotated(string content, RmQRVersion version, RmQREccLevel eccLevel, float degrees)
    {
        var data = Create(content, eccLevel, version);
        var widthPx = data.Width * 8;
        var heightPx = data.Height * 8;
        var diagonal = (int)Math.Sqrt(widthPx * widthPx + heightPx * heightPx);
        var canvasPx = diagonal + 32;
        var bitmap = new SKBitmap(new SKImageInfo(canvasPx, canvasPx, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        canvas.Translate(canvasPx / 2f, canvasPx / 2f);
        canvas.RotateDegrees(degrees);
        canvas.Translate(-widthPx / 2f, -heightPx / 2f);
        SymbolRenderer.Render(canvas, SKRect.Create(0, 0, widthPx, heightPx), data, SKColors.Black, SKColors.White);
        canvas.Flush();
        return bitmap;
    }

    [Test]
    [Arguments(RmQRVersion.R7x43)]
    [Arguments(RmQRVersion.R17x139)]
    public async Task Decode_MirroredImage(RmQRVersion version)
    {
        var content = ContentFor(version);
        var data = Create(content, RmQREccLevel.M, version);
        using var bitmap = RenderBitmap(data, modulePixelSize: 6);

        using var mirrored = new SKBitmap(bitmap.Width, bitmap.Height);
        using (var canvas = new SKCanvas(mirrored))
        {
            canvas.Clear(SKColors.White);
            canvas.Scale(-1, 1, bitmap.Width / 2f, 0);
            canvas.DrawBitmap(bitmap, 0, 0, SKSamplingOptions.Default);
        }

        var success = RmQRCodeDecoder.TryDecode(mirrored, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"version={version}, status={info.Status}");
        await Assert.That(text).IsEqualTo(content);
    }

    [Test]
    public async Task Decode_InvertedColors()
    {
        const string content = "RMQR IMAGE 123";
        var data = Create(content, RmQREccLevel.M, RmQRVersion.R11x59);
        using var bitmap = new RmQRCodeImageBuilder(data)
            .WithModulePixelSize(6)
            .WithColors(codeColor: SKColors.White, backgroundColor: SKColors.Black)
            .ToBitmap();

        var success = RmQRCodeDecoder.TryDecode(bitmap, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"status={info.Status}");
        await Assert.That(text).IsEqualTo(content);
    }

    #endregion

    #region Degradation subset (deterministic)

    [Test]
    [Arguments(RmQRVersion.R7x43)]
    [Arguments(RmQRVersion.R17x139)]
    public async Task Decode_JpegCompressionArtifacts(RmQRVersion version)
    {
        var content = ContentFor(version);
        var data = Create(content, RmQREccLevel.M, version);
        using var bitmap = RenderBitmap(data, modulePixelSize: 8);

        using var image = SKImage.FromBitmap(bitmap);
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, quality: 60);
        using var reloaded = SKBitmap.Decode(jpeg.AsSpan());

        var success = RmQRCodeDecoder.TryDecode(reloaded, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"version={version}, status={info.Status}");
        await Assert.That(text).IsEqualTo(content);
    }

    [Test]
    public async Task Decode_LowContrast()
    {
        const string content = "RMQR IMAGE 123";
        var data = Create(content, RmQREccLevel.M, RmQRVersion.R11x77);
        using var bitmap = RenderBitmap(data, modulePixelSize: 6);

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

        var success = RmQRCodeDecoder.TryDecode(low, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"status={info.Status}");
        await Assert.That(text).IsEqualTo(content);
    }

    [Test]
    public async Task Decode_AdditiveNoise_Deterministic()
    {
        const string content = "RMQR IMAGE 123";
        var data = Create(content, RmQREccLevel.M, RmQRVersion.R13x99);
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

        var success = RmQRCodeDecoder.TryDecode(noisy, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"status={info.Status}");
        await Assert.That(text).IsEqualTo(content);
    }

    #endregion

    #region Luminance / span overloads

    [Test]
    public async Task DecodeImage_LuminanceSpan_MatchesBitmapPath()
    {
        const string content = "RMQR IMAGE 123";
        var data = Create(content, RmQREccLevel.M, RmQRVersion.R11x59);
        using var bitmap = RenderBitmap(data, modulePixelSize: 6);

        var luminance = new byte[bitmap.Width * bitmap.Height];
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                luminance[y * bitmap.Width + x] = bitmap.GetPixel(x, y).Red;
            }
        }

        var success = RmQRCodeDecoder.TryDecodeImage(luminance, bitmap.Width, bitmap.Height, out var text, out var info);
        await Assert.That(success).IsTrue().Because($"status={info.Status}");
        await Assert.That(text).IsEqualTo(content);
        await Assert.That(info.Version).IsEqualTo(RmQRVersion.R11x59);

        var destination = new char[RmQRCodeDecoder.GetMaxDecodedLength(RmQRVersion.R17x139)];
        var spanSuccess = RmQRCodeDecoder.TryDecodeImage(luminance, bitmap.Width, bitmap.Height, destination, out var charsWritten, out var spanInfo);
        await Assert.That(spanSuccess).IsTrue();
        await Assert.That(new string(destination, 0, charsWritten)).IsEqualTo(content);
        await Assert.That(spanInfo.Version).IsEqualTo(info.Version);
    }

    [Test]
    [NotInParallel]
    public async Task DecodeImage_DestinationTooSmall_ReportsStatus_WithoutRunningTheRefinementSearch()
    {
        // A destination that cannot hold the payload is terminal for the finder that
        // read the symbol: it went through format decode and RS, so no perspective
        // variant, further frame of that finder or inverted retry can change the
        // outcome. Before the fix the too-small call cost ~250-500× the sized one (full
        // perspective search + inverted pass); now it must stay in the same order. The
        // bound is deliberately loose (50× + 250 ms) and the test runs alone: the guarded
        // regression is two orders of magnitude, a scheduling stall must not fail it.
        const string content = "RMQR IMAGE 123";
        var data = Create(content, RmQREccLevel.M, RmQRVersion.R13x99);
        using var bitmap = RenderBitmap(data, modulePixelSize: 6);
        var luminance = new byte[bitmap.Width * bitmap.Height];
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                luminance[y * bitmap.Width + x] = bitmap.GetPixel(x, y).Red;

        var sized = new char[RmQRCodeDecoder.GetMaxDecodedLength(RmQRVersion.R17x139)];
        var tiny = new char[4];

        // Warm up both paths once, then time.
        RmQRCodeDecoder.TryDecodeImage(luminance, bitmap.Width, bitmap.Height, sized, out _, out _);
        RmQRCodeDecoder.TryDecodeImage(luminance, bitmap.Width, bitmap.Height, tiny, out _, out _);

        const int iterations = 20;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
            RmQRCodeDecoder.TryDecodeImage(luminance, bitmap.Width, bitmap.Height, sized, out _, out _);
        var sizedElapsed = stopwatch.Elapsed;

        stopwatch.Restart();
        var ok = false;
        var info = default(RmQRCodeDecodeInfo);
        for (var i = 0; i < iterations; i++)
            ok = RmQRCodeDecoder.TryDecodeImage(luminance, bitmap.Width, bitmap.Height, tiny, out _, out info);
        var tinyElapsed = stopwatch.Elapsed;

        await Assert.That(ok).IsFalse();
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.DestinationTooSmall);
        await Assert.That(info.Version).IsEqualTo(RmQRVersion.R13x99);
        var bound = TimeSpan.FromTicks(sizedElapsed.Ticks * 50) + TimeSpan.FromMilliseconds(250);
        await Assert.That(tinyElapsed).IsLessThan(bound)
            .Because($"too-small destination must short-circuit: sized {sizedElapsed.TotalMilliseconds:F2} ms vs tiny {tinyElapsed.TotalMilliseconds:F2} ms over {iterations} runs");

        // Same report for a reflectance-reversed symbol (the inverted pass honours the terminal status too).
        var inverted = new byte[luminance.Length];
        for (var i = 0; i < inverted.Length; i++)
            inverted[i] = (byte)(255 - luminance[i]);
        await Assert.That(RmQRCodeDecoder.TryDecodeImage(inverted, bitmap.Width, bitmap.Height, tiny, out _, out var invertedInfo)).IsFalse();
        await Assert.That(invertedInfo.Status).IsEqualTo(DecodeStatus.DestinationTooSmall);
        await Assert.That(invertedInfo.Version).IsEqualTo(RmQRVersion.R13x99);
    }

    [Test]
    public async Task DecodeImage_DestinationTooSmallForOneSymbol_StillFindsAnotherThatFits()
    {
        // Two symbols in one image, the first (by finder confidence) too long for the
        // caller's destination: the too-small outcome is terminal for THAT finder only,
        // the other finder is still tried and its short payload is returned, whichever
        // symbol comes first in the image.
        var big = Create("RMQR IMAGE 123 LONGER PAYLOAD", RmQREccLevel.M, RmQRVersion.R13x99);
        var small = Create("AB1", RmQREccLevel.M, RmQRVersion.R7x43);
        using var bigBitmap = RenderBitmap(big, modulePixelSize: 6);
        using var smallBitmap = RenderBitmap(small, modulePixelSize: 6);
        var destination = new char[8];

        foreach (var bigFirst in new[] { true, false })
        {
            var width = Math.Max(bigBitmap.Width, smallBitmap.Width) + 24;
            var height = bigBitmap.Height + smallBitmap.Height + 36;
            using var canvas = new SKBitmap(width, height);
            using (var surface = new SKCanvas(canvas))
            {
                surface.Clear(SKColors.White);
                var (top, bottom) = bigFirst ? (bigBitmap, smallBitmap) : (smallBitmap, bigBitmap);
                surface.DrawBitmap(top, 12, 12, SKSamplingOptions.Default);
                surface.DrawBitmap(bottom, 12, 12 + top.Height + 12, SKSamplingOptions.Default);
            }

            var luminance = new byte[width * height];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    luminance[y * width + x] = canvas.GetPixel(x, y).Red;

            var ok = RmQRCodeDecoder.TryDecodeImage(luminance, width, height, destination, out var written, out var info);
            await Assert.That(ok).IsTrue().Because($"bigFirst={bigFirst}, status={info.Status}, version={info.Version}");
            await Assert.That(new string(destination, 0, written)).IsEqualTo("AB1");
            await Assert.That(info.Version).IsEqualTo(RmQRVersion.R7x43);
        }
    }

    [Test]
    public async Task DecodeImage_LuminanceTooSmall_Throws()
    {
        var luminance = new byte[10];
        var destination = new char[16];
        Assert.Throws<ArgumentException>(() => RmQRCodeDecoder.TryDecodeImage(luminance, 4, 4, out _, out _));
        Assert.Throws<ArgumentException>(() => RmQRCodeDecoder.TryDecodeImage(luminance, 4, 4, destination, out _, out _));
    }

#if !DEBUG
    [Test]
    public async Task DecodeImage_SpanDestination_DoesNotAllocate()
    {
        const string content = "RMQR IMAGE 123";
        var data = Create(content, RmQREccLevel.M, RmQRVersion.R17x139);
        using var bitmap = RenderBitmap(data, modulePixelSize: 6);
        var luminance = new byte[bitmap.Width * bitmap.Height];
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                luminance[y * bitmap.Width + x] = bitmap.GetPixel(x, y).Red;
        var destination = new char[RmQRCodeDecoder.GetMaxDecodedLength(RmQRVersion.R17x139)];

        // Warm up (JIT, pool)
        RmQRCodeDecoder.TryDecodeImage(luminance, bitmap.Width, bitmap.Height, destination, out _, out _);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var success = RmQRCodeDecoder.TryDecodeImage(luminance, bitmap.Width, bitmap.Height, destination, out var charsWritten, out _);
        var after = GC.GetAllocatedBytesForCurrentThread();

        await Assert.That(success).IsTrue();
        await Assert.That(new string(destination, 0, charsWritten)).IsEqualTo(content);
        await Assert.That(after - before).IsEqualTo(0L);
    }
#endif

    #endregion

    #region Negative cases

    [Test]
    public async Task Decode_StandardQrImage_IsRejected()
    {
        using var bitmap = new QRCodeImageBuilder("HELLO WORLD").WithModulePixelSize(8).ToBitmap();

        var success = RmQRCodeDecoder.TryDecode(bitmap, out _, out var info);

        await Assert.That(success).IsFalse();
        await Assert.That(info.Status).IsNotEqualTo(DecodeStatus.Success);
    }

    [Test]
    public async Task Decode_MicroQrImage_IsRejected()
    {
        var micro = MicroQRCodeGenerator.Create("12345", MicroQREccLevel.L);
        using var bitmap = new MicroQRCodeImageBuilder(micro).WithModulePixelSize(8).ToBitmap();

        var success = RmQRCodeDecoder.TryDecode(bitmap, out _, out var info);

        await Assert.That(success).IsFalse();
        await Assert.That(info.Status).IsNotEqualTo(DecodeStatus.Success);
    }

    [Test]
    public async Task OtherDecoders_RmQRImage_AreRejected()
    {
        var data = Create("RMQR IMAGE 123", RmQREccLevel.M, RmQRVersion.R11x59);
        using var bitmap = RenderBitmap(data, modulePixelSize: 6);

        await Assert.That(QRCodeDecoder.TryDecode(bitmap, out _, out _)).IsFalse();
        await Assert.That(MicroQRCodeDecoder.TryDecode(bitmap, out _, out _)).IsFalse();
    }

    [Test]
    public async Task Decode_BlankImage_ReturnsNotDetected()
    {
        using var bitmap = new SKBitmap(128, 64);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
        }

        var success = RmQRCodeDecoder.TryDecode(bitmap, out var text, out var info);

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

        var success = RmQRCodeDecoder.TryDecode(bitmap, out _, out var info);

        await Assert.That(success).IsFalse();
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.NotDetected);
    }

    [Test]
    public async Task Decode_NullBitmap_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => RmQRCodeDecoder.TryDecode((SKBitmap)null!, out _, out _));
    }

    [Test]
    public async Task DecodeLuminance_DimensionsOverflowInt_ReturnsNotDetected()
    {
        var status = RmQRImageDecoder.DecodeLuminance(
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
