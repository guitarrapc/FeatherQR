using SkiaSharp;

namespace QRImageDecodeSweep;

internal static class Pixels
{
    /// <summary>The symbol with its quiet zone as an isDark callback over the padded grid.</summary>
    public static (Func<int, int, bool> IsDark, int Columns, int Rows) Padded(Symbol symbol, int quietZone)
    {
        var columns = symbol.Width + 2 * quietZone;
        var rows = symbol.Height + 2 * quietZone;
        return ((row, column) =>
        {
            var r = row - quietZone;
            var c = column - quietZone;
            return r >= 0 && c >= 0 && r < symbol.Height && c < symbol.Width && symbol[r, c];
        }, columns, rows);
    }

    public static SKBitmap ToGrayBitmap(byte[] luminance, int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque));
        var span = bitmap.GetPixelSpan();
        for (var y = 0; y < height; y++)
            luminance.AsSpan(y * width, width).CopyTo(span.Slice(y * bitmap.RowBytes, width));
        return bitmap;
    }

    public static (byte[] Luminance, int Width, int Height) ToLuminance(SKBitmap source)
    {
        using var gray = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.Gray8, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(gray))
        {
            canvas.Clear(SKColors.White);
            using var image = SKImage.FromBitmap(source);
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        }
        var luminance = new byte[gray.Width * gray.Height];
        var span = gray.GetPixelSpan();
        for (var y = 0; y < gray.Height; y++)
            span.Slice(y * gray.RowBytes, gray.Width).CopyTo(luminance.AsSpan(y * gray.Width, gray.Width));
        return (luminance, gray.Width, gray.Height);
    }

    public static (byte[] Luminance, int Width, int Height) Resize(byte[] luminance, int width, int height, int newWidth, int newHeight, SKSamplingOptions sampling)
    {
        using var source = ToGrayBitmap(luminance, width, height);
        using var resized = source.Resize(new SKImageInfo(newWidth, newHeight, SKColorType.Gray8, SKAlphaType.Opaque), sampling);
        return ToLuminance(resized);
    }
}
