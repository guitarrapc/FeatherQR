namespace FeatherQR.Tests;

/// <summary>
/// Renders a module grid at 1 px/module and scales it up bilinearly, the way an image viewer or a browser enlarges a small code: every module edge is a ramp over a module or more, and no pixel away from a module's centre is wholly dark or light.
/// Each output pixel centre maps back to the source at (x + 0.5) · source / output − 0.5 and takes the four source pixels round it, clamped at the border.
/// The grid is drawn as given, so the quiet zone has to be part of it.
/// </summary>
internal static class BilinearUpscaleRenderer
{
    public static (byte[] Luminance, int Width, int Height) Render(Func<int, int, bool> isDark, int columns, int rows, float pixelsPerModule)
    {
        var width = (int)MathF.Round(columns * pixelsPerModule);
        var height = (int)MathF.Round(rows * pixelsPerModule);
        var scaleX = (float)columns / width;
        var scaleY = (float)rows / height;
        var luminance = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            var sourceY = (y + 0.5f) * scaleY - 0.5f;
            var top = (int)MathF.Floor(sourceY);
            var fy = sourceY - top;
            for (var x = 0; x < width; x++)
            {
                var sourceX = (x + 0.5f) * scaleX - 0.5f;
                var left = (int)MathF.Floor(sourceX);
                var fx = sourceX - left;
                var upper = Level(left, top) * (1f - fx) + Level(left + 1, top) * fx;
                var lower = Level(left, top + 1) * (1f - fx) + Level(left + 1, top + 1) * fx;
                luminance[y * width + x] = (byte)MathF.Round(upper * (1f - fy) + lower * fy);
            }
        }
        return (luminance, width, height);

        float Level(int column, int row)
        {
            column = Math.Clamp(column, 0, columns - 1);
            row = Math.Clamp(row, 0, rows - 1);
            return isDark(row, column) ? 0f : 255f;
        }
    }
}
