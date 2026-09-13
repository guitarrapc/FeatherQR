using SkiaSharp;
using FeatherQR.SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// The <c>SKCanvas.Render(data, width, height, …)</c> overloads hand every option to the area overload unchanged.
/// </summary>
/// <remarks>
/// <para>
/// A size overload builds <c>SKRect.Create(0, 0, width, height)</c> and forwards; there is no other logic in it,
/// which is exactly why nothing tested the forwarding. Mutation testing found that dropping
/// <c>finderPatternShape</c>, forcing <c>moduleSizePercent</c> to 1.0f, hardcoding <c>clearColor</c>, or dropping
/// Standard QR's <c>iconData</c> all passed the whole suite: the colours were pinned by
/// <c>SymbolRendererColorParameterTest</c> and the geometry by <c>SymbolRendererAreaFitTest</c>, and every other
/// caller of a size overload passed the tail at its default. A caller could have got
/// <c>canvas.Render(data, 512, 512, iconData: icon)</c> with no icon drawn.
/// </para>
/// <para>
/// Each case renders one option twice, through the size overload and through the area overload over the rectangle
/// the size overload would have built, and requires the two to be byte-identical. A dropped or altered argument
/// shows up as a difference. The control against the same render with no options set is what stops the comparison
/// going vacuous if the option ever stops reaching the pixels.
/// </para>
/// <para>
/// The canvas is deliberately larger than the requested size. It is the only way the clear colour is observable:
/// the size overload asks for a rectangle at the origin and the background covers exactly that, so a canvas no
/// larger leaves no cleared pixel to see.
/// </para>
/// </remarks>
public class SymbolRendererSizeOverloadForwardingTest
{
    private const string Content = "size-overload-forwarding";
    private const int Requested = 200;
    private const int CanvasSide = 260;
    private static readonly SKRect RequestedArea = SKRect.Create(0, 0, Requested, Requested);

    [Test]
    [Arguments("qr", "clearColor")]
    [Arguments("qr", "moduleShape")]
    [Arguments("qr", "moduleSizePercent")]
    [Arguments("qr", "gradient")]
    [Arguments("qr", "finderShape")]
    [Arguments("qr", "icon")]
    [Arguments("microqr", "clearColor")]
    [Arguments("microqr", "moduleShape")]
    [Arguments("microqr", "moduleSizePercent")]
    [Arguments("microqr", "gradient")]
    [Arguments("microqr", "finderShape")]
    [Arguments("rmqr", "clearColor")]
    [Arguments("rmqr", "moduleShape")]
    [Arguments("rmqr", "moduleSizePercent")]
    [Arguments("rmqr", "gradient")]
    [Arguments("rmqr", "finderShape")]
    public async Task SizeOverload_ForwardsTheOption(string symbology, string option)
    {
        using var logo = CreateLogo(32);
        var set = Options(option, logo);
        var unset = Options("none", logo);

        using var throughSize = Canvas(c => DrawSize(c, symbology, set));
        using var throughArea = Canvas(c => DrawArea(c, symbology, set));
        using var withoutIt = Canvas(c => DrawSize(c, symbology, unset));

        await Assert.That(DifferingBytes(throughSize.Bytes, throughArea.Bytes)).IsEqualTo(0)
            .Because($"{symbology}/{option}: the size overload hands it to the area overload unchanged");
        await Assert.That(DifferingBytes(throughSize.Bytes, withoutIt.Bytes)).IsNotEqualTo(0)
            .Because($"{symbology}/{option}: the option reaches the pixels, or the comparison above proves nothing");
    }

    /// <summary>
    /// An icon's border is painted in the background colour, and the renderer's icon call is the one place that
    /// colour is read outside the background fill itself.
    /// </summary>
    /// <remarks>
    /// Every other icon test in the suite draws on white, so substituting <see cref="SKColors.White"/> for the
    /// caller's background in that call changed nothing anywhere. This samples the border ring rather than the
    /// image or the field around it, which is the only region the substitution would have repainted.
    /// </remarks>
    [Test]
    [Arguments(0xFF, 0xD7, 0x00)]
    [Arguments(0x00, 0x80, 0x80)]
    public async Task Render_Icon_PaintsItsBorderInTheBackgroundColor(byte r, byte g, byte b)
    {
        var background = new SKColor(r, g, b);
        var qr = StandardQr();
        using var logo = CreateLogo(32);
        var icon = IconData.FromImage(logo);
        var (iconRect, borderRect) = SymbolRenderer.GetIconRects(qr, RequestedArea, icon);

        // Midway across the border ring on the left side, which the image never covers.
        var x = (int)((borderRect.Left + iconRect.Left) / 2);
        var y = (int)borderRect.MidY;

        using var painted = Canvas(c => SymbolRenderer.Render(c, RequestedArea, qr, SKColors.Black, background, icon));
        using var onWhite = Canvas(c => SymbolRenderer.Render(c, RequestedArea, qr, SKColors.Black, SKColors.White, icon));

        await Assert.That(painted.GetPixel(x, y)).IsEqualTo(background)
            .Because("the icon border takes the caller's background colour");
        await Assert.That(onWhite.GetPixel(x, y)).IsEqualTo(SKColors.White)
            .Because("the same ring on a white background, so the sample point is the ring and not the image");
    }

    private sealed record Option(SKColor? Clear, IconData? Icon, ModuleShape? Shape, float Percent, GradientOptions? Gradient, FinderPatternShape? Finder);

    private static Option Options(string option, SKBitmap logo) => option switch
    {
        "none" => new Option(null, null, null, 1.0f, null, null),
        "clearColor" => new Option(SKColors.Red, null, null, 1.0f, null, null),
        "icon" => new Option(null, IconData.FromImage(logo), null, 1.0f, null, null),
        "moduleShape" => new Option(null, null, CircleModuleShape.Default, 1.0f, null, null),
        "moduleSizePercent" => new Option(null, null, null, 0.8f, null, null),
        "gradient" => new Option(null, null, null, 1.0f, new GradientOptions([SKColors.Blue, SKColors.Red], GradientDirection.LeftToRight), null),
        "finderShape" => new Option(null, null, null, 1.0f, null, CircleFinderPatternShape.Default),
        _ => throw new ArgumentOutOfRangeException(nameof(option)),
    };

    private static void DrawSize(SKCanvas canvas, string symbology, Option o)
    {
        switch (symbology)
        {
            case "qr": canvas.Render(StandardQr(), Requested, Requested, o.Clear, SKColors.Black, SKColors.White, o.Icon, o.Shape, o.Percent, o.Gradient, o.Finder); break;
            case "microqr": canvas.Render(MicroQr(), Requested, Requested, o.Clear, SKColors.Black, SKColors.White, o.Shape, o.Percent, o.Gradient, o.Finder); break;
            case "rmqr": canvas.Render(RmQr(), Requested, Requested, o.Clear, SKColors.Black, SKColors.White, o.Shape, o.Percent, o.Gradient, o.Finder); break;
            default: throw new ArgumentOutOfRangeException(nameof(symbology));
        }
    }

    private static void DrawArea(SKCanvas canvas, string symbology, Option o)
    {
        switch (symbology)
        {
            case "qr": canvas.Render(StandardQr(), RequestedArea, o.Clear, SKColors.Black, SKColors.White, o.Icon, o.Shape, o.Percent, o.Gradient, o.Finder); break;
            case "microqr": canvas.Render(MicroQr(), RequestedArea, o.Clear, SKColors.Black, SKColors.White, o.Shape, o.Percent, o.Gradient, o.Finder); break;
            case "rmqr": canvas.Render(RmQr(), RequestedArea, o.Clear, SKColors.Black, SKColors.White, o.Shape, o.Percent, o.Gradient, o.Finder); break;
            default: throw new ArgumentOutOfRangeException(nameof(symbology));
        }
    }

    private static QRCodeData StandardQr() => QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 4 });
    private static MicroQRCodeData MicroQr() => MicroQRCodeGenerator.Create("micro", MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { QuietZoneSize = 2 });
    private static RmQRCodeData RmQr() => RmQRCodeGenerator.Create(Content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R11x59, QuietZoneSize = 2 });

    /// <summary>A canvas larger than <see cref="Requested"/> on both axes, so the cleared band beside the symbol survives the render.</summary>
    private static SKBitmap Canvas(Action<SKCanvas> draw)
    {
        var bitmap = new SKBitmap(new SKImageInfo(CanvasSide, CanvasSide, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        draw(canvas);
        return bitmap;
    }

    private static SKBitmap CreateLogo(int size)
    {
        var bitmap = new SKBitmap(size, size);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Magenta);
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
}
