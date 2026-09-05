#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:project ../../src/SkiaSharp.QrCode/SkiaSharp.QrCode.csproj
using SkiaSharp;
using SkiaSharp.QrCode;
using SkiaSharp.QrCode.Image;

// Verification sample for WithModulePixelSize (+ optional WithSize canvas pad):
// - Fixed image size can produce fractional module pixels.
// - Fixed module pixel size keeps each module aligned to exact pixels across versions.
// - Module + larger WithSize centers content and pads with clearColor.
// - Module-based icon sizing keeps logo body/border aligned to the same grid.

const string content = "HELLO-294";
const int fixedImageSize = 512;
const int modulePixelSize = 12;
const int paddedCanvasSize = 512;
var outputDirectory = Path.Combine(Environment.CurrentDirectory, "samples", "Dotfiles", "output", "module-pixel-size");
Directory.CreateDirectory(outputDirectory);
using var logo = SKBitmap.Decode(File.ReadAllBytes("samples/ConsoleApp/samples/instarich-logo.png"));
var icon = IconData.FromImageByModules(logo, iconSizeModules: 5, iconBorderModules: 1, maxCoreOccupancyPercent: 40);

var scenarios = new (int Version, int QuietZone)[]
{
    (1, 4),
    (10, 4),
    (20, 4),
};

foreach (var (version, quietZone) in scenarios)
{
    var qrData = QRCodeGenerator.Create(content, QREccLevel.H, requestedVersion: version, quietZoneSize: quietZone);
    var contentSide = qrData.Size * modulePixelSize;

    using var fixedSizeBitmap = new QRCodeImageBuilder(qrData)
        .WithSize(fixedImageSize, fixedImageSize)
        .WithColors(SKColors.Black, SKColors.White)
        .WithModuleShape(CircleModuleShape.Default, 0.92f)
        .WithFinderPatternShape(RoundedRectangleCircleFinderPatternShape.Default)
        .WithIcon(icon)
        .ToBitmap();

    using var modulePixelBitmap = new QRCodeImageBuilder(qrData)
        .WithModulePixelSize(modulePixelSize)
        .WithColors(SKColors.Black, SKColors.White)
        .WithModuleShape(CircleModuleShape.Default, 0.92f)
        .WithFinderPatternShape(RoundedRectangleCircleFinderPatternShape.Default)
        .WithIcon(icon)
        .ToBitmap();

    SaveBitmap(fixedSizeBitmap, Path.Combine(outputDirectory, $"v{version}_fixed-size_{fixedImageSize}.png"));
    SaveBitmap(modulePixelBitmap, Path.Combine(outputDirectory, $"v{version}_module-pixel-{modulePixelSize}.png"));

    Console.WriteLine($"Version v{version} (matrix size: {qrData.Size} modules)");
    Console.WriteLine($"  fixed-size         : {fixedImageSize}x{fixedImageSize}, effective module px = {(float)fixedImageSize / qrData.Size:F4}");
    Console.WriteLine($"  with-module-pixel  : {contentSide}x{contentSide}, module px = {modulePixelSize}");

    if (paddedCanvasSize >= contentSide)
    {
        using var paddedBitmap = new QRCodeImageBuilder(qrData)
            .WithModulePixelSize(modulePixelSize)
            .WithSize(paddedCanvasSize, paddedCanvasSize)
            .WithColors(SKColors.Black, SKColors.White, SKColors.Transparent)
            .WithModuleShape(CircleModuleShape.Default, 0.92f)
            .WithFinderPatternShape(RoundedRectangleCircleFinderPatternShape.Default)
            .WithIcon(icon)
            .ToBitmap();

        SaveBitmap(paddedBitmap, Path.Combine(outputDirectory, $"v{version}_module-pixel-{modulePixelSize}_pad-{paddedCanvasSize}.png"));
        Console.WriteLine($"  module+canvas-pad  : {paddedCanvasSize}x{paddedCanvasSize}, content = {contentSide}x{contentSide}");
    }
    else
    {
        Console.WriteLine($"  module+canvas-pad  : skipped ({paddedCanvasSize} < content {contentSide})");
    }

    var iconTotalModules = (icon.IconSizeModules ?? 0) + ((icon.IconBorderModules ?? 1) * 2);
    Console.WriteLine($"  icon               : body={icon.IconSizeModules} modules, border={icon.IconBorderModules} modules, total={iconTotalModules} modules");
    Console.WriteLine();
}

Console.WriteLine($"Saved comparison images to: {outputDirectory}");

static void SaveBitmap(SKBitmap bitmap, string outputPath)
{
    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var stream = File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
    data.SaveTo(stream);
}
