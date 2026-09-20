namespace FeatherQR.Tests;

/// <summary>
/// Renders a module grid crisp at a fractional scale and sub-pixel offset: each pixel takes the module under its centre, so every pixel is wholly dark or wholly light and a module comes out the scale rounded down or up wide.
/// The grid is drawn as given, so the quiet zone has to be part of it.
/// </summary>
internal static class NearestNeighbourRenderer
{
    public static (byte[] Luminance, int Width, int Height) Render(Func<int, int, bool> isDark, int columns, int rows, float pixelsPerModule, float offsetX, float offsetY)
    {
        var width = (int)MathF.Ceiling(columns * pixelsPerModule) + 2;
        var height = (int)MathF.Ceiling(rows * pixelsPerModule) + 2;
        var luminance = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var column = (int)MathF.Floor((x + 0.5f - offsetX) / pixelsPerModule);
                var row = (int)MathF.Floor((y + 0.5f - offsetY) / pixelsPerModule);
                luminance[y * width + x] = row >= 0 && column >= 0 && row < rows && column < columns && isDark(row, column) ? (byte)0 : (byte)255;
            }
        }
        return (luminance, width, height);
    }

    /// <summary>The image turned by quarter turns clockwise, then mirrored left to right.</summary>
    public static (byte[] Luminance, int Width, int Height) Turn(byte[] luminance, int width, int height, int quarterTurns, bool mirror)
    {
        for (var turn = 0; turn < quarterTurns; turn++)
        {
            var turned = new byte[luminance.Length];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                    turned[x * height + (height - 1 - y)] = luminance[y * width + x];
            }
            luminance = turned;
            (width, height) = (height, width);
        }
        if (mirror)
        {
            var mirrored = new byte[luminance.Length];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                    mirrored[y * width + (width - 1 - x)] = luminance[y * width + x];
            }
            luminance = mirrored;
        }
        return (luminance, width, height);
    }
}
