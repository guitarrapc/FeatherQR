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
// - Canvas the symbol does not reach takes the background rather than staying transparent.
//
// The "before" images are the real old output, not an imitation. Nothing in the library stretches,
// so the stretch is asked for with a canvas scale over a square area, which gives the same pixels
// the old fill did. The transparent pad is what naming clearColor produces. If a later change
// alters either, the decode line below flips or the pad stops being black, so regenerating says so.
//
// Output goes to docs/images/ rather than samples/Dotfiles/output/ because these are documentation
// assets that ship with the guide. samples/Dotfiles/output/ is gitignored.
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
    Report(stretchedPath, bitmap, featherQR: true, zxingNet: false, zxingCpp: true);
}

// After: fitted with equal whole-pixel modules and centered.
var fittedPath = Path.Combine(outputDirectory, "withsize-fitted.png");
using (var bitmap = new QRCodeImageBuilder(data).WithSize(900, 450).WithColors(SKColors.Black, SKColors.White).ToBitmap())
{
    Save(bitmap, fittedPath);
    Report(fittedPath, bitmap, featherQR: true, zxingNet: true, zxingCpp: true);
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

Console.WriteLine();
Console.WriteLine($"Saved to: {outputDirectory}");

static void Save(SKBitmap bitmap, string path)
{
    using var image = SKImage.FromBitmap(bitmap);
    using var png = image.Encode(SKEncodedImageFormat.Png, 100);
    using var stream = File.Create(path);
    png.SaveTo(stream);
}

// The claim each image makes in the guide is which readers find the symbol, so check each one it names.
// ZXing.Net runs with the settings the test suite's cross-checks use, zxing-cpp with its defaults.
static void Report(string path, SKBitmap bitmap, bool featherQR, bool zxingNet, bool zxingCpp)
{
    var feather = QRCodeDecoder.TryDecode(bitmap, out var text, out _) && text == Content;

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
