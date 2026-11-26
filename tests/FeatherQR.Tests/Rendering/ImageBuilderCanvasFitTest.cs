using SkiaSharp;
using FeatherQR.SkiaSharp;
using ZXing;
using ZXing.SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// A canvas aspect ratio the builder accepts must produce a symbol a reader can find.
/// </summary>
/// <remarks>
/// <para>
/// A symbol stretched across a non-square canvas keeps its module centres correct and throws nothing. What breaks is detection: a finder pattern is located by its 1:1:3:1:1 run along a line, and that ratio holds on one axis only once the cells stop being square. Where that starts is not a constant worth pinning, so these cases assert decodability well inside the range where a stretch fails rather than hunting for its edge.
/// </para>
/// <para>
/// Aspect ratio, not size: every canvas here is large enough that the module scale is not the variable under test. By default each module is the largest whole number of pixels both sides allow, so the symbol leaves a pad on both axes. <see cref="SymbolImageBuilderBase{TSelf}.WithFillCanvas(bool)"/> fits it at a fractional scale instead, and the cases about that fit's rounding ask for it.
/// </para>
/// <para>
/// The leftover canvas takes <c>clearColor</c> when one is set and the background otherwise, so every accepted canvas is opaque by default: a transparent default becomes black bands the moment the image is encoded as JPEG.
/// </para>
/// </remarks>
public class ImageBuilderCanvasFitTest
{
    private const string Content = "https://githu";

    /// <summary>Canvases whose aspect ratio a stretched symbol does not survive, in both orientations.</summary>
    public static IEnumerable<(string, int, int)> NonSquareCanvases()
    {
        foreach (var symbology in new[] { "qr", "microqr", "rmqr" })
        {
            yield return (symbology, 900, 450);
            yield return (symbology, 900, 300);
            yield return (symbology, 300, 900);
            yield return (symbology, 600, 400);
        }
    }

    [Test]
    [MethodDataSource(nameof(NonSquareCanvases))]
    public async Task WithSize_NonSquareCanvas_KeepsEveryModuleOnItsSquareCell(string symbology, int width, int height)
    {
        var symbol = Symbol.Create(symbology);
        using var bitmap = symbol.Render(width, height, styled: false, background: SKColors.White, clear: null);

        await Assert.That(bitmap.Width).IsEqualTo(width);
        await Assert.That(bitmap.Height).IsEqualTo(height);

        // Both cell sizes come out of the same scale by construction, so asserting they match here
        // would test the helper rather than the render. What the render has to answer for is the
        // module grid below, and the drawn-pixel span that WithSize_NonSquareCanvas_DarkModulesSpanASquare
        // measures without assuming any grid at all.
        var content = Letterbox(width, height, symbol.MatrixWidth, symbol.MatrixHeight);
        var cellWidth = content.Width / symbol.MatrixWidth;
        var cellHeight = content.Height / symbol.MatrixHeight;

        var wrong = 0;
        for (var row = 0; row < symbol.MatrixHeight; row++)
        {
            for (var col = 0; col < symbol.MatrixWidth; col++)
            {
                var x = (int)(content.Left + (col + 0.5f) * cellWidth);
                var y = (int)(content.Top + (row + 0.5f) * cellHeight);
                if ((bitmap.GetPixel(x, y).Red < 128) != symbol.IsDark(row, col)) wrong++;
            }
        }

        await Assert.That(wrong).IsEqualTo(0)
            .Because($"{symbology} at {width}x{height}: module centres must sit on the fitted grid");
    }

    /// <summary>
    /// The fitted grid is one hypothesis about where the symbol is, and the drawn pixels are another.
    /// A stretched render keeps its module centres correct too, so only a measurement that does
    /// not assume the grid can tell the two apart: the dark modules of a square symbology span a
    /// square, whatever the canvas.
    /// </summary>
    [Test]
    [Arguments("qr", 900, 450)]
    [Arguments("qr", 300, 900)]
    [Arguments("microqr", 900, 450)]
    [Arguments("microqr", 300, 900)]
    public async Task WithSize_NonSquareCanvas_DarkModulesSpanASquare(string symbology, int width, int height)
    {
        var symbol = Symbol.Create(symbology);
        using var bitmap = symbol.Render(width, height, styled: false, background: SKColors.White, clear: null);

        var box = DarkBoundingBox(bitmap);
        var ratio = (float)box.Width / box.Height;

        await Assert.That(ratio).IsBetween(0.98f, 1.02f)
            .Because($"{symbology} at {width}x{height}: dark bounding box {box.Width}x{box.Height}");
    }

    [Test]
    [MethodDataSource(nameof(NonSquareCanvases))]
    public async Task WithSize_NonSquareCanvas_StillDecodes(string symbology, int width, int height)
    {
        var symbol = Symbol.Create(symbology);
        using var bitmap = symbol.Render(width, height, styled: false, background: SKColors.White, clear: null);

        await Assert.That(symbol.TryDecode(bitmap, out var status)).IsTrue()
            .Because($"{symbology} at {width}x{height} reported {status}");
    }

    /// <summary>
    /// Styling narrows the aspect window a stretched symbol survives well below plain rendering's,
    /// so the same canvases have to hold with a circle-based module and finder shape.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(NonSquareCanvases))]
    public async Task WithSize_NonSquareCanvas_StillDecodesWhenStyled(string symbology, int width, int height)
    {
        var symbol = Symbol.Create(symbology);
        using var bitmap = symbol.Render(width, height, styled: true, background: SKColors.White, clear: null);

        await Assert.That(symbol.TryDecode(bitmap, out var status)).IsTrue()
            .Because($"{symbology} styled at {width}x{height} reported {status}");
    }

    /// <summary>
    /// Our own decoder tolerates a wider aspect band than some readers do, so the extreme case
    /// carries a third-party cross-check as well.
    /// </summary>
    [Test]
    [Arguments(900, 450)]
    [Arguments(900, 300)]
    [Arguments(300, 900)]
    public async Task WithSize_NonSquareCanvas_IsReadableByZXing(int width, int height)
    {
        var reader = new BarcodeReader
        {
            AutoRotate = true,
            Options = new ZXing.Common.DecodingOptions { TryHarder = true, TryInverted = true, PossibleFormats = new[] { BarcodeFormat.QR_CODE } },
        };

        var symbol = Symbol.Create("qr");
        using var bitmap = symbol.Render(width, height, styled: false, background: SKColors.White, clear: null);

        var result = reader.Decode(bitmap);
        await Assert.That(result).IsNotNull().Because($"ZXing could not read the {width}x{height} render");
        await Assert.That(result!.Text).IsEqualTo(Content);
    }

    // ─── The leftover canvas ───

    /// <summary>
    /// A canvas the symbol fits exactly must carry no pad at all.
    /// </summary>
    /// <remarks>Rounding can leave the centering offset just below zero, and flooring that gives -1: the symbol shifts a pixel and the far edges grow a band of pad. The pad is the background by default, so seeing it needs a clear colour that differs. These versions are ones whose ratio actually overshoots.</remarks>
    [Test]
    [Arguments(5, 400)]
    [Arguments(6, 450)]
    [Arguments(14, 720)]
    [Arguments(19, 1000)]
    public async Task WithSize_SquareCanvasTheSymbolFitsExactly_HasNoPad(int version, int canvasSide)
    {
        var data = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version), QuietZoneSize = 4 });
        using var bitmap = new QRCodeImageBuilder(data)
            .WithSize(canvasSide, canvasSide)
            .WithFillCanvas()
            .WithColors(SKColors.Black, SKColors.White).WithClearColor(SKColors.Red)
            .ToBitmap();

        var pad = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y) == SKColors.Red) pad++;
            }
        }

        await Assert.That(pad).IsEqualTo(0)
            .Because($"version {version} ({data.Size} modules) fills a {canvasSide}x{canvasSide} canvas exactly");
        // The symbol must also not have shifted: the far edge is quiet zone, so it is background.
        await Assert.That(bitmap.GetPixel(canvasSide - 1, canvasSide - 1)).IsEqualTo(SKColors.White);
    }

    /// <summary>
    /// A leftover that divides evenly puts the same number of pixels on both sides.
    /// </summary>
    /// <remarks>
    /// Same single-precision overshoot as <see cref="WithSize_SquareCanvasTheSymbolFitsExactly_HasNoPad"/>,
    /// one pixel further along: when the exact offset is a whole number the fitted rectangle reports
    /// it as that number minus about 1.5e-5, and flooring loses the pixel to the far side. Clamping
    /// at zero does not catch this one, because nothing here is negative.
    /// </remarks>
    [Test]
    [Arguments(5, 200, 400)]
    [Arguments(5, 400, 200)]
    public async Task WithSize_EvenLeftover_CentresTheSymbolExactly(int version, int width, int height)
    {
        var data = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version), QuietZoneSize = 4 });
        using var bitmap = new QRCodeImageBuilder(data)
            .WithSize(width, height)
            .WithFillCanvas()
            .WithColors(SKColors.Black, SKColors.White).WithClearColor(SKColors.Red)
            .ToBitmap();

        // The pad lands on the longer axis, so scan along that one.
        int near = 0, far = 0;
        if (height > width)
        {
            var column = bitmap.Width / 2;
            for (var y = 0; y < bitmap.Height && bitmap.GetPixel(column, y) == SKColors.Red; y++) near++;
            for (var y = bitmap.Height - 1; y >= 0 && bitmap.GetPixel(column, y) == SKColors.Red; y--) far++;
        }
        else
        {
            var row = bitmap.Height / 2;
            for (var x = 0; x < bitmap.Width && bitmap.GetPixel(x, row) == SKColors.Red; x++) near++;
            for (var x = bitmap.Width - 1; x >= 0 && bitmap.GetPixel(x, row) == SKColors.Red; x--) far++;
        }

        await Assert.That(near).IsGreaterThan(0).Because("the test must actually be looking at the pad");

        await Assert.That(near).IsEqualTo(far)
            .Because($"version {version} in {width}x{height} leaves an even number of pixels over");
    }

    /// <summary>
    /// An offset that is not a whole number of pixels is floored, so the two pad bands differ by
    /// one pixel rather than the content landing on a half pixel and blurring into its pad.
    /// </summary>
    [Test]
    public async Task WithSize_FractionalCentering_KeepsTheContentOnWholePixels()
    {
        // A square symbol fitted into 333 gives a 500-wide canvas an offset of exactly 83.5.
        var data = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 4 });

        using var bitmap = new QRCodeImageBuilder(data)
            .WithSize(500, 333)
            .WithFillCanvas()
            .WithColors(SKColors.Black, SKColors.White).WithClearColor(SKColors.Red)
            .ToBitmap();

        var row = bitmap.Height / 2;
        int left = 0, right = 0;
        for (var x = 0; x < bitmap.Width && bitmap.GetPixel(x, row) == SKColors.Red; x++) left++;
        for (var x = bitmap.Width - 1; x >= 0 && bitmap.GetPixel(x, row) == SKColors.Red; x--) right++;

        await Assert.That(left).IsEqualTo(83).Because("the offset floors from 83.5");
        await Assert.That(right).IsEqualTo(84).Because("the leftover pixel goes to the far side");
    }

    [Test]
    [Arguments("qr")]
    [Arguments("microqr")]
    [Arguments("rmqr")]
    public async Task LetterboxPad_DefaultsToTheBackgroundColour(string symbology)
    {
        var background = new SKColor(0xFF, 0xD7, 0x00, 0xFF);
        var symbol = Symbol.Create(symbology);
        using var bitmap = symbol.Render(900, 450, styled: false, background: background, clear: null);

        foreach (var (x, y) in Corners(bitmap))
        {
            await Assert.That(bitmap.GetPixel(x, y)).IsEqualTo(background).Because($"{symbology} pad at ({x}, {y})");
        }

        await Assert.That(bitmap.AlphaType).IsEqualTo(SKAlphaType.Opaque)
            .Because("an opaque background over the whole canvas leaves nothing to blend");
    }

    [Test]
    [Arguments("qr")]
    [Arguments("microqr")]
    [Arguments("rmqr")]
    public async Task LetterboxPad_TakesTheClearColourWhenOneIsSet(string symbology)
    {
        var symbol = Symbol.Create(symbology);
        using var bitmap = symbol.Render(900, 450, styled: false, background: SKColors.White, clear: SKColors.Red);

        // All four bands, not just the one the top-left corner happens to land in.
        foreach (var (x, y) in Corners(bitmap))
        {
            await Assert.That(bitmap.GetPixel(x, y)).IsEqualTo(SKColors.Red).Because($"{symbology} pad at ({x}, {y})");
        }
    }

    /// <summary>
    /// A pad that is neither opaque nor absent: the alpha has to survive to the surface, and the
    /// surface has to keep its alpha channel to carry it.
    /// </summary>
    [Test]
    public async Task LetterboxPad_TranslucentClearColour_KeepsItsAlpha()
    {
        var translucent = new SKColor(0xFF, 0x00, 0x00, 0x80);
        var symbol = Symbol.Create("qr");
        using var bitmap = symbol.Render(900, 450, styled: false, background: SKColors.White, clear: translucent);

        await Assert.That(bitmap.AlphaType).IsEqualTo(SKAlphaType.Premul);
        foreach (var (x, y) in Corners(bitmap))
        {
            await Assert.That(bitmap.GetPixel(x, y).Alpha).IsEqualTo((byte)0x80).Because($"pad at ({x}, {y})");
        }
    }

    /// <summary>
    /// An opaque background that covers the whole canvas makes the image opaque whatever the clear
    /// colour is, because nothing of the clear survives. Dropping that half of the decision leaves
    /// every default render carrying an alpha channel it does not use.
    /// </summary>
    [Test]
    public async Task SquareCanvas_OpaqueBackgroundUnderATransparentClearColour_StaysOpaque()
    {
        var symbol = Symbol.Create("qr");
        using var bitmap = symbol.Render(symbol.MatrixWidth * 16, symbol.MatrixHeight * 16, styled: false, background: SKColors.White, clear: SKColors.Transparent);

        await Assert.That(bitmap.AlphaType).IsEqualTo(SKAlphaType.Opaque);
        foreach (var (x, y) in Corners(bitmap))
        {
            await Assert.That(bitmap.GetPixel(x, y)).IsEqualTo(SKColors.White).Because($"({x}, {y})");
        }
    }

    /// <summary>
    /// A clear colour is a canvas colour: with a transparent background it shows through the symbol
    /// box as well as the pad, which is the half of its contract the pad rule does not describe.
    /// </summary>
    [Test]
    public async Task ClearColour_ShowsThroughATransparentBackground()
    {
        var symbol = Symbol.Create("qr");
        using var bitmap = symbol.Render(900, 450, styled: false, background: SKColors.Transparent, clear: SKColors.Red);

        var content = Letterbox(900, 450, symbol.MatrixWidth, symbol.MatrixHeight);
        var cell = content.Width / symbol.MatrixWidth;
        var quietZone = bitmap.GetPixel((int)(content.Left + cell * 0.5f), (int)(content.Top + cell * 0.5f));

        await Assert.That(bitmap.GetPixel(4, 4)).IsEqualTo(SKColors.Red).Because("pad");
        await Assert.That(quietZone).IsEqualTo(SKColors.Red).Because("the quiet zone, with nothing painted over the clear");
        await Assert.That(bitmap.AlphaType).IsEqualTo(SKAlphaType.Opaque);
    }

    /// <summary>
    /// Transparent surroundings with an opaque symbol background are available. They are
    /// asked for rather than assumed.
    /// </summary>
    [Test]
    public async Task LetterboxPad_StaysTransparentWhenTheClearColourIs()
    {
        var symbol = Symbol.Create("qr");
        using var bitmap = symbol.Render(900, 450, styled: false, background: SKColors.White, clear: SKColors.Transparent);

        await Assert.That(bitmap.GetPixel(4, 4).Alpha).IsEqualTo((byte)0);
        await Assert.That(bitmap.AlphaType).IsEqualTo(SKAlphaType.Premul);
    }

    /// <summary>
    /// The pad that <see cref="SymbolImageBuilderBase{TSelf}.WithModulePixelSize(int)"/> leaves
    /// follows the same rule: the background, not transparent, unless a clear colour is set.
    /// </summary>
    [Test]
    public async Task ModulePixelSizePad_DefaultsToTheBackgroundColour()
    {
        var background = new SKColor(0xFF, 0xD7, 0x00, 0xFF);
        var data = QRCodeGenerator.Create(Content, QREccLevel.M);
        var canvasSide = data.Size * 4 + 40;

        using var bitmap = new QRCodeImageBuilder(data)
            .WithModulePixelSize(4)
            .WithSize(canvasSide, canvasSide)
            .WithColors(SKColors.Black, background)
            .ToBitmap();

        await Assert.That(bitmap.GetPixel(0, 0)).IsEqualTo(background);
        await Assert.That(bitmap.AlphaType).IsEqualTo(SKAlphaType.Opaque);
    }

    /// <summary>
    /// A translucent background must not be painted twice. Filling the whole canvas with it and
    /// then letting the renderer paint it again over the symbol would leave the symbol box
    /// visibly less transparent than the pad around it.
    /// </summary>
    [Test]
    public async Task TranslucentBackground_PadAndSymbolBoxShareOneCoatOfPaint()
    {
        var background = new SKColor(0x00, 0x00, 0xFF, 0x80);
        var symbol = Symbol.Create("qr");
        using var bitmap = symbol.Render(900, 450, styled: false, background: background, clear: null);

        var pad = bitmap.GetPixel(4, 4);
        // Inside the fitted content, the quiet zone carries the background and nothing else.
        var content = Letterbox(900, 450, symbol.MatrixWidth, symbol.MatrixHeight);
        var cell = content.Width / symbol.MatrixWidth;
        var quietZone = bitmap.GetPixel((int)(content.Left + cell * 0.5f), (int)(content.Top + cell * 0.5f));

        await Assert.That(pad.Alpha).IsEqualTo(background.Alpha);
        await Assert.That(quietZone.Alpha).IsEqualTo(background.Alpha);
        await Assert.That(pad).IsEqualTo(quietZone);
    }

    /// <summary>
    /// The same single-coat rule where the pad has corners.
    /// </summary>
    /// <remarks>A filled canvas's pad is two bands with no corner, so it cannot see a corner painted twice. Whole-pixel modules inside a larger canvas can leave all four, and they are what the side bands stop short for.</remarks>
    [Test]
    public async Task TranslucentBackground_PadCornersTakeOneCoatToo()
    {
        var background = new SKColor(0x00, 0x00, 0xFF, 0x80);
        var data = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 4 });

        using var bitmap = new QRCodeImageBuilder(data)
            .WithModulePixelSize(4)
            .WithSize(400, 400)
            .WithColors(SKColors.Black, background)
            .ToBitmap();

        // Halfway along the top edge is band, the corner is where two bands could overlap.
        var band = bitmap.GetPixel(bitmap.Width / 2, 2);
        await Assert.That(band.Alpha).IsEqualTo(background.Alpha).Because("the top band");
        foreach (var (x, y) in Corners(bitmap))
        {
            await Assert.That(bitmap.GetPixel(x, y)).IsEqualTo(band).Because($"the corner at ({x}, {y})");
        }
    }

    /// <summary>The slack absorbed before flooring has to stay under the smallest gap a real offset can leave below a whole pixel, at every matrix size the data types accept.</summary>
    /// <remarks>The off-centre axis has an offset that is a multiple of <c>1 / (2 × the dimension that constrained the fit)</c>, so a larger matrix can sit closer to a boundary. A square one never can, since that makes the offsets multiples of a half. <see cref="SymbolImageBuilderBase{TSelf}.WithQuietZone(int)"/> caps at 10 modules, but the generator options and the data constructors go to 10,000, which reaches a 20,000-module matrix and a gap of about 2.5e-5. Both geometries here sit within 1e-3 of a boundary.</remarks>
    [Test]
    [Arguments(RmQRVersion.R7x139, 195, 533, 402, 0)]
    [Arguments(RmQRVersion.R9x139, 10, 806, 661, 256)]
    public async Task WithSize_OffsetJustBelowAWholePixel_IsNotRoundedUp(RmQRVersion version, int quietZone, int width, int height, int expectedTopPad)
    {
        var data = RmQRCodeGenerator.Create("0123456789", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = quietZone });
        using var bitmap = new RmQRCodeImageBuilder(data)
            .WithSize(width, height)
            .WithFillCanvas()
            .WithColors(SKColors.Black, SKColors.White).WithClearColor(SKColors.Red)
            .ToBitmap();

        var column = bitmap.Width / 2;
        var top = 0;
        for (var y = 0; y < bitmap.Height && bitmap.GetPixel(column, y) == SKColors.Red; y++) top++;

        await Assert.That(top).IsEqualTo(expectedTopPad);
    }

    [Test]
    public async Task SvgOutput_KeepsTheCanvasViewBoxAndFitsTheSymbol()
    {
        using var stream = new MemoryStream();
        new QRCodeImageBuilder(Content).WithSize(900, 450).SaveToSvg(stream);
        var svg = System.Text.Encoding.UTF8.GetString(stream.ToArray());

        await Assert.That(svg).Contains("viewBox=\"0 0 900 450\"");
        // The renderer fills its area with the background before drawing modules, so the fitted
        // content is a square rectangle offset by the pad: 29 modules of 15 pixels are 435, which
        // leaves 465 columns, 232 on the left, and 15 rows, 7 above. A band of pad lies on each side.
        await Assert.That(svg).Contains("<rect fill=\"white\" x=\"232\" y=\"7\" width=\"435\" height=\"435\"/>");
        await Assert.That(svg).Contains("<rect fill=\"white\" width=\"900\" height=\"7\"/>");
        await Assert.That(svg).Contains("<rect fill=\"white\" y=\"442\" width=\"900\" height=\"8\"/>");
        await Assert.That(svg).Contains("<rect fill=\"white\" y=\"7\" width=\"232\" height=\"435\"/>");
        await Assert.That(svg).Contains("<rect fill=\"white\" x=\"667\" y=\"7\" width=\"233\" height=\"435\"/>");
    }

    // ─── helpers ───

    /// <summary>
    /// A pinned module size is a promise the canvas can contradict, and it can contradict it on
    /// one axis alone. The other negative cases are too small on both, so either half of the
    /// guard satisfies them by itself.
    /// </summary>
    [Test]
    [Arguments(1000, 50)]
    [Arguments(50, 1000)]
    public async Task WithModulePixelSize_CanvasTooSmallOnOneAxisOnly_Throws(int width, int height)
    {
        var data = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 4 });
        var contentSide = data.Size * 4;
        await Assert.That(contentSide).IsGreaterThan(50);
        await Assert.That(contentSide).IsLessThan(1000);

        var ex = await Assert.That(() => new QRCodeImageBuilder(data).WithModulePixelSize(4).WithSize(width, height).ToBitmap())
            .Throws<InvalidOperationException>();
        await Assert.That(ex!.Message).Contains("smaller than QR content size");
    }

    /// <summary>
    /// Whole-pixel modules need a pixel each, so a canvas that gives less is refused, on one axis alone too. Filling keeps drawing below a
    /// pixel a module, as it always has.
    /// </summary>
    [Test]
    [Arguments(184, 184)]
    [Arguments(1000, 184)]
    [Arguments(184, 1000)]
    public async Task WithSize_UnderAPixelAModule_Throws_UnlessFilling(int width, int height)
    {
        var data = QRCodeGenerator.Create("too small", QREccLevel.M, new QRCodeGeneratorOptions { Version = 40 });
        await Assert.That(data.Size).IsEqualTo(185);

        var ex = await Assert.That(() => new QRCodeImageBuilder(data).WithSize(width, height).ToBitmap()).Throws<InvalidOperationException>();
        await Assert.That(ex!.Message).Contains("less than a pixel");
        await Assert.That(() => new QRCodeImageBuilder(data).WithSize(width, height).ToSvgString()).Throws<InvalidOperationException>();

        using var filled = new QRCodeImageBuilder(data).WithSize(width, height).WithFillCanvas().ToBitmap();
        await Assert.That((filled.Width, filled.Height)).IsEqualTo((width, height));

        using var floor = new QRCodeImageBuilder(data).WithSize(185, 185).ToBitmap();
        await Assert.That((floor.Width, floor.Height)).IsEqualTo((185, 185));
    }

    /// <summary>
    /// Skia makes no raster surface over 2^31 - 1 bytes and returns none rather than throwing. Every raster output says so with the
    /// layout's exception, through either way of sizing the image. Skia refuses before allocating, so the test takes no memory.
    /// The SVG has no surface and is still written.
    /// </summary>
    [Test]
    [Arguments("qr", false)]
    [Arguments("qr", true)]
    [Arguments("micro", false)]
    [Arguments("rmqr", false)]
    public async Task RasterOutputs_PastSkiasSurfaceLimit_ThrowInvalidOperation(string symbology, bool pinnedModuleSize)
    {
        var outputs = new (string Name, Action Run)[]
        {
            ("ToByteArray", () => Raster(symbology, pinnedModuleSize, b => b.ToByteArray(), b => b.ToByteArray(), b => b.ToByteArray())),
            ("ToImage", () => Raster(symbology, pinnedModuleSize, b => b.ToImage().Dispose(), b => b.ToImage().Dispose(), b => b.ToImage().Dispose())),
            ("ToBitmap", () => Raster(symbology, pinnedModuleSize, b => b.ToBitmap().Dispose(), b => b.ToBitmap().Dispose(), b => b.ToBitmap().Dispose())),
            ("SaveTo(Stream)", () => Raster(symbology, pinnedModuleSize, b => b.SaveTo(Stream.Null), b => b.SaveTo(Stream.Null), b => b.SaveTo(Stream.Null))),
            ("SaveTo(IBufferWriter)", () => Raster(symbology, pinnedModuleSize, b => b.SaveTo(new System.Buffers.ArrayBufferWriter<byte>()), b => b.SaveTo(new System.Buffers.ArrayBufferWriter<byte>()), b => b.SaveTo(new System.Buffers.ArrayBufferWriter<byte>()))),
        };
        foreach (var (name, run) in outputs)
        {
            var ex = await Assert.That(run).Throws<InvalidOperationException>().Because(name);
            await Assert.That(ex!.Message).Contains("too large").Because(name);
        }
        var async = await Assert.That(async () => await RasterAsync(symbology, pinnedModuleSize)).Throws<InvalidOperationException>();
        await Assert.That(async!.Message).Contains("too large");

        if (!pinnedModuleSize)
            await Assert.That(new QRCodeImageBuilder(Content).WithSize(100_000, 100_000).ToSvgString()).Contains("viewBox=\"0 0 100000 100000\"");
    }

    private static void Raster(string symbology, bool pinnedModuleSize, Action<QRCodeImageBuilder> qr, Action<MicroQRCodeImageBuilder> micro, Action<RmQRCodeImageBuilder> rmqr)
    {
        switch (symbology)
        {
            case "qr":
                // At least 21 modules a side, so over 420,000 pixels
                qr(pinnedModuleSize ? new QRCodeImageBuilder(Content).WithModulePixelSize(20_000) : new QRCodeImageBuilder(Content).WithSize(100_000, 100_000));
                break;
            case "micro":
                micro(new MicroQRCodeImageBuilder("12345").WithSize(100_000, 100_000));
                break;
            default:
                rmqr(new RmQRCodeImageBuilder("12345").WithSize(100_000, 100_000));
                break;
        }
    }

    private static Task RasterAsync(string symbology, bool pinnedModuleSize) => symbology switch
    {
        "qr" => (pinnedModuleSize ? new QRCodeImageBuilder(Content).WithModulePixelSize(20_000) : new QRCodeImageBuilder(Content).WithSize(100_000, 100_000)).SaveToAsync(Stream.Null),
        "micro" => new MicroQRCodeImageBuilder("12345").WithSize(100_000, 100_000).SaveToAsync(Stream.Null),
        _ => new RmQRCodeImageBuilder("12345").WithSize(100_000, 100_000).SaveToAsync(Stream.Null),
    };

    /// <summary>
    /// WebP takes at most 16,383 pixels a side, and Skia's encoder returns nothing past that, as it does for a format it has no encoder for.
    /// The message names both causes, so a wide image is not reported as a missing encoder alone.
    /// </summary>
    [Test]
    public async Task WebP_PastItsSideLimit_SaysTheImageMayBeTooLarge()
    {
        var fits = new QRCodeImageBuilder(Content).WithFormat(SKEncodedImageFormat.Webp, 80).WithSize(16_383, 64).ToByteArray();
        await Assert.That(fits.Length).IsGreaterThan(0);

        var ex = await Assert.That(() => new QRCodeImageBuilder(Content).WithFormat(SKEncodedImageFormat.Webp, 80).WithSize(16_384, 64).ToByteArray())
            .Throws<NotSupportedException>();
        await Assert.That(ex!.Message).Contains("Webp");
        await Assert.That(ex.Message).Contains("too large");
    }

    /// <summary>A pinned module size decides the modules, so filling changes nothing beside it.</summary>
    [Test]
    public async Task WithFillCanvas_BesideAPinnedModuleSize_ChangesNothing()
    {
        var data = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(3) });
        var pinned = new QRCodeImageBuilder(data).WithSize(500, 333).WithModulePixelSize(7).ToByteArray();

        await Assert.That(new QRCodeImageBuilder(data).WithSize(500, 333).WithModulePixelSize(7).WithFillCanvas().ToByteArray())
            .IsEquivalentTo(pinned, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(new QRCodeImageBuilder(data).WithModulePixelSize(7).WithFillCanvas().ToByteArray())
            .IsEquivalentTo(new QRCodeImageBuilder(data).WithModulePixelSize(7).ToByteArray(), TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    /// <summary>Equal modules are the default, and <c>WithFillCanvas(false)</c> returns to them.</summary>
    [Test]
    public async Task WithFillCanvas_False_IsTheDefault()
    {
        var data = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(3) });
        var equal = new QRCodeImageBuilder(data).WithSize(500, 340).ToByteArray();
        var filled = new QRCodeImageBuilder(data).WithSize(500, 340).WithFillCanvas().ToByteArray();

        await Assert.That(filled).IsNotEquivalentTo(equal, TUnit.Assertions.Enums.CollectionOrdering.Matching).Because("37 modules do not divide 340 pixels");
        await Assert.That(new QRCodeImageBuilder(data).WithSize(500, 340).WithFillCanvas().WithFillCanvas(false).ToByteArray())
            .IsEquivalentTo(equal, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(new QRCodeImageBuilder(data).WithSize(500, 340).ToSvgString())
            .IsEqualTo(new QRCodeImageBuilder(data).WithSize(500, 340).WithModulePixelSize(340 / 37).ToSvgString());
    }

    /// <summary>One pixel inside each corner, so an assertion reaches all four pad bands.</summary>
    private static IEnumerable<(int X, int Y)> Corners(SKBitmap bitmap)
    {
        yield return (2, 2);
        yield return (bitmap.Width - 3, 2);
        yield return (2, bitmap.Height - 3);
        yield return (bitmap.Width - 3, bitmap.Height - 3);
    }

    /// <summary>
    /// The symbol's rectangle at the largest whole number of pixels a module that fits, the spare pixels halved and rounded down on the left and above.
    /// </summary>
    private static SKRect Letterbox(int canvasWidth, int canvasHeight, int matrixWidth, int matrixHeight)
    {
        var module = Math.Min(canvasWidth / matrixWidth, canvasHeight / matrixHeight);
        return SKRect.Create(
            (canvasWidth - module * matrixWidth) / 2,
            (canvasHeight - module * matrixHeight) / 2,
            module * matrixWidth,
            module * matrixHeight);
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

    private sealed class Symbol
    {
        private readonly string _symbology;
        private readonly object _data;

        private Symbol(string symbology, object data, int matrixWidth, int matrixHeight)
        {
            _symbology = symbology;
            _data = data;
            MatrixWidth = matrixWidth;
            MatrixHeight = matrixHeight;
        }

        public int MatrixWidth { get; }

        public int MatrixHeight { get; }

        public static Symbol Create(string symbology) => symbology switch
        {
            "microqr" => CreateMicro(),
            "rmqr" => CreateRm(),
            _ => CreateStandard(),
        };

        private static Symbol CreateStandard()
        {
            var data = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 4 });
            return new Symbol("qr", data, data.Size, data.Size);
        }

        private static Symbol CreateMicro()
        {
            var data = MicroQRCodeGenerator.Create(Content, MicroQREccLevel.M, new MicroQRCodeGeneratorOptions { QuietZoneSize = 2 });
            return new Symbol("microqr", data, data.Size, data.Size);
        }

        private static Symbol CreateRm()
        {
            var data = RmQRCodeGenerator.Create(Content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R11x59, QuietZoneSize = 2 });
            return new Symbol("rmqr", data, data.Width, data.Height);
        }

        public bool IsDark(int row, int col) => _symbology switch
        {
            "microqr" => ((MicroQRCodeData)_data)[row, col],
            "rmqr" => ((RmQRCodeData)_data)[row, col],
            _ => ((QRCodeData)_data)[row, col],
        };

        public SKBitmap Render(int width, int height, bool styled, SKColor background, SKColor? clear)
        {
            switch (_symbology)
            {
                case "microqr":
                    {
                        var builder = new MicroQRCodeImageBuilder((MicroQRCodeData)_data).WithSize(width, height).WithColors(SKColors.Black, background).WithClearColor(clear);
                        if (styled) builder = builder.WithModuleShape(CircleModuleShape.Default, 0.85f).WithFinderPatternShape(CircleFinderPatternShape.Default);
                        return builder.ToBitmap();
                    }
                case "rmqr":
                    {
                        var builder = new RmQRCodeImageBuilder((RmQRCodeData)_data).WithSize(width, height).WithColors(SKColors.Black, background).WithClearColor(clear);
                        if (styled) builder = builder.WithModuleShape(CircleModuleShape.Default, 0.85f).WithFinderPatternShape(CircleFinderPatternShape.Default);
                        return builder.ToBitmap();
                    }
                default:
                    {
                        var builder = new QRCodeImageBuilder((QRCodeData)_data).WithSize(width, height).WithColors(SKColors.Black, background).WithClearColor(clear);
                        if (styled) builder = builder.WithModuleShape(CircleModuleShape.Default, 0.85f).WithFinderPatternShape(CircleFinderPatternShape.Default);
                        return builder.ToBitmap();
                    }
            }
        }

        public bool TryDecode(SKBitmap bitmap, out string status)
        {
            switch (_symbology)
            {
                case "microqr":
                    {
                        var ok = MicroQRCodeDecoderExtensions.TryDecode(bitmap, out var text, out var info);
                        status = info.Status.ToString();
                        return ok && text == Content;
                    }
                case "rmqr":
                    {
                        var ok = RmQRCodeDecoderExtensions.TryDecode(bitmap, out var text, out var info);
                        status = info.Status.ToString();
                        return ok && text == Content;
                    }
                default:
                    {
                        var ok = QRCodeDecoderExtensions.TryDecode(bitmap, out var text, out var info);
                        status = info.Status.ToString();
                        return ok && text == Content;
                    }
            }
        }
    }
}
