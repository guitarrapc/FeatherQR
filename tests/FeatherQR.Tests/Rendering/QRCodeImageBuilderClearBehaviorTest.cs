using SkiaSharp;
using FeatherQR.SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// The builder skips the initial canvas clear when the clear color cannot remain
/// visible (a fresh surface is already transparent, and an opaque QR background
/// covering the whole canvas overwrites the cleared pixels). These tests pin that
/// the builder output stays pixel-identical to the always-clear extension render.
/// </summary>
public class QRCodeImageBuilderClearBehaviorTest
{
    private const string TestContent = "clear-behavior-test";

    [Test]
    public async Task DefaultTransparentClear_FullCoverage_MatchesExtensionRender()
    {
        var qr = QRCodeGenerator.Create(TestContent, QREccLevel.M);

        var actual = BuilderPixels(new QRCodeImageBuilder(qr).WithSize(300, 300));
        var expected = ExtensionPixels(qr, 300, 300, SKRect.Create(0, 0, 300, 300), clearColor: null, backgroundColor: null);

        await Assert.That(actual).IsEquivalentTo(expected);
    }

    [Test]
    public async Task OpaqueClearColor_WithPadding_KeepsClearColorInPad()
    {
        const int modulePixelSize = 4;
        var qr = QRCodeGenerator.Create(TestContent, QREccLevel.M);
        var contentSide = qr.Size * modulePixelSize;
        var canvasSide = contentSide + 40;
        var origin = (canvasSide - contentSide) / 2;

        var builder = new QRCodeImageBuilder(qr)
            .WithModulePixelSize(modulePixelSize)
            .WithSize(canvasSide, canvasSide)
            .WithColors(clearColor: SKColors.Red);

        using var bitmap = builder.ToBitmap();

        // Pad outside content keeps the clear color; the clear must not be skipped.
        await Assert.That(bitmap.GetPixel(0, 0)).IsEquivalentTo(SKColors.Red);
        await Assert.That(bitmap.GetPixel(canvasSide - 1, canvasSide - 1)).IsEquivalentTo(SKColors.Red);

        var expected = ExtensionPixels(qr, canvasSide, canvasSide,
            SKRect.Create(origin, origin, contentSide, contentSide),
            clearColor: SKColors.Red, backgroundColor: null);
        await Assert.That(bitmap.Bytes).IsEquivalentTo(expected);
    }

    [Test]
    public async Task TranslucentBackground_FullCoverage_StillBlendsOverClearColor()
    {
        // A translucent QR background lets the clear color show through, so the
        // clear must still happen even when the content covers the whole canvas.
        var qr = QRCodeGenerator.Create(TestContent, QREccLevel.M);
        var translucentWhite = new SKColor(0xFF, 0xFF, 0xFF, 0x80);

        var actual = BuilderPixels(new QRCodeImageBuilder(qr)
            .WithSize(300, 300)
            .WithColors(backgroundColor: translucentWhite, clearColor: SKColors.Red));
        var expected = ExtensionPixels(qr, 300, 300, SKRect.Create(0, 0, 300, 300),
            clearColor: SKColors.Red, backgroundColor: translucentWhite);

        await Assert.That(actual).IsEquivalentTo(expected);
    }

    [Test]
    public async Task OpaqueClearColor_FullCoverage_MatchesExtensionRender()
    {
        // Opaque background covering the whole canvas: the clear is skipped, and
        // the output must be identical to the always-clear render.
        var qr = QRCodeGenerator.Create(TestContent, QREccLevel.M);

        var actual = BuilderPixels(new QRCodeImageBuilder(qr)
            .WithSize(300, 300)
            .WithColors(clearColor: SKColors.Red));
        var expected = ExtensionPixels(qr, 300, 300, SKRect.Create(0, 0, 300, 300),
            clearColor: SKColors.Red, backgroundColor: null);

        await Assert.That(actual).IsEquivalentTo(expected);
    }

    private static byte[] BuilderPixels(QRCodeImageBuilder builder)
    {
        using var bitmap = builder.ToBitmap();
        return bitmap.Bytes;
    }

    private static byte[] ExtensionPixels(QRCodeData qr, int width, int height, SKRect contentRect, SKColor? clearColor, SKColor? backgroundColor)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Render(qr, contentRect, clearColor: clearColor, backgroundColor: backgroundColor);
        return bitmap.Bytes;
    }
}
