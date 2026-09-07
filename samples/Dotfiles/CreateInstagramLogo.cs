#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:project ../../src/FeatherQR.SkiaSharp/FeatherQR.SkiaSharp.csproj
using SkiaSharp;

// Generate a square Instagram-style logo with Skia.
// Use this instead of samples/ConsoleApp/samples/insta.png (133x135, slightly non-square).

const int size = 128;
var outputDirectory = Path.Combine(Environment.CurrentDirectory, "samples", "Dotfiles", "output", "logos");
Directory.CreateDirectory(outputDirectory);
var outputPath = Path.Combine(outputDirectory, "insta-logo.png");

using var logo = CreateInstagramLogo(size);
SaveBitmap(logo, outputPath);

Console.WriteLine($"Created square Instagram logo: {logo.Width}x{logo.Height}");
Console.WriteLine($"Saved: {outputPath}");

static SKBitmap CreateInstagramLogo(int size)
{
    var bitmap = new SKBitmap(size, size);
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(SKColors.Transparent);

    // Instagram gradient: orange -> pink -> purple
    var colors = new[]
    {
        SKColor.Parse("FCAF45"),
        SKColor.Parse("F77737"),
        SKColor.Parse("E1306C"),
        SKColor.Parse("C13584"),
        SKColor.Parse("833AB4"),
    };
    var positions = new[] { 0f, 0.25f, 0.5f, 0.75f, 1f };

    using var shader = SKShader.CreateLinearGradient(
        new SKPoint(0, 0),
        new SKPoint(size, size),
        colors,
        positions,
        SKShaderTileMode.Clamp);

    // Rounded square background
    var corner = size * 0.22f;
    var bgRect = SKRect.Create(0, 0, size, size);
    using (var bgPaint = new SKPaint { IsAntialias = true, Shader = shader, Style = SKPaintStyle.Fill })
    {
        canvas.DrawRoundRect(bgRect, corner, corner, bgPaint);
    }

    // Camera body (rounded square outline)
    var inset = size * 0.22f;
    var cameraRect = SKRect.Create(inset, inset, size - inset * 2, size - inset * 2);
    var cameraCorner = size * 0.12f;
    var stroke = size * 0.055f;

    using (var strokePaint = new SKPaint
    {
        IsAntialias = true,
        Color = SKColors.White,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = stroke,
        StrokeJoin = SKStrokeJoin.Round,
    })
    {
        canvas.DrawRoundRect(cameraRect, cameraCorner, cameraCorner, strokePaint);

        // Lens
        var lensRadius = size * 0.145f;
        canvas.DrawCircle(size * 0.5f, size * 0.5f, lensRadius, strokePaint);

        // Viewfinder / flash dot (top-right inside camera body)
        var dotRadius = size * 0.035f;
        var dotX = size * 0.68f;
        var dotY = size * 0.32f;
        using var fillPaint = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.White,
            Style = SKPaintStyle.Fill,
        };
        canvas.DrawCircle(dotX, dotY, dotRadius, fillPaint);
    }

    return bitmap;
}

static void SaveBitmap(SKBitmap bitmap, string path)
{
    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var stream = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
    data.SaveTo(stream);
}
