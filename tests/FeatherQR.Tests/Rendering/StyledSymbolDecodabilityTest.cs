using SkiaSharp;
using FeatherQR.SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// Styling a symbol must never cost it its scannability, on any symbology.
/// </summary>
/// <remarks>
/// <para>
/// A module shape and a module size below 100% put gaps between modules. That is harmless for
/// data modules, which every decoder reads by sampling the centre of the cell, and fatal for the
/// finder pattern, which is located by scanning for the 1:1:3:1:1 run of dark and light along a
/// line. Gaps split the three-module dark centre into three separate runs, the ratio no longer
/// occurs anywhere in the image, and the symbol stops being found at all.
/// </para>
/// <para>
/// So the renderer draws the finder as a whole pattern, never as styled modules. These cases are
/// the guard: they fail with <c>NotDetected</c> if the module shape or size ever reaches the
/// finder again. The bound they pin is deliberately blunt, "it decodes", because the failure this
/// prevents is not a loss of accuracy but a total loss of detection.
/// </para>
/// </remarks>
public class StyledSymbolDecodabilityTest
{
    public enum Style
    {
        Rectangle,
        Circle,
        RoundedRectangle,
    }

    private static ModuleShape ShapeOf(Style style) => style switch
    {
        Style.Circle => CircleModuleShape.Default,
        Style.RoundedRectangle => RoundedRectangleModuleShape.Default,
        _ => RectangleModuleShape.Default,
    };

    // 98% is here because that is where the defect first showed: a gap of one fiftieth of a module
    // is invisible to a reader and still enough to break the run the detector looks for.
    [Test]
    [Arguments(Style.Rectangle, 100)]
    [Arguments(Style.Rectangle, 98)]
    [Arguments(Style.Rectangle, 92)]
    [Arguments(Style.Rectangle, 75)]
    [Arguments(Style.Circle, 100)]
    [Arguments(Style.Circle, 92)]
    [Arguments(Style.RoundedRectangle, 92)]
    public async Task StandardQR_Styled_StillDecodes(Style style, int sizePercent)
    {
        using var bitmap = new QRCodeImageBuilder("https://github.com/guitarrapc/FeatherQR")
            .WithSize(900, 900)
            .WithErrorCorrection(QREccLevel.M)
            .WithQuietZone(4)
            .WithColors(SKColors.Black, SKColors.White)
            .WithModuleShape(ShapeOf(style), sizePercent / 100f)
            .ToBitmap();

        await Assert.That(QRCodeImageDecoder.TryDecode(bitmap, out var text, out var info)).IsTrue()
            .Because($"status={info.Status}");
        await Assert.That(text).IsEqualTo("https://github.com/guitarrapc/FeatherQR");
    }

    [Test]
    [Arguments(Style.Rectangle, 100)]
    [Arguments(Style.Rectangle, 98)]
    [Arguments(Style.Rectangle, 92)]
    [Arguments(Style.Rectangle, 75)]
    [Arguments(Style.Circle, 100)]
    [Arguments(Style.Circle, 92)]
    [Arguments(Style.RoundedRectangle, 92)]
    public async Task MicroQR_Styled_StillDecodes(Style style, int sizePercent)
    {
        using var bitmap = new MicroQRCodeImageBuilder("https://githu")
            .WithSize(504, 504)
            .WithErrorCorrection(MicroQREccLevel.M)
            .WithQuietZone(2)
            .WithColors(SKColors.Black, SKColors.White)
            .WithModuleShape(ShapeOf(style), sizePercent / 100f)
            .ToBitmap();

        await Assert.That(MicroQRCodeImageDecoder.TryDecode(bitmap, out var text, out var info)).IsTrue()
            .Because($"status={info.Status}");
        await Assert.That(text).IsEqualTo("https://githu");
    }

    [Test]
    [Arguments(Style.Rectangle, 100)]
    [Arguments(Style.Rectangle, 98)]
    [Arguments(Style.Rectangle, 92)]
    [Arguments(Style.Rectangle, 75)]
    [Arguments(Style.Circle, 100)]
    [Arguments(Style.Circle, 92)]
    [Arguments(Style.RoundedRectangle, 92)]
    public async Task RmQR_Styled_StillDecodes(Style style, int sizePercent)
    {
        using var bitmap = new RmQRCodeImageBuilder("https://githu")
            .WithSize(630, 160)
            .WithErrorCorrection(RmQREccLevel.M)
            .WithVersion(RmQRVersion.R11x59)
            .WithQuietZone(2)
            .WithColors(SKColors.Black, SKColors.White)
            .WithModuleShape(ShapeOf(style), sizePercent / 100f)
            .ToBitmap();

        await Assert.That(RmQRCodeImageDecoder.TryDecode(bitmap, out var text, out var info)).IsTrue()
            .Because($"status={info.Status}");
        await Assert.That(text).IsEqualTo("https://githu");
    }

    /// <summary>
    /// A decorative finder keeps the concentric rings, only rounding their corners, so it stays
    /// detectable: what the detector needs is that the rings are continuous, not that they are
    /// square. This is the case for keeping the styling API open rather than pinning the finder
    /// to one appearance.
    /// </summary>
    [Test]
    [Arguments("rectangle")]
    [Arguments("circle")]
    [Arguments("rounded")]
    [Arguments("roundedCircle")]
    public async Task StandardQR_DecorativeFinder_StillDecodes(string finder)
    {
        FinderPatternShape shape = finder switch
        {
            "circle" => CircleFinderPatternShape.Default,
            "rounded" => RoundedRectangleFinderPatternShape.Default,
            "roundedCircle" => RoundedRectangleCircleFinderPatternShape.Default,
            _ => RectangleFinderPatternShape.Default,
        };

        using var bitmap = new QRCodeImageBuilder("https://github.com/guitarrapc/FeatherQR")
            .WithSize(900, 900)
            .WithErrorCorrection(QREccLevel.M)
            .WithQuietZone(4)
            .WithColors(SKColors.Black, SKColors.White)
            .WithModuleShape(CircleModuleShape.Default, 0.85f)
            .WithFinderPatternShape(shape)
            .ToBitmap();

        await Assert.That(QRCodeImageDecoder.TryDecode(bitmap, out _, out var info)).IsTrue()
            .Because($"status={info.Status}");
    }

    /// <summary>
    /// Alignment patterns are located from a predicted position by their single dark centre
    /// module, not by a run ratio, so shrinking them is harmless. Pinned across the versions that
    /// have one at all (version 1 has none) up to the largest, because a future change that
    /// extended the finder rule to alignment patterns would cost draw calls for nothing.
    /// </summary>
    [Test]
    [Arguments(2, "https://githu")]
    [Arguments(7, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [Arguments(25, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task StandardQR_AlignmentPatterns_TolerateShrunkModules(int version, string content)
    {
        using var bitmap = new QRCodeImageBuilder(content)
            .WithSize(1200, 1200)
            .WithErrorCorrection(QREccLevel.M)
            .WithVersion(version)
            .WithQuietZone(4)
            .WithColors(SKColors.Black, SKColors.White)
            .WithModuleShape(RectangleModuleShape.Default, 0.75f)
            .ToBitmap();

        await Assert.That(QRCodeImageDecoder.TryDecode(bitmap, out var text, out var info)).IsTrue()
            .Because($"status={info.Status}");
        await Assert.That(text).IsEqualTo(content);
    }
}
