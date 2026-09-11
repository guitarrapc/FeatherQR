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
        foreach (var (x, y) in new[] { (5, 5), (bitmap.Width - 5, bitmap.Height - 5) })
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

        await Assert.That(differing).IsEqualTo(0).Because($"{symbology} {style} in {width}x{height}");
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

    /// <summary>
    /// An inverted area is a caller's mistake, but it must not reach past its own bounds onto the rest
    /// of the canvas, which the area taken literally never did.
    /// </summary>
    [Test]
    [Arguments("qr", "width")]
    [Arguments("qr", "height")]
    [Arguments("qr", "both")]
    [Arguments("microqr", "width")]
    [Arguments("microqr", "height")]
    [Arguments("microqr", "both")]
    public async Task Render_InvertedArea_StaysInsideItsBounds(string symbology, string inverted)
    {
        var bounds = SKRect.Create(100, 100, 400, 200);
        using var bitmap = DrawInBounds(symbology, Invert(bounds, inverted));

        var outside = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (!bounds.Contains(x + 0.5f, y + 0.5f) && bitmap.GetPixel(x, y) != SKColors.Red) outside++;
            }
        }

        await Assert.That(outside).IsEqualTo(0).Because($"{symbology} with an inverted {inverted}");
    }

    /// <summary>
    /// An inverted area keeps the orientation it asks for: the render is the upright one mirrored
    /// along each inverted axis, as it was when the area was taken literally.
    /// </summary>
    [Test]
    [Arguments("qr", "width")]
    [Arguments("qr", "height")]
    [Arguments("qr", "both")]
    [Arguments("microqr", "width")]
    [Arguments("microqr", "height")]
    [Arguments("microqr", "both")]
    public async Task Render_InvertedArea_MirrorsTheUprightRender(string symbology, string inverted)
    {
        var bounds = SKRect.Create(100, 100, 400, 200);
        using var upright = DrawInBounds(symbology, bounds);
        using var mirrored = DrawInBounds(symbology, Invert(bounds, inverted));

        var flipX = inverted is "width" or "both";
        var flipY = inverted is "height" or "both";
        var differing = 0;
        for (var y = (int)bounds.Top; y < (int)bounds.Bottom; y++)
        {
            for (var x = (int)bounds.Left; x < (int)bounds.Right; x++)
            {
                var sourceX = flipX ? (int)(bounds.Left + bounds.Right) - 1 - x : x;
                var sourceY = flipY ? (int)(bounds.Top + bounds.Bottom) - 1 - y : y;
                if (mirrored.GetPixel(x, y) != upright.GetPixel(sourceX, sourceY)) differing++;
            }
        }

        await Assert.That(differing).IsEqualTo(0).Because($"{symbology} with an inverted {inverted}");
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
    /// </summary>
    [Test]
    [Arguments(1000.25f, 1000.5f, 400f, 400.01f)]
    [Arguments(1000f, 1000f, 400f, 401f)]
    [Arguments(700f, 200f, 300f, 450f)]
    public async Task GetSquareArea_MoreThanRoundingFromSquare_IsFitted(float left, float top, float width, float height)
    {
        var area = SKRect.Create(left, top, width, height);
        var fitted = SymbolRenderer.GetSquareArea(area);

        await Assert.That(fitted.Width).IsEqualTo(fitted.Height).Within(1e-3f).Because($"{area} became {fitted}");
        await Assert.That(fitted.Height).IsEqualTo(Math.Min(area.Width, area.Height)).Within(1e-3f).Because($"{area} became {fitted}");
        await Assert.That(fitted == area).IsFalse().Because($"{area} must not come back as given");
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
            default: throw new ArgumentOutOfRangeException(nameof(entry), $"{symbology}/{entry}");
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
