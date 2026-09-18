namespace FeatherQR.Tests;

/// <summary>
/// Renders a module grid half a pixel off the pixel grid with a 4-module quiet zone: every module edge falls inside a pixel that comes out <c>edgeGray</c>.
/// A grey darker than the decoder's threshold widens every dark run by a pixel (ink spread), a lighter one narrows it (erosion).
/// </summary>
internal static class HalfPixelRenderer
{
    public static (byte[] Luminance, int Width, int Height) Render(Func<int, int, bool> isDark, int columns, int rows, int pixelsPerModule, byte edgeGray)
    {
        const int quietZone = 4;
        var width = (columns + 2 * quietZone) * pixelsPerModule + 1;
        var height = (rows + 2 * quietZone) * pixelsPerModule + 1;
        var luminance = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                // Two samples per axis at the pixel's quarter points, grid shifted by half a pixel
                var dark = 0;
                for (var sy = 0; sy < 2; sy++)
                {
                    for (var sx = 0; sx < 2; sx++)
                    {
                        var row = (int)Math.Floor((y + 0.25f + sy * 0.5f - 0.5f) / pixelsPerModule) - quietZone;
                        var column = (int)Math.Floor((x + 0.25f + sx * 0.5f - 0.5f) / pixelsPerModule) - quietZone;
                        if (row >= 0 && column >= 0 && row < rows && column < columns && isDark(row, column))
                            dark++;
                    }
                }
                luminance[y * width + x] = dark switch { 0 => (byte)255, 4 => (byte)0, _ => edgeGray };
            }
        }
        return (luminance, width, height);
    }
}
