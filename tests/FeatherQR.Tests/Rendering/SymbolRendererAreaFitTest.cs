using SkiaSharp;
using FeatherQR.SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// The low-level renderer fits the symbol into the area it is given, the rule the image builders follow.
/// </summary>
/// <remarks>
/// <para>
/// Standard QR and Micro QR used to fill the area on both axes, so a non-square area gave the symbol non-square modules and nothing could find it, while rMQR's overload and every builder fitted. The same width and height gave a readable symbol from one entry point and an unreadable one from another.
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
