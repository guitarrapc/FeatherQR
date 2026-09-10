using SkiaSharp;
using FeatherQR.SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// The builder renders to an opaque surface when the base layer (QR background
/// fill or cleared canvas) is opaque everywhere, so encoders emit alpha-less
/// output (RGB PNG). These tests pin the opacity decision and that the visible
/// pixels stay identical to the premul (always-alpha) extension render.
/// </summary>
public class QRCodeImageBuilderOpaqueEncodingTest
{
    private const string TestContent = "opaque-encoding-test";

    [Test]
    public async Task DefaultColors_ProducesOpaqueImage()
    {
        var qr = QRCodeGenerator.Create(TestContent, QREccLevel.M);

        using var image = new QRCodeImageBuilder(qr).WithSize(300, 300).ToImage();

        await Assert.That(image.AlphaType).IsEqualTo(SKAlphaType.Opaque);
    }

    [Test]
    public async Task TransparentClearWithPadding_KeepsAlpha()
    {
        // Transparent surroundings are asked for by name; the pad follows the background
        // otherwise, so the surface has to keep its alpha channel only when they are.
        const int modulePixelSize = 4;
        var qr = QRCodeGenerator.Create(TestContent, QREccLevel.M);
        var canvasSide = qr.Size * modulePixelSize + 40;

        using var image = new QRCodeImageBuilder(qr)
            .WithModulePixelSize(modulePixelSize)
            .WithSize(canvasSide, canvasSide)
            .WithColors(clearColor: SKColors.Transparent)
            .ToImage();

        await Assert.That(image.AlphaType).IsEqualTo(SKAlphaType.Premul);

        using var bitmap = SKBitmap.FromImage(image);
        await Assert.That(bitmap.GetPixel(0, 0).Alpha).IsEqualTo((byte)0);
    }

    /// <summary>
    /// The same canvas without that request: the pad takes the opaque background, so the image is
    /// opaque and a JPEG of it has no black bands where the alpha used to be.
    /// </summary>
    [Test]
    public async Task DefaultClearWithPadding_ProducesOpaqueImage()
    {
        const int modulePixelSize = 4;
        var qr = QRCodeGenerator.Create(TestContent, QREccLevel.M);
        var canvasSide = qr.Size * modulePixelSize + 40;

        using var image = new QRCodeImageBuilder(qr)
            .WithModulePixelSize(modulePixelSize)
            .WithSize(canvasSide, canvasSide)
            .ToImage();

        await Assert.That(image.AlphaType).IsEqualTo(SKAlphaType.Opaque);

        using var bitmap = SKBitmap.FromImage(image);
        await Assert.That(bitmap.GetPixel(0, 0)).IsEqualTo(SKColors.White);
    }

    [Test]
    public async Task OpaqueClearWithPadding_ProducesOpaqueImage()
    {
        const int modulePixelSize = 4;
        var qr = QRCodeGenerator.Create(TestContent, QREccLevel.M);
        var canvasSide = qr.Size * modulePixelSize + 40;

        using var image = new QRCodeImageBuilder(qr)
            .WithModulePixelSize(modulePixelSize)
            .WithSize(canvasSide, canvasSide)
            .WithColors(clearColor: SKColors.Red)
            .ToImage();

        await Assert.That(image.AlphaType).IsEqualTo(SKAlphaType.Opaque);
    }

    [Test]
    public async Task TranslucentBackground_WithTransparentClear_KeepsAlpha()
    {
        // Translucent background over a transparent canvas: pixels stay
        // translucent, so the alpha channel must be preserved.
        var qr = QRCodeGenerator.Create(TestContent, QREccLevel.M);
        var translucentWhite = new SKColor(0xFF, 0xFF, 0xFF, 0x80);

        using var image = new QRCodeImageBuilder(qr)
            .WithSize(300, 300)
            .WithColors(backgroundColor: translucentWhite)
            .ToImage();

        await Assert.That(image.AlphaType).IsEqualTo(SKAlphaType.Premul);

        using var bitmap = SKBitmap.FromImage(image);
        await Assert.That(bitmap.GetPixel(0, 0).Alpha < byte.MaxValue).IsTrue();
    }

    [Test]
    public async Task TranslucentBackground_OverOpaqueClear_ProducesOpaqueImage()
    {
        // The opaque cleared canvas is the base layer; the translucent background
        // blends over it and every pixel stays opaque.
        var qr = QRCodeGenerator.Create(TestContent, QREccLevel.M);
        var translucentWhite = new SKColor(0xFF, 0xFF, 0xFF, 0x80);

        using var image = new QRCodeImageBuilder(qr)
            .WithSize(300, 300)
            .WithColors(backgroundColor: translucentWhite, clearColor: SKColors.Red)
            .ToImage();

        await Assert.That(image.AlphaType).IsEqualTo(SKAlphaType.Opaque);
    }

    [Test]
    public async Task OpaquePng_DecodesToSamePixelsAsPremulRender()
    {
        // The encoded PNG must stay visually identical to the premul render;
        // only the alpha channel representation may differ.
        var qr = QRCodeGenerator.Create(TestContent, QREccLevel.M);
        const int size = 300;

        var png = QRCodeImageBuilder.GetPngBytes(qr, size);
        using var decoded = SKBitmap.Decode(png);

        using var reference = new SKBitmap(size, size);
        using (var canvas = new SKCanvas(reference))
        {
            canvas.Render(qr, SKRect.Create(0, 0, size, size));
        }

        await Assert.That(decoded.Width).IsEqualTo(size);
        await Assert.That(decoded.Height).IsEqualTo(size);
        for (var y = 0; y < size; y += 3)
        {
            for (var x = 0; x < size; x += 3)
            {
                await Assert.That(decoded.GetPixel(x, y)).IsEqualTo(reference.GetPixel(x, y));
            }
        }
    }
}
