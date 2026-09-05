#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:project ../../src/SkiaSharp.QrCode/SkiaSharp.QrCode.csproj
using SkiaSharp;
using SkiaSharp.QrCode;
using SkiaSharp.QrCode.Image;

// fix confirmation for: https://github.com/guitarrapc/SkiaSharp.QrCode/issues/299

var backgroundColor = new SKColor(0xEA, 0xFB, 0x00, 0xFF);
var codeColor = SKColors.Green;
var qr = QRCodeGenerator.Create("finder-shape-background-test", QREccLevel.M);

var imageSize = qr.Size * 10;
var area = SKRect.Create(0, 0, imageSize, imageSize);
var outputDirectory = Path.Combine(Environment.CurrentDirectory, "samples", "Dotfiles", "output", "finder-shapes");
Directory.CreateDirectory(outputDirectory);

var finderPatternShapes = new FinderPatternShape[]
{
    RectangleFinderPatternShape.Default,
    CircleFinderPatternShape.Default,
    RoundedRectangleFinderPatternShape.Default,
    RoundedRectangleCircleFinderPatternShape.Default,
};

foreach (var finderPatternShape in finderPatternShapes)
{
    using var bitmap = new SKBitmap(imageSize, imageSize);
    using var canvas = new SKCanvas(bitmap);

    SymbolRenderer.Render(canvas, area, qr, codeColor, backgroundColor, finderPatternShape: finderPatternShape);

    var finderRect = SymbolRenderer.GetFinderPatternRect(qr, 0, area);
    var moduleSize = finderRect.Width / 7f;
    var ringSampleX = (int)MathF.Round(finderRect.Left + moduleSize * 1.5f);
    var ringSampleY = (int)MathF.Round(finderRect.Top + moduleSize * 3.5f);
    var centerSampleX = (int)MathF.Round(finderRect.Left + moduleSize * 3.5f);
    var centerSampleY = (int)MathF.Round(finderRect.Top + moduleSize * 3.5f);

    var ringPixel = bitmap.GetPixel(ringSampleX, ringSampleY);
    var centerPixel = bitmap.GetPixel(centerSampleX, centerSampleY);

    var outputPath = Path.Combine(outputDirectory, $"{finderPatternShape.GetType().Name}.png");
    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var stream = File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
    data.SaveTo(stream);

    Console.WriteLine($"{finderPatternShape.GetType().Name}: ring={ringPixel}, center={centerPixel}");
    Console.WriteLine($"saved: {outputPath}");
}
