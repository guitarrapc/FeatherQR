#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property ManagePackageVersionsCentrally=false
#:package SkiaSharp.QrCode@1.0.0
// Pinned to the released 1.0.0 package on purpose, so every identifier below is that package's
// API, not this repository's. A rename sweep over samples/ has to skip this file.
using SkiaSharp;
using SkiaSharp.QrCode;
using SkiaSharp.QrCode.Image;

// Repro for: https://github.com/guitarrapc/SkiaSharp.QrCode/issues/337
// Maui sample: https://github.com/geertgeerits/MauiSkiaSharpQrCode

var backgroundColor = SKColors.Yellow;
var outputDirectory = Path.Combine(Environment.CurrentDirectory, "samples", "Dotfiles", "output", "finder-pattern-always-black");
Directory.CreateDirectory(outputDirectory);

var scenarios = new (string Name, string Text, Func<string, byte[]> Generate)[]
{
    ("Test1-RoundedRectangleFinderPatternShape", "Test 1", GenerateArtQrCodeAsync1),
    ("Test2-CircleFinderPatternShape", "Test 2", GenerateArtQrCodeAsync2),
    ("Test3-RectangleFinderPatternShape", "Test 3", GenerateArtQrCodeAsync3),
    ("Test4-DefaultFinder", "Test 4", GenerateArtQrCodeAsync4),
};

foreach (var (name, text, generate) in scenarios)
{
    var pngBytes = generate(text);

    var outputPath = Path.Combine(outputDirectory, $"{name}.png");
    await File.WriteAllBytesAsync(outputPath, pngBytes);

    using var bitmap = SKBitmap.Decode(pngBytes);
    var qr = QRCodeGenerator.CreateQrCode(text, ECCLevel.H);
    var contentRect = SKRect.Create(0, 0, bitmap.Width, bitmap.Height);
    var finderRect = QRCodeRenderer.GetFinderPatternRect(qr, 0, contentRect);
    var moduleSize = finderRect.Width / 7f;
    var ringSampleX = (int)MathF.Round(finderRect.Left + moduleSize * 1.5f);
    var ringSampleY = (int)MathF.Round(finderRect.Top + moduleSize * 3.5f);
    var centerSampleX = (int)MathF.Round(finderRect.Left + moduleSize * 3.5f);
    var centerSampleY = (int)MathF.Round(finderRect.Top + moduleSize * 3.5f);

    var ringPixel = bitmap.GetPixel(ringSampleX, ringSampleY);
    var centerPixel = bitmap.GetPixel(centerSampleX, centerSampleY);
    var ringOk = ringPixel == backgroundColor;

    Console.WriteLine($"{name}: ring={ringPixel} (ok={ringOk}), center={centerPixel}");
    Console.WriteLine($"saved: {outputPath}");
}

// RoundedRectangleFinderPatternShape
static byte[] GenerateArtQrCodeAsync1(string text)
{
    var gradient = new GradientOptions(
        [SKColors.Blue, SKColors.Purple, SKColors.Pink],
        GradientDirection.TopLeftToBottomRight,
        [0f, 0.5f, 1f]);

    var qrCode = new QRCodeImageBuilder(text)
        .WithSize(800, 800)
        .WithErrorCorrection(ECCLevel.H)
        .WithColors(codeColor: SKColors.Black, backgroundColor: SKColors.Yellow, clearColor: SKColors.Transparent)
        .WithGradient(gradient)
        .WithFinderPatternShape(RoundedRectangleFinderPatternShape.Default)
        .WithModuleShape(RoundedRectangleModuleShape.Default, sizePercent: 0.9f);

    return qrCode.ToByteArray();
}

// CircleFinderPatternShape
static byte[] GenerateArtQrCodeAsync2(string text)
{
    var gradient = new GradientOptions(
        [SKColors.Blue, SKColors.Purple, SKColors.Pink],
        GradientDirection.TopLeftToBottomRight,
        [0f, 0.5f, 1f]);

    var qrCode = new QRCodeImageBuilder(text)
        .WithSize(800, 800)
        .WithErrorCorrection(ECCLevel.H)
        .WithColors(codeColor: SKColors.Black, backgroundColor: SKColors.Yellow, clearColor: SKColors.Transparent)
        .WithGradient(gradient)
        .WithFinderPatternShape(CircleFinderPatternShape.Default)
        .WithModuleShape(CircleModuleShape.Default, sizePercent: 0.9f);

    return qrCode.ToByteArray();
}

// RectangleFinderPatternShape
static byte[] GenerateArtQrCodeAsync3(string text)
{
    var gradient = new GradientOptions(
        [SKColors.Blue, SKColors.Purple, SKColors.Pink],
        GradientDirection.TopLeftToBottomRight,
        [0f, 0.5f, 1f]);

    var qrCode = new QRCodeImageBuilder(text)
        .WithSize(800, 800)
        .WithErrorCorrection(ECCLevel.H)
        .WithColors(codeColor: SKColors.Black, backgroundColor: SKColors.Yellow, clearColor: SKColors.Transparent)
        .WithGradient(gradient)
        .WithFinderPatternShape(RectangleFinderPatternShape.Default)
        .WithModuleShape(RectangleModuleShape.Default, sizePercent: 0.9f);

    return qrCode.ToByteArray();
}

// Background color is correct when not using the 'WithFinderPatternShape'
static byte[] GenerateArtQrCodeAsync4(string text)
{
    var gradient = new GradientOptions(
        [SKColors.Blue, SKColors.Purple, SKColors.Pink],
        GradientDirection.TopLeftToBottomRight,
        [0f, 0.5f, 1f]);

    var qrCode = new QRCodeImageBuilder(text)
        .WithSize(800, 800)
        .WithErrorCorrection(ECCLevel.H)
        .WithColors(codeColor: SKColors.Black, backgroundColor: SKColors.Yellow, clearColor: SKColors.Transparent)
        .WithGradient(gradient)
        .WithModuleShape(RectangleModuleShape.Default, sizePercent: 0.9f);

    return qrCode.ToByteArray();
}
