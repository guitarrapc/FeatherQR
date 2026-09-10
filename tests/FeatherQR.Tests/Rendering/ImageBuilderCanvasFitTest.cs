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
/// The square symbologies used to stretch the symbol across a non-square canvas. Module centres stayed correct and nothing threw, so what broke was detection: a finder pattern is located by its 1:1:3:1:1 run along a line, and that ratio holds on one axis only once the cells stop being square. Where that starts is not a constant worth pinning — measured, failures start around 1.25:1 and nothing survives past 1.8:1 — so these cases assert decodability well inside the band rather than hunting for the edge.
/// </para>
/// <para>
/// Aspect ratio, not size: <see cref="SymbolImageBuilderBase{TSelf}.WithSize(int, int)"/> has no lower bound, and a canvas too small for the symbol gives it sub-pixel modules that nothing reads. Every canvas here is large enough that the module scale is not the variable under test.
/// </para>
/// <para>
/// The leftover canvas takes <c>clearColor</c> when one is set and the background otherwise, so every accepted canvas is opaque by default: a transparent default becomes black bands the moment the image is encoded as JPEG.
/// </para>
/// </remarks>
public class ImageBuilderCanvasFitTest
{
    private const string Content = "https://githu";

    /// <summary>Canvases whose aspect ratio the fill behaviour could not survive, in both orientations.</summary>
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
    /// The fitted grid is one hypothesis about where the symbol is; the drawn pixels are another.
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
    /// Styling narrows the aspect window well below what plain rendering survives, and it is where
    /// the fill behaviour failed first, so the same canvases have to hold with a circle-based
    /// module and finder shape.
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
    /// <remarks>Rounding can leave the centering offset just below zero, and flooring that gives -1: the symbol shifts a pixel and the far edges grow a band of pad. The pad is the background by default, so seeing it needs a clear colour that differs; these versions are ones whose ratio actually overshoots.</remarks>
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
            .WithColors(SKColors.Black, SKColors.White, SKColors.Red)
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
            .WithColors(SKColors.Black, SKColors.White, SKColors.Red)
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
            .WithColors(SKColors.Black, SKColors.White, SKColors.Red)
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
        using var bitmap = symbol.Render(512, 512, styled: false, background: SKColors.White, clear: SKColors.Transparent);

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
    /// Transparent surroundings with an opaque symbol background stay available; they are now
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
    /// follows the same rule. This is the one place where the default changes without the
    /// geometry changing: it used to be transparent.
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
    /// <remarks>A letterbox pad is two bands with no corner, so the non-square case cannot see a corner painted twice. A pinned module size inside a larger canvas has all four, and is what the side bands stop short for.</remarks>
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
    /// <remarks>Offsets are multiples of <c>1 / (2 × matrix width)</c>, so a wider matrix can sit closer to a boundary; a square one never can, since both offsets land on halves. <see cref="SymbolImageBuilderBase{TSelf}.WithQuietZone(int)"/> caps at 10 modules, but the generator options and the data constructors go to 10,000, which reaches a 20,000-module matrix and a gap of about 2.5e-5. Both geometries here sit within 1e-3 of a boundary.</remarks>
    [Test]
    [Arguments(RmQRVersion.R7x139, 195, 533, 402, 0)]
    [Arguments(RmQRVersion.R9x139, 10, 806, 661, 256)]
    public async Task WithSize_OffsetJustBelowAWholePixel_IsNotRoundedUp(RmQRVersion version, int quietZone, int width, int height, int expectedTopPad)
    {
        var data = RmQRCodeGenerator.Create("0123456789", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = quietZone });
        using var bitmap = new RmQRCodeImageBuilder(data)
            .WithSize(width, height)
            .WithColors(SKColors.Black, SKColors.White, SKColors.Red)
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
        // content is a square rectangle offset by the pad, with a band of pad on either side.
        await Assert.That(svg).Contains("<rect fill=\"white\" x=\"225\" width=\"450\" height=\"450\"/>");
        await Assert.That(svg).Contains("<rect fill=\"white\" width=\"225\" height=\"450\"/>");
        await Assert.That(svg).Contains("<rect fill=\"white\" x=\"675\" width=\"225\" height=\"450\"/>");
    }

    // ─── helpers ───

    /// <summary>
    /// A pinned module size is a promise the canvas can contradict, and it can contradict it on
    /// one axis alone. The existing negatives are all too small on both, so either half of the
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

    /// <summary>One pixel inside each corner, so an assertion reaches all four pad bands.</summary>
    private static IEnumerable<(int X, int Y)> Corners(SKBitmap bitmap)
    {
        yield return (2, 2);
        yield return (bitmap.Width - 3, 2);
        yield return (2, bitmap.Height - 3);
        yield return (bitmap.Width - 3, bitmap.Height - 3);
    }

    /// <summary>
    /// The largest rectangle with the matrix aspect ratio that fits, centered on whole pixels.
    /// </summary>
    /// <remarks>
    /// The arithmetic mirrors the builder, double and all: a helper that computes the fit less
    /// precisely stops describing the grid the builder actually draws on.
    /// </remarks>
    private static SKRect Letterbox(int canvasWidth, int canvasHeight, int matrixWidth, int matrixHeight)
    {
        var scale = Math.Min((double)canvasWidth / matrixWidth, (double)canvasHeight / matrixHeight);
        var width = matrixWidth * scale;
        var height = matrixHeight * scale;
        return SKRect.Create(
            Math.Max(0f, (float)Math.Floor((canvasWidth - width) / 2 + 1e-6)),
            Math.Max(0f, (float)Math.Floor((canvasHeight - height) / 2 + 1e-6)),
            (float)width,
            (float)height);
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
                        var builder = new MicroQRCodeImageBuilder((MicroQRCodeData)_data).WithSize(width, height).WithColors(SKColors.Black, background, clear);
                        if (styled) builder = builder.WithModuleShape(CircleModuleShape.Default, 0.85f).WithFinderPatternShape(CircleFinderPatternShape.Default);
                        return builder.ToBitmap();
                    }
                case "rmqr":
                    {
                        var builder = new RmQRCodeImageBuilder((RmQRCodeData)_data).WithSize(width, height).WithColors(SKColors.Black, background, clear);
                        if (styled) builder = builder.WithModuleShape(CircleModuleShape.Default, 0.85f).WithFinderPatternShape(CircleFinderPatternShape.Default);
                        return builder.ToBitmap();
                    }
                default:
                    {
                        var builder = new QRCodeImageBuilder((QRCodeData)_data).WithSize(width, height).WithColors(SKColors.Black, background, clear);
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
                        var ok = MicroQRCodeImageDecoder.TryDecode(bitmap, out var text, out var info);
                        status = info.Status.ToString();
                        return ok && text == Content;
                    }
                case "rmqr":
                    {
                        var ok = RmQRCodeImageDecoder.TryDecode(bitmap, out var text, out var info);
                        status = info.Status.ToString();
                        return ok && text == Content;
                    }
                default:
                    {
                        var ok = QRCodeImageDecoder.TryDecode(bitmap, out var text, out var info);
                        status = info.Status.ToString();
                        return ok && text == Content;
                    }
            }
        }
    }
}
