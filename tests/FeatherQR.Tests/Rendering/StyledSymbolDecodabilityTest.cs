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
    /// A decorative finder reshapes the concentric rings without breaking them, so it stays
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
        var shape = FinderShapeOf(finder);

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
    /// A canvas that is not square gives the square symbologies non-square cells, and the finder
    /// pattern has to follow them. Sampling every module centre against the matrix is the direct
    /// statement of that: the finder is drawn after the modules and is not clipped, so a pattern
    /// built from one axis paints its light ring over whatever is next to it.
    /// </summary>
    [Test]
    [Arguments(900, 450)]
    [Arguments(450, 900)]
    [Arguments(900, 820)]
    [Arguments(600, 540)]
    [Arguments(512, 512)]
    public async Task StandardQR_StyledOnAnyAspect_RendersEveryModule(int width, int height)
    {
        var data = QRCodeGenerator.Create("HELLO FEATHERQR 12345", QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 4 });
        using var bitmap = new QRCodeImageBuilder(data)
            .WithSize(width, height)
            .WithColors(SKColors.Black, SKColors.White)
            .WithModuleShape(RectangleModuleShape.Default, 0.9f)
            .ToBitmap();

        await AssertModuleCentres(bitmap, data.Size, data.Size, (row, col) => data[row, col]);
    }

    [Test]
    [Arguments(504, 252)]
    [Arguments(252, 504)]
    [Arguments(504, 460)]
    [Arguments(504, 504)]
    public async Task MicroQR_StyledOnAnyAspect_RendersEveryModule(int width, int height)
    {
        var data = MicroQRCodeGenerator.Create("https://githu", MicroQREccLevel.M, new MicroQRCodeGeneratorOptions { QuietZoneSize = 2 });
        using var bitmap = new MicroQRCodeImageBuilder(data)
            .WithSize(width, height)
            .WithColors(SKColors.Black, SKColors.White)
            .WithModuleShape(RectangleModuleShape.Default, 0.9f)
            .ToBitmap();

        await AssertModuleCentres(bitmap, data.Size, data.Size, (row, col) => data[row, col]);
    }

    /// <summary>
    /// A decorative finder rounds or cuts its own corner modules, so their centres are fair game;
    /// what it must never do is reach outside its seven-by-seven box. That is the half of the
    /// non-square defect that corrupted data: the light ring, sized from the width alone, ran past
    /// the bottom edge of a wide finder and over the modules below it.
    /// </summary>
    [Test]
    [Arguments("rectangle")]
    [Arguments("circle")]
    [Arguments("rounded")]
    [Arguments("roundedCircle")]
    public async Task StandardQR_DecorativeFinderOnWideCanvas_StaysInsideItsBox(string finder)
    {
        var shape = FinderShapeOf(finder);

        const int quietZone = 4;
        var data = QRCodeGenerator.Create("HELLO FEATHERQR 12345", QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = quietZone });
        using var bitmap = new QRCodeImageBuilder(data)
            .WithSize(900, 450)
            .WithColors(SKColors.Black, SKColors.White)
            .WithModuleShape(RectangleModuleShape.Default, 0.9f)
            .WithFinderPatternShape(shape)
            .ToBitmap();

        var core = data.Size - quietZone * 2;
        await AssertModuleCentres(bitmap, data.Size, data.Size, (row, col) => data[row, col], (row, col) =>
        {
            var coreRow = row - quietZone;
            var coreCol = col - quietZone;
            if (coreRow < 0 || coreCol < 0 || coreRow >= core || coreCol >= core)
                return false;
            return (coreRow < 7 || coreRow >= core - 7) && coreCol < 7
                || coreRow < 7 && coreCol >= core - 7;
        });
    }

    /// <summary>
    /// The aspect ratios a symbol still decodes at must not depend on whether it is styled. At
    /// 900x820 the plain render decodes; before the finder was drawn per axis the styled one at
    /// the same size did not, which is the user-visible half of the same defect.
    /// </summary>
    [Test]
    [Arguments(900, 900)]
    [Arguments(900, 840)]
    [Arguments(900, 820)]
    public async Task StandardQR_StyledMildAspect_DecodesWhereverPlainDoes(int width, int height)
    {
        var data = QRCodeGenerator.Create("HELLO FEATHERQR 12345", QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 4 });

        using var plain = new QRCodeImageBuilder(data).WithSize(width, height).WithColors(SKColors.Black, SKColors.White).ToBitmap();
        if (!QRCodeImageDecoder.TryDecode(plain, out _, out _))
            return; // Outside the decoder's aspect envelope either way; nothing to compare.

        using var styled = new QRCodeImageBuilder(data)
            .WithSize(width, height)
            .WithColors(SKColors.Black, SKColors.White)
            .WithModuleShape(RectangleModuleShape.Default, 0.9f)
            .ToBitmap();

        await Assert.That(QRCodeImageDecoder.TryDecode(styled, out _, out var info)).IsTrue()
            .Because($"plain decodes at {width}x{height} but styled reported {info.Status}");
    }

    /// <summary>
    /// Every module centre in the render must carry the matrix's value for that module.
    /// <paramref name="skip"/> excludes modules a decorative shape is allowed to redraw.
    /// </summary>
    private static async Task AssertModuleCentres(SKBitmap bitmap, int matrixWidth, int matrixHeight, Func<int, int, bool> isDark, Func<int, int, bool>? skip = null)
    {
        var cellWidth = (float)bitmap.Width / matrixWidth;
        var cellHeight = (float)bitmap.Height / matrixHeight;

        for (var row = 0; row < matrixHeight; row++)
        {
            for (var col = 0; col < matrixWidth; col++)
            {
                if (skip is not null && skip(row, col))
                    continue;

                var x = (int)((col + 0.5f) * cellWidth);
                var y = (int)((row + 0.5f) * cellHeight);
                var rendered = bitmap.GetPixel(x, y).Red < 128;

                await Assert.That(rendered).IsEqualTo(isDark(row, col))
                    .Because($"module ({row}, {col}) at pixel ({x}, {y}) in a {bitmap.Width}x{bitmap.Height} render");
            }
        }
    }

    /// <summary>
    /// Micro QR and rMQR gained <c>WithFinderPatternShape</c> with this rule, and nothing else
    /// exercises it: a mutant that dropped the argument from both builders and reverted their
    /// crisp-edges hook passed the whole suite. These cases are what makes that revert fail.
    /// </summary>
    [Test]
    [Arguments("circle")]
    [Arguments("rounded")]
    [Arguments("roundedCircle")]
    public async Task MicroQR_DecorativeFinder_StillDecodes(string finder)
    {
        static byte[] Render(FinderPatternShape? shape)
        {
            var builder = new MicroQRCodeImageBuilder("https://githu")
                .WithSize(504, 504)
                .WithErrorCorrection(MicroQREccLevel.M)
                .WithQuietZone(2)
                .WithColors(SKColors.Black, SKColors.White)
                .WithModuleShape(CircleModuleShape.Default, 0.85f);
            return (shape is null ? builder : builder.WithFinderPatternShape(shape)).ToByteArray();
        }

        var styled = Render(FinderShapeOf(finder));
        using var bitmap = SKBitmap.Decode(styled);

        await Assert.That(MicroQRCodeImageDecoder.TryDecode(bitmap, out var text, out var info)).IsTrue()
            .Because($"status={info.Status}");
        await Assert.That(text).IsEqualTo("https://githu");
        // The shape has to reach the renderer, not just be accepted: dropping the builder's new
        // argument falls back to the plain square, which still decodes and would hide the revert.
        await Assert.That(styled).IsNotEquivalentTo(Render(null));
    }

    [Test]
    [Arguments("circle")]
    [Arguments("rounded")]
    [Arguments("roundedCircle")]
    public async Task RmQR_DecorativeFinder_StillDecodes(string finder)
    {
        static byte[] Render(FinderPatternShape? shape)
        {
            var builder = new RmQRCodeImageBuilder("https://githu")
                .WithSize(630, 160)
                .WithErrorCorrection(RmQREccLevel.M)
                .WithVersion(RmQRVersion.R11x59)
                .WithQuietZone(2)
                .WithColors(SKColors.Black, SKColors.White)
                .WithModuleShape(CircleModuleShape.Default, 0.85f);
            return (shape is null ? builder : builder.WithFinderPatternShape(shape)).ToByteArray();
        }

        var styled = Render(FinderShapeOf(finder));
        using var bitmap = SKBitmap.Decode(styled);

        await Assert.That(RmQRCodeImageDecoder.TryDecode(bitmap, out var text, out var info)).IsTrue()
            .Because($"status={info.Status}");
        await Assert.That(text).IsEqualTo("https://githu");
        await Assert.That(styled).IsNotEquivalentTo(Render(null));
    }

    /// <summary>
    /// A curved finder needs antialiasing, so the SVG must not carry <c>crispEdges</c> once one is
    /// set; a square one does not, so it must keep it. This is the other half of the same builder
    /// change: the crisp-edges hook on Micro QR and rMQR was a constant <c>true</c> before it.
    /// </summary>
    [Test]
    [Arguments("microqr")]
    [Arguments("rmqr")]
    public async Task DecorativeFinder_DropsCrispEdgesFromSvg(string symbology)
    {
        string Svg(FinderPatternShape? shape)
        {
            using var stream = new MemoryStream();
            if (symbology == "rmqr")
            {
                var rm = new RmQRCodeImageBuilder("https://githu").WithSize(630, 160).WithVersion(RmQRVersion.R11x59);
                (shape is null ? rm : rm.WithFinderPatternShape(shape)).SaveToSvg(stream);
            }
            else
            {
                var micro = new MicroQRCodeImageBuilder("https://githu").WithSize(504, 504);
                (shape is null ? micro : micro.WithFinderPatternShape(shape)).SaveToSvg(stream);
            }
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }

        await Assert.That(Svg(null)).Contains("crispEdges");
        await Assert.That(Svg(CircleFinderPatternShape.Default)).DoesNotContain("crispEdges");
        // A square finder draws no curve, so it must not cost the whole symbol its crisp edges.
        await Assert.That(Svg(RectangleFinderPatternShape.Default)).Contains("crispEdges");
    }

    /// <summary>
    /// The circle-based finders are drawn as ovals inscribed in the ring rects, not as circles
    /// sized from the shorter axis. On a square area the two are the same pixels, so only a
    /// non-square one can tell them apart: a circle sized from the short axis under-draws along
    /// the long one and the symbol stops decoding well before the grid-following oval does.
    /// </summary>
    [Test]
    [Arguments("rectangle")]
    [Arguments("circle")]
    [Arguments("rounded")]
    [Arguments("roundedCircle")]
    public async Task StandardQR_DecorativeFinderOnWideCanvas_StillDecodes(string finder)
    {
        var data = QRCodeGenerator.Create("HELLO FEATHERQR 12345", QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 4 });
        using var bitmap = new QRCodeImageBuilder(data)
            .WithSize(900, 540)
            .WithColors(SKColors.Black, SKColors.White)
            .WithModuleShape(RectangleModuleShape.Default, 0.9f)
            .WithFinderPatternShape(FinderShapeOf(finder))
            .ToBitmap();

        await Assert.That(QRCodeImageDecoder.TryDecode(bitmap, out _, out var info)).IsTrue()
            .Because($"status={info.Status}");
    }

    /// <summary>
    /// The canvas extensions are the third entry point into the same renderer, and they forward
    /// the shape by hand. Dropping that argument leaves the plain square, which still decodes, so
    /// the pin has to be that the pixels differ from the render without it.
    /// </summary>
    [Test]
    [Arguments("microqr")]
    [Arguments("rmqr")]
    public async Task CanvasExtension_ForwardsTheFinderShape(string symbology)
    {
        byte[] Render(FinderPatternShape? shape)
        {
            var (width, height) = symbology == "rmqr" ? (630, 160) : (504, 504);
            using var bitmap = new SKBitmap(width, height);
            using (var canvas = new SKCanvas(bitmap))
            {
                if (symbology == "rmqr")
                {
                    var data = RmQRCodeGenerator.Create("https://githu", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R11x59, QuietZoneSize = 2 });
                    canvas.Render(data, SKRect.Create(0, 0, width, height), SKColors.White, SKColors.Black, SKColors.White, CircleModuleShape.Default, 0.85f, null, shape);
                }
                else
                {
                    var data = MicroQRCodeGenerator.Create("https://githu", MicroQREccLevel.M, new MicroQRCodeGeneratorOptions { QuietZoneSize = 2 });
                    canvas.Render(data, SKRect.Create(0, 0, width, height), SKColors.White, SKColors.Black, SKColors.White, CircleModuleShape.Default, 0.85f, null, shape);
                }
            }
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            return encoded.ToArray();
        }

        var styled = Render(CircleFinderPatternShape.Default);
        await Assert.That(styled).IsNotEquivalentTo(Render(null));

        using var decoded = SKBitmap.Decode(styled);
        var ok = symbology == "rmqr"
            ? RmQRCodeImageDecoder.TryDecode(decoded, out _, out _)
            : MicroQRCodeImageDecoder.TryDecode(decoded, out _, out _);
        await Assert.That(ok).IsTrue();
    }

    private static FinderPatternShape FinderShapeOf(string finder) => finder switch
    {
        "circle" => CircleFinderPatternShape.Default,
        "rounded" => RoundedRectangleFinderPatternShape.Default,
        "roundedCircle" => RoundedRectangleCircleFinderPatternShape.Default,
        _ => RectangleFinderPatternShape.Default,
    };

    /// <summary>
    /// Alignment patterns are located from a predicted position by their single dark centre
    /// module, not by a run ratio, so shrinking them is harmless. Pinned across the versions that
    /// have one at all (version 1 has none) up to version 40, because a future change that
    /// extended the finder rule to alignment patterns would cost draw calls for nothing.
    /// </summary>
    [Test]
    [Arguments(2)]
    [Arguments(7)]
    [Arguments(25)]
    [Arguments(40)]
    public async Task StandardQR_AlignmentPatterns_TolerateShrunkModules(int version)
    {
        const string content = "https://githu";
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
