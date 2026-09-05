using SkiaSharp;
using FeatherQR;
using FeatherQR.SkiaSharp;

namespace BlazorWasm;

/// <summary>
/// Translates <see cref="QrOptions"/> into FeatherQR API calls, shared by the
/// live <c>SKCanvasView</c> preview (<see cref="SymbolRenderer"/>) and the PNG/SVG
/// exports (<see cref="QRCodeImageBuilder"/>).
/// </summary>
public static class QrImageFactory
{
    private static SKBitmap? s_builtInLogo;

    /// <summary>Encodes the content into a QR module matrix.</summary>
    public static QRCodeData CreateQrData(QrOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Content))
            throw new ArgumentException("Content is empty.");

        return QRCodeGenerator.Create(
            options.Content.AsSpan(),
            options.Ecc,
            new QRCodeGeneratorOptions
            {
                Version = options.Version == -1 ? QRVersionRange.Any : QRVersionRange.Exactly(options.Version),
                QuietZoneSize = Math.Clamp(options.QuietZone, 0, 10),
            });
    }

    /// <summary>Encodes the content into a Micro QR module matrix.</summary>
    public static MicroQRCodeData CreateMicroQRData(QrOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Content))
            throw new ArgumentException("Content is empty.");

        return MicroQRCodeGenerator.Create(
            options.Content.AsSpan(),
            options.MicroEcc,
            new MicroQRCodeGeneratorOptions
            {
                Version = options.Version is >= 1 and <= 4 ? (MicroQRVersion)options.Version : null,
                QuietZoneSize = Math.Clamp(options.QuietZone, 0, 10),
            });
    }

    /// <summary>
    /// Builds the Micro QR image builder for PNG/SVG export. Micro QR has no icon
    /// overlay or finder pattern shape options (single finder, no ECC headroom),
    /// so the page hides those controls.
    /// </summary>
    public static MicroQRCodeImageBuilder CreateMicroBuilder(QrOptions options, MicroQRCodeData data)
    {
        var size = Math.Clamp(options.Size, 64, 2048);
        return new MicroQRCodeImageBuilder(data)
            .WithSize(size, size)
            .WithColors(GetForegroundColor(options), GetBackgroundColor(options))
            .WithModuleShape(CreateModuleShape(options), Math.Clamp(options.ModuleSizePercent, 0.5f, 1.0f))
            .WithGradient(CreateGradient(options));
    }

    /// <summary>Encodes the content into an rMQR module matrix.</summary>
    public static RmQRCodeData CreateRmQRData(QrOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Content))
            throw new ArgumentException("Content is empty.");

        // A fixed version already pins the height; the fixed-height option applies to
        // automatic selection only (the library rejects a disagreeing pair).
        var fixedVersion = options.Version is >= 1 and <= 32 ? (RmQRVersion)options.Version : (RmQRVersion?)null;
        return RmQRCodeGenerator.Create(options.Content.AsSpan(), options.RmEcc, new RmQRCodeGeneratorOptions
        {
            Version = fixedVersion,
            FitStrategy = options.RmFitStrategy,
            Height = fixedVersion is null ? options.RmHeight : null,
            QuietZoneSize = Math.Clamp(options.QuietZone, 0, 10),
        });
    }

    /// <summary>
    /// Builds the rMQR image builder for PNG/SVG export: the export size is the image
    /// width, the height follows the rectangular symbol. rMQR has no icon overlay or
    /// finder pattern shape options, so the page hides those controls.
    /// </summary>
    public static RmQRCodeImageBuilder CreateRmBuilder(QrOptions options, RmQRCodeData data)
    {
        return new RmQRCodeImageBuilder(data)
            .WithWidth(Math.Clamp(options.Size, 64, 2048))
            .WithColors(GetForegroundColor(options), GetBackgroundColor(options))
            .WithModuleShape(CreateModuleShape(options), Math.Clamp(options.ModuleSizePercent, 0.5f, 1.0f))
            .WithGradient(CreateGradient(options));
    }

    /// <summary>Builds the image builder for PNG/SVG export with the current visual options.</summary>
    public static QRCodeImageBuilder CreateBuilder(QrOptions options, QRCodeData data, SKBitmap? customLogo)
    {
        var size = Math.Clamp(options.Size, 64, 2048);
        return new QRCodeImageBuilder(data)
            .WithSize(size, size)
            .WithColors(GetForegroundColor(options), GetBackgroundColor(options))
            .WithModuleShape(CreateModuleShape(options), Math.Clamp(options.ModuleSizePercent, 0.5f, 1.0f))
            .WithFinderPatternShape(CreateFinderShape(options))
            .WithGradient(CreateGradient(options))
            .WithIcon(CreateIcon(options, customLogo));
    }

    public static SKColor GetForegroundColor(QrOptions options)
        => ParseColor(options.Foreground, SKColors.Black);

    public static SKColor GetBackgroundColor(QrOptions options)
        => options.TransparentBackground ? SKColors.Transparent : ParseColor(options.Background, SKColors.White);

    /// <summary>Returns null for the default rectangle shape.</summary>
    public static ModuleShape? CreateModuleShape(QrOptions options) => options.ModuleShape switch
    {
        ModuleShapeKind.Circle => CircleModuleShape.Default,
        ModuleShapeKind.Rounded => new RoundedRectangleModuleShape(Math.Clamp(options.ModuleCornerRadius, 0f, 1f)),
        _ => null,
    };

    /// <summary>Returns null for Auto: standard pattern, or the module shape when one is set.</summary>
    public static FinderPatternShape? CreateFinderShape(QrOptions options) => options.FinderShape switch
    {
        FinderShapeKind.Rectangle => RectangleFinderPatternShape.Default,
        FinderShapeKind.Circle => CircleFinderPatternShape.Default,
        FinderShapeKind.Rounded => RoundedRectangleFinderPatternShape.Default,
        FinderShapeKind.RoundedCircle => RoundedRectangleCircleFinderPatternShape.Default,
        _ => null,
    };

    public static GradientOptions? CreateGradient(QrOptions options)
    {
        if (!options.GradientEnabled || options.GradientDirection == GradientDirection.None)
            return null;

        return new GradientOptions(
            [ParseColor(options.GradientStart, SKColors.Black), ParseColor(options.GradientEnd, SKColors.Black)],
            options.GradientDirection);
    }

    public static IconData? CreateIcon(QrOptions options, SKBitmap? customLogo)
    {
        var bitmap = options.LogoMode switch
        {
            LogoMode.BuiltIn => s_builtInLogo ??= CreateBuiltInLogo(),
            LogoMode.Custom => customLogo,
            _ => null,
        };
        if (bitmap is null)
            return null;

        return IconData.FromImage(
            bitmap,
            iconSizePercent: Math.Clamp(options.LogoSizePercent, 1, 40),
            iconBorderWidth: Math.Clamp(options.LogoBorderWidth, 0, 24));
    }

    private static SKColor ParseColor(string value, SKColor fallback)
        => SKColor.TryParse(value, out var color) ? color : fallback;

    /// <summary>
    /// Draws the built-in logo: an Instagram-style camera glyph on a warm-to-purple
    /// gradient rounded square. Rendered with SkiaSharp itself, so no binary asset is shipped.
    /// </summary>
    private static SKBitmap CreateBuiltInLogo()
    {
        const int S = 256;
        var bitmap = new SKBitmap(new SKImageInfo(S, S, SKImageInfo.PlatformColorType, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        // Gradient rounded square (bottom-left warm to top-right purple).
        using var background = new SKPaint { IsAntialias = true };
        background.Shader = SKShader.CreateLinearGradient(
            new SKPoint(0, S),
            new SKPoint(S, 0),
            [SKColor.Parse("#FA7E1E"), SKColor.Parse("#D62976"), SKColor.Parse("#962FBF")],
            null,
            SKShaderTileMode.Clamp);
        canvas.DrawRoundRect(SKRect.Create(0, 0, S, S), S * 0.22f, S * 0.22f, background);

        // White camera outline: body, lens, flash dot.
        using var stroke = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = S * 0.055f,
            Color = SKColors.White,
            StrokeCap = SKStrokeCap.Round,
        };
        var inset = S * 0.19f;
        canvas.DrawRoundRect(SKRect.Create(inset, inset, S - 2 * inset, S - 2 * inset), S * 0.12f, S * 0.12f, stroke);
        canvas.DrawCircle(S / 2f, S / 2f, S * 0.15f, stroke);
        using var dot = new SKPaint { IsAntialias = true, Color = SKColors.White };
        canvas.DrawCircle(S * 0.685f, S * 0.315f, S * 0.033f, dot);

        return bitmap;
    }
}
