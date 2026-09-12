#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:project ../../src/FeatherQR.SkiaSharp/FeatherQR.SkiaSharp.csproj
#:package ZXing.Net.Bindings.SkiaSharp.V2
#:package ZXingCpp
using SkiaSharp;
using FeatherQR;
using FeatherQR.SkiaSharp;

// Generates the before/after images embedded in docs/migration.md for the 2.0.0 rendering
// changes, and checks each one as it writes it:
// - WithSize(w, h) fits the symbol instead of stretching it across a non-square canvas.
// - SymbolRenderer.Render fits the symbol into a non-square area instead of filling it.
// - Canvas the symbol does not reach takes the background rather than staying transparent.
//
// The "before" images are the real old output, not an imitation. Nothing in the library stretches
// any more, so the stretch is asked for with a canvas scale over a square area, which gives the
// same pixels the old fill did (checked against the old renderer for both images). The
// transparent pad is what naming clearColor still produces. If a later change alters either, the
// decode line below flips and the pad sample stops being transparent, so regenerating says so.
//
// Output goes to docs/images/ rather than samples/Dotfiles/output/ because these are documentation
// assets that ship with the guide; samples/Dotfiles/output/ is gitignored.
//
// Run from the repository root:
//   dotnet run "samples/Dotfiles/MigrationImages_1.x-2.0.cs"

var outputDirectory = Path.Combine(Environment.CurrentDirectory, "docs", "images");
Directory.CreateDirectory(outputDirectory);

const string Content = "https://example.com";
var data = QRCodeGenerator.Create(Content, QREccLevel.M);

// --- WithSize(900, 450) --------------------------------------------------------------------

// Before: filled across both axes, so the modules are twice as wide as they are tall.
var stretchedPath = Path.Combine(outputDirectory, "withsize-stretched.png");
using (var bitmap = new SKBitmap(900, 450))
{
    using (var canvas = new SKCanvas(bitmap))
    {
        canvas.Scale(2f, 1f);
        canvas.Render(data, 450, 450, SKColors.White, SKColors.Black, SKColors.White);
    }

    Save(bitmap, stretchedPath);
    Report(stretchedPath, bitmap, featherQR: false, zxingNet: false, zxingCpp: true);
}

// After: fitted at one uniform module scale and centered.
var fittedPath = Path.Combine(outputDirectory, "withsize-fitted.png");
using (var bitmap = new QRCodeImageBuilder(data).WithSize(900, 450).WithColors(SKColors.Black, SKColors.White).ToBitmap())
{
    Save(bitmap, fittedPath);
    Report(fittedPath, bitmap, featherQR: true, zxingNet: true, zxingCpp: true);
}

// --- SymbolRenderer.Render into a non-square slot ------------------------------------------

// A 200x300 slot for the code on a card, which is what the low-level renderer is for. The card's
// own colour around the white slot shows where the area ends.
var slot = SKRect.Create(40, 40, 200, 300);

// Before: the slot was filled on both axes, so the modules are half again as tall as they are wide.
var rendererStretchedPath = Path.Combine(outputDirectory, "renderer-stretched.png");
using (var bitmap = Card(280, 380, canvas =>
{
    canvas.Translate(slot.Left, slot.Top);
    canvas.Scale(1f, slot.Height / slot.Width);
    SymbolRenderer.Render(canvas, SKRect.Create(0, 0, slot.Width, slot.Width), data, SKColors.Black, SKColors.White);
}))
{
    Save(bitmap, rendererStretchedPath);
    Report(rendererStretchedPath, bitmap, featherQR: false, zxingNet: true, zxingCpp: true);
}

// After: the same call, given the slot, centers a square symbol and gives the rest of it the background.
var rendererFittedPath = Path.Combine(outputDirectory, "renderer-fitted.png");
using (var bitmap = Card(280, 380, canvas => SymbolRenderer.Render(canvas, slot, data, SKColors.Black, SKColors.White)))
{
    Save(bitmap, rendererFittedPath);
    Report(rendererFittedPath, bitmap, featherQR: true, zxingNet: true, zxingCpp: true);
}

// --- Padding -------------------------------------------------------------------------------

// JPEG carries no alpha, so it is the format that shows what a transparent pad costs: the
// padding 1.x left behind is written out black.
var transparentPadPath = Path.Combine(outputDirectory, "padding-transparent.jpg");
File.WriteAllBytes(transparentPadPath, new QRCodeImageBuilder(data)
    .WithModulePixelSize(8)
    .WithSize(400, 400)
    .WithColors(SKColors.Black, SKColors.White).WithClearColor(SKColors.Transparent)
    .WithFormat(SKEncodedImageFormat.Jpeg, 90)
    .ToByteArray());
ReportPad(transparentPadPath, expectTransparent: true);

var backgroundPadPath = Path.Combine(outputDirectory, "padding-background.jpg");
File.WriteAllBytes(backgroundPadPath, new QRCodeImageBuilder(data)
    .WithModulePixelSize(8)
    .WithSize(400, 400)
    .WithColors(SKColors.Black, SKColors.White)
    .WithFormat(SKEncodedImageFormat.Jpeg, 90)
    .ToByteArray());
ReportPad(backgroundPadPath, expectTransparent: false);

// Asking for transparency by name, as a PNG that keeps it. A transparent pad looks exactly like a
// white one on a white page, so what the guide embeds is that PNG over a checkerboard, the way an
// image editor shows alpha. The alpha is asserted on the real output before it is composited.
var clearColorPath = Path.Combine(outputDirectory, "padding-clearcolor-transparent.png");
{
    var png = new QRCodeImageBuilder(data)
        .WithModulePixelSize(8)
        .WithSize(400, 400)
        .WithBackgroundColor(SKColors.White).WithClearColor(SKColors.Transparent)
        .ToByteArray();

    using var symbol = SKBitmap.Decode(png);
    var pad = symbol.GetPixel(2, 2);
    Console.WriteLine($"{(pad.Alpha == 0 ? "ok  " : "WRONG")} {Path.GetFileName(clearColorPath)}: pad alpha={pad.Alpha}, expected 0");

    using var surface = SKSurface.Create(new SKImageInfo(symbol.Width, symbol.Height));
    DrawCheckerboard(surface.Canvas, symbol.Width, symbol.Height);
    surface.Canvas.DrawBitmap(symbol, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), null);

    using var image = surface.Snapshot();
    using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
    using var stream = File.Create(clearColorPath);
    encoded.SaveTo(stream);
}

Console.WriteLine();
Console.WriteLine($"Saved to: {outputDirectory}");

// The conventional "this is transparent" backdrop: 16px squares in two greys.
static void DrawCheckerboard(SKCanvas canvas, int width, int height)
{
    const int Square = 16;
    using var light = new SKPaint { Color = new SKColor(0xFF, 0xFF, 0xFF), IsAntialias = false };
    using var dark = new SKPaint { Color = new SKColor(0xCC, 0xCC, 0xCC), IsAntialias = false };
    for (var y = 0; y < height; y += Square)
    {
        for (var x = 0; x < width; x += Square)
        {
            var even = (x / Square + y / Square) % 2 == 0;
            canvas.DrawRect(SKRect.Create(x, y, Square, Square), even ? light : dark);
        }
    }
}

// A plain card in a warm colour, with the code drawn where the caller says.
static SKBitmap Card(int width, int height, Action<SKCanvas> drawCode)
{
    var bitmap = new SKBitmap(width, height);
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(new SKColor(0xF2, 0xEE, 0xE6));
    canvas.Save();
    drawCode(canvas);
    canvas.Restore();
    return bitmap;
}

static void Save(SKBitmap bitmap, string path)
{
    using var image = SKImage.FromBitmap(bitmap);
    using var png = image.Encode(SKEncodedImageFormat.Png, 100);
    using var stream = File.Create(path);
    png.SaveTo(stream);
}

// The claim each image makes in the guide is which readers find the symbol, so check each one it names.
// ZXing.Net runs with the settings the test suite's cross-checks use; zxing-cpp with its defaults.
static void Report(string path, SKBitmap bitmap, bool featherQR, bool zxingNet, bool zxingCpp)
{
    var feather = QRCodeImageDecoder.TryDecode(bitmap, out var text, out _) && text == Content;

    var netReader = new ZXing.SkiaSharp.BarcodeReader
    {
        AutoRotate = true,
        Options = new ZXing.Common.DecodingOptions { TryHarder = true, TryInverted = true, PossibleFormats = [ZXing.BarcodeFormat.QR_CODE] },
    };
    var net = netReader.Decode(bitmap)?.Text == Content;

    using var rgba = bitmap.Copy(SKColorType.Rgba8888);
    var view = new ZXingCpp.ImageView(rgba.Bytes, rgba.Width, rgba.Height, ZXingCpp.ImageFormat.RGBA);
    var cpp = new ZXingCpp.BarcodeReader().From(view).Any(r => r.Text == Content);

    var agrees = feather == featherQR && net == zxingNet && cpp == zxingCpp;
    Console.WriteLine($"{(agrees ? "ok  " : "WRONG")} {Path.GetFileName(path)}: FeatherQR={feather}, ZXing.Net={net}, zxing-cpp={cpp}; expected {featherQR}/{zxingNet}/{zxingCpp}");
}

// The pad claim is the pixel in the corner: black once JPEG has flattened a transparent one.
static void ReportPad(string path, bool expectTransparent)
{
    using var bitmap = SKBitmap.Decode(path);
    var pad = bitmap.GetPixel(2, 2);
    var isBlack = pad.Red < 32 && pad.Green < 32 && pad.Blue < 32;
    var agrees = isBlack == expectTransparent;
    Console.WriteLine($"{(agrees ? "ok  " : "WRONG")} {Path.GetFileName(path)}: pad={pad}, expected {(expectTransparent ? "black (flattened transparency)" : "the background")}");
}
