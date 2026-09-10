using SkiaSharp;
using FeatherQR.SkiaSharp;

namespace FeatherQR.Tests;

public class FinderPatternShapeColorTest
{
    [Test]
    public async Task CustomFinderPatternShape_ReceivesConfiguredBackgroundPaint()
    {
        var backgroundColor = SKColors.Yellow;
        var qr = QRCodeGenerator.Create("finder-background-paint-test", QREccLevel.M);
        var imageSize = qr.Size * 10;
        using var bitmap = new SKBitmap(imageSize, imageSize);
        using var canvas = new SKCanvas(bitmap);
        var finderPatternShape = new BackgroundPaintFinderPatternShape();

        SymbolRenderer.Render(
            canvas,
            SKRect.Create(0, 0, imageSize, imageSize),
            qr,
            SKColors.Black,
            backgroundColor,
            finderPatternShape: finderPatternShape);

        await Assert.That(finderPatternShape.BackgroundPaintDrawCount).IsEqualTo(3);
        await Assert.That(finderPatternShape.BackgroundColorDrawCount).IsEqualTo(0);
        await Assert.That(finderPatternShape.ReceivedBackgroundColor).IsEqualTo(backgroundColor);
    }

    [Test]
    public async Task LegacyCustomFinderPatternShape_ColorOverloadRemainsCompatible()
    {
        var backgroundColor = SKColors.Yellow;
        var qr = QRCodeGenerator.Create("legacy-finder-shape-test", QREccLevel.M);
        using var bitmap = new SKBitmap(qr.Size * 10, qr.Size * 10);
        using var canvas = new SKCanvas(bitmap);
        var finderPatternShape = new LegacyBackgroundColorFinderPatternShape();

        SymbolRenderer.Render(
            canvas,
            SKRect.Create(0, 0, bitmap.Width, bitmap.Height),
            qr,
            SKColors.Black,
            backgroundColor,
            finderPatternShape: finderPatternShape);

        await Assert.That(finderPatternShape.DrawCount).IsEqualTo(3);
        await Assert.That(finderPatternShape.ReceivedBackgroundColor).IsEqualTo(backgroundColor);
    }

    [Test]
    [MethodDataSource(nameof(GetFinderPatternShapes))]
    public async Task CustomFinderPatternShape_UsesBackgroundColorForInnerRing(FinderPatternShape finderPatternShape)
    {
        var backgroundColor = new SKColor(0xEA, 0xFB, 0x00, 0xFF);
        var codeColor = SKColors.Green;
        var qr = QRCodeGenerator.Create("finder-shape-background-test", QREccLevel.M);

        var imageSize = qr.Size * 10;
        var area = SKRect.Create(0, 0, imageSize, imageSize);
        using var bitmap = new SKBitmap(imageSize, imageSize);
        using var canvas = new SKCanvas(bitmap);

        SymbolRenderer.Render(
            canvas,
            area,
            qr,
            codeColor,
            backgroundColor,
            finderPatternShape: finderPatternShape);

        var finderRect = SymbolRenderer.GetFinderPatternRect(qr, 0, area);
        var moduleSize = finderRect.Width / 7f;
        var ringSampleX = (int)MathF.Round(finderRect.Left + moduleSize * 1.5f);
        var ringSampleY = (int)MathF.Round(finderRect.Top + moduleSize * 3.5f);
        var centerSampleX = (int)MathF.Round(finderRect.Left + moduleSize * 3.5f);
        var centerSampleY = (int)MathF.Round(finderRect.Top + moduleSize * 3.5f);

        var ringPixel = bitmap.GetPixel(ringSampleX, ringSampleY);
        var centerPixel = bitmap.GetPixel(centerSampleX, centerSampleY);

        await Assert.That(ringPixel).IsEquivalentTo(backgroundColor);
        await Assert.That(centerPixel).IsEquivalentTo(codeColor);
    }

    [Test]
    [MethodDataSource(nameof(GetFinderPatternShapes))]
    public async Task CustomFinderPatternShape_WithAntialiasedModuleShape_UsesBackgroundColorForInnerRing(FinderPatternShape finderPatternShape)
    {
        // Regression: even when modules are rendered with antialiasing (e.g., circles),
        // finder pattern light areas must still use the configured background color.
        var backgroundColor = new SKColor(0xEA, 0xFB, 0x00, 0xFF);
        var codeColor = SKColors.Green;
        var qr = QRCodeGenerator.Create("finder-shape-antialias-test", QREccLevel.M);

        var imageSize = qr.Size * 10;
        var area = SKRect.Create(0, 0, imageSize, imageSize);
        using var bitmap = new SKBitmap(imageSize, imageSize);
        using var canvas = new SKCanvas(bitmap);

        SymbolRenderer.Render(
            canvas,
            area,
            qr,
            codeColor,
            backgroundColor,
            moduleShape: CircleModuleShape.Default,
            moduleSizePercent: 0.9f,
            finderPatternShape: finderPatternShape);

        var finderRect = SymbolRenderer.GetFinderPatternRect(qr, 0, area);
        var moduleSize = finderRect.Width / 7f;
        var ringSampleX = (int)MathF.Round(finderRect.Left + moduleSize * 1.5f);
        var ringSampleY = (int)MathF.Round(finderRect.Top + moduleSize * 3.5f);

        var ringPixel = bitmap.GetPixel(ringSampleX, ringSampleY);

        await Assert.That(ringPixel).IsEquivalentTo(backgroundColor);
    }

    [Test]
    public async Task ToByteArray_GradientCircleFinderPattern_UsesBackgroundColorForInnerRing()
    {
        const string content = "Test 2";
        var backgroundColor = SKColors.Yellow;
        var pngBytes = new QRCodeImageBuilder(content)
            .WithSize(800, 800)
            .WithErrorCorrection(QREccLevel.H)
            .WithColors(SKColors.Black, backgroundColor, SKColors.Transparent)
            .WithGradient(new GradientOptions(
                [SKColors.Blue, SKColors.Purple, SKColors.Pink],
                GradientDirection.TopLeftToBottomRight,
                [0f, 0.5f, 1f]))
            .WithFinderPatternShape(CircleFinderPatternShape.Default)
            .WithModuleShape(CircleModuleShape.Default, 0.9f)
            .ToByteArray();

        using var bitmap = SKBitmap.Decode(pngBytes) ?? throw new InvalidOperationException("Failed to decode generated PNG.");
        var qr = QRCodeGenerator.Create(content, QREccLevel.H);
        var finderRect = SymbolRenderer.GetFinderPatternRect(
            qr,
            0,
            SKRect.Create(0, 0, bitmap.Width, bitmap.Height));
        var moduleSize = finderRect.Width / 7f;
        var ringPixel = bitmap.GetPixel(
            (int)MathF.Round(finderRect.Left + moduleSize * 1.5f),
            (int)MathF.Round(finderRect.Top + moduleSize * 3.5f));

        await Assert.That(ringPixel).IsEqualTo(backgroundColor);
    }

    [Test]
    [MethodDataSource(nameof(GetFinderPatternShapes))]
    public async Task ToByteArray_TransparentBackground_KeepsFinderPatternInnerRingTransparent(FinderPatternShape finderPatternShape)
    {
        const string content = "transparent-finder-background-test";
        var pngBytes = new QRCodeImageBuilder(content)
            .WithSize(800, 800)
            .WithErrorCorrection(QREccLevel.H)
            .WithColors(SKColors.Black, new SKColor(0xFF, 0xFF, 0x00, 0x00), SKColors.Transparent)
            .WithGradient(new GradientOptions(
                [SKColors.Blue, SKColors.Purple, SKColors.Pink],
                GradientDirection.TopLeftToBottomRight,
                [0f, 0.5f, 1f]))
            .WithFinderPatternShape(finderPatternShape)
            .WithModuleShape(RoundedRectangleModuleShape.Default, 0.9f)
            .ToByteArray();

        using var bitmap = SKBitmap.Decode(pngBytes) ?? throw new InvalidOperationException("Failed to decode generated PNG.");
        var qr = QRCodeGenerator.Create(content, QREccLevel.H);
        var finderRect = SymbolRenderer.GetFinderPatternRect(
            qr,
            0,
            SKRect.Create(0, 0, bitmap.Width, bitmap.Height));
        var moduleSize = finderRect.Width / 7f;
        var ringPixel = bitmap.GetPixel(
            (int)MathF.Round(finderRect.Left + moduleSize * 1.5f),
            (int)MathF.Round(finderRect.Top + moduleSize * 3.5f));
        var centerPixel = bitmap.GetPixel(
            (int)MathF.Round(finderRect.Left + moduleSize * 3.5f),
            (int)MathF.Round(finderRect.Top + moduleSize * 3.5f));

        await Assert.That(ringPixel.Alpha).IsEqualTo((byte)0);
        await Assert.That(centerPixel.Alpha).IsEqualTo((byte)255);
    }

    [Test]
    [MethodDataSource(nameof(GetFinderPatternShapes))]
    public async Task TranslucentBackground_FinderPatternInnerRingMatchesRenderedBackground(FinderPatternShape finderPatternShape)
    {
        var backgroundColor = new SKColor(0xFF, 0xFF, 0xFF, 0x80);
        var qr = QRCodeGenerator.Create("translucent-finder-background-test", QREccLevel.M);
        var imageSize = qr.Size * 10;
        var area = SKRect.Create(0, 0, imageSize, imageSize);
        using var bitmap = new SKBitmap(imageSize, imageSize);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Red);

        SymbolRenderer.Render(
            canvas,
            area,
            qr,
            SKColors.Black,
            backgroundColor,
            finderPatternShape: finderPatternShape);

        var finderRect = SymbolRenderer.GetFinderPatternRect(qr, 0, area);
        var moduleSize = finderRect.Width / 7f;
        var ringPixel = bitmap.GetPixel(
            (int)MathF.Round(finderRect.Left + moduleSize * 1.5f),
            (int)MathF.Round(finderRect.Top + moduleSize * 3.5f));
        var quietZonePixel = bitmap.GetPixel(0, 0);

        await Assert.That(ringPixel).IsEqualTo(quietZonePixel);
    }

    [Test]
    [Arguments(false)]
    public async Task RectangleFinderPatternShape_RequiresAntialiasing_IsFalse(bool expected)
    {
        await Assert.That(RectangleFinderPatternShape.Default.RequiresAntialiasing).IsEqualTo(expected);
    }

    [Test]
    [Arguments(true)]
    public async Task CircleFinderPatternShape_RequiresAntialiasing_IsTrue(bool expected)
    {
        await Assert.That(CircleFinderPatternShape.Default.RequiresAntialiasing).IsEqualTo(expected);
    }

    [Test]
    [Arguments(true)]
    public async Task RoundedRectangleFinderPatternShape_RequiresAntialiasing_IsTrue(bool expected)
    {
        await Assert.That(RoundedRectangleFinderPatternShape.Default.RequiresAntialiasing).IsEqualTo(expected);
    }

    [Test]
    [Arguments(true)]
    public async Task RoundedRectangleCircleFinderPatternShape_RequiresAntialiasing_IsTrue(bool expected)
    {
        await Assert.That(RoundedRectangleCircleFinderPatternShape.Default.RequiresAntialiasing).IsEqualTo(expected);
    }

    public static IEnumerable<Func<FinderPatternShape>> GetFinderPatternShapes()
    {
        yield return () => RectangleFinderPatternShape.Default;
        yield return () => CircleFinderPatternShape.Default;
        yield return () => RoundedRectangleFinderPatternShape.Default;
        yield return () => RoundedRectangleCircleFinderPatternShape.Default;
    }

    /// <summary>
    /// The finder's light ring must carry the background, never the dark colour, on every
    /// symbology and whether or not the caller asked for a shape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cases above are Standard QR with an explicit shape, which is all that could reach a
    /// <see cref="FinderPatternShape"/> when they were written. Styling now routes Micro QR and
    /// rMQR through one too, and Standard QR reaches it without being asked, so the two defects
    /// those cases pin apply to paths they do not touch: the ring coming out dark
    /// (issue 337) and a transparent background not showing through it (issue 354). The finder is
    /// drawn over modules that are already there, so restoring the background under a non-opaque
    /// one needs an isolated layer; a mutant that dropped that layer passed the whole suite.
    /// </para>
    /// <para>
    /// The ring is sampled at module (1.5, 3.5) and the dark centre at (3.5, 3.5), as the
    /// Standard QR cases above do.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments("qr", "opaque")]
    [Arguments("qr", "transparent")]
    [Arguments("qr", "translucent")]
    [Arguments("microqr", "opaque")]
    [Arguments("microqr", "transparent")]
    [Arguments("microqr", "translucent")]
    [Arguments("rmqr", "opaque")]
    [Arguments("rmqr", "transparent")]
    [Arguments("rmqr", "translucent")]
    public async Task StyledSymbol_FinderInnerRing_CarriesTheBackground(string symbology, string background)
    {
        var backgroundColor = background switch
        {
            "transparent" => SKColors.Transparent,
            "translucent" => new SKColor(0x00, 0x00, 0xFF, 0x80),
            _ => new SKColor(0xFF, 0xD7, 0x00, 0xFF),
        };

        SKBitmap bitmap;
        int quietZone, matrixWidth;
        if (symbology == "rmqr")
        {
            quietZone = 2;
            matrixWidth = 59 + quietZone * 2;
            var data = RmQRCodeGenerator.Create("https://githu", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R11x59, QuietZoneSize = quietZone });
            var height = (int)((float)630 / matrixWidth * (11 + quietZone * 2));
            bitmap = new RmQRCodeImageBuilder(data)
                .WithSize(630, height)
                .WithColors(SKColors.Black, backgroundColor, SKColors.Transparent)
                .WithModuleShape(RectangleModuleShape.Default, 0.9f)
                .ToBitmap();
        }
        else if (symbology == "microqr")
        {
            quietZone = 2;
            var data = MicroQRCodeGenerator.Create("https://githu", MicroQREccLevel.M, new MicroQRCodeGeneratorOptions { QuietZoneSize = quietZone });
            matrixWidth = data.Size;
            bitmap = new MicroQRCodeImageBuilder(data)
                .WithSize(504, 504)
                .WithColors(SKColors.Black, backgroundColor, SKColors.Transparent)
                .WithModuleShape(RectangleModuleShape.Default, 0.9f)
                .ToBitmap();
        }
        else
        {
            quietZone = 4;
            var data = QRCodeGenerator.Create("https://githu", QREccLevel.H, new QRCodeGeneratorOptions { QuietZoneSize = quietZone });
            matrixWidth = data.Size;
            bitmap = new QRCodeImageBuilder(data)
                .WithSize(800, 800)
                .WithColors(SKColors.Black, backgroundColor, SKColors.Transparent)
                .WithModuleShape(RectangleModuleShape.Default, 0.9f)
                .ToBitmap();
        }

        using (bitmap)
        {
            var module = (float)bitmap.Width / matrixWidth;
            var ring = bitmap.GetPixel(
                (int)MathF.Round((quietZone + 1.5f) * module),
                (int)MathF.Round((quietZone + 3.5f) * module));
            var centre = bitmap.GetPixel(
                (int)MathF.Round((quietZone + 3.5f) * module),
                (int)MathF.Round((quietZone + 3.5f) * module));

            // Fully transparent has no one encoding (the cleared pixel is 0x00000000, the
            // requested colour 0x00FFFFFF), so it is pinned by its alpha, as the cases above do.
            if (background == "transparent")
                await Assert.That(ring.Alpha).IsEqualTo((byte)0).Because($"{symbology} finder ring over a transparent background");
            else
                await Assert.That(ring).IsEqualTo(backgroundColor).Because($"{symbology} {background} finder ring");

            await Assert.That(centre).IsEqualTo(SKColors.Black).Because($"{symbology} {background} finder centre");
        }
    }

    private sealed class BackgroundPaintFinderPatternShape : FinderPatternShape
    {
        public override bool RequiresAntialiasing => false;

        public int BackgroundPaintDrawCount { get; private set; }

        public int BackgroundColorDrawCount { get; private set; }

        public SKColor ReceivedBackgroundColor { get; private set; }

        public override void Draw(SKCanvas canvas, SKRect rect, SKPaint paint)
        {
        }

        public override void Draw(SKCanvas canvas, SKRect rect, SKPaint paint, SKColor backgroundColor)
        {
            BackgroundColorDrawCount++;
        }

        public override void Draw(SKCanvas canvas, SKRect rect, SKPaint paint, SKPaint backgroundPaint)
        {
            BackgroundPaintDrawCount++;
            ReceivedBackgroundColor = backgroundPaint.Color;
        }
    }

    private sealed class LegacyBackgroundColorFinderPatternShape : FinderPatternShape
    {
        public int DrawCount { get; private set; }

        public SKColor ReceivedBackgroundColor { get; private set; }

        public override void Draw(SKCanvas canvas, SKRect rect, SKPaint paint)
        {
        }

        public override void Draw(SKCanvas canvas, SKRect rect, SKPaint paint, SKColor backgroundColor)
        {
            DrawCount++;
            ReceivedBackgroundColor = backgroundColor;
        }
    }
}
