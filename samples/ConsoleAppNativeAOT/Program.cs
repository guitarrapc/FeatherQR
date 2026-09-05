using SkiaSharp;
using FeatherQR;
using FeatherQR.SkiaSharp;

var content = "https://github.com/guitarrapc/FeatherQR/blob/main/README.md?foo=sample&bar=dummy";
var outputDir = "bin/output";

// prepare
Directory.CreateDirectory(outputDir);

var path = Path.Combine(outputDir, "instagram_frame.png");

// Create larger canvas for frame
var canvasSize = 1200;
var qrSize = 900;
var topPadding = 80;
var sidePadding = (canvasSize - qrSize) / 2;
var textY = topPadding + qrSize + 50;

var info = new SKImageInfo(canvasSize, canvasSize);
using var surface = SKSurface.Create(info);
var canvas = surface.Canvas;

// Draw Background
{
    canvas.Clear(SKColors.White);

    var r = 100; // corner radius

    // Draw gray border (outer rounded rectangle)
    using (var borderPaint = new SKPaint
    {
        Color = SKColor.Parse("E0E0E0"), // Light gray border
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 3,
        IsAntialias = false
    })
    {
        var borderRect = new SKRoundRect(SKRect.Create(30, 30, canvasSize - 60, canvasSize - 60), r, r);
        canvas.DrawRoundRect(borderRect, borderPaint);
    }

    // Draw rounded rectangle background
    using (var bgPaint = new SKPaint
    {
        Color = SKColors.White,
        Style = SKPaintStyle.Fill,
        IsAntialias = false
    })
    {
        var bgRect = new SKRoundRect(SKRect.Create(30, 30, canvasSize - 60, canvasSize - 60), r, r);
        canvas.DrawRoundRect(bgRect, bgPaint);
    }
}

// Generate QR data
{
    var qrData = QRCodeGenerator.CreateQrCode(content, ECCLevel.H, new QRCodeGeneratorOptions { QuietZoneSize = 2 });

    // Instagram gradient
    var instagramGradient = new GradientOptions(
        [
            SKColor.Parse("FCAF45"),
                SKColor.Parse("F77737"),
                SKColor.Parse("E1306C"),
                SKColor.Parse("C13584"),
                SKColor.Parse("833AB4")
        ],
        GradientDirection.BottomLeftToTopRight,
        [0f, 0.25f, 0.5f, 0.75f, 1f]);

    // Render QR code
    var qrRect = SKRect.Create(sidePadding, topPadding, qrSize, qrSize);
    QRCodeRenderer.Render(
        canvas,
        qrRect,
        qrData,
        codeColor: null,
        backgroundColor: SKColors.White,
        moduleShape: CircleModuleShape.Default,
        moduleSizePercent: 0.95f,
        gradientOptions: instagramGradient);
}

// Draw bottom text
{
    using (var titleFont = new SKFont
    {
        Size = 48,
        Typeface = SKTypeface.FromFamilyName("sans-serif", SKFontStyle.Normal),
    })
    using (var appFont = new SKFont
    {
        Size = 36,
        Typeface = SKTypeface.FromFamilyName("sans-serif", SKFontStyle.Normal),
    })
    using (var paint = new SKPaint
    {
        Color = SKColors.DarkOrange,
        IsAntialias = false
    })
    {
        var bottomText = "POST SHARED ON " + DateTime.Now.ToString("MMM dd").ToUpper();
        var textWidth = titleFont.MeasureText(bottomText);
        canvas.DrawText(bottomText,
            (canvasSize - textWidth) / 2,
            textY,
            SKTextAlign.Left,
            titleFont,
            paint);

        var usernameText = "BY SKIASHARP.QRCODE";
        var usernameWidth = appFont.MeasureText(usernameText);
        canvas.DrawText(usernameText,
            (canvasSize - usernameWidth) / 2,
            textY + 60,
            SKTextAlign.Left,
            appFont,
            paint);
    }
}

// Save
using var image = surface.Snapshot();
using var data = image.Encode(SKEncodedImageFormat.Png, 100);
using var stream = File.OpenWrite(path);
data.SaveTo(stream);
