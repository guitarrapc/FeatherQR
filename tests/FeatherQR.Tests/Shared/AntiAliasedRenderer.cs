using SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// Renders a module grid as one anti-aliased path at a fractional scale and sub-pixel offset: every module edge lands inside a pixel, which comes out grey in proportion to the dark area it covers.
/// The grid is drawn as given, so the quiet zone has to be part of it.
/// </summary>
internal static class AntiAliasedRenderer
{
    public static (byte[] Luminance, int Width, int Height) Render(Func<int, int, bool> isDark, int columns, int rows, float pixelsPerModule, float offsetX, float offsetY)
    {
        var width = (int)MathF.Ceiling(columns * pixelsPerModule) + 2;
        var height = (int)MathF.Ceiling(rows * pixelsPerModule) + 2;
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            canvas.Translate(offsetX, offsetY);
            canvas.Scale(pixelsPerModule);

            // One path, so abutting modules leave no seam between them
            using var path = new SKPath();
            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    if (!isDark(row, column))
                        continue;
                    var start = column;
                    while (column + 1 < columns && isDark(row, column + 1))
                        column++;
                    path.AddRect(new SKRect(start, row, column + 1, row + 1));
                }
            }
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            canvas.DrawPath(path, paint);
        }

        var luminance = new byte[width * height];
        for (var y = 0; y < height; y++)
            bitmap.GetPixelSpan().Slice(y * bitmap.RowBytes, width).CopyTo(luminance.AsSpan(y * width, width));
        return (luminance, width, height);
    }
}
