using SkiaSharp;
using System.Buffers;
using System.Text;
using FeatherQR.SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// <see cref="RmQRCodeImageBuilder"/>: full-matrix module-to-pixel parity for every
/// version, the rectangular layout rules (module pixel size, letterbox into an
/// explicit canvas, width-only default with aspect-ratio height), quiet zone
/// default, static helpers, SVG structure, validation, and the low-level renderer /
/// canvas extension entry points.
/// </summary>
public class RmQRCodeImageBuilderUnitTest
{
    public static IEnumerable<RmQRVersion> AllVersions() => Enum.GetValues<RmQRVersion>();

    private static bool IsDark(SKColor c) => c.Red < 128 && c.Green < 128 && c.Blue < 128;

    // ---- construction ------------------------------------------------------------------

    [Test]
    public async Task Constructor_ValidContent_Success()
    {
        var builder = new RmQRCodeImageBuilder("rMQR");
        await Assert.That(builder).IsNotNull();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments(null)]
    public async Task Constructor_InvalidContent_ThrowsArgumentException(string? content)
    {
        await Assert.That(() => new RmQRCodeImageBuilder(content!)).Throws<ArgumentException>();
    }

    [Test]
    public async Task Constructor_NullData_ThrowsArgumentNullException()
    {
        await Assert.That(() => new RmQRCodeImageBuilder((RmQRCodeData)null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task WithEciMode_ExplicitUtf8_UsesTheEciAwareGeneratorPath()
    {
        var expected = RmQRCodeGenerator.Create("a", RmQREccLevel.H, new RmQRCodeGeneratorOptions { EciMode = EciMode.Utf8 });
        using var bitmap = new RmQRCodeImageBuilder("a")
            .WithErrorCorrection(RmQREccLevel.H)
            .WithEciMode(EciMode.Utf8)
            .WithModulePixelSize(1)
            .ToBitmap();

        await Assert.That(bitmap.Width).IsEqualTo(expected.Width);
        await Assert.That(bitmap.Height).IsEqualTo(expected.Height);
    }

    [Test]
    public async Task WithEciMode_WhenDataWasProvided_ThrowsImmediately()
    {
        var data = RmQRCodeGenerator.Create("a", RmQREccLevel.M);
        await Assert.That(() => new RmQRCodeImageBuilder(data).WithEciMode(EciMode.Utf8)).Throws<InvalidOperationException>();
    }

    // ---- module-to-pixel parity --------------------------------------------------------

    [Test]
    [MethodDataSource(nameof(AllVersions))]
    public async Task ToBitmap_ModulePixelSize_EveryModuleMatchesMatrix_BothEcc(RmQRVersion version)
    {
        foreach (var ecc in new[] { RmQREccLevel.M, RmQREccLevel.H })
        {
            const int modulePixelSize = 4;
            var data = RmQRCodeGenerator.Create("RM" + (int)version, ecc, new RmQRCodeGeneratorOptions { Version = version });
            using var bitmap = new RmQRCodeImageBuilder(data).WithModulePixelSize(modulePixelSize).ToBitmap();

            await Assert.That(bitmap.Width).IsEqualTo(data.Width * modulePixelSize);
            await Assert.That(bitmap.Height).IsEqualTo(data.Height * modulePixelSize);
            for (var row = 0; row < data.Height; row++)
            {
                for (var col = 0; col < data.Width; col++)
                {
                    var actual = bitmap.GetPixel(col * modulePixelSize + modulePixelSize / 2, row * modulePixelSize + modulePixelSize / 2);
                    if (IsDark(actual) != data[row, col])
                        Assert.Fail($"{version}-{ecc}: pixel for module ({row},{col}) is {actual}, expected {(data[row, col] ? "dark" : "light")}");
                }
            }
        }
    }

    [Test]
    public async Task ToBitmap_CustomColors_AndCircleShape_UseConfiguredColors()
    {
        const int modulePixelSize = 6;
        var data = RmQRCodeGenerator.Create("0123456789", RmQREccLevel.M);
        using var bitmap = new RmQRCodeImageBuilder(data)
            .WithModulePixelSize(modulePixelSize)
            .WithColors(SKColors.DarkBlue, SKColors.LightYellow)
            .WithModuleShape(CircleModuleShape.Default, 0.9f)
            .ToBitmap();

        for (var row = 0; row < data.Height; row++)
            for (var col = 0; col < data.Width; col++)
            {
                var actual = bitmap.GetPixel(col * modulePixelSize + modulePixelSize / 2, row * modulePixelSize + modulePixelSize / 2);
                await Assert.That(actual).IsEqualTo(data[row, col] ? SKColors.DarkBlue : SKColors.LightYellow);
            }
    }

    // ---- layout ---------------------------------------------------------------------------

    [Test]
    public async Task ContentBuilder_DefaultQuietZone_IsTwoModules()
    {
        using var bitmap = new RmQRCodeImageBuilder("0123456789").WithModulePixelSize(3).ToBitmap();
        var data = RmQRCodeGenerator.Create("0123456789", RmQREccLevel.M); // default quiet zone 2 → R11x27 → 31 × 15
        await Assert.That(bitmap.Width).IsEqualTo(31 * 3);
        await Assert.That(bitmap.Height).IsEqualTo(15 * 3);
        await Assert.That(data.Width).IsEqualTo(31);
    }

    [Test]
    public async Task DefaultCanvas_IsWidth512_WithAspectRatioHeight()
    {
        // R11x27 + quiet zone 2 = 31 × 15 → 512 wide, round(512 × 15 / 31) = 248 high.
        using var bitmap = new RmQRCodeImageBuilder("0123456789").ToBitmap();
        await Assert.That(bitmap.Width).IsEqualTo(512);
        await Assert.That(bitmap.Height).IsEqualTo(248);
        // Whole image is symbol background (white) at the corners: no letterbox pad worth mentioning.
        await Assert.That(bitmap.GetPixel(0, 0)).IsEqualTo(SKColors.White);
    }

    [Test]
    [MethodDataSource(nameof(AllVersions))]
    public async Task DefaultCanvas_WidthOnly_FillsTheWholeCanvas_EveryVersion(RmQRVersion version)
    {
        // Width-only sizing (the static helpers' rule and the no-size default) must give the
        // symbol at exactly that width: no letterbox band from the rounded height, and an
        // opaque image (the wide versions used to lose 1-3 columns to transparent pad).
        var data = RmQRCodeGenerator.Create("RM" + (int)version, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = version });
        using var bitmap = new RmQRCodeImageBuilder(data).ToBitmap();
        await Assert.That(bitmap.Width).IsEqualTo(512);
        await Assert.That(bitmap.Height).IsEqualTo((int)Math.Round(512d * data.Height / data.Width));
        await Assert.That(bitmap.AlphaType).IsEqualTo(SKAlphaType.Opaque);
        var midRow = bitmap.Height / 2;
        for (var x = 0; x < bitmap.Width; x++)
        {
            if (bitmap.GetPixel(x, midRow).Alpha != byte.MaxValue)
                Assert.Fail($"{version}: transparent pad at column {x}");
        }
        for (var y = 0; y < bitmap.Height; y++)
        {
            if (bitmap.GetPixel(bitmap.Width / 2, y).Alpha != byte.MaxValue)
                Assert.Fail($"{version}: transparent pad at row {y}");
        }
        // Corners are quiet zone (symbol background), never clear color.
        await Assert.That(bitmap.GetPixel(0, 0)).IsEqualTo(SKColors.White);
        await Assert.That(bitmap.GetPixel(bitmap.Width - 1, bitmap.Height - 1)).IsEqualTo(SKColors.White);

        // Static helper PNG is opaque as well (encoded without an alpha channel: the
        // codec reports the decoded surface as opaque).
        using var png = SKBitmap.Decode(RmQRCodeImageBuilder.GetPngBytes(data));
        await Assert.That(png.Width).IsEqualTo(512);
        await Assert.That(png.AlphaType).IsEqualTo(SKAlphaType.Opaque);
        await Assert.That(png.GetPixel(0, png.Height / 2)).IsEqualTo(SKColors.White);
        await Assert.That(png.GetPixel(png.Width - 1, png.Height / 2)).IsEqualTo(SKColors.White);
    }

    [Test]
    public async Task WithWidth_IsThePublicWidthOnlyRule_AndDefersToSizeAndModulePixelSize()
    {
        var data = RmQRCodeGenerator.Create("0123456789", RmQREccLevel.M); // 31 × 15
        using var wide = new RmQRCodeImageBuilder(data).WithWidth(1024).ToBitmap();
        await Assert.That(wide.Width).IsEqualTo(1024);
        await Assert.That(wide.Height).IsEqualTo((int)Math.Round(1024d * 15 / 31));
        await Assert.That(wide.GetPixel(0, wide.Height / 2)).IsEqualTo(SKColors.White);
        await Assert.That(wide.GetPixel(1023, wide.Height / 2)).IsEqualTo(SKColors.White);

        // Module centers land on the matrix at the uniform width scale.
        var scale = 1024f / 31;
        for (var row = 0; row < data.Height; row++)
            for (var col = 0; col < data.Width; col++)
                if (IsDark(wide.GetPixel((int)((col + 0.5f) * scale), (int)((row + 0.5f) * scale))) != data[row, col])
                    Assert.Fail($"width-only module ({row},{col}) mismatch");

        using var explicitSize = new RmQRCodeImageBuilder(data).WithWidth(1024).WithSize(200, 200).ToBitmap();
        await Assert.That((explicitSize.Width, explicitSize.Height)).IsEqualTo((200, 200));
        using var modulePixel = new RmQRCodeImageBuilder(data).WithWidth(1024).WithModulePixelSize(3).ToBitmap();
        await Assert.That((modulePixel.Width, modulePixel.Height)).IsEqualTo((31 * 3, 15 * 3));

        await Assert.That(() => new RmQRCodeImageBuilder(data).WithWidth(0)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task WithSize_ExplicitCanvas_LetterboxesUniformly_NeverStretches()
    {
        // 31 × 15 modules into a 400 × 400 canvas: scale = min(400/31, 400/15) = 12.9;
        // content = 400 × 193.5, centered vertically (pad top ≈ 103), pad keeps the clear color.
        var data = RmQRCodeGenerator.Create("0123456789", RmQREccLevel.M);
        using var bitmap = new RmQRCodeImageBuilder(data)
            .WithSize(400, 400)
            .WithColors(SKColors.Black, SKColors.White, SKColors.Red)
            .ToBitmap();

        await Assert.That(bitmap.Width).IsEqualTo(400);
        await Assert.That(bitmap.Height).IsEqualTo(400);
        await Assert.That(bitmap.GetPixel(200, 10)).IsEqualTo(SKColors.Red);      // top pad
        await Assert.That(bitmap.GetPixel(200, 390)).IsEqualTo(SKColors.Red);     // bottom pad
        await Assert.That(bitmap.GetPixel(2, 200)).IsEqualTo(SKColors.White);     // symbol background spans the full width
        // Module centers: scale 400/31 horizontally and the SAME scale vertically.
        var scale = 400f / 31;
        var padTop = MathF.Floor((400 - 15 * scale) / 2);
        for (var row = 0; row < data.Height; row++)
            for (var col = 0; col < data.Width; col++)
            {
                var px = bitmap.GetPixel((int)((col + 0.5f) * scale), (int)(padTop + (row + 0.5f) * scale));
                if (IsDark(px) != data[row, col])
                    Assert.Fail($"letterboxed module ({row},{col}) mismatch");
            }

        // Tall-and-narrow canvas: pad appears left/right instead.
        using var tall = new RmQRCodeImageBuilder(data).WithSize(100, 400).WithColors(SKColors.Black, SKColors.White, SKColors.Red).ToBitmap();
        await Assert.That(tall.GetPixel(50, 5)).IsEqualTo(SKColors.Red);
        await Assert.That(tall.GetPixel(50, 200)).IsNotEqualTo(SKColors.Red);
    }

    [Test]
    public async Task WithModulePixelSize_AndLargerCanvas_PadsAndCentersOnWholePixels()
    {
        var data = RmQRCodeGenerator.Create("0123456789", RmQREccLevel.M); // 31 × 15
        const int modulePixelSize = 4;
        using var bitmap = new RmQRCodeImageBuilder(data)
            .WithModulePixelSize(modulePixelSize)
            .WithSize(200, 100)
            .ToBitmap();

        var expectedLeft = (200 - 31 * modulePixelSize) / 2;
        var expectedTop = (100 - 15 * modulePixelSize) / 2;
        // No clear color was asked for, so the pad takes the background and the image stays opaque.
        await Assert.That(bitmap.GetPixel(0, 0)).IsEqualTo(SKColors.White);
        await Assert.That(bitmap.AlphaType).IsEqualTo(SKAlphaType.Opaque);
        await Assert.That(bitmap.GetPixel(expectedLeft, expectedTop)).IsEqualTo(SKColors.White);
        await Assert.That(bitmap.GetPixel(expectedLeft + 2 * modulePixelSize + 1, expectedTop + 2 * modulePixelSize + 1)).IsEqualTo(SKColors.Black); // core (0,0)
    }

    [Test]
    public async Task WithModulePixelSize_AndTooSmallCanvas_ThrowsInvalidOperationException()
    {
        var ex = await Assert.That(() => new RmQRCodeImageBuilder("0123456789").WithModulePixelSize(4).WithSize(50, 50).ToBitmap()).Throws<InvalidOperationException>();
        await Assert.That(ex!.Message).Contains("smaller than QR content size");
    }

    // ---- symbology options ---------------------------------------------------------------

    [Test]
    public async Task WithVersion_FitStrategy_Height_AffectTheGeneratedSymbol()
    {
        using var area = new RmQRCodeImageBuilder("012345678901").WithModulePixelSize(1).ToBitmap();      // R11x27 → 31 × 15
        using var height = new RmQRCodeImageBuilder("012345678901").WithFitStrategy(RmQRFitStrategy.MinimizeHeight).WithModulePixelSize(1).ToBitmap(); // R7x43 → 47 × 11
        using var fixedHeight = new RmQRCodeImageBuilder("012345678901").WithHeight(RmQRHeight.H13).WithModulePixelSize(1).ToBitmap(); // R13x27 → 31 × 17
        using var version = new RmQRCodeImageBuilder("012345678901").WithVersion(RmQRVersion.R17x139).WithModulePixelSize(1).ToBitmap();

        await Assert.That((area.Width, area.Height)).IsEqualTo((31, 15));
        await Assert.That((height.Width, height.Height)).IsEqualTo((47, 11));
        await Assert.That((fixedHeight.Width, fixedHeight.Height)).IsEqualTo((31, 17));
        await Assert.That((version.Width, version.Height)).IsEqualTo((143, 21));
    }

    [Test]
    public async Task SymbologyOptions_DataBuilder_ThrowInvalidOperationException()
    {
        var data = RmQRCodeGenerator.Create("1", RmQREccLevel.M);
        await Assert.That(() => new RmQRCodeImageBuilder(data).WithErrorCorrection(RmQREccLevel.H)).Throws<InvalidOperationException>();
        await Assert.That(() => new RmQRCodeImageBuilder(data).WithVersion(RmQRVersion.R7x43)).Throws<InvalidOperationException>();
        await Assert.That(() => new RmQRCodeImageBuilder(data).WithFitStrategy(RmQRFitStrategy.MinimizeHeight)).Throws<InvalidOperationException>();
        await Assert.That(() => new RmQRCodeImageBuilder(data).WithHeight(RmQRHeight.H7)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task SymbologyOptions_InvalidValues_ThrowArgumentOutOfRangeException()
    {
        await Assert.That(() => new RmQRCodeImageBuilder("1").WithVersion((RmQRVersion)0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new RmQRCodeImageBuilder("1").WithFitStrategy((RmQRFitStrategy)9)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new RmQRCodeImageBuilder("1").WithHeight((RmQRHeight)8)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ContentBuilder_TooLong_ThrowsOnRender_WithActionableMessage()
    {
        var ex = await Assert.That(() => new RmQRCodeImageBuilder(new string('a', 151)).ToBitmap()).Throws<ArgumentException>();
        await Assert.That(ex!.Message).Contains("R17x139");
    }

    // ---- static helpers ------------------------------------------------------------------

    [Test]
    public async Task StaticHelpers_SizeIsWidth_HeightFollowsAspect()
    {
        var png = RmQRCodeImageBuilder.GetPngBytes("0123456789", RmQREccLevel.M, size: 310);
        using var bitmap = SKBitmap.Decode(png);
        await Assert.That(bitmap.Width).IsEqualTo(310);
        await Assert.That(bitmap.Height).IsEqualTo(150); // 31 × 15 → 310 × 150

        var data = RmQRCodeGenerator.Create("0123456789", RmQREccLevel.M);
        using var fromData = SKBitmap.Decode(RmQRCodeImageBuilder.GetPngBytes(data, size: 62));
        await Assert.That((fromData.Width, fromData.Height)).IsEqualTo((62, 30));
    }

    [Test]
    [Arguments(SKEncodedImageFormat.Png)]
    [Arguments(SKEncodedImageFormat.Jpeg)]
    [Arguments(SKEncodedImageFormat.Webp)]
    public async Task GetImageBytes_DifferentFormats_ReturnsValidBytes(SKEncodedImageFormat format)
    {
        var bytes = RmQRCodeImageBuilder.GetImageBytes("Hello rMQR", format, RmQREccLevel.H, 256, 90);
        using var bitmap = SKBitmap.Decode(bytes);
        await Assert.That(bitmap).IsNotNull();
        await Assert.That(bitmap.Width).IsEqualTo(256);
    }

    [Test]
    public async Task SavePng_WritePng_WriteImage_ProduceDecodableOutput()
    {
        using var stream = new MemoryStream();
        RmQRCodeImageBuilder.SavePng("Hello", stream, size: 128);
        await Assert.That(stream.Length).IsGreaterThan(0);

        var writer = new ArrayBufferWriter<byte>();
        RmQRCodeImageBuilder.WritePng("Hello", writer, size: 128);
        using var decoded = SKBitmap.Decode(writer.WrittenSpan.ToArray());
        await Assert.That(decoded.Width).IsEqualTo(128);

        var writer2 = new ArrayBufferWriter<byte>();
        RmQRCodeImageBuilder.WriteImage(RmQRCodeGenerator.Create("Hello", RmQREccLevel.M), writer2, SKEncodedImageFormat.Webp, 128, 80);
        await Assert.That(writer2.WrittenCount).IsGreaterThan(0);
    }

    // ---- SVG -----------------------------------------------------------------------------

    [Test]
    public async Task Svg_ViewBoxIsRectangular_AndCrispEdgesForRectangles()
    {
        var svg = RmQRCodeImageBuilder.GetSvgString("0123456789", RmQREccLevel.M, size: 310);
        await Assert.That(svg).Contains("viewBox=\"0 0 310 150\"");
        await Assert.That(svg).Contains("shape-rendering=\"crispEdges\"");

        var round = new RmQRCodeImageBuilder("0123456789").WithModuleShape(CircleModuleShape.Default).ToSvgString();
        await Assert.That(round).DoesNotContain("crispEdges");

        var bytes = RmQRCodeImageBuilder.GetSvgBytes("0123456789");
        await Assert.That(Encoding.UTF8.GetString(bytes)).Contains("<svg");
        using var stream = new MemoryStream();
        RmQRCodeImageBuilder.SaveSvg("0123456789", stream);
        await Assert.That(stream.Length).IsGreaterThan(0);
        var writer = new ArrayBufferWriter<byte>();
        RmQRCodeImageBuilder.WriteSvg("0123456789", writer);
        await Assert.That(writer.WrittenCount).IsGreaterThan(0);
    }

    // ---- low-level renderer and canvas extensions ------------------------------------------

    [Test]
    public async Task Renderer_LetterboxesIntoArea_AndExtensionsAgree()
    {
        var data = RmQRCodeGenerator.Create("0123456789", RmQREccLevel.M); // 31 × 15
        using var direct = new SKBitmap(200, 200);
        using (var canvas = new SKCanvas(direct))
        {
            canvas.Clear(SKColors.Red);
            SymbolRenderer.Render(canvas, SKRect.Create(0, 0, 200, 200), data, null, null);
        }
        // Background covers the whole area; symbol letterboxed vertically (200/31 = 6.45 per module → 96.8 px high).
        await Assert.That(direct.GetPixel(100, 5)).IsEqualTo(SKColors.White);
        var scale = 200f / 31;
        var top = (200 - 15 * scale) / 2;
        await Assert.That(IsDark(direct.GetPixel((int)((2 + 0.5f) * scale), (int)(top + (2 + 0.5f) * scale)))).IsTrue(); // core (0,0)
        await Assert.That(IsDark(direct.GetPixel((int)((3 + 0.5f) * scale), (int)(top + (3 + 0.5f) * scale)))).IsFalse(); // core (1,1) light ring

        using var viaExtension = new SKBitmap(200, 200);
        using (var canvas = new SKCanvas(viaExtension))
            canvas.Render(data, 200, 200, clearColor: SKColors.Red);
        for (var y = 0; y < 200; y += 7)
            for (var x = 0; x < 200; x += 7)
                if (direct.GetPixel(x, y) != viaExtension.GetPixel(x, y))
                    Assert.Fail($"extension differs from renderer at ({x},{y})");

        using var viaAreaExtension = new SKBitmap(200, 200);
        using (var canvas = new SKCanvas(viaAreaExtension))
            canvas.Render(data, SKRect.Create(0, 0, 200, 200), clearColor: SKColors.Red);
        await Assert.That(viaAreaExtension.GetPixel(100, 5)).IsEqualTo(SKColors.White);

        await Assert.That(() => SymbolRenderer.Render(null!, SKRect.Create(0, 0, 10, 10), (RmQRCodeData)null!, null, null)).Throws<ArgumentNullException>();
    }
}
