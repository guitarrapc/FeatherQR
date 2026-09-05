using SkiaSharp;
using FeatherQR.SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// The renderer collapses horizontal runs of dark modules into single rects for the
/// default rectangle shape at 100% module size. These tests pin that the merged
/// fast path is pixel-identical to per-module drawing (forced through a rect shape
/// of a different type, which bypasses the fast-path type check).
/// </summary>
public class SymbolRendererRunMergeParityTest
{
    // Draws exactly like RectangleModuleShape, but as a different type the renderer
    // routes it through the per-module path instead of the run-merged fast path.
    private sealed class PerModuleRectangleShape : ModuleShape
    {
        public override bool RequiresAntialiasing => false;
        public override void Draw(SKCanvas canvas, SKRect rect, SKPaint paint) => canvas.DrawRect(rect, paint);
    }

    [Test]
    [Arguments("HELLO WORLD 2026", 0, 290)] // v1-2, no quiet zone, exact multiple
    [Arguments("HELLO WORLD 2026", 4, 290)] // v1-2, standard quiet zone
    [Arguments("HELLO WORLD 2026", 4, 411)] // fractional cell size
    [Arguments("https://github.com/guitarrapc/FeatherQR/blob/main/README.md?foo=sample&bar=dummy", 4, 512)] // v~5
    public async Task MergedRuns_MatchPerModuleRendering(string content, int quietZone, int imageSize)
    {
        var qr = QRCodeGenerator.Create(content, QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = quietZone });

        var merged = RenderPixels(qr, imageSize, moduleShape: null);
        var perModule = RenderPixels(qr, imageSize, moduleShape: new PerModuleRectangleShape());

        await Assert.That(merged).IsEquivalentTo(perModule);
    }

    [Test]
    public async Task MergedRuns_WithGradient_MatchPerModuleRendering()
    {
        var qr = QRCodeGenerator.Create("gradient-run-merge-parity", QREccLevel.M);
        var gradient = new GradientOptions([SKColors.Purple, SKColors.Orange], GradientDirection.TopLeftToBottomRight);

        var merged = RenderPixels(qr, 512, moduleShape: null, gradientOptions: gradient);
        var perModule = RenderPixels(qr, 512, moduleShape: new PerModuleRectangleShape(), gradientOptions: gradient);

        await Assert.That(merged).IsEquivalentTo(perModule);
    }

    [Test]
    [Arguments(0.3f, 0.7f, 1.0f)]   // sub-pixel translation
    [Arguments(0f, 0f, 1.37f)]      // fractional upscale
    [Arguments(5.5f, 3.25f, 0.61f)] // sub-pixel translation + fractional downscale
    public async Task MergedRuns_WithAxisPreservingCanvasTransform_MatchPerModuleRendering(float dx, float dy, float scale)
    {
        // Axis-preserving transforms (translation/scale) keep both paths
        // pixel-identical: they compute the same edge coordinates and rasterize
        // without antialiasing. Rotation is intentionally outside the parity
        // guarantee, non-axis-aligned rasterization rounds shared edges at
        // sub-pixel level, which affects per-module drawing between adjacent
        // modules just the same (measured ~0.003% of pixels at 7-30 degrees).
        var qr = QRCodeGenerator.Create("transform-parity", QREccLevel.M);

        var merged = RenderTransformedPixels(qr, moduleShape: null, dx, dy, scale);
        var perModule = RenderTransformedPixels(qr, new PerModuleRectangleShape(), dx, dy, scale);

        await Assert.That(merged).IsEquivalentTo(perModule);
    }

    [Test]
    public async Task MergedRuns_WithFinderPatternShape_MatchPerModuleRendering()
    {
        // Runs must break at finder pattern modules so the custom finder shape
        // is not painted over.
        var qr = QRCodeGenerator.Create("finder-run-merge-parity", QREccLevel.M);

        var merged = RenderPixels(qr, 512, moduleShape: null, finderPatternShape: RectangleFinderPatternShape.Default);
        var perModule = RenderPixels(qr, 512, moduleShape: new PerModuleRectangleShape(), finderPatternShape: RectangleFinderPatternShape.Default);

        await Assert.That(merged).IsEquivalentTo(perModule);
    }

    private static byte[] RenderTransformedPixels(QRCodeData qr, ModuleShape? moduleShape, float dx, float dy, float scale)
    {
        // Render area uses a fractional cell size (411 / module count) inside a
        // larger bitmap so scaled content stays within bounds.
        using var bitmap = new SKBitmap(600, 600);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(dx, dy);
        canvas.Scale(scale);

        SymbolRenderer.Render(
            canvas,
            SKRect.Create(10, 10, 411, 411),
            qr,
            SKColors.Black,
            SKColors.White,
            moduleShape: moduleShape);

        return bitmap.Bytes;
    }

    private static byte[] RenderPixels(
        QRCodeData qr,
        int imageSize,
        ModuleShape? moduleShape,
        GradientOptions? gradientOptions = null,
        FinderPatternShape? finderPatternShape = null)
    {
        var area = SKRect.Create(0, 0, imageSize, imageSize);
        using var bitmap = new SKBitmap(imageSize, imageSize);
        using var canvas = new SKCanvas(bitmap);

        SymbolRenderer.Render(
            canvas,
            area,
            qr,
            SKColors.Black,
            SKColors.White,
            moduleShape: moduleShape,
            gradientOptions: gradientOptions,
            finderPatternShape: finderPatternShape);

        return bitmap.Bytes;
    }
}
