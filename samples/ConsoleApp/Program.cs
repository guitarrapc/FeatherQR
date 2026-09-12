using SkiaSharp;
using FeatherQR;
using FeatherQR.SkiaSharp;

Console.OutputEncoding = System.Text.Encoding.UTF8;

static string ParseOutputDir(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--output-dir")
        {
            if (i + 1 >= args.Length)
                throw new ArgumentException("--output-dir requires a path");
            return Path.GetFullPath(args[i + 1]);
        }

        const string prefix = "--output-dir=";
        if (args[i].StartsWith(prefix, StringComparison.Ordinal))
            return Path.GetFullPath(args[i][prefix.Length..]);
    }

    // Default: ./samples relative to cwd (gallery when cwd is the project directory).
    return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "samples"));
}

var outputDir = ParseOutputDir(args);
var iconPath = Path.Combine(outputDir, "test.png");
// Square Instagram logo (128x128). Prefer over samples/insta.png (133x135, non-square).
var iconInstaPath = Path.Combine(outputDir, "instarich-logo.png");

var content = "https://github.com/guitarrapc/FeatherQR/blob/main/README.md";

// prepare
Directory.CreateDirectory(outputDir);

// Static method (Simplest)
Console.WriteLine("""
    Pattern 1: Static Method (Simplest)
      - Best for: Quick QR code generation with default settings
      - API: QRCodeImageBuilder.GetPngBytes()
    """);
{
    var path = Path.Combine(outputDir, "pattern1_static_method.png");

    // One-liner: Generate PNG bytes with default settings
    var pngBytes = QRCodeImageBuilder.GetPngBytes(content);
    File.WriteAllBytes(path, pngBytes);

    Console.WriteLine($"  ✓ Saved to: {path}");
}
Console.WriteLine();

// Static Method with Stream
Console.WriteLine("""
    Pattern 2: Static Method with Stream
      - Best for: Direct file output without byte array allocation
      - API: QRCodeImageBuilder.SavePng()
    """);
{
    var path = Path.Combine(outputDir, "pattern2_static_stream.png");

    using var stream = File.OpenWrite(path);
    QRCodeImageBuilder.SavePng(content, stream, QREccLevel.H, size: 512);

    Console.WriteLine($"  ✓ Saved to: {path}");
}
Console.WriteLine();

// Builder Pattern (Basic)
Console.WriteLine("""
    Pattern 3: Builder Pattern (Basic)
      - Best for: Customizing size, format, error correction
      - API: new QRCodeImageBuilder().WithXxx().ToByteArray()
    """);
{
    var path = Path.Combine(outputDir, "pattern3_builder_basic.png");

    var qrBuilder = new QRCodeImageBuilder(content)
        .WithSize(1024, 1024)
        .WithErrorCorrection(QREccLevel.H)
        .WithQuietZone(2);

    var pngBytes = qrBuilder.ToByteArray();
    File.WriteAllBytes(path, pngBytes);

    Console.WriteLine($"  ✓ Saved to: {path}");
}
Console.WriteLine();

// Module Pixel Size (sharp module edges)
Console.WriteLine("""
    Pattern 3b: WithModulePixelSize
      - Best for: Sharp module edges / logo alignment (integer pixels per module)
      - API: new QRCodeImageBuilder().WithModulePixelSize()
      - Output side = QR matrix size * modulePixelSize
    """);
{
    var path = Path.Combine(outputDir, "pattern3b_module_pixel_size.png");

    var pngBytes = new QRCodeImageBuilder(content)
        .WithModulePixelSize(10)
        .WithErrorCorrection(QREccLevel.H)
        .WithQuietZone(4)
        .ToByteArray();

    File.WriteAllBytes(path, pngBytes);

    Console.WriteLine($"  ✓ Saved to: {path}");
}
Console.WriteLine();

// Module Pixel Size + fixed canvas padding
Console.WriteLine("""
    Pattern 3c: WithModulePixelSize + WithSize (centered padding)
      - Best for: Module-aligned content inside a fixed UI frame
      - API: WithModulePixelSize(n) + WithSize(w, h)  (canvas must be >= content)
      - Extra space is centered padding: clearColor when set, the background colour otherwise
    """);
{
    var path = Path.Combine(outputDir, "pattern3c_module_pixel_size_padded.png");

    // Content is module-aligned; larger canvas pads with a visible clearColor.
    var pngBytes = new QRCodeImageBuilder(content)
        .WithModulePixelSize(8)
        .WithSize(512, 512)
        .WithClearColor(SKColor.Parse("E8EEF5"))
        .WithErrorCorrection(QREccLevel.H)
        .WithQuietZone(4)
        .ToByteArray();

    File.WriteAllBytes(path, pngBytes);

    Console.WriteLine($"  ✓ Saved to: {path}");
}
Console.WriteLine();

// Builder Pattern (Advanced - Custom Colors)
Console.WriteLine("""
    Pattern 4: Builder Pattern (Advanced - Custom Colors
      - Best for: Custom colors and styling
      - API: WithColors(codeColor, backgroundColor) + WithClearColor()
    """);
{
    var path = Path.Combine(outputDir, "pattern4_builder_colors.png");
    using var stream = File.OpenWrite(path);

    new QRCodeImageBuilder(content)
        .WithSize(800, 800)
        .WithErrorCorrection(QREccLevel.H)
        .WithColors(
            codeColor: SKColor.Parse("000080"),       // Navy
            backgroundColor: SKColor.Parse("FFE4B5")) // Moccasin
        .WithClearColor(SKColors.Transparent)
        .SaveTo(stream);

    Console.WriteLine($"  ✓ Saved to: {path}");
}
Console.WriteLine();

// Builder Pattern (Advanced - Module Shape)
Console.WriteLine("""
    Pattern 5: Builder Pattern (Advanced - Module Shape)
      - Best for: Custom module shapes (circles, rounded rectangles)
      - API: new QRCodeImageBuilder().WithModuleShape()
    """);
{
    var path = Path.Combine(outputDir, "pattern5_builder_shape.png");

    var qrBuilder = new QRCodeImageBuilder(content)
        .WithSize(800, 800)
        .WithErrorCorrection(QREccLevel.H)
        .WithModuleShape(CircleModuleShape.Default, sizePercent: 0.95f)
        .WithCodeColor(SKColors.DarkBlue);

    using var image = qrBuilder.ToImage();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var stream = File.OpenWrite(path);
    data.SaveTo(stream);

    Console.WriteLine($"  ✓ Saved to: {path}");
}
Console.WriteLine();

// Builder Pattern (Advanced - Gradient)
Console.WriteLine("""
    Pattern 6: Builder Pattern (Advanced - Gradient)
      - Best for: Gradient effects on QR code
      - API: new QRCodeImageBuilder().WithGradient()
    """);
{
    var path = Path.Combine(outputDir, "pattern6_builder_gradient.png");

    var gradient = new GradientOptions(
        [SKColors.Blue, SKColors.Purple, SKColors.Pink],
        GradientDirection.TopLeftToBottomRight,
        [0f, 0.5f, 1f]);

    var qrBuilder = new QRCodeImageBuilder(content)
        .WithSize(800, 800)
        .WithErrorCorrection(QREccLevel.H)
        .WithGradient(gradient)
        .WithModuleShape(RoundedRectangleModuleShape.Default, sizePercent: 0.9f);

    var bitmap = qrBuilder.ToBitmap();
    using (bitmap)
    using (var data = bitmap.Encode(SKEncodedImageFormat.Png, 100))
    using (var stream = File.OpenWrite(path))
    {
        data.SaveTo(stream);
    }

    Console.WriteLine($"  ✓ Saved to: {path}");
}
Console.WriteLine();

// Builder Pattern (Advanced - Embedded Icon)
Console.WriteLine("""
    Pattern 7: Builder Pattern (Advanced - Icon Overlay)
      - Best for: QR codes with logo/icon in center
      - API: IconData.FromImageByModules() + WithModulePixelSize() + WithIcon()
    """);
{
    var path = Path.Combine(outputDir, "pattern7_builder_icon.png");

    using var logo = SKBitmap.Decode(File.ReadAllBytes(iconPath));
    // Module-based icon sizing keeps logo body/border on the QR grid.
    var icon = IconData.FromImageByModules(logo, iconSizeModules: 7, iconBorderModules: 2);

    var qrBuilder = new QRCodeImageBuilder(content)
        .WithModulePixelSize(12)
        .WithErrorCorrection(QREccLevel.H) // H recommended for icons
        .WithColors(codeColor: SKColors.DarkGreen, backgroundColor: SKColors.LightYellow)
        .WithIcon(icon);

    var pngBytes = qrBuilder.ToByteArray();
    File.WriteAllBytes(path, pngBytes);

    Console.WriteLine($"  ✓ Saved to: {path}");
}
Console.WriteLine();

// Builder Pattern Full Customization
Console.WriteLine("""
    Pattern 8: Builder Pattern (Full Customization)
      - Best for: Maximum customization - all features combined
      - API: Chaining all With methods
    """);
{
    var path = Path.Combine(outputDir, "pattern8_builder_full.png");

    using var logo = SKBitmap.Decode(File.ReadAllBytes(iconPath));
    using var font = new SKFont
    {
        Size = 18,
        Typeface = SKTypeface.FromFamilyName("sans-serif", SKFontStyle.Bold)
    };
    var icon = new IconData
    {
        Icon = new ImageTextIconShape(logo, "FooBar", SKColors.Black, font, horizontalAlign: SKTextAlign.Center, verticalAlign: TextVerticalAlignment.Bottom, textPadding: 2),
        IconSizePercent = 13,
        IconBorderWidth = 18,
    };

    var gradient = new GradientOptions(
        [SKColor.Parse("FF6B35"), SKColor.Parse("F7931E"), SKColor.Parse("FDC830")],
        GradientDirection.LeftToRight);

    var qrBuilder = new QRCodeImageBuilder(content)
        .WithSize(1024, 1024)
        .WithFormat(SKEncodedImageFormat.Png, quality: 100)
        .WithErrorCorrection(QREccLevel.H)
        .WithEciMode(EciMode.Utf8)
        .WithQuietZone(3)
        .WithBackgroundColor(SKColors.White).WithClearColor(SKColors.Transparent)
        .WithModuleShape(RoundedRectangleModuleShape.Default, sizePercent: 0.92f)
        .WithGradient(gradient)
        .WithFinderPatternShape(RoundedRectangleFinderPatternShape.Default)
        .WithIcon(icon);

    using var stream = File.OpenWrite(path);
    qrBuilder.SaveTo(stream);

    Console.WriteLine($"  ✓ Saved to: {path}");
}
Console.WriteLine();

// SymbolRenderer Pattern (Low-level)
Console.WriteLine("""
    Pattern 9: SymbolRenderer Pattern (Low-level)
      - Best for: Advanced canvas control, custom rendering logic
      - API: QRCodeGenerator + SymbolRenderer.Render()
    """);
{
    var path = Path.Combine(outputDir, "pattern9_renderer.png");

    // Generate QR data
    var qrData = QRCodeGenerator.Create(content, QREccLevel.H, new QRCodeGeneratorOptions { QuietZoneSize = 4 });

    // Create canvas
    var info = new SKImageInfo(800, 800);
    using var surface = SKSurface.Create(info);
    var canvas = surface.Canvas;

    // Render with custom options
    using var logo = SKBitmap.Decode(File.ReadAllBytes(iconPath));
    var icon = IconData.FromImage(logo, iconBorderWidth: 9);
    var gradient = new GradientOptions([SKColors.DarkViolet, SKColors.DeepPink], GradientDirection.TopLeftToBottomRight);

    SymbolRenderer.Render(
        canvas,
        SKRect.Create(0, 0, info.Width, info.Height),
        qrData,
        codeColor: null, // Use gradient instead
        backgroundColor: SKColors.White,
        iconData: icon,
        moduleShape: CircleModuleShape.Default,
        moduleSizePercent: 0.9f,
        gradientOptions: gradient);

    // Save
    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var stream = File.OpenWrite(path);
    data.SaveTo(stream);

    Console.WriteLine($"  ✓ Saved to: {path}");
}
Console.WriteLine();

// SkiaSharp Canvas Direct (Most Low-level)
Console.WriteLine("""
    Pattern 10: SkiaSharp Canvas Direct (Most Low-level)
      - Best for: Maximum control, custom post-processing
      - API: QRCodeGenerator + SKCanvas + Manual rendering
    """);
{
    var path = Path.Combine(outputDir, "pattern10_canvas_direct.png");

    // Generate QR data
    var qrData = QRCodeGenerator.Create(content, QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 4 });

    // Create canvas
    var info = new SKImageInfo(600, 600);
    using var surface = SKSurface.Create(info);
    var canvas = surface.Canvas;

    // Clear background
    canvas.Clear(SKColors.White);

    // Render QR code
    canvas.Render(qrData, info.Width, info.Height);

    // Custom post-processing: Add text below QR code
    using var font = new SKFont
    {
        Size = 24,
        Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
    };
    using var paint = new SKPaint
    {
        Color = SKColors.Black,
        IsAntialias = true, // enable antialiasing for text
    };

    var text = "Scan Me!";
    var textWidth = font.MeasureText(text);
    canvas.DrawText(text, (info.Width - textWidth) / 2, info.Height - 10, SKTextAlign.Left, font, paint);

    // Save
    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var stream = File.OpenWrite(path);
    data.SaveTo(stream);

    Console.WriteLine($"  ✓ Saved to: {path}");
}
Console.WriteLine();

// IBufferWriter Pattern (High Performance)
Console.WriteLine("""
    Pattern 11: IBufferWriter Pattern (High Performance)
      - Best for: ASP.NET Core, high-performance scenarios
      - API: QRCodeImageBuilder.WritePng()
    """);
{
    var path = Path.Combine(outputDir, "pattern11_buffer_writer.png");

    using var fileStream = File.Create(path);
    var bufferWriter = System.IO.Pipelines.PipeWriter.Create(fileStream);

    QRCodeImageBuilder.WritePng(content, bufferWriter, QREccLevel.M, size: 512);
    bufferWriter.Complete();

    Console.WriteLine($"  ✓ Saved to: {path}");
}
Console.WriteLine();

// Raster formats (PNG / JPEG / WebP)
Console.WriteLine("""
    Pattern 12: Raster Output (PNG / JPEG / WebP)
      - Best for: Supporting multiple raster formats; default is PNG
      - API: WithFormat() / QRCodeImageBuilder.GetImageBytes()
    """);
{
    // Builder: switch format with WithFormat (quality applies to JPEG / WebP)
    var builderFormats = new[]
    {
        (SKEncodedImageFormat.Png, "png", 100),
        (SKEncodedImageFormat.Jpeg, "jpg", 90),
        (SKEncodedImageFormat.Webp, "webp", 80),
    };

    foreach (var (format, ext, quality) in builderFormats)
    {
        var path = Path.Combine(outputDir, $"pattern12_format.{ext}");
        var bytes = new QRCodeImageBuilder(content)
            .WithSize(512, 512)
            .WithFormat(format, quality)
            .ToByteArray();
        File.WriteAllBytes(path, bytes);
        Console.WriteLine($"  ✓ WithFormat({format}): {path}");
    }

    // One-liner helper (same formats)
    var jpegBytes = QRCodeImageBuilder.GetImageBytes(
        content, SKEncodedImageFormat.Jpeg, QREccLevel.M, size: 512, quality: 90);
    var oneLinerPath = Path.Combine(outputDir, "pattern12_format_oneliner.jpg");
    File.WriteAllBytes(oneLinerPath, jpegBytes);
    Console.WriteLine($"  ✓ GetImageBytes(Jpeg): {oneLinerPath}");
}
Console.WriteLine();

// Use Rounded Rectangle for finder patterns
Console.WriteLine("""
    Pattern 13: Rounded Rectangle for Finder Patterns
      - Best for: Stylish finder patterns with rounded corners
      - API: QRCodeImageBuilder().WithFinderPatternShape()
    """);
{
    var path = Path.Combine(outputDir, "pattern13_finderpattern_rounded_rectangle.png");

    var pngBytes = new QRCodeImageBuilder(content)
        .WithSize(512, 512)
        .WithFinderPatternShape(RectangleFinderPatternShape.Default)
        //.WithFinderPatternShape(CircleFinderPatternShape.Default)
        //.WithFinderPatternShape(RoundedRectangleFinderPatternShape.Default)
        //.WithFinderPatternShape(RoundedRectangleCircleFinderPatternShape.Default)
        .ToByteArray();

    File.WriteAllBytes(path, pngBytes);

    Console.WriteLine($"  ✓ Saved to: {path}");
}
Console.WriteLine();

// Instagram-style Profile QR Code
Console.WriteLine("""
    Pattern 14: Instagram-style Profile QR Code
      - Best for: Demonstrating complex styling with a square logo
      - API: WithModulePixelSize + FromImageByModules + gradient + shapes
    """);
{
    var path = Path.Combine(outputDir, "pattern14_instagram_style.png");

    // Instagram gradient colors (orange -> pink -> purple)
    var instagramGradient = new GradientOptions([
            SKColor.Parse("FCAF45"),  // Orange
            SKColor.Parse("F77737"),  // Orange-Red
            SKColor.Parse("E1306C"),  // Pink
            SKColor.Parse("C13584"),  // Purple
            SKColor.Parse("833AB4")   // Deep Purple
        ],
        GradientDirection.TopLeftToBottomRight,
        [0f, 0.25f, 0.5f, 0.75f, 1f]);

    using var logo = SKBitmap.Decode(File.ReadAllBytes(iconInstaPath));
    var icon = IconData.FromImageByModules(logo, iconSizeModules: 7, iconBorderModules: 2, maxCoreOccupancyPercent: 40);

    var qrBuilder = new QRCodeImageBuilder(content)
        .WithModulePixelSize(12)
        .WithErrorCorrection(QREccLevel.H)
        .WithQuietZone(4)
        .WithBackgroundColor(SKColors.White).WithClearColor(SKColors.White)
        .WithModuleShape(CircleModuleShape.Default, sizePercent: 0.95f)
        .WithFinderPatternShape(RoundedRectangleCircleFinderPatternShape.Default)
        .WithGradient(instagramGradient)
        .WithIcon(icon);

    var pngBytes = qrBuilder.ToByteArray();
    File.WriteAllBytes(path, pngBytes);

    Console.WriteLine($"  ✓ Saved to: {path}");
}
Console.WriteLine();

// Instagram-style with custom frame and text
Console.WriteLine("""
    Pattern 15: Instagram-style with Custom Frame
      - Best for: Branded QR codes with text overlay
      - API: SymbolRenderer + Custom canvas drawing
    """);
{
    var path = Path.Combine(outputDir, "pattern15_instagram_frame.png");

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
            IsAntialias = true, // enable antialiasing for smooth corners
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
            IsAntialias = true, // enable antialiasing for smooth corners
        })
        {
            var bgRect = new SKRoundRect(SKRect.Create(30, 30, canvasSize - 60, canvasSize - 60), r, r);
            canvas.DrawRoundRect(bgRect, bgPaint);
        }
    }

    // Generate QR data
    {
        var qrData = QRCodeGenerator.Create(content, QREccLevel.H, new QRCodeGeneratorOptions { QuietZoneSize = 2 });

        // Instagram gradient
        var instagramGradient = new GradientOptions([
                SKColor.Parse("FCAF45"),  // Orange
                SKColor.Parse("F77737"),  // Orange-Red
                SKColor.Parse("E1306C"),  // Pink
                SKColor.Parse("C13584"),  // Purple
                SKColor.Parse("833AB4")   // Deep Purple
            ],
            GradientDirection.TopLeftToBottomRight,
            [0f, 0.25f, 0.5f, 0.75f, 1f]);

        // Render QR code (module-aligned size inside the frame)
        using var logo = SKBitmap.Decode(File.ReadAllBytes(iconInstaPath));
        var icon = IconData.FromImageByModules(logo, iconSizeModules: 7, iconBorderModules: 2, maxCoreOccupancyPercent: 40);

        var qrRect = SKRect.Create(sidePadding, topPadding, qrSize, qrSize);
        SymbolRenderer.Render(
            canvas,
            qrRect,
            qrData,
            codeColor: null,
            backgroundColor: SKColors.White,
            iconData: icon,
            moduleShape: CircleModuleShape.Default,
            moduleSizePercent: 0.95f,
            gradientOptions: instagramGradient,
            finderPatternShape: RoundedRectangleCircleFinderPatternShape.Default);
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
            Color = SKColors.OrangeRed,
            IsAntialias = true, // enable antialiasing for text
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

            var usernameText = "@SKIASHARP.QRCODE";
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

    Console.WriteLine($"  ✓ Saved to: {path}");
}
Console.WriteLine();

// Compress & Decompress QR Code Data
Console.WriteLine("""
    Pattern 16: Compress & Decompress QR Code Data
      - Best for: Reducing size of QR code data storage/transmission
    """);
{
    var path = Path.Combine(outputDir, "pattern16_compress_decompress.png");

    // compression to zstandard ...
    var qrCodeData = QRCodeGenerator.Create(content, QREccLevel.L);
    var src = qrCodeData.GetRawData();
    var size = qrCodeData.GetRawDataSize();

    var maxSize = NativeCompressions.Zstandard.GetMaxCompressedLength(size);
    var compressed = new byte[maxSize];
    NativeCompressions.Zstandard.Compress(src, compressed, NativeCompressions.ZstandardCompressionOptions.Default);

    // decompression from zstandard ...
    var decompressed = NativeCompressions.Zstandard.Decompress(compressed);

    var qr = new QRCodeData(decompressed, 4);
    var pngBytes = new QRCodeImageBuilder(qr)
        .WithSize(512, 512)
        .ToByteArray();
    File.WriteAllBytes(path, pngBytes);

    Console.WriteLine($"  ✓ Saved to: {path}");
}
Console.WriteLine();

// Console Output of QR Code
Console.WriteLine("""
    Pattern 17: Console Output of QR Code
      - Best for: Quick visual verification in console
      - API: QRCodeGenerator.Create() + Console.Write()
    """);
{
    var qrCodeData = QRCodeGenerator.Create(content, QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 4 });
    for (var row = 0; row < qrCodeData.Size; row++)
    {
        for (var col = 0; col < qrCodeData.Size; col++)
        {
            Console.Write(qrCodeData[row, col] ? "🔵" : "  ");
        }
        Console.Write("\n");
    }
}
Console.WriteLine();

// Module-aligned logo: avoid ugly module/logo overlap
Console.WriteLine("""
    Pattern 18: Module-aligned Logo (avoid overlapping module dots)
      - Best for: Clean logo islands that sit on whole modules
      - Compare percent sizing (fractional edges) vs FromImageByModules + WithModulePixelSize
    """);
{
    using var logo = SKBitmap.Decode(File.ReadAllBytes(iconInstaPath));

    // Before: percent/pixel sizing on a fixed box, logo edges can cut through modules.
    {
        var path = Path.Combine(outputDir, "pattern18a_logo_percent_overlap.png");
        var icon = IconData.FromImage(logo, iconSizePercent: 18, iconBorderWidth: 8);

        var pngBytes = new QRCodeImageBuilder(content)
            .WithSize(512, 512)
            .WithErrorCorrection(QREccLevel.H)
            .WithQuietZone(4)
            .WithModuleShape(CircleModuleShape.Default, sizePercent: 0.92f)
            .WithIcon(icon)
            .ToByteArray();

        File.WriteAllBytes(path, pngBytes);
        Console.WriteLine($"  ✓ Before (percent sizing): {path}");
    }

    // After: module-based icon + integer module pixels, border covers whole modules cleanly.
    {
        var path = Path.Combine(outputDir, "pattern18b_logo_module_aligned.png");
        var icon = IconData.FromImageByModules(logo, iconSizeModules: 7, iconBorderModules: 2, maxCoreOccupancyPercent: 40);

        var pngBytes = new QRCodeImageBuilder(content)
            .WithModulePixelSize(12)
            .WithErrorCorrection(QREccLevel.H)
            .WithQuietZone(4)
            .WithModuleShape(CircleModuleShape.Default, sizePercent: 0.92f)
            .WithFinderPatternShape(RoundedRectangleCircleFinderPatternShape.Default)
            .WithIcon(icon)
            .ToByteArray();

        File.WriteAllBytes(path, pngBytes);
        Console.WriteLine($"  ✓ After  (module-aligned): {path}");
    }
}
Console.WriteLine();

// Showcase: Aurora gradient
Console.WriteLine("""
    Pattern 19: Showcase, Aurora
      - Best for: Attractive gradient + soft circle modules + rounded finder eyes
    """);
{
    var path = Path.Combine(outputDir, "pattern19_showcase_aurora.png");

    var aurora = new GradientOptions(
        [
            SKColor.Parse("00C9A7"),
            SKColor.Parse("1B9CFC"),
            SKColor.Parse("6C5CE7"),
            SKColor.Parse("A29BFE"),
        ],
        GradientDirection.TopLeftToBottomRight,
        [0f, 0.35f, 0.7f, 1f]);

    var pngBytes = new QRCodeImageBuilder(content)
        .WithModulePixelSize(12)
        .WithErrorCorrection(QREccLevel.H)
        .WithQuietZone(3)
        .WithBackgroundColor(SKColor.Parse("0B1320")).WithClearColor(SKColor.Parse("0B1320"))
        .WithModuleShape(CircleModuleShape.Default, sizePercent: 0.88f)
        .WithFinderPatternShape(RoundedRectangleCircleFinderPatternShape.Default)
        .WithGradient(aurora)
        .ToByteArray();

    File.WriteAllBytes(path, pngBytes);
    Console.WriteLine($"  ✓ Saved to: {path}");
}
Console.WriteLine();

// Showcase: Sunset brand card with padded canvas + logo
Console.WriteLine("""
    Pattern 20: Showcase, Sunset Brand Card
      - Best for: Module-aligned logo + fixed canvas padding + warm gradient
    """);
{
    var path = Path.Combine(outputDir, "pattern20_showcase_sunset_brand.png");

    using var logo = SKBitmap.Decode(File.ReadAllBytes(iconInstaPath));
    var icon = IconData.FromImageByModules(logo, iconSizeModules: 7, iconBorderModules: 2, maxCoreOccupancyPercent: 40);

    var sunset = new GradientOptions(
        [
            SKColor.Parse("FF9A3C"),
            SKColor.Parse("FF6B6B"),
            SKColor.Parse("C44569"),
            SKColor.Parse("2C2C54"),
        ],
        GradientDirection.TopToBottom,
        [0f, 0.35f, 0.7f, 1f]);

    // Content stays module-aligned; WithSize adds centered padding for a card-like frame.
    var pngBytes = new QRCodeImageBuilder(content)
        .WithModulePixelSize(10)
        .WithSize(640, 640)
        .WithErrorCorrection(QREccLevel.H)
        .WithQuietZone(4)
        .WithBackgroundColor(SKColors.White).WithClearColor(SKColor.Parse("FFF5EE"))
        .WithModuleShape(RoundedRectangleModuleShape.Default, sizePercent: 0.9f)
        .WithFinderPatternShape(RoundedRectangleFinderPatternShape.Default)
        .WithGradient(sunset)
        .WithIcon(icon)
        .ToByteArray();

    File.WriteAllBytes(path, pngBytes);
    Console.WriteLine($"  ✓ Saved to: {path}");
}
Console.WriteLine();

// Showcase: Neon night with icon+text
Console.WriteLine("""
    Pattern 21: Showcase, Neon Night (icon + text)
      - Best for: Combining gradient, shapes, and ImageTextIconShape
    """);
{
    var path = Path.Combine(outputDir, "pattern21_showcase_neon_night.png");

    using var logo = SKBitmap.Decode(File.ReadAllBytes(iconPath));
    using var font = new SKFont
    {
        Size = 14,
        Typeface = SKTypeface.FromFamilyName("sans-serif", SKFontStyle.Bold),
    };

    var icon = new IconData
    {
        Icon = new ImageTextIconShape(
            logo,
            "SCAN ME",
            SKColor.Parse("00F5D4"),
            font,
            horizontalAlign: SKTextAlign.Center,
            verticalAlign: TextVerticalAlignment.Bottom,
            textPadding: 2),
        IconSizeModules = 5,
        IconBorderModules = 3,
        MaxCoreOccupancyPercent = 40,
    };

    var neon = new GradientOptions(
        [
            SKColor.Parse("00F5D4"),
            SKColor.Parse("00BBF9"),
            SKColor.Parse("9B5DE5"),
            SKColor.Parse("F15BB5"),
        ],
        GradientDirection.TopRightToBottomLeft,
        [0f, 0.33f, 0.66f, 1f]);

    var pngBytes = new QRCodeImageBuilder(content)
        .WithModulePixelSize(11)
        .WithErrorCorrection(QREccLevel.H)
        .WithQuietZone(4)
        .WithBackgroundColor(SKColor.Parse("111111")).WithClearColor(SKColor.Parse("111111"))
        .WithModuleShape(RoundedRectangleModuleShape.Default, sizePercent: 0.85f)
        .WithFinderPatternShape(RoundedRectangleCircleFinderPatternShape.Default)
        .WithGradient(neon)
        .WithIcon(icon)
        .ToByteArray();

    File.WriteAllBytes(path, pngBytes);
    Console.WriteLine($"  ✓ Saved to: {path}");
}
Console.WriteLine();

// SVG output (vector format)
Console.WriteLine("""
    Pattern 22: SVG Output (Vector Format)
      - Best for: Print, web embedding, infinite scaling without quality loss
      - API: QRCodeImageBuilder.SaveSvg() / GetSvgString() / SaveToSvg() / ToSvgString()
    """);
{
    // One-liner: save SVG to stream
    var path = Path.Combine(outputDir, "pattern22_svg_simple.svg");
    using (var stream = File.Create(path))
    {
        QRCodeImageBuilder.SaveSvg(content, stream, QREccLevel.M, size: 512);
    }
    Console.WriteLine($"  ✓ Saved to: {path}");

    // Builder: all styling options work in SVG too (shapes, gradients, finder patterns)
    var styledPath = Path.Combine(outputDir, "pattern22_svg_styled.svg");
    var gradient = new GradientOptions(
        [SKColor.Parse("1B9CFC"), SKColor.Parse("6C5CE7")],
        GradientDirection.TopLeftToBottomRight);

    using (var stream = File.Create(styledPath))
    {
        new QRCodeImageBuilder(content)
            .WithModulePixelSize(10)
            .WithErrorCorrection(QREccLevel.H)
            .WithModuleShape(RoundedRectangleModuleShape.Default, sizePercent: 0.9f)
            .WithFinderPatternShape(RoundedRectangleFinderPatternShape.Default)
            .WithGradient(gradient)
            .SaveToSvg(stream);
    }
    Console.WriteLine($"  ✓ Saved to: {styledPath}");

    // String output: embed directly into HTML or manipulate as text
    var svgString = new QRCodeImageBuilder(content)
        .WithSize(256, 256)
        .ToSvgString();
    var firstLineEnd = svgString.IndexOf('\n');
    var firstLine = firstLineEnd >= 0 ? svgString.Substring(0, firstLineEnd) : svgString;
    Console.WriteLine($"  ✓ ToSvgString(): {svgString.Length} chars, starts with: {firstLine}");

    // Static one-liner string output for inline HTML embedding
    var inlineSvg = QRCodeImageBuilder.GetSvgString(content, QREccLevel.M, size: 256);
    Console.WriteLine($"  ✓ GetSvgString(): viewBox present: {inlineSvg.Contains("viewBox=\"0 0 256 256\"")}");
}
Console.WriteLine();

// Decode
Console.WriteLine("""
    Pattern 23: Decode (QRCodeData / image round-trip)
      - Best for: Round-trip validation, reading rendered QR images
      - API: QRCodeDecoder.TryDecode()
    """);
{
    // 23a. Matrix round-trip: decode straight from QRCodeData (no image involved)
    var qrData = QRCodeGenerator.Create(content, QREccLevel.M);
    if (QRCodeDecoder.TryDecode(qrData, out var decoded, out var info))
    {
        Console.WriteLine($"  ✓ Matrix decode: \"{Truncate(decoded, 48)}\"");
        Console.WriteLine($"    version {info.Version}, ECC {info.EccLevel}, mask {info.MaskPattern}, {info.ErrorsCorrected} codewords corrected");
    }

    // 23b. Image decode: read back a PNG produced earlier in this run
    var pngPath = Path.Combine(outputDir, "pattern1_static_method.png");
    using (var bitmap = SKBitmap.Decode(File.ReadAllBytes(pngPath)))
    {
        if (QRCodeDecoder.TryDecode(bitmap, out var fromImage, out var imageInfo))
        {
            Console.WriteLine($"  ✓ Image decode ({Path.GetFileName(pngPath)}): \"{Truncate(fromImage, 48)}\" (version {imageInfo.Version})");
        }
        else
        {
            Console.WriteLine($"  ✗ Image decode failed: {imageInfo.Status}");
        }
    }

    // 23c. Rotation tolerance: the detector resolves arbitrary rotation
    var rotatedPath = Path.Combine(outputDir, "pattern23_rotated.png");
    using (var source = SKBitmap.Decode(File.ReadAllBytes(pngPath)))
    using (var rotated = new SKBitmap(new SKImageInfo((int)(source.Width * 1.5f), (int)(source.Height * 1.5f))))
    {
        using (var canvas = new SKCanvas(rotated))
        {
            canvas.Clear(SKColors.White);
            canvas.Translate(rotated.Width / 2f, rotated.Height / 2f);
            canvas.RotateDegrees(30);
            canvas.Translate(-source.Width / 2f, -source.Height / 2f);
            canvas.DrawBitmap(source, 0, 0, SKSamplingOptions.Default);
        }

        using (var image = SKImage.FromBitmap(rotated))
        using (var encoded = image.Encode(SKEncodedImageFormat.Png, 100))
        using (var stream = File.OpenWrite(rotatedPath))
        {
            encoded.SaveTo(stream);
        }

        if (QRCodeDecoder.TryDecode(rotated, out var fromRotated, out _))
        {
            Console.WriteLine($"  ✓ 30° rotated image still decodes: \"{Truncate(fromRotated, 48)}\"");
        }
    }

    // 23d. Error correction at work: flip modules, Reed-Solomon repairs them
    var damaged = QRCodeGenerator.Create("error correction demo", QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 0 });
    var size = damaged.Size;
    var modules = new byte[size * size];
    for (var y = 0; y < size; y++)
    {
        for (var x = 0; x < size; x++)
        {
            modules[y * size + x] = damaged[y, x] ? (byte)1 : (byte)0;
        }
    }
    modules[9 * size + 9] ^= 1;   // flip three data modules
    modules[10 * size + 11] ^= 1;
    modules[12 * size + 10] ^= 1;
    if (QRCodeDecoder.TryDecode(modules, size, out var repaired, out var repairInfo))
    {
        Console.WriteLine($"  ✓ 3 flipped modules repaired by Reed-Solomon: \"{repaired}\" ({repairInfo.ErrorsCorrected} codewords corrected)");
    }

    // 23e. Failure reporting: statuses explain why a decode failed
    var blank = new byte[25 * 25];
    QRCodeDecoder.TryDecode(blank, 25, out _, out var failInfo);
    Console.WriteLine($"  ✓ Failure diagnostics: blank matrix reports {failInfo.Status}");

    static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}
Console.WriteLine();

// Micro QR, ISO/IEC 18004 M2-L numeric example (11×11 core, 13×13 with quiet zone)
var microContent = "01234567";

Console.WriteLine("""
    Pattern 24: Micro QR, Static Method (Simplest)
      - Best for: Quick Micro QR generation with default settings
      - API: MicroQRCodeImageBuilder.GetPngBytes()
    """);
{
    var path = Path.Combine(outputDir, "pattern24_microqr_static.png");

    var pngBytes = MicroQRCodeImageBuilder.GetPngBytes(microContent, MicroQREccLevel.L, size: 256);
    File.WriteAllBytes(path, pngBytes);

    Console.WriteLine($"  ✓ Saved to: {path}");
}
Console.WriteLine();

Console.WriteLine("""
    Pattern 25: Micro QR, Builder (colors, module shape, gradient)
      - Best for: Styled Micro QR within symbology limits (no icon overlay)
      - API: new MicroQRCodeImageBuilder().WithXxx()
      - Default quiet zone is 2 modules (Standard QR uses 4)
    """);
{
    var path = Path.Combine(outputDir, "pattern25_microqr_styled.png");
    var gradient = new GradientOptions(
        [SKColor.Parse("00B894"), SKColor.Parse("0984E3")],
        GradientDirection.TopLeftToBottomRight);

    var pngBytes = new MicroQRCodeImageBuilder("SKU-42")
        .WithModulePixelSize(14)
        .WithErrorCorrection(MicroQREccLevel.M)
        .WithColors(codeColor: SKColor.Parse("2D3436"), backgroundColor: SKColors.White)
        .WithModuleShape(RoundedRectangleModuleShape.Default, sizePercent: 0.92f)
        .WithGradient(gradient)
        .ToByteArray();

    File.WriteAllBytes(path, pngBytes);

    Console.WriteLine($"  ✓ Saved to: {path}");
}
Console.WriteLine();

Console.WriteLine("""
    Pattern 26: Micro QR, Decode (matrix / image round-trip)
      - Best for: Round-trip validation, reading rendered Micro QR images
      - API: MicroQRCodeDecoder.TryDecode()
      - QRCodeDecoder stays Standard QR-only (cross-symbology rejection)
    """);
{
    var microData = MicroQRCodeGenerator.Create(microContent, MicroQREccLevel.L);
    if (MicroQRCodeDecoder.TryDecode(microData, out var decoded, out var info))
    {
        Console.WriteLine($"  ✓ Matrix decode: \"{decoded}\" (version {info.Version}, ECC {info.EccLevel})");
    }

    var pngPath = Path.Combine(outputDir, "pattern24_microqr_static.png");
    using (var bitmap = SKBitmap.Decode(File.ReadAllBytes(pngPath)))
    {
        if (MicroQRCodeDecoder.TryDecode(bitmap, out var fromImage, out var imageInfo))
        {
            Console.WriteLine($"  ✓ Image decode ({Path.GetFileName(pngPath)}): \"{fromImage}\" (version {imageInfo.Version})");
        }
        else
        {
            Console.WriteLine($"  ✗ Image decode failed: {imageInfo.Status}");
        }

        QRCodeDecoder.TryDecode(bitmap, out _, out var stdInfo);
        Console.WriteLine($"  ✓ Standard QR decoder on Micro QR image: {stdInfo.Status} (expected NotDetected)");
    }
}
Console.WriteLine();

// rMQR (ISO/IEC 23941): rectangular symbols for narrow print lanes, R7x43-R17x139
var rmqrContent = "https://example.com/r/12345";

Console.WriteLine("""
    Pattern 27: rMQR, Static Method (Simplest)
      - Best for: Quick rectangular symbols with default settings
      - API: RmQRCodeImageBuilder.GetPngBytes()
      - size is the image WIDTH; the height follows the symbol aspect ratio
    """);
{
    var path = Path.Combine(outputDir, "pattern27_rmqr_static.png");

    var pngBytes = RmQRCodeImageBuilder.GetPngBytes(rmqrContent, RmQREccLevel.M, size: 512);
    File.WriteAllBytes(path, pngBytes);

    Console.WriteLine($"  ✓ Saved to: {path}");
}
Console.WriteLine();

Console.WriteLine("""
    Pattern 28: rMQR, Builder (fit strategy, fixed height, styling)
      - Best for: Fitting a label lane of fixed height; styled output within symbology limits
      - API: new RmQRCodeImageBuilder().WithHeight() / WithFitStrategy() / WithVersion()
      - Default fit is fewest modules (matches other encoders): 12 digits at M give R11x27, not the flatter R7x43;
        use MinimizeHeight or WithHeight for the flattest symbol
    """);
{
    var path = Path.Combine(outputDir, "pattern28_rmqr_fixed_height.png");
    var gradient = new GradientOptions(
        [SKColor.Parse("00B894"), SKColor.Parse("0984E3")],
        GradientDirection.LeftToRight);

    var pngBytes = new RmQRCodeImageBuilder(rmqrContent)
        .WithHeight(RmQRHeight.H9)                  // 9 modules high, width chosen automatically
        .WithErrorCorrection(RmQREccLevel.H)
        .WithModulePixelSize(12)
        .WithColors(codeColor: SKColor.Parse("2D3436"), backgroundColor: SKColors.White)
        .WithModuleShape(RoundedRectangleModuleShape.Default, sizePercent: 0.9f)
        .WithGradient(gradient)
        .ToByteArray();
    File.WriteAllBytes(path, pngBytes);

    var flat = RmQRCodeGenerator.Create("012345678901", RmQREccLevel.M, new RmQRCodeGeneratorOptions { FitStrategy = RmQRFitStrategy.MinimizeHeight });
    var smallest = RmQRCodeGenerator.Create("012345678901", RmQREccLevel.M);
    Console.WriteLine($"  ✓ Saved to: {path}");
    Console.WriteLine($"  ✓ 12 digits at M: default fit {smallest.Version} ({smallest.Width - 4}×{smallest.Height - 4} core), MinimizeHeight {flat.Version}");
}
Console.WriteLine();

Console.WriteLine("""
    Pattern 29: rMQR, Decode (matrix / image round-trip)
      - Best for: Round-trip validation, reading rendered rMQR images
      - API: RmQRCodeDecoder.TryDecode()
      - Explicitly typed like MicroQRCodeDecoder; QRCodeDecoder stays Standard QR-only
    """);
{
    var rmData = RmQRCodeGenerator.Create(rmqrContent, RmQREccLevel.M);
    if (RmQRCodeDecoder.TryDecode(rmData, out var decoded, out var info))
    {
        Console.WriteLine($"  ✓ Matrix decode: \"{decoded}\" ({info.Version}, ECC {info.EccLevel})");
    }

    var pngPath = Path.Combine(outputDir, "pattern27_rmqr_static.png");
    using (var bitmap = SKBitmap.Decode(File.ReadAllBytes(pngPath)))
    {
        if (RmQRCodeDecoder.TryDecode(bitmap, out var fromImage, out var imageInfo))
        {
            Console.WriteLine($"  ✓ Image decode ({Path.GetFileName(pngPath)}): \"{fromImage}\" ({imageInfo.Version}, {imageInfo.ErrorsCorrected} corrected)");
        }
        else
        {
            Console.WriteLine($"  ✗ Image decode failed: {imageInfo.Status}");
        }

        QRCodeDecoder.TryDecode(bitmap, out _, out var stdInfo);
        Console.WriteLine($"  ✓ Standard QR decoder on rMQR image: {stdInfo.Status} (expected NotDetected)");
    }
}
Console.WriteLine();

Console.WriteLine("""
    Pattern 30: Renderer-Neutral Geometry (GetModuleRectangles)
      - Best for: Rendering without SkiaSharp (hand-rolled SVG, System.Drawing, WPF, PDF, any draw-call API)
      - API: QRCodeData.GetModuleRectangles() / TryGetModuleRectangles() / GetModuleRectanglesMaxCount()
      - Dark modules come back as merged rectangles in module coordinates (same space as data[row, col])
    """);
{
    var data = QRCodeGenerator.Create(content, QREccLevel.M);

    // Merged rectangles vs one-rect-per-module: merging roughly halves the element count.
    var rects = data.GetModuleRectangles();
    var darkCount = 0;
    for (var row = 0; row < data.Size; row++)
        for (var col = 0; col < data.Size; col++)
            if (data[row, col])
                darkCount++;
    Console.WriteLine($"  ✓ {darkCount} dark modules merged into {rects.Length} rectangles ({(double)darkCount / rects.Length:F2}x fewer elements)");

    // Hand-rolled SVG with no SkiaSharp involved: viewBox in module units (1 unit = 1 module,
    // quiet zone included), so the consumer scales by CSS/attributes instead of a baked pixel size.
    var path = Path.Combine(outputDir, "pattern30_geometry_svg.svg");
    var sb = new System.Text.StringBuilder();
    sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {data.Size} {data.Size}\" shape-rendering=\"crispEdges\">");
    sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"#fff\"/><path fill=\"#000\" d=\"");
    foreach (var r in rects)
    {
        // InvariantCulture: some locales format negative numbers with a non-ASCII minus sign.
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"M{r.X},{r.Y}h{r.Width}v{r.Height}h{-r.Width}z");
    }
    sb.Append("\"/></svg>");
    File.WriteAllText(path, sb.ToString());
    Console.WriteLine($"  ✓ Saved to: {path} (no SkiaSharp in this pipeline)");

    // Allocation-free variant: size a pooled buffer with the O(1) upper bound.
    var buffer = System.Buffers.ArrayPool<ModuleRect>.Shared.Rent(data.GetModuleRectanglesMaxCount());
    try
    {
        data.TryGetModuleRectangles(buffer, out var written);
        Console.WriteLine($"  ✓ TryGetModuleRectangles with pooled buffer: {written} rectangles (max bound {data.GetModuleRectanglesMaxCount()})");
    }
    finally
    {
        System.Buffers.ArrayPool<ModuleRect>.Shared.Return(buffer);
    }

    // Micro QR and rMQR expose the same members; rMQR is rectangular (Width/Height instead of Size).
    var rm = RmQRCodeGenerator.Create(rmqrContent, RmQREccLevel.M);
    Console.WriteLine($"  ✓ rMQR {rm.Version}: {rm.GetModuleRectangles().Length} rectangles in a {rm.Width}x{rm.Height} module grid");
}
Console.WriteLine();

Console.WriteLine("=== All patterns completed! ===");
Console.WriteLine($"Output directory: {Path.GetFullPath(outputDir)}");
