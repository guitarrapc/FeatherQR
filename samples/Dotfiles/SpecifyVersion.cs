#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:project ../../src/FeatherQR.SkiaSharp/FeatherQR.SkiaSharp.csproj
using SkiaSharp;
using FeatherQR;
using FeatherQR.SkiaSharp;

// fix confirmation for: https://github.com/guitarrapc/SkiaSharp.QrCode/issues/296 & https://github.com/guitarrapc/SkiaSharp.QrCode/issues/292

var content = "abc";
var iconPath = "samples/ConsoleApp/samples/insta.png";
var outputDirectory = Path.Combine(Environment.CurrentDirectory, "samples", "Dotfiles", "output", "specify-version");
var autoOutputPath = Path.Combine(outputDirectory, "auto-version.png");
var fixedOutputPath = Path.Combine(outputDirectory, "version-10.png");
Directory.CreateDirectory(outputDirectory);

// For this example, we'll use the test icon
using var logo = SKBitmap.Decode(File.ReadAllBytes(iconPath));
var icon = IconData.FromImage(logo, iconSizePercent: 14, iconBorderWidth: 1);

var autoVersionBuilder = new QRCodeImageBuilder(content)
    .WithSize(1024, 1024)
    .WithErrorCorrection(QREccLevel.H)
    .WithQuietZone(4)
    .WithColors(
        codeColor: SKColor.Parse("ff6000"),
        backgroundColor: SKColors.White)
    .WithClearColor(SKColors.White)
    .WithModuleShape(CircleModuleShape.Default, sizePercent: 1.0f)
    .WithFinderPatternShape(RoundedRectangleCircleFinderPatternShape.Default)
    .WithIcon(icon);

var fixedVersionBuilder = new QRCodeImageBuilder(content)
    .WithSize(1024, 1024)
    .WithErrorCorrection(QREccLevel.H)
    .WithVersion(10)
    .WithQuietZone(4)
    .WithColors(
        codeColor: SKColor.Parse("ff6000"),
        backgroundColor: SKColors.White)
    .WithClearColor(SKColors.White)
    .WithModuleShape(CircleModuleShape.Default, sizePercent: 1.0f)
    .WithFinderPatternShape(RoundedRectangleCircleFinderPatternShape.Default)
    .WithIcon(icon);

var autoPngBytes = autoVersionBuilder.ToByteArray();
var fixedPngBytes = fixedVersionBuilder.ToByteArray();

File.WriteAllBytes(autoOutputPath, autoPngBytes);
File.WriteAllBytes(fixedOutputPath, fixedPngBytes);

var autoData = QRCodeGenerator.Create(content, QREccLevel.H);
var version10Data = QRCodeGenerator.Create(content, QREccLevel.H, new QRCodeGeneratorOptions { Version = 10 });
var autoCoreModules = autoData.Size - 8; // quiet zone = 4 on each side
var version10CoreModules = version10Data.Size - 8; // quiet zone = 4 on each side

Console.WriteLine($"  ✓ Auto version: v{autoData.Version} ({autoCoreModules}x{autoCoreModules} modules)");
Console.WriteLine($"  ✓ Fixed version: v{version10Data.Version} ({version10CoreModules}x{version10CoreModules} modules)");
Console.WriteLine($"  ✓ Saved to: {autoOutputPath}");
Console.WriteLine($"  ✓ Saved to: {fixedOutputPath}");
