using SkiaSharp;
using FeatherQR.SkiaSharp;

namespace FeatherQR.Tests;

public class FinderPatternShapeColorTest
{
    /// <summary>
    /// The contract a custom shape is written against: it is handed the dark paint once per finder
    /// and draws the dark modules only. The light ring is whatever it leaves undrawn, so the
    /// background shows through it on its own, opaque or not, and the renderer neither paints the
    /// ring nor erases it. A shape that painted the ring itself, with a light paint the renderer
    /// used to hand it, could only be made right on a translucent background by erasing through a
    /// layer, which SVG output cannot express; the layer is gone and so is that overload.
    /// </summary>
    [Test]
    [Arguments(255)]
    [Arguments(128)]
    public async Task CustomFinderPatternShape_DrawsDarkModulesOnly_RingShowsTheBackground(int alpha)
    {
        var backgroundColor = SKColors.Yellow.WithAlpha((byte)alpha);
        var qr = QRCodeGenerator.Create("finder-dark-only-test", QREccLevel.M);
        var imageSize = qr.Size * 10;
        var area = SKRect.Create(0, 0, imageSize, imageSize);
        using var bitmap = new SKBitmap(imageSize, imageSize);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Red);
        var finderPatternShape = new CentreOnlyFinderPatternShape();

        SymbolRenderer.Render(canvas, area, qr, SKColors.Black, backgroundColor, finderPatternShape: finderPatternShape);

        await Assert.That(finderPatternShape.DrawCount).IsEqualTo(3);
        await Assert.That(finderPatternShape.ReceivedPaintColor).IsEqualTo(SKColors.Black);

        var finderRect = SymbolRenderer.GetFinderPatternRect(qr, 0, area);
        var moduleSize = finderRect.Width / 7f;
        SKColor At(float col, float row) => bitmap.GetPixel(
            (int)MathF.Round(finderRect.Left + moduleSize * col),
            (int)MathF.Round(finderRect.Top + moduleSize * row));

        // The shape drew its centre and nothing else, so the ring and the outer square both carry the
        // rendered background, which the quiet zone also carries: a layer that cleared or a renderer
        // that painted the ring would show up as a pixel that differs from the quiet zone.
        var quietZonePixel = bitmap.GetPixel(0, 0);
        await Assert.That(At(3.5f, 3.5f)).IsEqualTo(SKColors.Black);
        await Assert.That(At(1.5f, 3.5f)).IsEqualTo(quietZonePixel);
        await Assert.That(At(0.5f, 3.5f)).IsEqualTo(quietZonePixel);
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
            .WithColors(SKColors.Black, backgroundColor).WithClearColor(SKColors.Transparent)
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
            .WithColors(SKColors.Black, new SKColor(0xFF, 0xFF, 0x00, 0x00))
            .WithClearColor(SKColors.Transparent)
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
    /// (issue 337) and a transparent background not showing through it (issue 354). The ring is a
    /// hole in the finder's drawing, so what shows through it is whatever the renderer put beneath,
    /// which these cases hold to the rendered background on every symbology.
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
                .WithColors(SKColors.Black, backgroundColor).WithClearColor(SKColors.Transparent)
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
                .WithColors(SKColors.Black, backgroundColor).WithClearColor(SKColors.Transparent)
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
                .WithColors(SKColors.Black, backgroundColor).WithClearColor(SKColors.Transparent)
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
            // The quiet zone is painted with the requested background and nothing else, so it is
            // what the ring has to match. Comparing against the requested colour instead would be
            // reading through a premultiplied round trip that does not preserve every channel
            // (0x33123456 comes back 0x33143255), which has nothing to do with the finder.
            var quietZonePixel = bitmap.GetPixel((int)MathF.Round(module * 0.5f), (int)MathF.Round(module * 0.5f));

            await Assert.That(ring).IsEqualTo(quietZonePixel).Because($"{symbology} {background} finder ring");
            // Alpha does survive the round trip exactly, and it is the half of this that issue 354
            // is about: the ring must let the background through rather than being painted over.
            await Assert.That(ring.Alpha).IsEqualTo(backgroundColor.Alpha).Because($"{symbology} {background} finder ring alpha");
            await Assert.That(centre).IsEqualTo(SKColors.Black).Because($"{symbology} {background} finder centre");
        }
    }

    /// <summary>
    /// The same two defects, on the canvas shape that only became reachable when the square
    /// symbologies started letterboxing instead of stretching.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue 337 is the light ring drawn dark and issue 354 is a transparent background not
    /// showing through it. Both live in the finder's own painting, which the aspect change does
    /// not touch, but that change moved everything around it: the symbol no longer starts at the
    /// canvas origin, and the canvas outside it is now painted with a pad colour of its own. A
    /// ring that read its colour from the wrong layer, or a pad painted over the symbol rather
    /// than around it, would show up here and nowhere else in this class, whose other cases all
    /// render onto a canvas the symbol fills.
    /// </para>
    /// <para>
    /// Sampled relative to the fitted content rather than the canvas: module (1.5, 3.5) for the
    /// ring and (3.5, 3.5) for the dark centre, as the cases above do.
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
    public async Task StyledSymbol_FinderInnerRing_CarriesTheBackground_OnANonSquareCanvas(string symbology, string background)
    {
        var backgroundColor = background switch
        {
            "transparent" => SKColors.Transparent,
            "translucent" => new SKColor(0x00, 0x00, 0xFF, 0x80),
            _ => new SKColor(0xFF, 0xD7, 0x00, 0xFF),
        };

        const int canvasWidth = 900;
        const int canvasHeight = 450;

        SKBitmap bitmap;
        int quietZone, matrixWidth, matrixHeight;
        if (symbology == "rmqr")
        {
            quietZone = 2;
            matrixWidth = 59 + quietZone * 2;
            matrixHeight = 11 + quietZone * 2;
            var data = RmQRCodeGenerator.Create("https://githu", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R11x59, QuietZoneSize = quietZone });
            bitmap = new RmQRCodeImageBuilder(data)
                .WithSize(canvasWidth, canvasHeight)
                .WithColors(SKColors.Black, backgroundColor)
                .WithModuleShape(RectangleModuleShape.Default, 0.9f)
                .ToBitmap();
        }
        else if (symbology == "microqr")
        {
            quietZone = 2;
            var data = MicroQRCodeGenerator.Create("https://githu", MicroQREccLevel.M, new MicroQRCodeGeneratorOptions { QuietZoneSize = quietZone });
            matrixWidth = matrixHeight = data.Size;
            bitmap = new MicroQRCodeImageBuilder(data)
                .WithSize(canvasWidth, canvasHeight)
                .WithColors(SKColors.Black, backgroundColor)
                .WithModuleShape(RectangleModuleShape.Default, 0.9f)
                .ToBitmap();
        }
        else
        {
            quietZone = 4;
            var data = QRCodeGenerator.Create("https://githu", QREccLevel.H, new QRCodeGeneratorOptions { QuietZoneSize = quietZone });
            matrixWidth = matrixHeight = data.Size;
            bitmap = new QRCodeImageBuilder(data)
                .WithSize(canvasWidth, canvasHeight)
                .WithColors(SKColors.Black, backgroundColor)
                .WithModuleShape(RectangleModuleShape.Default, 0.9f)
                .ToBitmap();
        }

        using (bitmap)
        {
            var module = Math.Min((double)canvasWidth / matrixWidth, (double)canvasHeight / matrixHeight);
            // Mirrors the builder's own arithmetic, double and epsilon included.
            var left = Math.Max(0d, Math.Floor((canvasWidth - module * matrixWidth) / 2 + 1e-6));
            var top = Math.Max(0d, Math.Floor((canvasHeight - module * matrixHeight) / 2 + 1e-6));

            SKColor At(double col, double row) => bitmap.GetPixel(
                (int)Math.Round(left + col * module),
                (int)Math.Round(top + row * module));

            var ring = At(quietZone + 1.5f, quietZone + 3.5f);
            var centre = At(quietZone + 3.5f, quietZone + 3.5f);
            var quietZonePixel = At(0.5f, 0.5f);

            await Assert.That(ring).IsEqualTo(quietZonePixel).Because($"{symbology} {background} finder ring");
            await Assert.That(ring.Alpha).IsEqualTo(backgroundColor.Alpha).Because($"{symbology} {background} finder ring alpha");
            await Assert.That(centre).IsEqualTo(SKColors.Black).Because($"{symbology} {background} finder centre");
            // The pad takes the background when no clear colour is set, so a pad painted over the
            // symbol instead of around it would leave the quiet zone denser than the canvas edge.
            await Assert.That(bitmap.GetPixel(2, 2)).IsEqualTo(quietZonePixel).Because($"{symbology} {background} pad");
        }
    }

    /// <summary>
    /// The shape of <c>samples/Dotfiles/FinderPatternAlwaysBlack.cs</c>, the repro for issue 337,
    /// on a canvas that letterboxes: a gradient over the modules, an explicit finder shape, an
    /// opaque background, and transparent surroundings asked for by name. The gradient is what
    /// made the ring come out dark, since it is a shader on the paint the finder also draws with.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(GetFinderPatternShapes))]
    public async Task GradientStyledSymbol_FinderInnerRing_CarriesTheBackground_OnANonSquareCanvas(FinderPatternShape finderPatternShape)
    {
        var backgroundColor = SKColors.Yellow;
        const int quietZone = 4;
        const int canvasWidth = 900;
        const int canvasHeight = 450;

        var data = QRCodeGenerator.Create("Test 1", QREccLevel.H, new QRCodeGeneratorOptions { QuietZoneSize = quietZone });
        using var bitmap = new QRCodeImageBuilder(data)
            .WithSize(canvasWidth, canvasHeight)
            .WithColors(SKColors.Black, backgroundColor).WithClearColor(SKColors.Transparent)
            .WithGradient(new GradientOptions([SKColors.Blue, SKColors.Purple, SKColors.Pink], GradientDirection.TopLeftToBottomRight, [0f, 0.5f, 1f]))
            .WithFinderPatternShape(finderPatternShape)
            .WithModuleShape(RoundedRectangleModuleShape.Default, sizePercent: 0.9f)
            .ToBitmap();

        var module = Math.Min((double)canvasWidth / data.Size, (double)canvasHeight / data.Size);
        var left = Math.Max(0d, Math.Floor((canvasWidth - module * data.Size) / 2 + 1e-6));
        var top = Math.Max(0d, Math.Floor((canvasHeight - module * data.Size) / 2 + 1e-6));

        SKColor At(double col, double row) => bitmap.GetPixel(
            (int)Math.Round(left + col * module),
            (int)Math.Round(top + row * module));

        await Assert.That(At(quietZone + 1.5f, quietZone + 3.5f)).IsEqualTo(backgroundColor)
            .Because($"{finderPatternShape.GetType().Name} ring");
        await Assert.That(At(0.5f, 0.5f)).IsEqualTo(backgroundColor)
            .Because($"{finderPatternShape.GetType().Name} quiet zone");
        // Transparent surroundings were asked for by name, so the pad must not take the background.
        await Assert.That(bitmap.GetPixel(2, 2).Alpha).IsEqualTo((byte)0)
            .Because($"{finderPatternShape.GetType().Name} pad");
    }

    /// <summary>
    /// Draws the 3x3 centre and nothing else: the outer square is deliberately left out so a
    /// renderer that painted or erased around the shape would be visible in the pixels.
    /// </summary>
    private sealed class CentreOnlyFinderPatternShape : FinderPatternShape
    {
        public override bool RequiresAntialiasing => false;

        public int DrawCount { get; private set; }

        public SKColor ReceivedPaintColor { get; private set; }

        public override void Draw(SKCanvas canvas, SKRect rect, SKPaint paint)
        {
            DrawCount++;
            ReceivedPaintColor = paint.Color;
            var moduleWidth = rect.Width / 7f;
            var moduleHeight = rect.Height / 7f;
            canvas.DrawRect(SKRect.Create(rect.Left + moduleWidth * 2, rect.Top + moduleHeight * 2, moduleWidth * 3, moduleHeight * 3), paint);
        }
    }
}
