using SkiaSharp;
using FeatherQR.SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// The low-level renderer fits the symbol into the area it is given, the rule the image builders follow.
/// </summary>
/// <remarks>
/// <para>
/// Standard QR and Micro QR used to fill the area on both axes, so a non-square area gave the symbol non-square modules, found or missed depending on the aspect ratio and the reader, while rMQR's overload and every builder fitted. The same width and height gave a readable symbol from one entry point and an unreadable one from another.
/// </para>
/// <para>
/// The public geometry helpers answer for the same area the caller passed to <c>Render</c>, so they have to fit the same way, or they point beside what was drawn.
/// </para>
/// <para>
/// Pre-distorting a symbol stays possible through the canvas transform, which is where a caller who owns the canvas says it.
/// </para>
/// </remarks>
public class SymbolRendererAreaFitTest
{
    private const string Content = "https://githu";

    public static IEnumerable<(string, string, int, int)> NonSquareAreas()
    {
        foreach (var symbology in new[] { "qr", "microqr" })
        {
            foreach (var entry in new[] { "renderer", "extensionArea", "extensionSize" })
            {
                yield return (symbology, entry, 900, 450);
                yield return (symbology, entry, 300, 900);
                // This payload still decoded stretched to 1.5:1, so this row cannot catch a lost fit (the
                // two above do); it pins that a canvas which was readable before stays readable fitted.
                yield return (symbology, entry, 600, 400);
            }
        }

        // rMQR has always fitted; it stays in the table as the reference the others now match.
        yield return ("rmqr", "renderer", 900, 450);
        yield return ("rmqr", "renderer", 300, 900);
    }

    [Test]
    [MethodDataSource(nameof(NonSquareAreas))]
    public async Task Render_NonSquareArea_Decodes(string symbology, string entry, int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            Draw(canvas, symbology, entry, SKRect.Create(0, 0, width, height), SKColors.White);
        }

        await Assert.That(TryDecode(symbology, bitmap)).IsTrue()
            .Because($"{symbology} through {entry} at {width}x{height}");
    }

    /// <summary>
    /// A stretched render keeps every module centre where a stretched grid expects it, so decoding
    /// alone could pass for the wrong reason on a lenient reader. The drawn pixels settle it without
    /// assuming any grid: a square symbology's dark modules span a square centred in the area.
    /// </summary>
    [Test]
    [Arguments("qr", 900, 450)]
    [Arguments("qr", 300, 900)]
    [Arguments("microqr", 900, 450)]
    [Arguments("microqr", 300, 900)]
    public async Task Render_NonSquareArea_DarkModulesSpanASquareCentredInTheArea(string symbology, int width, int height)
    {
        var area = SKRect.Create(20, 30, width, height);
        using var bitmap = Canvas(area, SKColors.White, c => Draw(c, symbology, "renderer", area, SKColors.White));

        var box = DarkBoundingBox(bitmap);
        var module = Math.Min(width, height) / (float)Size(symbology);

        await Assert.That((float)box.Width / box.Height).IsBetween(0.98f, 1.02f)
            .Because($"{symbology} in {width}x{height}: dark box {box.Width}x{box.Height}");
        await Assert.That(Math.Abs(box.MidX - area.MidX)).IsLessThanOrEqualTo(module)
            .Because("the symbol is centred horizontally");
        await Assert.That(Math.Abs(box.MidY - area.MidY)).IsLessThanOrEqualTo(module)
            .Because("the symbol is centred vertically");
    }

    /// <summary>
    /// The area is the caller's, so the background covers all of it, not just the fitted symbol,
    /// and nothing outside it is touched. rMQR's overload has always done exactly this.
    /// </summary>
    [Test]
    [Arguments("qr")]
    [Arguments("microqr")]
    public async Task Render_NonSquareArea_BackgroundCoversTheWholeAreaAndNothingElse(string symbology)
    {
        var background = new SKColor(0xFF, 0xD7, 0x00, 0xFF);
        var area = SKRect.Create(20, 30, 900, 450);
        using var bitmap = Canvas(area, SKColors.Red, c => Draw(c, symbology, "renderer", area, background));

        foreach (var (x, y) in new[] { (21, 31), (918, 31), (21, 478), (918, 478) })
        {
            await Assert.That(bitmap.GetPixel(x, y)).IsEqualTo(background).Because($"inside the area at ({x}, {y})");
        }
        // Above and below the area as well as beside it: a symbol fitted to the longer side would spill there.
        foreach (var (x, y) in new[] { (5, 5), (bitmap.Width - 5, bitmap.Height - 5), ((int)area.MidX, 25), ((int)area.MidX, (int)area.Bottom + 15) })
        {
            await Assert.That(bitmap.GetPixel(x, y)).IsEqualTo(SKColors.Red).Because($"outside the area at ({x}, {y})");
        }
    }

    /// <summary>
    /// Pre-distortion for an output device whose dots are not square is asked for with the canvas
    /// transform: a square area under a 2:1 scale comes out twice as wide as it is tall.
    /// </summary>
    [Test]
    [Arguments("qr")]
    [Arguments("microqr")]
    public async Task Render_UnderANonUniformCanvasScale_StretchesWithTheCanvas(string symbology)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(900, 450, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            canvas.Scale(2f, 1f);
            Draw(canvas, symbology, "renderer", SKRect.Create(0, 0, 450, 450), SKColors.White);
        }

        var box = DarkBoundingBox(bitmap);
        await Assert.That((float)box.Width / box.Height).IsBetween(1.98f, 2.02f)
            .Because($"dark box {box.Width}x{box.Height}");
    }

    // ─── Geometry helpers agree with the render ───

    [Test]
    [Arguments(0, 900, 450)]
    [Arguments(1, 900, 450)]
    [Arguments(2, 900, 450)]
    [Arguments(0, 300, 900)]
    [Arguments(1, 300, 900)]
    [Arguments(2, 300, 900)]
    public async Task GetFinderPatternRect_NonSquareArea_OutlinesTheDrawnFinder(int index, int width, int height)
    {
        var data = StandardQr();
        var area = SKRect.Create(20, 30, width, height);
        using var bitmap = Canvas(area, SKColors.White, c => SymbolRenderer.Render(c, area, data, SKColors.Black, SKColors.White));

        var rect = SymbolRenderer.GetFinderPatternRect(data, index, area);
        var cell = rect.Width / 7;

        await Assert.That(rect.Width).IsEqualTo(rect.Height).Within(0.001f).Because("a finder is 7x7 square modules");
        await Assert.That(IsDark(bitmap, rect.Left + cell / 2, rect.Top + cell / 2)).IsTrue().Because("outer ring, first module");
        await Assert.That(IsDark(bitmap, rect.Right - cell / 2, rect.Bottom - cell / 2)).IsTrue().Because("outer ring, last module");
        await Assert.That(IsDark(bitmap, rect.MidX, rect.MidY)).IsTrue().Because("centre stone");
        await Assert.That(IsDark(bitmap, rect.Left - cell / 2, rect.MidY)).IsFalse().Because("light module beside the finder");
    }

    /// <summary>
    /// Fitting a square into a square changes nothing, so the helper gives one answer whether it is
    /// passed the caller's area or the square the symbol was actually drawn in.
    /// </summary>
    [Test]
    [Arguments(900, 450)]
    [Arguments(300, 900)]
    public async Task GetFinderPatternRect_CallersAreaAndFittedSquare_GiveTheSameRect(int width, int height)
    {
        var data = StandardQr();
        var area = SKRect.Create(20, 30, width, height);
        var side = Math.Min(width, height);
        var fitted = SKRect.Create(area.Left + (width - side) / 2f, area.Top + (height - side) / 2f, side, side);

        for (var i = 0; i < 3; i++)
        {
            await Assert.That(SymbolRenderer.GetFinderPatternRect(data, i, area)).IsEqualTo(SymbolRenderer.GetFinderPatternRect(data, i, fitted))
                .Because($"finder {i}");
        }
    }

    [Test]
    [Arguments("modules", 900, 450)]
    [Arguments("modules", 300, 900)]
    [Arguments("percent", 900, 450)]
    [Arguments("percent", 300, 900)]
    public async Task GetIconRects_NonSquareArea_CentresASquareIconInTheSymbol(string sizing, int width, int height)
    {
        var data = QRCodeGenerator.Create(Content, QREccLevel.H, new QRCodeGeneratorOptions { QuietZoneSize = 4 });
        using var logo = new SKBitmap(32, 32);
        var icon = sizing == "modules"
            ? IconData.FromImageByModules(logo, iconSizeModules: 7, iconBorderModules: 1, maxCoreOccupancyPercent: 40)
            : IconData.FromImage(logo, iconSizePercent: 15, iconBorderWidth: 2);
        var area = SKRect.Create(20, 30, width, height);

        var (iconRect, borderRect) = SymbolRenderer.GetIconRects(data, area, icon);

        await Assert.That(iconRect.Width).IsEqualTo(iconRect.Height).Within(0.001f).Because("icon");
        await Assert.That(borderRect.Width).IsEqualTo(borderRect.Height).Within(0.001f).Because("border");
        await Assert.That(iconRect.MidX).IsEqualTo(area.MidX).Within(0.01f);
        await Assert.That(iconRect.MidY).IsEqualTo(area.MidY).Within(0.01f);
        if (sizing == "modules")
        {
            var module = Math.Min(width, height) / (float)data.Size;
            await Assert.That(iconRect.Width).IsEqualTo(7 * module).Within(0.01f).Because("7 modules of the fitted symbol");
        }
    }

    public static IEnumerable<(string, string, int, int)> StyledNonSquareAreas()
    {
        foreach (var symbology in new[] { "qr", "microqr" })
        {
            foreach (var style in new[] { "circleModules", "gappedModules", "circleFinder", "gradient", "translucentFinder" })
            {
                yield return (symbology, style, 900, 450);
                yield return (symbology, style, 300, 900);
            }
        }
    }

    /// <summary>
    /// Every path through the renderer fits, not only the merged runs plain modules take: per-module
    /// drawing (custom shapes, gaps), the finder shapes (including the layer a translucent background
    /// needs) and the gradient land exactly where they would in the fitted square drawn on its own.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(StyledNonSquareAreas))]
    public async Task Render_NonSquareArea_StyledSymbolDrawsAsInItsFittedSquare(string symbology, string style, int width, int height)
    {
        var area = SKRect.Create(20, 30, width, height);
        var side = Math.Min(width, height);
        var square = SKRect.Create(area.Left + (width - side) / 2f, area.Top + (height - side) / 2f, side, side);

        using var inArea = Canvas(area, SKColors.Gray, c => DrawStyled(c, symbology, style, area));
        using var inSquare = Canvas(area, SKColors.Gray, c => DrawStyled(c, symbology, style, square));

        var differing = 0;
        for (var y = (int)square.Top; y < (int)square.Bottom; y++)
        {
            for (var x = (int)square.Left; x < (int)square.Right; x++)
            {
                if (inArea.GetPixel(x, y) != inSquare.GetPixel(x, y)) differing++;
            }
        }

        // The bands beside the symbol are the background and nothing else: a styled path that drew past
        // its fitted square would land here, where comparing the square alone cannot see it.
        var band = inArea.GetPixel((int)area.Left + 1, (int)area.Top + 1);
        var strayed = 0;
        for (var y = (int)area.Top; y < (int)area.Bottom; y++)
        {
            for (var x = (int)area.Left; x < (int)area.Right; x++)
            {
                if (x >= (int)square.Left && x < (int)square.Right && y >= (int)square.Top && y < (int)square.Bottom) continue;
                if (inArea.GetPixel(x, y) != band) strayed++;
            }
        }

        await Assert.That(differing).IsEqualTo(0).Because($"{symbology} {style} in {width}x{height}");
        await Assert.That(band).IsNotEqualTo(SKColors.Gray).Because("the background covers the bands");
        await Assert.That(strayed).IsEqualTo(0).Because($"{symbology} {style} draws nothing in the bands of {width}x{height}");
    }

    /// <summary>
    /// The canvas extensions clear the canvas and hand the renderer the area, so their placement and
    /// background are the renderer's: a 900x450 call draws the fitted symbol with the background across
    /// the whole area, not a square in the corner with the clear colour beside it.
    /// </summary>
    [Test]
    [Arguments("qr", "size")]
    [Arguments("qr", "area")]
    [Arguments("microqr", "size")]
    [Arguments("microqr", "area")]
    public async Task CanvasExtension_NonSquareArea_DrawsWhatTheRendererDraws(string symbology, string overload)
    {
        var background = new SKColor(0xFF, 0xD7, 0x00, 0xFF);
        var area = overload == "size" ? SKRect.Create(0, 0, 900, 450) : SKRect.Create(20, 30, 900, 450);

        using var viaExtension = Canvas(area, SKColors.Blue, c =>
        {
            switch (symbology, overload)
            {
                case ("qr", "size"): c.Render(StandardQr(), 900, 450, SKColors.Red, SKColors.Black, background); break;
                case ("qr", "area"): c.Render(StandardQr(), area, SKColors.Red, SKColors.Black, background); break;
                case ("microqr", "size"): c.Render(MicroQr(), 900, 450, SKColors.Red, SKColors.Black, background); break;
                default: c.Render(MicroQr(), area, SKColors.Red, SKColors.Black, background); break;
            }
        });
        using var viaRenderer = Canvas(area, SKColors.Red, c => Draw(c, symbology, "renderer", area, background));

        // Compared in order: the same pixels moved elsewhere are a different image.
        await Assert.That(DifferingBytes(viaExtension.Bytes, viaRenderer.Bytes)).IsEqualTo(0);
    }

    public static IEnumerable<(string, string)> InvertedAreas()
    {
        foreach (var symbology in new[] { "qr", "microqr", "rmqr" })
        {
            foreach (var axis in new[] { "width", "height", "both" })
            {
                yield return (symbology, axis);
            }
        }
    }

    /// <summary>
    /// An inverted area reads two ways, a mirror or the same bounds written backwards, and for an
    /// asymmetric symbol the two are different pictures, so it is refused rather than guessed. All
    /// three symbologies refuse it alike; rMQR used to draw outside an area inverted vertically.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(InvertedAreas))]
    public async Task Render_InvertedArea_Throws(string symbology, string axis)
    {
        var area = Invert(SKRect.Create(100, 100, 400, 200), axis);
        using var bitmap = new SKBitmap(600, 400);
        using var canvas = new SKCanvas(bitmap);

        var exception = await Assert.That(() => Draw(canvas, symbology, "renderer", area, SKColors.White)).Throws<ArgumentException>()
            .Because($"{symbology} with an inverted {axis}");

        // The message is the only place a caller meets the mirror recipe, and a bare -1 scale draws off-canvas.
        await Assert.That(exception!.Message).Contains("about the area");
    }

    [Test]
    [Arguments(float.NaN)]
    [Arguments(float.PositiveInfinity)]
    [Arguments(float.NegativeInfinity)]
    public async Task Render_NonFiniteArea_Throws(float value)
    {
        using var bitmap = new SKBitmap(100, 100);
        using var canvas = new SKCanvas(bitmap);

        foreach (var symbology in new[] { "qr", "microqr", "rmqr" })
        {
            await Assert.That(() => Draw(canvas, symbology, "renderer", new SKRect(0, 0, value, 50), SKColors.White)).Throws<ArgumentException>()
                .Because(symbology);
        }
    }

    /// <summary>
    /// Finite edges can still span more than a float holds, and an infinite width breaks the fit.
    /// </summary>
    [Test]
    [Arguments("width")]
    [Arguments("height")]
    public async Task Render_AreaSpanningMoreThanAFloat_Throws(string axis)
    {
        var area = axis == "width" ? new SKRect(float.MinValue, 0, float.MaxValue, 50) : new SKRect(0, float.MinValue, 50, float.MaxValue);
        using var bitmap = new SKBitmap(100, 100);
        using var canvas = new SKCanvas(bitmap);

        foreach (var symbology in new[] { "qr", "microqr", "rmqr" })
        {
            foreach (var entry in new[] { "renderer", "extensionArea" })
            {
                await Assert.That(() => Draw(canvas, symbology, entry, area, SKColors.White)).Throws<ArgumentException>()
                    .Because($"{symbology} through {entry}");
            }
        }
        await Assert.That(() => SymbolRenderer.GetFinderPatternRect(StandardQr(), 0, area)).Throws<ArgumentException>();
    }

    /// <summary>
    /// A zero-sized area has one reading, nothing fits, and a canvas before layout or a collapsed
    /// panel produces it routinely, so it draws nothing rather than throwing from a paint callback.
    /// </summary>
    [Test]
    [Arguments("qr", "width")]
    [Arguments("qr", "height")]
    [Arguments("microqr", "width")]
    [Arguments("microqr", "height")]
    [Arguments("rmqr", "width")]
    [Arguments("rmqr", "height")]
    public async Task Render_ZeroSizeArea_DrawsNothing(string symbology, string axis)
    {
        var area = axis == "width" ? SKRect.Create(50, 50, 0, 40) : SKRect.Create(50, 50, 40, 0);
        using var bitmap = DrawInBounds(symbology, area);

        var drawn = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y) != SKColors.Red) drawn++;
            }
        }

        await Assert.That(drawn).IsEqualTo(0).Because($"{symbology} with a zero {axis}");
    }

    public static IEnumerable<(string, string, string)> EmptySymbolOverlays()
    {
        foreach (var areaKind in new[] { "zeroWidth", "zeroHeight", "sliver", "tallSliver" })
        {
            foreach (var overlay in new[] { "percentIcon", "moduleIcon", "captionIcon", "fixedDots" })
            {
                yield return ("qr", overlay, areaKind);
            }
            yield return ("microqr", "fixedDots", areaKind);
            yield return ("rmqr", "fixedDots", areaKind);
        }
    }

    /// <summary>
    /// Everything drawn over the modules has to come to nothing too. What is sized in pixels rather
    /// than in cells survives a symbol with no size: an icon border, a caption, and a caller-written
    /// module shape that draws a fixed dot. The sliver is an area whose fitted symbol collapses to
    /// nothing without the area itself being zero.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(EmptySymbolOverlays))]
    public async Task Render_SymbolWithNoSize_DrawsNothing(string symbology, string overlay, string areaKind)
    {
        var area = EmptyArea(areaKind);
        using var logo = new SKBitmap(32, 32);
        logo.Erase(SKColors.Blue);
        using var font = new SKFont(SKTypeface.Default, 20);
        using var bitmap = new SKBitmap(new SKImageInfo(200, 200, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Red);
            TranslateToBitmap(canvas, areaKind);
            DrawWithOverlay(canvas, symbology, overlay, area, logo, font);
        }

        await Assert.That(ChangedPixels(bitmap, SKColors.Red)).IsEqualTo(0)
            .Because($"{symbology} with {overlay} in a {areaKind} area");
    }

    /// <summary>
    /// The helper describes the render, so an area the symbol comes out of with no size gives empty
    /// rects at its centre, not a border the renderer never draws.
    /// </summary>
    [Test]
    [Arguments("percent", "zeroWidth")]
    [Arguments("percent", "zeroHeight")]
    [Arguments("percent", "sliver")]
    [Arguments("percent", "tallSliver")]
    [Arguments("modules", "zeroWidth")]
    [Arguments("modules", "zeroHeight")]
    [Arguments("modules", "sliver")]
    [Arguments("modules", "tallSliver")]
    public async Task GetIconRects_SymbolWithNoSize_GivesEmptyRectsAtTheCentre(string sizing, string areaKind)
    {
        var area = EmptyArea(areaKind);
        var data = QRCodeGenerator.Create(Content, QREccLevel.H, new QRCodeGeneratorOptions { QuietZoneSize = 4 });
        using var logo = new SKBitmap(32, 32);
        var icon = sizing == "modules"
            ? IconData.FromImageByModules(logo, iconSizeModules: 7, iconBorderModules: 1, maxCoreOccupancyPercent: 40)
            : IconData.FromImage(logo, iconSizePercent: 15, iconBorderWidth: 2);

        var (iconRect, borderRect) = SymbolRenderer.GetIconRects(data, area, icon);

        var centre = new SKRect(area.MidX, area.MidY, area.MidX, area.MidY);
        await Assert.That(iconRect).IsEqualTo(centre).Because("icon");
        await Assert.That(borderRect).IsEqualTo(centre).Because("border");
    }

    /// <summary>
    /// An icon that does not fit is a mistake whatever the area's size, and a collapsed panel must not
    /// hide it until the panel opens. The helper throws for these already, so the render has to agree.
    /// </summary>
    [Test]
    [Arguments("percentZero")]
    [Arguments("negativeBorder")]
    [Arguments("overOccupancy")]
    [Arguments("borderModulesOverflow")]
    [Arguments("sizeModulesOverflow")]
    public async Task Render_InvalidIcon_ThrowsEvenWhenNothingFits(string kind)
    {
        var data = QRCodeGenerator.Create(Content, QREccLevel.H, new QRCodeGeneratorOptions { QuietZoneSize = 4 });
        using var logo = new SKBitmap(32, 32);
        var icon = kind switch
        {
            "percentZero" => IconData.FromImage(logo, iconSizePercent: 0, iconBorderWidth: 2),
            "negativeBorder" => IconData.FromImage(logo, iconSizePercent: 15, iconBorderWidth: -1),
            // Module counts near int.MaxValue: the total used to wrap negative and pass both limits.
            "borderModulesOverflow" => IconData.FromImageByModules(logo, iconSizeModules: 1, iconBorderModules: int.MaxValue, maxCoreOccupancyPercent: 100),
            "sizeModulesOverflow" => IconData.FromImageByModules(logo, iconSizeModules: int.MaxValue, iconBorderModules: 1, maxCoreOccupancyPercent: 100),
            _ => IconData.FromImageByModules(logo, iconSizeModules: data.GetCoreSize(), iconBorderModules: 1, maxCoreOccupancyPercent: 100),
        };
        using var bitmap = new SKBitmap(new SKImageInfo(200, 200, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Blue);

        foreach (var area in new[] { SKRect.Create(50, 50, 0, 40), SKRect.Create(50, 50, 100, 100) })
        {
            // The type is the contract: an out-of-range setting is an argument error, an icon too big for
            // the symbol is not, and the docs name both.
            if (kind is "overOccupancy" or "borderModulesOverflow" or "sizeModulesOverflow")
            {
                await Assert.That(() => SymbolRenderer.Render(canvas, area, data, SKColors.Black, SKColors.White, icon))
                    .Throws<InvalidOperationException>().Because($"the renderer with {kind} in {area.Width}x{area.Height}");
                await Assert.That(() => canvas.Render(data, area, SKColors.Red, SKColors.Black, SKColors.White, icon))
                    .Throws<InvalidOperationException>().Because($"the extension with {kind} in {area.Width}x{area.Height}");
            }
            else
            {
                await Assert.That(() => SymbolRenderer.Render(canvas, area, data, SKColors.Black, SKColors.White, icon))
                    .Throws<ArgumentOutOfRangeException>().Because($"the renderer with {kind} in {area.Width}x{area.Height}");
                await Assert.That(() => canvas.Render(data, area, SKColors.Red, SKColors.Black, SKColors.White, icon))
                    .Throws<ArgumentOutOfRangeException>().Because($"the extension with {kind} in {area.Width}x{area.Height}");
            }
        }

        await Assert.That(ChangedPixels(bitmap, SKColors.Blue)).IsEqualTo(0)
            .Because("the extension refuses the icon before it clears the canvas");
    }

    /// <summary>
    /// The extensions clear the canvas, so every argument they refuse has to be refused before that,
    /// or a refused call costs the caller whatever was on the canvas.
    /// </summary>
    [Test]
    [Arguments("qr", "nullData")]
    [Arguments("qr", "moduleSizeAboveOne")]
    [Arguments("qr", "moduleSizeNaN")]
    [Arguments("qr", "undefinedGradientDirection")]
    [Arguments("microqr", "nullData")]
    [Arguments("microqr", "moduleSizeAboveOne")]
    [Arguments("microqr", "moduleSizeNaN")]
    [Arguments("microqr", "undefinedGradientDirection")]
    [Arguments("rmqr", "nullData")]
    [Arguments("rmqr", "moduleSizeAboveOne")]
    [Arguments("rmqr", "moduleSizeNaN")]
    [Arguments("rmqr", "undefinedGradientDirection")]
    public async Task CanvasExtension_InvalidArgument_ThrowsBeforeClearingTheCanvas(string symbology, string argument)
    {
        var area = SKRect.Create(10, 10, 100, 100);
        var percent = argument == "moduleSizeAboveOne" ? 2f : argument == "moduleSizeNaN" ? float.NaN : 1f;
        using var bitmap = new SKBitmap(new SKImageInfo(150, 150, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Blue);

        var gradient = argument == "undefinedGradientDirection"
            ? new GradientOptions([SKColors.Blue, SKColors.Red], (GradientDirection)99)
            : null;

        await Assert.That(() => RenderThroughExtension(canvas, symbology, area, argument == "nullData", percent, gradient))
            .Throws<ArgumentException>().Because($"{symbology} with {argument}");
        await Assert.That(ChangedPixels(bitmap, SKColors.Blue)).IsEqualTo(0)
            .Because($"{symbology} with {argument} must leave the canvas as it was");

        // The renderer refuses the same arguments; only the clear is the extensions' own.
        await Assert.That(() => RenderThroughRenderer(canvas, symbology, area, argument == "nullData", percent, gradient))
            .Throws<ArgumentException>().Because($"the renderer with {argument}");
        await Assert.That(ChangedPixels(bitmap, SKColors.Blue)).IsEqualTo(0)
            .Because($"the renderer with {argument} must refuse it before drawing");
    }

    /// <summary>
    /// A null canvas is a mistake at every size, and the check that catches it must not sit behind the
    /// early return for a symbol with no size.
    /// </summary>
    [Test]
    [Arguments("qr")]
    [Arguments("microqr")]
    [Arguments("rmqr")]
    public async Task Render_NullCanvas_Throws(string symbology)
    {
        foreach (var area in new[] { SKRect.Create(0, 0, 0, 40), SKRect.Create(0, 0, 40, 40) })
        {
            await Assert.That(() => RenderThroughRenderer(null!, symbology, area, false, 1f, null))
                .Throws<ArgumentNullException>().Because($"the renderer at {area.Width}x{area.Height}");
            await Assert.That(() => RenderThroughExtension(null!, symbology, area, false, 1f, null))
                .Throws<ArgumentNullException>().Because($"the extension at {area.Width}x{area.Height}");
        }
    }

    /// <summary>
    /// An area no symbol can be placed in still gets its background: the area is the caller's, and only
    /// the symbol is missing. Past an aspect ratio of about 2^25 the centering offset swallows the
    /// square symbologies' side, and rMQR's rectangle goes the same way about three powers of two
    /// later, so the fit is measured here rather than assumed. That nothing else is drawn is pinned by
    /// the sliver cases instead: at these coordinates a float step is thousands of units wide, so no
    /// shape of any size survives being placed, whatever the renderer does.
    /// </summary>
    [Test]
    [Arguments("qr")]
    [Arguments("microqr")]
    [Arguments("rmqr")]
    public async Task Render_AreaTooLongForAnySymbol_StillPaintsItsBackground(string symbology)
    {
        var area = SKRect.Create(0, 0, 4e10f, 64f);
        var fitted = symbology == "rmqr"
            ? SymbolRenderer.GetLetterboxedArea(area, RmQr().Width, RmQr().Height)
            : SymbolRenderer.GetSquareArea(area);
        using var bitmap = new SKBitmap(new SKImageInfo(80, 80, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Red);
            Draw(canvas, symbology, "renderer", area, SKColors.White);
        }

        await Assert.That(fitted.Width * fitted.Height).IsEqualTo(0)
            .Because($"{symbology} has no symbol to place in this area, fit {fitted}");
        await Assert.That(ChangedPixels(bitmap, SKColors.Red)).IsEqualTo(80 * 64)
            .Because($"{symbology} paints the background over the whole area");
    }

    /// <summary>
    /// The empty-area cases live at coordinates where a float step is coarse, and the canvas is moved
    /// to bring them onto the bitmap. This is the control for that move: the same origin with an area
    /// the symbol fits in has to paint, or those cases would pass by drawing off the bitmap.
    /// </summary>
    [Test]
    [Arguments("sliver")]
    [Arguments("tallSliver")]
    public async Task Render_AtTheEmptyAreaCoordinates_PaintsWhenASymbolFits(string areaKind)
    {
        var origin = EmptyArea(areaKind);
        using var bitmap = new SKBitmap(new SKImageInfo(200, 200, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Red);
            TranslateToBitmap(canvas, areaKind);
            SymbolRenderer.Render(canvas, SKRect.Create(origin.Left, origin.Top, 40, 40), StandardQr(), SKColors.Black, SKColors.White);
        }

        await Assert.That(ChangedPixels(bitmap, SKColors.Red)).IsGreaterThan(1000)
            .Because($"the {areaKind} coordinates are on the bitmap once a symbol fits");
    }

    /// <summary>
    /// Both geometry helpers answer for a symbol that was never drawn the same way: an empty rect, not
    /// one with a cell of the other axis's size.
    /// </summary>
    [Test]
    public async Task GeometryHelpers_AreaTooLongForAnySymbol_GiveEmptyRects()
    {
        var area = SKRect.Create(0, 0, 4e10f, 64f);
        var data = QRCodeGenerator.Create(Content, QREccLevel.H, new QRCodeGeneratorOptions { QuietZoneSize = 4 });
        using var logo = new SKBitmap(32, 32);

        // Where the rect sits matters as much as its size: a caller placing something beside the finder
        // reads its corner, so it has to be the symbol's corner rather than the canvas origin.
        var fitted = SymbolRenderer.GetSquareArea(area);
        var corner = new SKRect(fitted.Left, fitted.Top, fitted.Left, fitted.Top);
        for (var i = 0; i < 3; i++)
        {
            var finder = SymbolRenderer.GetFinderPatternRect(data, i, area);
            await Assert.That(finder).IsEqualTo(corner).Because($"finder {i} was {finder}, fitted {fitted}");
        }

        var centre = new SKRect(fitted.MidX, fitted.MidY, fitted.MidX, fitted.MidY);
        var (iconRect, borderRect) = SymbolRenderer.GetIconRects(data, area, IconData.FromImage(logo, iconSizePercent: 15, iconBorderWidth: 2));
        await Assert.That(iconRect).IsEqualTo(centre).Because("icon");
        await Assert.That(borderRect).IsEqualTo(centre).Because("border");
    }

    /// <summary>
    /// <see cref="GradientDirection.None"/> is the documented way to ask for no gradient, so the
    /// argument check has to let it through to the solid-colour path rather than refuse it.
    /// </summary>
    [Test]
    [Arguments("qr")]
    [Arguments("microqr")]
    [Arguments("rmqr")]
    public async Task Render_GradientDirectionNone_DrawsWhatNoGradientDraws(string symbology)
    {
        var area = SKRect.Create(10, 10, 180, 180);
        var none = new GradientOptions([SKColors.Blue, SKColors.Red], GradientDirection.None);

        using var withNone = Canvas(area, SKColors.Red, c => RenderThroughRenderer(c, symbology, area, false, 1f, none));
        using var without = Canvas(area, SKColors.Red, c => RenderThroughRenderer(c, symbology, area, false, 1f, null));

        await Assert.That(DifferingBytes(withNone.Bytes, without.Bytes)).IsEqualTo(0)
            .Because($"{symbology} draws the same with no gradient and with a gradient of None");
    }

    /// <summary>
    /// The size overloads answer for the canvas, then the symbol, then the size, as the area overloads
    /// do; a caller reading the first failure must not be sent to the wrong argument.
    /// </summary>
    [Test]
    [Arguments("qr")]
    [Arguments("microqr")]
    [Arguments("rmqr")]
    public async Task CanvasExtension_SizeOverload_ReportsTheCanvasBeforeTheSymbolAndTheSize(string symbology)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(50, 50, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);

        var noCanvas = await Assert.That(() => RenderThroughSizeExtension(null!, symbology, -1, 100, true)).Throws<ArgumentNullException>();
        await Assert.That(noCanvas!.ParamName).IsEqualTo("canvas").Because(symbology);

        var noData = await Assert.That(() => RenderThroughSizeExtension(canvas, symbology, -1, 100, true)).Throws<ArgumentNullException>();
        await Assert.That(noData!.ParamName).IsEqualTo("data").Because(symbology);

        var badSize = await Assert.That(() => RenderThroughSizeExtension(canvas, symbology, -1, 100, false)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(badSize!.ParamName).IsEqualTo("width").Because(symbology);
    }

    /// <summary>
    /// Icon settings are refused whether or not the icon has a shape to draw, so the render and the
    /// helper agree on what they refuse.
    /// </summary>
    [Test]
    public async Task Render_IconSettingsWithoutAShape_AreStillRefused()
    {
        var data = QRCodeGenerator.Create(Content, QREccLevel.H, new QRCodeGeneratorOptions { QuietZoneSize = 4 });
        var icon = new IconData { Icon = null!, IconSizePercent = 500 };
        var area = SKRect.Create(10, 10, 100, 100);
        using var bitmap = new SKBitmap(new SKImageInfo(150, 150, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);

        await Assert.That(() => SymbolRenderer.Render(canvas, area, data, SKColors.Black, SKColors.White, icon)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => canvas.Render(data, area, SKColors.Red, SKColors.Black, SKColors.White, icon)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => SymbolRenderer.GetIconRects(data, area, icon)).Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// The extensions clear whatever their size, so a zero size wipes the canvas and draws no code.
    /// </summary>
    [Test]
    [Arguments("qr", "area")]
    [Arguments("qr", "size")]
    [Arguments("microqr", "area")]
    [Arguments("microqr", "size")]
    [Arguments("rmqr", "area")]
    [Arguments("rmqr", "size")]
    public async Task CanvasExtension_ZeroSize_ClearsTheCanvasAndDrawsNoCode(string symbology, string overload)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(100, 100, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Blue);
            // A shape whose size is in pixels, so "drew no code" means the symbol, not just degenerate rects.
            var area = SKRect.Create(10, 10, 0, 40);
            if (overload == "area")
                RenderThroughExtension(canvas, symbology, area, SKColors.White, FixedDotModuleShape.Default);
            else
                RenderThroughExtension(canvas, symbology, 0, 40, FixedDotModuleShape.Default);
        }

        var untouched = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y) == SKColors.Blue) untouched++;
            }
        }

        await Assert.That(untouched).IsEqualTo(0).Because($"{symbology} through the {overload} overload clears the canvas");
        await Assert.That(ChangedPixels(bitmap, bitmap.GetPixel(0, 0))).IsEqualTo(0).Because("and draws no code");
    }

    /// <summary>
    /// The extensions refuse an inverted area before they clear the canvas, so a refused call leaves
    /// whatever was drawn there untouched.
    /// </summary>
    [Test]
    [Arguments("qr")]
    [Arguments("microqr")]
    [Arguments("rmqr")]
    public async Task CanvasExtension_InvertedArea_ThrowsBeforeClearing(string symbology)
    {
        var area = Invert(SKRect.Create(10, 10, 80, 40), "width");
        using var bitmap = new SKBitmap(new SKImageInfo(100, 100, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Blue);

        await Assert.That(() => RenderThroughExtension(canvas, symbology, area)).Throws<ArgumentException>();
        await Assert.That(bitmap.GetPixel(50, 50)).IsEqualTo(SKColors.Blue);
    }

    [Test]
    [Arguments("qr", -1, 100)]
    [Arguments("qr", 100, -1)]
    [Arguments("microqr", -1, 100)]
    [Arguments("microqr", 100, -1)]
    [Arguments("rmqr", -1, 100)]
    [Arguments("rmqr", 100, -1)]
    public async Task CanvasExtension_NegativeSize_Throws(string symbology, int width, int height)
    {
        using var bitmap = new SKBitmap(100, 100);
        using var canvas = new SKCanvas(bitmap);

        await Assert.That(() => RenderThroughExtension(canvas, symbology, width, height)).Throws<ArgumentOutOfRangeException>()
            .Because($"{symbology} at {width}x{height}");
    }

    [Test]
    [Arguments("width")]
    [Arguments("height")]
    [Arguments("both")]
    public async Task GeometryHelpers_InvertedArea_Throw(string axis)
    {
        var data = QRCodeGenerator.Create(Content, QREccLevel.H, new QRCodeGeneratorOptions { QuietZoneSize = 4 });
        using var logo = new SKBitmap(32, 32);
        var icon = IconData.FromImageByModules(logo, iconSizeModules: 7, iconBorderModules: 1, maxCoreOccupancyPercent: 40);
        var area = Invert(SKRect.Create(100, 100, 400, 200), axis);

        await Assert.That(() => SymbolRenderer.GetFinderPatternRect(data, 0, area)).Throws<ArgumentException>();
        await Assert.That(() => SymbolRenderer.GetIconRects(data, area, icon)).Throws<ArgumentException>();
    }

    /// <summary>
    /// A mirrored symbol, for a transfer print or a sticker read through glass, is asked for with the
    /// canvas: flip it about the area and draw into the area as usual.
    /// </summary>
    [Test]
    [Arguments("qr")]
    [Arguments("microqr")]
    [Arguments("rmqr")]
    public async Task Render_UnderAMirroringCanvasScale_MirrorsTheUprightRender(string symbology)
    {
        var bounds = SKRect.Create(100, 100, 400, 200);
        using var upright = DrawInBounds(symbology, bounds);
        using var mirrored = new SKBitmap(new SKImageInfo(600, 400, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(mirrored))
        {
            canvas.Clear(SKColors.Red);
            canvas.Translate(bounds.Left + bounds.Right, 0);
            canvas.Scale(-1f, 1f);
            Draw(canvas, symbology, "renderer", bounds, SKColors.White);
        }

        var differing = 0;
        for (var y = (int)bounds.Top; y < (int)bounds.Bottom; y++)
        {
            for (var x = (int)bounds.Left; x < (int)bounds.Right; x++)
            {
                if (mirrored.GetPixel(x, y) != upright.GetPixel((int)(bounds.Left + bounds.Right) - 1 - x, y)) differing++;
            }
        }

        await Assert.That(differing).IsEqualTo(0).Because(symbology);
    }

    // ─── Fractional areas ───

    /// <summary>
    /// Squares built the way a caller builds them at fractional coordinates (DPI scaling gives thirds
    /// and fifths): <c>SKRect.Create(x, y, s, s)</c> stores right and bottom as rounded sums, so the
    /// two sides can come out a rounding step apart.
    /// </summary>
    private static IEnumerable<SKRect> FractionalSquares()
    {
        yield return SKRect.Create(177.82019f, 185.56491f, 417.3587f, 417.3587f);
        yield return SKRect.Create(1.5021764f, 1.9307674f, 126.768936f, 126.768936f);
        var random = new Random(20260911);
        for (var i = 0; i < 500; i++)
        {
            var side = 50 + random.NextSingle() * 900;
            yield return SKRect.Create(random.NextSingle() * 1000, random.NextSingle() * 1000, side, side);
        }

        // Near the origin the far edge is the largest coordinate, so the slack has to be measured from it.
        for (var i = 0; i < 500; i++)
        {
            var side = 50 + random.NextSingle() * 900;
            yield return SKRect.Create(random.NextSingle() * 4, random.NextSingle() * 4, side, side);
        }

        // A canvas translated to its centre or far corner puts areas at negative coordinates, where the
        // largest coordinate is the most negative one.
        for (var i = 0; i < 500; i++)
        {
            var side = 50 + random.NextSingle() * 900;
            yield return SKRect.Create(-2000 + random.NextSingle() * 1000, -2000 + random.NextSingle() * 1000, side, side);
        }

        // A canvas scale rounds each edge on its own, so these squares need both float steps of slack per
        // axis; a one-step slack shrinks them. Found by searching scaled squares for the widest rounding.
        foreach (var (x, y, side, scale) in new[]
        {
            (-23.474365f, 1407.5728f, 664.66833f, 2.25f),
            (1838.7437f, -356.29858f, 836.80383f, 2.5f),
            (37.519897f, 1639.5476f, 72.111755f, 1.5f),
            (-38.00049f, 1466.7385f, 139.25348f, 1.5f),
            (-501.33374f, -380.40295f, 669.52924f, 2.25f),
        })
        {
            yield return new SKRect(x * scale, y * scale, (x + side) * scale, (y + side) * scale);
        }

        // Far from the origin a float step is a pixel or more, so the slack has to follow the edges' own precision.
        for (var i = 0; i < 500; i++)
        {
            var side = 50 + random.NextSingle() * 900;
            yield return SKRect.Create(-1.2e7f + random.NextSingle() * 2.4e7f, -1.2e7f + random.NextSingle() * 2.4e7f, side, side);
        }
    }

    private static IEnumerable<SKRect> FractionalRects()
    {
        var random = new Random(911);
        for (var i = 0; i < 500; i++)
        {
            yield return SKRect.Create(random.NextSingle() * 1000, random.NextSingle() * 1000, 50 + random.NextSingle() * 900, 50 + random.NextSingle() * 900);
        }
        for (var i = 0; i < 500; i++)
        {
            yield return SKRect.Create(-2000 + random.NextSingle() * 1000, -2000 + random.NextSingle() * 1000, 50 + random.NextSingle() * 900, 50 + random.NextSingle() * 900);
        }
        for (var i = 0; i < 500; i++)
        {
            yield return SKRect.Create(-1.2e7f + random.NextSingle() * 2.4e7f, -1.2e7f + random.NextSingle() * 2.4e7f, 50 + random.NextSingle() * 900, 50 + random.NextSingle() * 900);
        }
    }

    /// <summary>
    /// A square the caller meant comes back as given, so it draws exactly the pixels it drew before
    /// the fit existed. Shrinking it by the rounding step moved module edges by a whole pixel at some
    /// offsets.
    /// </summary>
    [Test]
    public async Task GetSquareArea_SquareAtFractionalCoordinates_ComesBackAsGiven()
    {
        var changed = FractionalSquares().Where(area => SymbolRenderer.GetSquareArea(area) != area).ToArray();

        await Assert.That(changed.Length).IsEqualTo(0)
            .Because(changed.Length == 0 ? "" : $"first: {changed[0]} became {SymbolRenderer.GetSquareArea(changed[0])}");
    }

    /// <summary>
    /// The slack is for float rounding and nothing more. An area a hundredth of a pixel from square,
    /// far enough from the origin that a looser relative slack would swallow it, is still fitted, and
    /// so is one a whole pixel off; otherwise a later loosening would bring the stretch back unnoticed.
    /// At 1e7 a float step is a pixel, and a slack of a few steps there still fits 5 px off.
    /// </summary>
    [Test]
    [Arguments(1000.25f, 1000.5f, 400f, 400.01f)]
    [Arguments(1000f, 1000f, 400f, 401f)]
    [Arguments(700f, 200f, 300f, 450f)]
    [Arguments(1e7f, 1e7f, 400f, 410f)]
    [Arguments(1e7f, 1e7f, 400f, 405f)]
    public async Task GetSquareArea_MoreThanRoundingFromSquare_IsFitted(float left, float top, float width, float height)
    {
        var area = SKRect.Create(left, top, width, height);
        var fitted = SymbolRenderer.GetSquareArea(area);

        await Assert.That(fitted.Width).IsEqualTo(fitted.Height).Within(1e-3f).Because($"{area} became {fitted}");
        await Assert.That(fitted.Height).IsEqualTo(Math.Min(area.Width, area.Height)).Within(1e-3f).Because($"{area} became {fitted}");
        await Assert.That(fitted == area).IsFalse().Because($"{area} must not come back as given");
    }

    /// <summary>
    /// The limit of a slack measured in float steps, written down because it looks like a defect and is
    /// not fixable from here: at 1e9 a step is 64, so this 400x600 area is stored as 384x576 and passes
    /// as square. Tightening cannot separate the two: a square whose edges are each scaled needs two
    /// steps per axis (measured over three million of them), and this rectangle is one and a half steps
    /// from square, so any slack that fits real squares admits it. Nothing renders at these coordinates
    /// either way, since the same step quantises every module edge.
    /// </summary>
    [Test]
    public async Task GetSquareArea_WhereAFloatStepDwarfsTheArea_CannotTellASquareFromARectangle()
    {
        var area = SKRect.Create(1e9f, 1e9f, 400f, 600f);

        await Assert.That(area.Width).IsEqualTo(384f).Because("the area cannot be stored as asked at 1e9");
        await Assert.That(area.Height).IsEqualTo(576f).Because("the area cannot be stored as asked at 1e9");
        await Assert.That(SymbolRenderer.GetSquareArea(area)).IsEqualTo(area).Because("192 is within two float steps per axis");
    }

    /// <summary>
    /// The slack is a share of the coordinates, and near the top of the float range adding the two
    /// axes' coordinates before scaling them overflows, which would take any area for a square.
    /// </summary>
    [Test]
    public async Task GetSquareArea_NearTheTopOfTheFloatRange_IsStillFitted()
    {
        var area = SKRect.Create(2e38f, 2e38f, 1e36f, 2e36f);
        var fitted = SymbolRenderer.GetSquareArea(area);

        await Assert.That(fitted == area).IsFalse().Because($"{area} must not come back as given");
        // A float step at 2e38 is 2e31, a 2e-5 share of these sides.
        await Assert.That(fitted.Width / fitted.Height).IsEqualTo(1f).Within(1e-4f).Because($"{area} became {fitted}");
        await Assert.That(fitted.Height / area.Width).IsEqualTo(1f).Within(1e-4f).Because($"{area} became {fitted}");
    }

    /// <summary>
    /// The renderer hands the helpers an area it already fitted and a caller hands them the original,
    /// so fitting a fitted area has to change nothing, at fractional coordinates too.
    /// </summary>
    [Test]
    public async Task GetSquareArea_FittingTwice_IsFittingOnce()
    {
        var unstable = FractionalRects().Where(area =>
        {
            var once = SymbolRenderer.GetSquareArea(area);
            return SymbolRenderer.GetSquareArea(once) != once;
        }).ToArray();

        await Assert.That(unstable.Length).IsEqualTo(0)
            .Because(unstable.Length == 0 ? "" : $"first: {unstable[0]}");
    }

    [Test]
    public async Task GeometryHelpers_FractionalArea_GiveTheRectsTheRendererDrawsIn()
    {
        var data = QRCodeGenerator.Create(Content, QREccLevel.H, new QRCodeGeneratorOptions { QuietZoneSize = 4 });
        using var logo = new SKBitmap(32, 32);
        var icon = IconData.FromImageByModules(logo, iconSizeModules: 7, iconBorderModules: 1, maxCoreOccupancyPercent: 40);

        var disagreeing = 0;
        foreach (var area in FractionalRects())
        {
            // What Render passes its helpers: the area it drew into.
            var drawn = SymbolRenderer.GetSquareArea(area);
            for (var i = 0; i < 3; i++)
            {
                if (SymbolRenderer.GetFinderPatternRect(data, i, area) != SymbolRenderer.GetFinderPatternRect(data, i, drawn)) disagreeing++;
            }
            if (SymbolRenderer.GetIconRects(data, area, icon) != SymbolRenderer.GetIconRects(data, drawn, icon)) disagreeing++;
        }

        await Assert.That(disagreeing).IsEqualTo(0);
    }

    // ─── Helpers ───

    private static QRCodeData StandardQr() => QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 4 });

    private static MicroQRCodeData MicroQr() => MicroQRCodeGenerator.Create(Content, MicroQREccLevel.M, new MicroQRCodeGeneratorOptions { QuietZoneSize = 2 });

    private static RmQRCodeData RmQr() => RmQRCodeGenerator.Create(Content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R11x59, QuietZoneSize = 2 });

    private static int Size(string symbology) => symbology switch
    {
        "qr" => StandardQr().Size,
        "microqr" => MicroQr().Size,
        _ => throw new ArgumentOutOfRangeException(nameof(symbology)),
    };

    private static void Draw(SKCanvas canvas, string symbology, string entry, SKRect area, SKColor background)
    {
        switch (symbology, entry)
        {
            case ("qr", "renderer"): SymbolRenderer.Render(canvas, area, StandardQr(), SKColors.Black, background); break;
            case ("qr", "extensionArea"): canvas.Render(StandardQr(), area, background, SKColors.Black, background); break;
            case ("qr", "extensionSize"): canvas.Render(StandardQr(), (int)area.Width, (int)area.Height, background, SKColors.Black, background); break;
            case ("microqr", "renderer"): SymbolRenderer.Render(canvas, area, MicroQr(), SKColors.Black, background); break;
            case ("microqr", "extensionArea"): canvas.Render(MicroQr(), area, background, SKColors.Black, background); break;
            case ("microqr", "extensionSize"): canvas.Render(MicroQr(), (int)area.Width, (int)area.Height, background, SKColors.Black, background); break;
            case ("rmqr", "renderer"): SymbolRenderer.Render(canvas, area, RmQr(), SKColors.Black, background); break;
            case ("rmqr", "extensionArea"): canvas.Render(RmQr(), area, background, SKColors.Black, background); break;
            case ("rmqr", "extensionSize"): canvas.Render(RmQr(), (int)area.Width, (int)area.Height, background, SKColors.Black, background); break;
            // An unknown entry must not look like a refused area: this throw is not an ArgumentException.
            default: throw new InvalidOperationException($"{symbology}/{entry}");
        }
    }

    private static void DrawStyled(SKCanvas canvas, string symbology, string style, SKRect area)
    {
        ModuleShape? shape = style == "circleModules" ? CircleModuleShape.Default : null;
        var percent = style == "gappedModules" ? 0.8f : 1f;
        FinderPatternShape? finder = style is "circleFinder" or "translucentFinder" ? CircleFinderPatternShape.Default : null;
        var gradient = style == "gradient" ? new GradientOptions([SKColors.Blue, SKColors.Red], GradientDirection.LeftToRight) : null;
        var background = style == "translucentFinder" ? new SKColor(0xFF, 0xFF, 0xFF, 0x80) : SKColors.White;

        if (symbology == "qr")
            SymbolRenderer.Render(canvas, area, StandardQr(), SKColors.Black, background, null, shape, percent, gradient, finder);
        else
            SymbolRenderer.Render(canvas, area, MicroQr(), SKColors.Black, background, shape, percent, gradient, finder);
    }

    private static bool TryDecode(string symbology, SKBitmap bitmap) => symbology switch
    {
        "qr" => QRCodeImageDecoder.TryDecode(bitmap, out var text) && text == Content,
        "microqr" => MicroQRCodeImageDecoder.TryDecode(bitmap, out var text, out _) && text == Content,
        "rmqr" => RmQRCodeImageDecoder.TryDecode(bitmap, out var text, out _) && text == Content,
        _ => throw new ArgumentOutOfRangeException(nameof(symbology)),
    };

    /// <summary>A canvas with a margin around <paramref name="area"/>, cleared to <paramref name="clear"/> before drawing.</summary>
    private static SKBitmap Canvas(SKRect area, SKColor clear, Action<SKCanvas> draw)
    {
        var bitmap = new SKBitmap(new SKImageInfo((int)area.Right + 20, (int)area.Bottom + 30, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(clear);
        draw(canvas);
        return bitmap;
    }

    private static bool IsDark(SKBitmap bitmap, float x, float y) => bitmap.GetPixel((int)x, (int)y).Red < 128;

    /// <summary>An area whose fitted symbol has no size: zero on one axis, or a sliver a float step wide.</summary>
    private static SKRect EmptyArea(string kind) => kind switch
    {
        "zeroWidth" => SKRect.Create(50, 50, 0, 40),
        "zeroHeight" => SKRect.Create(50, 50, 40, 0),
        // One float step wide. At x or y 1000 a step is coarser than that, so the centred square's
        // other side rounds to 0. Both axes, because a guard that tests one of them passes the other.
        "sliver" => new SKRect(50f, 1000f, MathF.BitIncrement(50f), 1001f),
        _ => new SKRect(1000f, 50f, 1001f, MathF.BitIncrement(50f)),
    };

    /// <summary>Moves the canvas so an area placed at 1000, where a float step is coarse, lands on the bitmap.</summary>
    private static void TranslateToBitmap(SKCanvas canvas, string areaKind)
    {
        if (areaKind == "sliver") canvas.Translate(0, -950);
        if (areaKind == "tallSliver") canvas.Translate(-950, 0);
    }

    private static int ChangedPixels(SKBitmap bitmap, SKColor expected)
    {
        var changed = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y) != expected) changed++;
            }
        }
        return changed;
    }

    /// <summary>Draws with something whose size is in pixels rather than in cells, which a symbol with no size must still not draw.</summary>
    private static void DrawWithOverlay(SKCanvas canvas, string symbology, string overlay, SKRect area, SKBitmap logo, SKFont font)
    {
        var data = QRCodeGenerator.Create(Content, QREccLevel.H, new QRCodeGeneratorOptions { QuietZoneSize = 4 });
        ModuleShape? shape = overlay == "fixedDots" ? FixedDotModuleShape.Default : null;
        var background = new SKColor(0xFF, 0xFF, 0xFF, 0x80);
        IconData? icon = overlay switch
        {
            "percentIcon" => IconData.FromImage(logo, iconSizePercent: 15, iconBorderWidth: 2),
            "moduleIcon" => IconData.FromImageByModules(logo, iconSizeModules: 7, iconBorderModules: 1, maxCoreOccupancyPercent: 40),
            "captionIcon" => new IconData { Icon = new ImageTextIconShape(logo, "Caption", SKColors.Black, font), IconSizePercent = 15, IconBorderWidth = 2 },
            _ => null,
        };

        switch (symbology)
        {
            case "qr":
                SymbolRenderer.Render(canvas, area, data, SKColors.Black, background, icon, shape, 1f, null, CircleFinderPatternShape.Default);
                break;
            case "microqr":
                SymbolRenderer.Render(canvas, area, MicroQr(), SKColors.Black, background, shape, 1f, null, CircleFinderPatternShape.Default);
                break;
            default:
                SymbolRenderer.Render(canvas, area, RmQr(), SKColors.Black, background, shape, 1f, null, CircleFinderPatternShape.Default);
                break;
        }
    }

    /// <summary>A caller-written shape that ignores the cell it is given, the way a shape drawing a fixed-size marker does.</summary>
    private sealed class FixedDotModuleShape : ModuleShape
    {
        public static readonly FixedDotModuleShape Default = new();

        public override bool RequiresAntialiasing => false;

        public override void Draw(SKCanvas canvas, SKRect rect, SKPaint paint)
            => canvas.DrawRect(SKRect.Create(rect.MidX - 2, rect.MidY - 2, 4, 4), paint);
    }

    private static void RenderThroughExtension(SKCanvas canvas, string symbology, SKRect area, bool nullData, float moduleSizePercent, GradientOptions? gradient)
    {
        switch (symbology)
        {
            case "qr": canvas.Render(nullData ? null! : StandardQr(), area, SKColors.Red, SKColors.Black, SKColors.White, null, null, moduleSizePercent, gradient); break;
            case "microqr": canvas.Render(nullData ? null! : MicroQr(), area, SKColors.Red, SKColors.Black, SKColors.White, null, moduleSizePercent, gradient); break;
            default: canvas.Render(nullData ? null! : RmQr(), area, SKColors.Red, SKColors.Black, SKColors.White, null, moduleSizePercent, gradient); break;
        }
    }

    private static void RenderThroughRenderer(SKCanvas canvas, string symbology, SKRect area, bool nullData, float moduleSizePercent, GradientOptions? gradient)
    {
        switch (symbology)
        {
            case "qr": SymbolRenderer.Render(canvas, area, nullData ? null! : StandardQr(), SKColors.Black, SKColors.White, null, null, moduleSizePercent, gradient); break;
            case "microqr": SymbolRenderer.Render(canvas, area, nullData ? null! : MicroQr(), SKColors.Black, SKColors.White, null, moduleSizePercent, gradient); break;
            default: SymbolRenderer.Render(canvas, area, nullData ? null! : RmQr(), SKColors.Black, SKColors.White, null, moduleSizePercent, gradient); break;
        }
    }

    private static void RenderThroughRenderer(SKCanvas canvas, string symbology, SKRect area, SKColor background, ModuleShape shape)
    {
        switch (symbology)
        {
            case "qr": SymbolRenderer.Render(canvas, area, StandardQr(), SKColors.Black, background, null, shape); break;
            case "microqr": SymbolRenderer.Render(canvas, area, MicroQr(), SKColors.Black, background, shape); break;
            default: SymbolRenderer.Render(canvas, area, RmQr(), SKColors.Black, background, shape); break;
        }
    }

    private static void RenderThroughSizeExtension(SKCanvas canvas, string symbology, int width, int height, bool nullData)
    {
        switch (symbology)
        {
            case "qr": canvas.Render(nullData ? null! : StandardQr(), width, height); break;
            case "microqr": canvas.Render(nullData ? null! : MicroQr(), width, height); break;
            default: canvas.Render(nullData ? null! : RmQr(), width, height); break;
        }
    }

    private static void RenderThroughExtension(SKCanvas canvas, string symbology, SKRect area, SKColor clearColor, ModuleShape shape)
    {
        switch (symbology)
        {
            case "qr": canvas.Render(StandardQr(), area, clearColor, SKColors.Black, SKColors.White, null, shape); break;
            case "microqr": canvas.Render(MicroQr(), area, clearColor, SKColors.Black, SKColors.White, shape); break;
            default: canvas.Render(RmQr(), area, clearColor, SKColors.Black, SKColors.White, shape); break;
        }
    }

    private static void RenderThroughExtension(SKCanvas canvas, string symbology, int width, int height, ModuleShape shape)
    {
        switch (symbology)
        {
            case "qr": canvas.Render(StandardQr(), width, height, null, SKColors.Black, SKColors.White, null, shape); break;
            case "microqr": canvas.Render(MicroQr(), width, height, null, SKColors.Black, SKColors.White, shape); break;
            default: canvas.Render(RmQr(), width, height, null, SKColors.Black, SKColors.White, shape); break;
        }
    }

    private static void RenderThroughExtension(SKCanvas canvas, string symbology, SKRect area)
    {
        switch (symbology)
        {
            case "qr": canvas.Render(StandardQr(), area, SKColors.Red); break;
            case "microqr": canvas.Render(MicroQr(), area, SKColors.Red); break;
            default: canvas.Render(RmQr(), area, SKColors.Red); break;
        }
    }

    private static void RenderThroughExtension(SKCanvas canvas, string symbology, int width, int height)
    {
        switch (symbology)
        {
            case "qr": canvas.Render(StandardQr(), width, height); break;
            case "microqr": canvas.Render(MicroQr(), width, height); break;
            default: canvas.Render(RmQr(), width, height); break;
        }
    }

    private static SKRect Invert(SKRect bounds, string axis) => axis switch
    {
        "width" => new SKRect(bounds.Right, bounds.Top, bounds.Left, bounds.Bottom),
        "height" => new SKRect(bounds.Left, bounds.Bottom, bounds.Right, bounds.Top),
        _ => new SKRect(bounds.Right, bounds.Bottom, bounds.Left, bounds.Top),
    };

    /// <summary>A 600x400 canvas cleared to red, with the symbol drawn into <paramref name="area"/>.</summary>
    private static SKBitmap DrawInBounds(string symbology, SKRect area)
    {
        var bitmap = new SKBitmap(new SKImageInfo(600, 400, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Red);
        Draw(canvas, symbology, "renderer", area, SKColors.White);
        return bitmap;
    }

    private static int DifferingBytes(byte[] actual, byte[] expected)
    {
        if (actual.Length != expected.Length)
            return Math.Max(actual.Length, expected.Length);

        var differing = 0;
        for (var i = 0; i < actual.Length; i++)
        {
            if (actual[i] != expected[i]) differing++;
        }
        return differing;
    }

    private static SKRectI DarkBoundingBox(SKBitmap bitmap)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Red >= 128) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        return new SKRectI(minX, minY, maxX + 1, maxY + 1);
    }
}
