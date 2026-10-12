namespace FeatherQR.Tests;

/// <summary>
/// Renders a module grid under a texture finer than a module, as a screen photographed close up shows its pixel grid: the light pixels are a checkerboard of white and a level dark enough to fall under the threshold, and the dark modules are black.
/// A block of two cells by two holds as many of each, so the image scaled down by twice the cell is a plain two-level symbol, and at any larger scale no run of light is longer than a cell.
/// The grid is drawn as given, so the quiet zone has to be part of it.
/// </summary>
internal static class FineTextureRenderer
{
    /// <param name="pixelsPerModule">A module's side in pixels; a multiple of twice <paramref name="cell"/> keeps module edges on block edges when the offsets are too.</param>
    /// <param name="offsetX">Textured pixels left of the grid.</param>
    /// <param name="offsetY">Textured pixels above the grid.</param>
    /// <param name="cell">The checkerboard's cell in pixels.</param>
    /// <param name="dimmed">The darker level of a light pixel.</param>
    public static (byte[] Luminance, int Width, int Height) Render(Func<int, int, bool> isDark, int columns, int rows, int pixelsPerModule, int offsetX = 0, int offsetY = 0, int cell = 1, byte dimmed = 40)
    {
        var width = offsetX + columns * pixelsPerModule;
        var height = offsetY + rows * pixelsPerModule;
        var luminance = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var column = x >= offsetX ? (x - offsetX) / pixelsPerModule : -1;
                var row = y >= offsetY ? (y - offsetY) / pixelsPerModule : -1;
                if (row >= 0 && column >= 0 && isDark(row, column))
                    continue;
                luminance[y * width + x] = ((x / cell + y / cell) & 1) == 0 ? (byte)255 : dimmed;
            }
        }
        return (luminance, width, height);
    }
}
