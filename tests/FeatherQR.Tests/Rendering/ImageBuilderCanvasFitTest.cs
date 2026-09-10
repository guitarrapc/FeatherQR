using SkiaSharp;
using FeatherQR.SkiaSharp;
using ZXing;
using ZXing.SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// A canvas the builder accepts must produce a symbol a reader can find.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SymbolImageBuilderBase{TSelf}.WithSize(int, int)"/> takes a width and a height
/// independently, and the square symbologies used to stretch the symbol across both. Module
/// centres stayed correct, so the data was intact and nothing threw: what broke was detection,
/// because the finder pattern is located by the 1:1:3:1:1 run of dark and light along a line and
/// that ratio only holds on one axis once the cells stop being square. Past roughly 1.67:1 no
/// reader found the symbol at all, and with a circle-based module or finder shape the limit came
/// down to 1.25:1. The builder now fits the symbol into the canvas with a uniform module scale
/// (letterbox), as the rectangular rMQR builder always did.
/// </para>
/// <para>
/// The leftover canvas takes <c>clearColor</c> when one is set and the background colour
/// otherwise, so the default output of every accepted canvas is opaque: a transparent default
/// would turn into black bands the moment the image is encoded as JPEG.
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

        var content = Letterbox(width, height, symbol.MatrixWidth, symbol.MatrixHeight);
        var cellWidth = content.Width / symbol.MatrixWidth;
        var cellHeight = content.Height / symbol.MatrixHeight;
        await Assert.That(Math.Abs(cellWidth - cellHeight)).IsLessThan(0.001f)
            .Because("the fitted content rectangle must give square cells");

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
    /// Styling narrowed the aspect window far more than plain rendering did (1.25:1 against
    /// 1.67:1 for Standard QR), so the same canvases have to hold with a circle-based module and
    /// finder shape, which is where the fill behaviour failed first.
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

    [Test]
    [Arguments("qr")]
    [Arguments("microqr")]
    [Arguments("rmqr")]
    public async Task LetterboxPad_DefaultsToTheBackgroundColour(string symbology)
    {
        var background = new SKColor(0xFF, 0xD7, 0x00, 0xFF);
        var symbol = Symbol.Create(symbology);
        using var bitmap = symbol.Render(900, 450, styled: false, background: background, clear: null);

        await Assert.That(bitmap.GetPixel(4, 4)).IsEqualTo(background).Because($"{symbology} pad");
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

        await Assert.That(bitmap.GetPixel(4, 4)).IsEqualTo(SKColors.Red).Because($"{symbology} pad");
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

    /// <summary>The largest rectangle with the matrix aspect ratio that fits, centered on whole pixels.</summary>
    private static SKRect Letterbox(int canvasWidth, int canvasHeight, int matrixWidth, int matrixHeight)
    {
        var scale = Math.Min((float)canvasWidth / matrixWidth, (float)canvasHeight / matrixHeight);
        var width = matrixWidth * scale;
        var height = matrixHeight * scale;
        return SKRect.Create(
            (float)Math.Floor((canvasWidth - width) / 2),
            (float)Math.Floor((canvasHeight - height) / 2),
            width,
            height);
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
