using SkiaSharp;
using FeatherQR.SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// <see cref="SymbolRenderer"/>'s <c>Render</c> takes its two colours as values, so the defaults live in the
/// <see cref="SKCanvasExtensions"/> that can omit them.
/// </summary>
/// <remarks>
/// The colours are the one place where the rule "<see langword="null"/> means the option is absent, never the default value"
/// used to be broken on the static surface: two required parameters typed <c>SKColor?</c>, where <see langword="null"/> meant black and white.
/// The extensions keep their optional <c>SKColor?</c> parameters, because a C# optional parameter needs a constant default and
/// <c>default(SKColor)</c> is transparent, and resolve them before forwarding. These tests pin that resolution, and the one thing
/// a caller who now has to write a code colour needs to know about it: a gradient replaces it, the way a shader replaces <c>SKPaint.Color</c>.
/// </remarks>
public class SymbolRendererColorParameterTest
{
    private const string Content = "renderer-colour-parameters";
    private static readonly SKRect Area = SKRect.Create(10, 10, 180, 180);

    /// <summary>
    /// Omitting both colours on the extension draws exactly what naming black on white on the renderer draws.
    /// A swapped or wrong default in the forward shows up here as differing bytes.
    /// </summary>
    [Test]
    [Arguments("qr")]
    [Arguments("microqr")]
    [Arguments("rmqr")]
    public async Task CanvasExtension_OmittedColors_DrawBlackOnWhite(string symbology)
    {
        using var omitted = Canvas(c => RenderThroughExtension(c, symbology, null, null));
        using var named = Canvas(c => RenderThroughRenderer(c, symbology, SKColors.Black, SKColors.White));

        await Assert.That(DifferingBytes(omitted.Bytes, named.Bytes)).IsEqualTo(0)
            .Because($"{symbology}: the extension's omitted colours are black on white");
    }

    /// <summary>
    /// A colour named on the extension reaches the renderer unchanged. The control against black on white
    /// proves the forward passes the argument rather than the default.
    /// </summary>
    [Test]
    [Arguments("qr")]
    [Arguments("microqr")]
    [Arguments("rmqr")]
    public async Task CanvasExtension_NamedColors_ReachTheRenderer(string symbology)
    {
        using var throughExtension = Canvas(c => RenderThroughExtension(c, symbology, SKColors.Navy, SKColors.Yellow));
        using var throughRenderer = Canvas(c => RenderThroughRenderer(c, symbology, SKColors.Navy, SKColors.Yellow));
        using var defaults = Canvas(c => RenderThroughRenderer(c, symbology, SKColors.Black, SKColors.White));

        await Assert.That(DifferingBytes(throughExtension.Bytes, throughRenderer.Bytes)).IsEqualTo(0)
            .Because($"{symbology}: named colours are forwarded as given");
        await Assert.That(DifferingBytes(throughExtension.Bytes, defaults.Bytes)).IsNotEqualTo(0)
            .Because($"{symbology}: navy on yellow is not black on white, or the control proves nothing");
    }

    /// <summary>
    /// With a gradient the code colour is not used: two renders that differ only in it are byte-identical.
    /// The control against the solid render proves the gradient was drawn at all.
    /// </summary>
    [Test]
    [Arguments("qr")]
    [Arguments("microqr")]
    [Arguments("rmqr")]
    public async Task Render_Gradient_ReplacesTheCodeColor(string symbology)
    {
        var gradient = new GradientOptions([SKColors.Blue, SKColors.Red], GradientDirection.LeftToRight);

        using var black = Canvas(c => RenderThroughRenderer(c, symbology, SKColors.Black, SKColors.White, gradient));
        using var red = Canvas(c => RenderThroughRenderer(c, symbology, SKColors.Red, SKColors.White, gradient));
        using var solid = Canvas(c => RenderThroughRenderer(c, symbology, SKColors.Black, SKColors.White));

        await Assert.That(DifferingBytes(black.Bytes, red.Bytes)).IsEqualTo(0)
            .Because($"{symbology}: under a gradient the code colour changes nothing");
        await Assert.That(DifferingBytes(black.Bytes, solid.Bytes)).IsNotEqualTo(0)
            .Because($"{symbology}: the gradient was drawn, or the identity above is vacuous");
    }

    /// <summary>
    /// A gradient whose direction is <see cref="GradientDirection.None"/> paints the modules in the code colour,
    /// the same pixels as no gradient at all; the control shows the colour is the one named, not a default.
    /// </summary>
    [Test]
    [Arguments("qr")]
    [Arguments("microqr")]
    [Arguments("rmqr")]
    public async Task Render_GradientDirectionNone_PaintsTheCodeColor(string symbology)
    {
        var none = new GradientOptions([SKColors.Blue, SKColors.Red], GradientDirection.None);

        using var withNone = Canvas(c => RenderThroughRenderer(c, symbology, SKColors.DarkGreen, SKColors.White, none));
        using var solid = Canvas(c => RenderThroughRenderer(c, symbology, SKColors.DarkGreen, SKColors.White));
        using var otherColor = Canvas(c => RenderThroughRenderer(c, symbology, SKColors.Black, SKColors.White, none));

        await Assert.That(DifferingBytes(withNone.Bytes, solid.Bytes)).IsEqualTo(0)
            .Because($"{symbology}: a None gradient draws what no gradient draws");
        await Assert.That(DifferingBytes(withNone.Bytes, otherColor.Bytes)).IsNotEqualTo(0)
            .Because($"{symbology}: the solid path paints the named code colour");
    }

    private static QRCodeData StandardQr() => QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 4 });
    private static MicroQRCodeData MicroQr() => MicroQRCodeGenerator.Create("micro", MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { QuietZoneSize = 2 });
    private static RmQRCodeData RmQr() => RmQRCodeGenerator.Create(Content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R11x59, QuietZoneSize = 2 });

    private static void RenderThroughRenderer(SKCanvas canvas, string symbology, SKColor codeColor, SKColor backgroundColor, GradientOptions? gradient = null)
    {
        switch (symbology)
        {
            case "qr": SymbolRenderer.Render(canvas, Area, StandardQr(), codeColor, backgroundColor, gradientOptions: gradient); break;
            case "microqr": SymbolRenderer.Render(canvas, Area, MicroQr(), codeColor, backgroundColor, gradientOptions: gradient); break;
            case "rmqr": SymbolRenderer.Render(canvas, Area, RmQr(), codeColor, backgroundColor, gradientOptions: gradient); break;
            default: throw new ArgumentOutOfRangeException(nameof(symbology));
        }
    }

    /// <summary>The area overloads; the size overloads delegate to them, so the forward is tested once.</summary>
    private static void RenderThroughExtension(SKCanvas canvas, string symbology, SKColor? codeColor, SKColor? backgroundColor)
    {
        switch (symbology)
        {
            case "qr": canvas.Render(StandardQr(), Area, codeColor: codeColor, backgroundColor: backgroundColor); break;
            case "microqr": canvas.Render(MicroQr(), Area, codeColor: codeColor, backgroundColor: backgroundColor); break;
            case "rmqr": canvas.Render(RmQr(), Area, codeColor: codeColor, backgroundColor: backgroundColor); break;
            default: throw new ArgumentOutOfRangeException(nameof(symbology));
        }
    }

    /// <summary>A transparent canvas around <see cref="Area"/>: the extensions clear to transparent, so both paths start from the same pixels.</summary>
    private static SKBitmap Canvas(Action<SKCanvas> draw)
    {
        var bitmap = new SKBitmap(new SKImageInfo((int)Area.Right + 20, (int)Area.Bottom + 20, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        draw(canvas);
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
