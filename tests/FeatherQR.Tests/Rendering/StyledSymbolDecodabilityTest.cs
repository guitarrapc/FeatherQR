using System.Globalization;
using System.Xml.Linq;
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
    /// A canvas that is not square is fitted, so the cells stay square and the finder pattern has
    /// to land on them. Sampling every module centre against the matrix is the direct statement of
    /// that: the finder is drawn after the modules and is not clipped, so a pattern built from one
    /// axis paints its light ring over whatever is next to it.
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
    /// what it must never do is reach outside its seven-by-seven box. On a wide canvas the light
    /// ring once ran past the bottom edge of a stretched finder and over the modules below it.
    /// The builder now fits the symbol, so the finder here is square; the per-axis drawing that
    /// fixed the overrun is pinned directly by <see cref="DecorativeFinder_NonSquareRect_RingsFollowTheGrid"/>.
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
    /// Whether a symbol decodes must not depend on whether it is styled. These canvases were once
    /// mild stretches, where at 900x820 the styled render failed while the plain one decoded; the
    /// builder now fits the symbol, so they are square renders at slightly different sizes, and the
    /// per-axis finder drawing that fixed the stretch is pinned by
    /// <see cref="DecorativeFinder_NonSquareRect_RingsFollowTheGrid"/>.
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
            return; // The plain render does not decode either; nothing to compare.

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
    /// <remarks>
    /// The builder fits the symbol into the canvas with one uniform module scale and centers it,
    /// so the grid to sample is that fitted rectangle, not the canvas.
    /// </remarks>
    private static async Task AssertModuleCentres(SKBitmap bitmap, int matrixWidth, int matrixHeight, Func<int, int, bool> isDark, Func<int, int, bool>? skip = null)
    {
        // Mirrors the builder's own arithmetic, double and epsilon included; a grid computed less
        // precisely drifts a pixel from the one the builder drew on.
        var cell = Math.Min((double)bitmap.Width / matrixWidth, (double)bitmap.Height / matrixHeight);
        var left = Math.Max(0d, Math.Floor((bitmap.Width - cell * matrixWidth) / 2 + 1e-6));
        var top = Math.Max(0d, Math.Floor((bitmap.Height - cell * matrixHeight) / 2 + 1e-6));

        for (var row = 0; row < matrixHeight; row++)
        {
            for (var col = 0; col < matrixWidth; col++)
            {
                if (skip is not null && skip(row, col))
                    continue;

                var x = (int)(left + (col + 0.5f) * cell);
                var y = (int)(top + (row + 0.5f) * cell);
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
    /// The background's alpha changes the background rect and nothing else in the SVG. Every
    /// built-in finder shape used to be cut out of its background with a blend mode inside a layer
    /// when the alpha was below 255, and <c>SKSvgCanvas</c> dropped the layer whole, so the
    /// translucent document lost its finder patterns: 9 elements on Standard QR, 3 on the others.
    /// The ring is a hole in the drawing now, so the two documents differ only in the
    /// <c>fill-opacity</c> of the background.
    /// </summary>
    [Test]
    [Arguments("qr", "rectangle")]
    [Arguments("qr", "circle")]
    [Arguments("qr", "rounded")]
    [Arguments("qr", "roundedCircle")]
    [Arguments("microqr", "rectangle")]
    [Arguments("microqr", "circle")]
    [Arguments("microqr", "rounded")]
    [Arguments("microqr", "roundedCircle")]
    [Arguments("rmqr", "rectangle")]
    [Arguments("rmqr", "circle")]
    [Arguments("rmqr", "rounded")]
    [Arguments("rmqr", "roundedCircle")]
    public async Task DecorativeFinder_TranslucentBackground_DrawsTheSameSvgElements(string symbology, string finder)
    {
        var shape = FinderShapeOf(finder);

        string Svg(byte alpha)
        {
            var background = SKColors.White.WithAlpha(alpha);
            return symbology switch
            {
                "rmqr" => new RmQRCodeImageBuilder("https://githu").WithSize(630, 160).WithVersion(RmQRVersion.R11x59)
                    .WithBackgroundColor(background).WithFinderPatternShape(shape).ToSvgString(),
                "microqr" => new MicroQRCodeImageBuilder("https://githu").WithSize(504, 504)
                    .WithBackgroundColor(background).WithFinderPatternShape(shape).ToSvgString(),
                _ => new QRCodeImageBuilder("https://githu").WithSize(512, 512)
                    .WithBackgroundColor(background).WithFinderPatternShape(shape).ToSvgString(),
            };
        }

        static string WithoutOpacity(string svg)
        {
            var doc = System.Xml.Linq.XDocument.Parse(svg);
            foreach (var attribute in doc.Descendants().SelectMany(e => e.Attributes("fill-opacity")).ToArray())
                attribute.Remove();
            return doc.ToString();
        }

        var opaque = Svg(255);
        var translucent = Svg(128);

        // The premise: alpha 128 really is written as an opacity, so the comparison below is not
        // trivially true because both documents dropped it.
        await Assert.That(translucent).Contains("fill-opacity");
        await Assert.That(WithoutOpacity(translucent)).IsEqualTo(WithoutOpacity(opaque));
    }

    /// <summary>
    /// The finder pattern is actually in the SVG document, and it is the whole pattern.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The test above compares one alpha against another, which a document that lost its finder at
    /// both alphas satisfies just as well; this one is the positive claim behind it. Measured as
    /// mutations that the suite did not catch before it existed: a renderer that skipped
    /// <c>DrawSingleFinder</c> in SVG output only (Micro QR and rMQR lose their finder, raster
    /// untouched), the curved shapes skipping the draw that carries their ring, and the square
    /// shape dropping its four outer bands and keeping only the 3x3 centre. Each is the defect
    /// this rule exists to prevent, and each passed.
    /// </para>
    /// <para>
    /// Probed at the centre of the pattern and at the middle of each of its four outer edges, all
    /// of which are dark in a finder pattern, plus the four light ring midpoints, which are not.
    /// The region an element paints, never its bounding box: a bounding box says yes everywhere
    /// inside a curved shape's outer edge, so a solid disc would answer every dark probe and the
    /// light ones could not be asserted at all.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments("qr", "rectangle")]
    [Arguments("qr", "circle")]
    [Arguments("qr", "rounded")]
    [Arguments("qr", "roundedCircle")]
    [Arguments("microqr", "rectangle")]
    [Arguments("microqr", "circle")]
    [Arguments("microqr", "rounded")]
    [Arguments("microqr", "roundedCircle")]
    [Arguments("rmqr", "rectangle")]
    [Arguments("rmqr", "circle")]
    [Arguments("rmqr", "rounded")]
    [Arguments("rmqr", "roundedCircle")]
    [Arguments("qr", "styled")]
    [Arguments("microqr", "styled")]
    [Arguments("rmqr", "styled")]
    public async Task DecorativeFinder_SvgCarriesTheWholeFinderPattern(string symbology, string finder)
    {
        // "styled" is the other way in: no finder-shape call at all, just styled modules, which the
        // renderer answers by substituting the square. It is the ordinary case and the one the
        // named-shape arms cannot see.
        var styledRoute = finder == "styled";
        var shape = FinderShapeOf(finder);
        var square = styledRoute || finder == "rectangle";

        foreach (var alpha in new byte[] { 255, 128, 0 })
        {
            var background = SKColors.White.WithAlpha(alpha);
            var layout = LayoutOf(symbology);

            TSelf Route<TSelf>(SymbolImageBuilderBase<TSelf> builder) where TSelf : SymbolImageBuilderBase<TSelf>
                => styledRoute
                    ? builder.WithModuleShape(CircleModuleShape.Default, 0.85f)
                    : builder.WithFinderPatternShape(shape);

            var svg = symbology switch
            {
                "rmqr" => Route(new RmQRCodeImageBuilder(RmQRData()).WithSize(layout.CanvasWidth, layout.CanvasHeight)
                    .WithBackgroundColor(background)).ToSvgString(),
                "microqr" => Route(new MicroQRCodeImageBuilder(MicroQrData()).WithSize(layout.CanvasWidth, layout.CanvasHeight)
                    .WithBackgroundColor(background)).ToSvgString(),
                _ => Route(new QRCodeImageBuilder(QrData()).WithSize(layout.CanvasWidth, layout.CanvasHeight)
                    .WithBackgroundColor(background)).ToSvgString(),
            };

            var doc = XDocument.Parse(svg);
            // Everything drawn in the code colour: the background rect names white, the finder's own
            // elements name nothing and inherit black.
            var dark = doc.Root!.Descendants()
                .Where(e => !string.Equals(e.Attribute("fill")?.Value, "white", StringComparison.OrdinalIgnoreCase))
                .Select(SvgRegion)
                .Where(region => region is not null)
                .ToArray();

            try
            {
                foreach (var box in layout.FinderBoxes())
                {
                    var module = box.Width / 7f;
                    var inside = dark.Where(region => Contains(box, region!.Bounds)).ToArray();

                    // Present at all, and the whole pattern: the centre and all four outer edges are dark,
                    // and the four midpoints of the light ring between them are not. Regions rather than
                    // bounding boxes, or a solid disc would answer every dark probe and no light one.
                    foreach (var (col, row, isDark) in new[]
                    {
                        (3.5f, 3.5f, true), (3.5f, 0.5f, true), (0.5f, 3.5f, true), (6.5f, 3.5f, true), (3.5f, 6.5f, true),
                        (1.5f, 3.5f, false), (3.5f, 1.5f, false), (5.5f, 3.5f, false), (3.5f, 5.5f, false),
                    })
                    {
                        var x = box.Left + col * module;
                        var y = box.Top + row * module;
                        await Assert.That(inside.Any(region => region!.Contains(x, y))).IsEqualTo(isDark)
                            .Because($"{symbology} {finder} alpha {alpha}: module ({col}, {row}) of the finder at {box.Left},{box.Top} should be {(isDark ? "dark" : "the light ring")}");
                    }

                    // Five elements for the square, which is also what tells an explicitly named square
                    // from the merged module runs that draw the same picture with fifteen rectangles.
                    if (square)
                        await Assert.That(inside.Length).IsEqualTo(5).Because($"{symbology} {finder} alpha {alpha}");
                }
            }
            finally
            {
                foreach (var region in dark)
                    region!.Dispose();
            }
        }
    }

    /// <summary>
    /// The finder's antialiasing follows the finder shape, not the module shape: a square finder
    /// beside circular modules is drawn crisp, and a curved one is not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sized so the module grid lands off whole pixels, because that is the only place the crisp
    /// half shows: at 504 for Micro QR or 630 for rMQR the finder edges fall on pixel boundaries
    /// and antialiasing is a no-op for straight edges, which is why the approval goldens (all
    /// built with <c>WithModulePixelSize</c>) cannot see it and a revert to the old
    /// "only ever turn it on" form passed the whole suite.
    /// </para>
    /// <para>
    /// The curved half is the other direction, and it needs saying separately: with only the
    /// square shape asserted, the renderer could assign a constant <see langword="false"/> instead
    /// of the shape's answer and nothing would fail — measured as a surviving mutation on
    /// <c>DrawSingleFinder</c>, where a Micro QR circle finder came out aliased.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments("qr")]
    [Arguments("microqr")]
    [Arguments("rmqr")]
    public async Task StyledModules_FinderAntialiasing_FollowsTheFinderShape(string symbology)
    {
        // One pixel more than the sizes the test above uses, which are whole modules across.
        var even = LayoutOf(symbology);
        var layout = even with { CanvasWidth = even.CanvasWidth + 1, CanvasHeight = even.CanvasHeight + 1 };
        var boxes = layout.FinderBoxes();

        SKBitmap Render(FinderPatternShape finder) => symbology switch
        {
            "rmqr" => new RmQRCodeImageBuilder(RmQRData()).WithSize(layout.CanvasWidth, layout.CanvasHeight)
                .WithColors(SKColors.Black, SKColors.White)
                .WithModuleShape(CircleModuleShape.Default, 0.85f)
                .WithFinderPatternShape(finder).ToBitmap(),
            "microqr" => new MicroQRCodeImageBuilder(MicroQrData()).WithSize(layout.CanvasWidth, layout.CanvasHeight)
                .WithColors(SKColors.Black, SKColors.White)
                .WithModuleShape(CircleModuleShape.Default, 0.85f)
                .WithFinderPatternShape(finder).ToBitmap(),
            _ => new QRCodeImageBuilder(QrData()).WithSize(layout.CanvasWidth, layout.CanvasHeight)
                .WithColors(SKColors.Black, SKColors.White)
                .WithModuleShape(CircleModuleShape.Default, 0.85f)
                .WithFinderPatternShape(finder).ToBitmap(),
        };

        (int InsideFinders, int Elsewhere) Count(SKBitmap bitmap)
        {
            static bool IsIntermediate(SKColor pixel) => pixel.Red is > 8 and < 247;

            var insideFinders = 0;
            var elsewhere = 0;
            for (var y = 0; y < bitmap.Height; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    if (!IsIntermediate(bitmap.GetPixel(x, y)))
                        continue;
                    // The finder pattern owns its whole 7x7 box: the modules under it are skipped, so
                    // an intermediate pixel there can only come from the finder's own edges.
                    if (boxes.Any(b => b.Contains(x + 0.5f, y + 0.5f)))
                        insideFinders++;
                    else
                        elsewhere++;
                }
            }

            return (insideFinders, elsewhere);
        }

        using (var straight = Render(RectangleFinderPatternShape.Default))
        {
            var counted = Count(straight);
            await Assert.That(counted.InsideFinders).IsEqualTo(0).Because($"{symbology}: the square finder must be drawn crisp");
            // The premise: this canvas does antialias something, so the count above is a result and
            // not an artifact of a grid that happens to land on whole pixels.
            await Assert.That(counted.Elsewhere).IsGreaterThan(0).Because($"{symbology}: the circular modules must still be antialiased");
        }

        using (var curved = Render(CircleFinderPatternShape.Default))
        {
            // And the other direction, or the renderer could answer a constant instead of the shape.
            await Assert.That(Count(curved).InsideFinders).IsGreaterThan(0)
                .Because($"{symbology}: a curved finder must be antialiased");
        }
    }

    private const string FinderGeometryContent = "https://githu";

    private static QRCodeData QrData()
        => QRCodeGenerator.Create(FinderGeometryContent, QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 4 });

    private static MicroQRCodeData MicroQrData()
        => MicroQRCodeGenerator.Create(FinderGeometryContent, MicroQREccLevel.M, new MicroQRCodeGeneratorOptions { QuietZoneSize = 2 });

    private static RmQRCodeData RmQRData()
        => RmQRCodeGenerator.Create(FinderGeometryContent, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R11x59, QuietZoneSize = 2 });

    /// <summary>The canvas and matrix a finder-geometry case renders on. Every canvas is a whole number of modules across, so the crisp-edge case can add one pixel and stop being one.</summary>
    private static SvgLayout LayoutOf(string symbology) => symbology switch
    {
        "rmqr" => new SvgLayout(630, 160, 59 + 2 * 2, 11 + 2 * 2, 2, false),
        "microqr" => new SvgLayout(504, 504, MicroQrData().Size, MicroQrData().Size, 2, false),
        _ => new SvgLayout(493, 493, QrData().Size, QrData().Size, 4, true),
    };

    /// <summary>The fitted content rectangle and the finder boxes inside it, as the builder computes them.</summary>
    private readonly record struct SvgLayout(int CanvasWidth, int CanvasHeight, int MatrixWidth, int MatrixHeight, int QuietZone, bool ThreeFinders)
    {
        public SKRect[] FinderBoxes()
        {
            // Mirrors the builder's own arithmetic, double and epsilon included.
            var module = Math.Min((double)CanvasWidth / MatrixWidth, (double)CanvasHeight / MatrixHeight);
            var left = Math.Max(0d, Math.Floor((CanvasWidth - module * MatrixWidth) / 2 + 1e-6));
            var top = Math.Max(0d, Math.Floor((CanvasHeight - module * MatrixHeight) / 2 + 1e-6));
            var quietZone = QuietZone;

            SKRect Box(int col, int row) => SKRect.Create(
                (float)(left + (quietZone + col) * module),
                (float)(top + (quietZone + row) * module),
                (float)(module * 7),
                (float)(module * 7));

            var coreWidth = MatrixWidth - QuietZone * 2;
            var coreHeight = MatrixHeight - QuietZone * 2;
            return ThreeFinders
                ? [Box(0, 0), Box(coreWidth - 7, 0), Box(0, coreHeight - 7)]
                : [Box(0, 0)];
        }
    }

    /// <summary>
    /// A circle finder reaches SVG as ovals, never as a flattened path.
    /// </summary>
    /// <remarks>
    /// <c>SKSvgCanvas</c> writes an oval as one <c>&lt;ellipse&gt;</c> and a path as thousands of
    /// quadratic segments, so the encoding is the difference between a 6 KB document and a 50 KB
    /// one. The sizes here are the ones that catch a squareness test written too tightly: the fit
    /// leaves two of the three finder rects a few float ulps off square, and a strict comparison
    /// sent them down the path branch at every canvas size but one.
    /// </remarks>
    [Test]
    [Arguments(512, 512)]
    [Arguments(513, 513)]
    [Arguments(500, 512)]
    [Arguments(900, 450)]
    public async Task DecorativeCircleFinder_ReachesSvgAsOvals(int width, int height)
    {
        var svg = new QRCodeImageBuilder(QrData()).WithSize(width, height)
            .WithFinderPatternShape(CircleFinderPatternShape.Default)
            .ToSvgString();

        var doc = XDocument.Parse(svg);
        var ns = doc.Root!.Name.Namespace;

        // Two per finder: the ring as a stroked oval, the centre as a filled one.
        await Assert.That(doc.Root.Descendants(ns + "ellipse").Count()).IsEqualTo(6).Because($"{width}x{height}");
        await Assert.That(doc.Root.Descendants(ns + "path").Count()).IsEqualTo(0).Because($"{width}x{height}");
    }

    /// <summary>
    /// A shape hands the paint back as it found it, and the square one draws no seam whatever paint it is given.
    /// </summary>
    /// <remarks>
    /// A shape may change the paint it is lent, which is how a ring is drawn as a stroke, so every
    /// one of them has to put it back: the renderer reuses that paint for the next finder and the
    /// icon. The square shape's outer ring is four abutting bands, and abutting antialiased edges
    /// do not composite back to opaque, so it also holds the paint to the straight edges it
    /// declares — a caller's antialiased paint, or a custom shape that declares antialiasing and
    /// delegates here, drew a grey line across the ring before it did.
    /// </remarks>
    [Test]
    [Arguments("rectangle")]
    [Arguments("circle")]
    [Arguments("rounded")]
    [Arguments("roundedCircle")]
    public async Task DecorativeFinder_DrawnWithAnAntialiasedPaint_RestoresItAndDrawsNoSeam(string finder)
    {
        var rect = SKRect.Create(10.3f, 10.3f, 100.5f, 100.5f);
        using var bitmap = new SKBitmap(140, 140, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Fill, StrokeWidth = 3f };
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            FinderShapeOf(finder).Draw(canvas, rect, paint);
        }

        await Assert.That(paint.IsAntialias).IsTrue().Because($"{finder} must restore IsAntialias");
        await Assert.That(paint.Style).IsEqualTo(SKPaintStyle.Fill).Because($"{finder} must restore Style");
        await Assert.That(paint.StrokeWidth).IsEqualTo(3f).Because($"{finder} must restore StrokeWidth");

        if (finder != "rectangle")
            return;

        // Straight edges on whole-pixel-free coordinates: every pixel is either ink or ground.
        var intermediate = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Red is > 8 and < 247)
                    intermediate++;
            }
        }

        await Assert.That(intermediate).IsEqualTo(0).Because("the square finder's abutting bands must not seam");
    }

    /// <summary>The region an SVG element actually paints, in the document's own coordinates, or <see langword="null"/> if it paints nothing.</summary>
    /// <remarks>
    /// The real region, not a bounding box: a bounding box says yes to every point inside a curved
    /// shape's outer edge, so a solid disc would pass a ring test written against one.
    /// </remarks>
    private static SKPath? SvgRegion(XElement element)
    {
        float Read(string name, float fallback = 0f)
            => float.TryParse(element.Attribute(name)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

        SKPath? region;
        switch (element.Name.LocalName)
        {
            case "rect":
                using (var builder = new SKPathBuilder())
                {
                    builder.AddRect(SKRect.Create(Read("x"), Read("y"), Read("width"), Read("height")));
                    region = builder.Detach();
                }
                break;
            case "ellipse":
            case "circle":
                // An omitted ry means "the same as rx", and a circle names one r for both.
                var radiusX = Read("rx", Read("r"));
                var radiusY = Read("ry", radiusX);
                using (var builder = new SKPathBuilder())
                {
                    builder.AddOval(new SKRect(Read("cx") - radiusX, Read("cy") - radiusY, Read("cx") + radiusX, Read("cy") + radiusY));
                    region = builder.Detach();
                }
                break;
            case "path":
                region = SKPath.ParseSvgPathData(element.Attribute("d")?.Value ?? string.Empty);
                break;
            default:
                return null;
        }

        if (region is null)
            return null;

        if (string.Equals(element.Attribute("fill-rule")?.Value, "evenodd", StringComparison.Ordinal))
            region.FillType = SKPathFillType.EvenOdd;

        // A stroked outline paints the band along its geometry rather than the inside of it, which
        // is how a ring reaches the document as one <ellipse>.
        var stroke = element.Attribute("stroke")?.Value;
        if (string.IsNullOrEmpty(stroke) || string.Equals(stroke, "none", StringComparison.OrdinalIgnoreCase))
            return region;

        using var pen = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = Read("stroke-width", 1f) };
        var outline = pen.GetFillPath(region);
        region.Dispose();
        return outline;
    }

    /// <summary>Whether the outer rectangle contains the inner one, with a tolerance for the document's rounded coordinates.</summary>
    private static bool Contains(SKRect outer, SKRect inner)
    {
        // Half a pixel: a stroked ring's outline lands on the box edge and rounds either way.
        const float Epsilon = 0.5f;
        return inner.Left >= outer.Left - Epsilon
            && inner.Top >= outer.Top - Epsilon
            && inner.Right <= outer.Right + Epsilon
            && inner.Bottom <= outer.Bottom + Epsilon;
    }

    /// <summary>
    /// Every decorative finder decodes in a wide canvas. The builder fits the symbol, so the
    /// finder is square here; whether the circle-based ones follow a non-square grid, which only a
    /// direct call can ask of them now, is <see cref="DecorativeFinder_NonSquareRect_RingsFollowTheGrid"/>.
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
    /// Drawn straight into a non-square rect, which a caller can still do, every finder keeps its
    /// rings on that rect's own seven-by-seven grid and inside it. Across the middle row and the
    /// middle column the cells read dark, light, three dark, light, dark whatever the shape; a ring
    /// sized from the shorter side would leave the long axis's ring cells light, and one sized from
    /// the longer side would spill past the short edges.
    /// </summary>
    [Test]
    [Arguments("rectangle", 20, 10)]
    [Arguments("rectangle", 10, 20)]
    [Arguments("circle", 20, 10)]
    [Arguments("circle", 10, 20)]
    [Arguments("rounded", 20, 10)]
    [Arguments("rounded", 10, 20)]
    [Arguments("roundedCircle", 20, 10)]
    [Arguments("roundedCircle", 10, 20)]
    public async Task DecorativeFinder_NonSquareRect_RingsFollowTheGrid(string finder, int cellWidth, int cellHeight)
    {
        var rect = SKRect.Create(10, 10, 7 * cellWidth, 7 * cellHeight);
        using var bitmap = new SKBitmap((int)rect.Right + 10, (int)rect.Bottom + 10);
        using (var canvas = new SKCanvas(bitmap))
        {
            // The ring is left undrawn, so its cells read as the red the canvas was cleared to.
            canvas.Clear(SKColors.Red);
            using var dark = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            FinderShapeOf(finder).Draw(canvas, rect, dark);
        }

        bool[] ring = [true, false, true, true, true, false, true];
        for (var i = 0; i < 7; i++)
        {
            var alongRow = bitmap.GetPixel((int)(rect.Left + (i + 0.5f) * cellWidth), (int)(rect.Top + 3.5f * cellHeight));
            var alongColumn = bitmap.GetPixel((int)(rect.Left + 3.5f * cellWidth), (int)(rect.Top + (i + 0.5f) * cellHeight));
            await Assert.That(alongRow.Red < 128).IsEqualTo(ring[i]).Because($"{finder}, middle row, cell {i}: {alongRow}");
            await Assert.That(alongColumn.Red < 128).IsEqualTo(ring[i]).Because($"{finder}, middle column, cell {i}: {alongColumn}");
        }

        var outside = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (!rect.Contains(x + 0.5f, y + 0.5f) && bitmap.GetPixel(x, y) != SKColors.Red) outside++;
            }
        }
        await Assert.That(outside).IsEqualTo(0).Because($"{finder} must not draw outside its rect");
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
