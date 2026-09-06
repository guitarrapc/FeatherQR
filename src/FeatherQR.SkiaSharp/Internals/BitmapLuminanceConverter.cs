using SkiaSharp;
using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.SkiaSharp.Internals;

/// <summary>
/// Converts SkiaSharp bitmaps to 8-bit grayscale luminance buffers.
/// </summary>
/// <remarks>
/// Reads the pixel layout out of the bitmap and hands the bytes to the core (<see cref="LuminanceConverter"/>).
/// Fast paths cover the color types QR sources actually use (Gray8, Bgra8888, Rgba8888, Rgb888x); anything else is redrawn once into Bgra8888.
/// Transparent pixels are composited against white, QR quiet zones are white by definition, and transparent-background PNGs are a common input.
/// </remarks>
internal static class BitmapLuminanceConverter
{
    /// <summary>
    /// Converts bitmap pixels to luminance (width × height bytes, row-major).
    /// </summary>
    /// <param name="bitmap">Source bitmap.</param>
    /// <param name="luminance">Destination buffer, at least Width × Height bytes.</param>
    public static void Convert(SKBitmap bitmap, Span<byte> luminance)
    {
        using (var pixmap = bitmap.PeekPixels())
        {
            if (pixmap is not null && TryConvertPixmap(pixmap, luminance))
                return;
        }

        // Unsupported layout: redraw once into a known format (rare path)
        var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var converted = new SKBitmap(info);
        using (var canvas = new SKCanvas(converted))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(bitmap, 0, 0, SKSamplingOptions.Default);
        }
        using var convertedPixmap = converted.PeekPixels();
        if (convertedPixmap is null || !TryConvertPixmap(convertedPixmap, luminance))
            throw new NotSupportedException($"Unsupported bitmap color type: {bitmap.ColorType}");
    }

    private static bool TryConvertPixmap(SKPixmap pixmap, Span<byte> luminance)
    {
        if (!TryGetLayout(pixmap.ColorType, out var layout))
            return false;

        var premultiplied = pixmap.AlphaType == SKAlphaType.Premul;
        LuminanceConverter.Convert(pixmap.GetPixelSpan(), pixmap.Width, pixmap.Height, pixmap.RowBytes, layout, premultiplied, luminance);
        return true;
    }

    /// <summary>The core layout for a Skia color type, for the four the core converts directly.</summary>
    internal static bool TryGetLayout(SKColorType colorType, out PixelLayout layout)
    {
        switch (colorType)
        {
            case SKColorType.Gray8:
                layout = PixelLayout.Gray8;
                return true;
            case SKColorType.Bgra8888:
                layout = PixelLayout.Bgra8888;
                return true;
            case SKColorType.Rgba8888:
                layout = PixelLayout.Rgba8888;
                return true;
            case SKColorType.Rgb888x:
                layout = PixelLayout.Rgb888x;
                return true;
            default:
                layout = default;
                return false;
        }
    }
}
