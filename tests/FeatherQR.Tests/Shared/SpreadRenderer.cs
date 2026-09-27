namespace FeatherQR.Tests;

/// <summary>
/// Renders a module grid whose dark area is grown or shrunk by a fixed distance at every edge, as ink spread or a thin print draws it: every dark run along any line is longer or shorter by the same amount, and every light run the other way.
/// The grid is turned about its centre and sampled 4 × 4 a pixel; a 4-module quiet zone surrounds it.
/// </summary>
internal static class SpreadRenderer
{
    /// <param name="isDark">The module grid, row then column.</param>
    /// <param name="columns">Modules across.</param>
    /// <param name="rows">Modules down.</param>
    /// <param name="pixelsPerModule">Scale.</param>
    /// <param name="degrees">Turn, clockwise.</param>
    /// <param name="spreadModules">How far every dark edge moves outward, in modules; negative shrinks the dark area.</param>
    /// <param name="binarized">Each pixel 0 or 255 at its sampled coverage's midpoint, as a camera that thresholds does.</param>
    public static (byte[] Luminance, int Width, int Height) Render(Func<int, int, bool> isDark, int columns, int rows, float pixelsPerModule, float degrees, float spreadModules, bool binarized)
    {
        var spanX = columns + 8f;
        var spanY = rows + 8f;
        var diagonal = MathF.Sqrt(spanX * spanX + spanY * spanY) * pixelsPerModule;
        var side = (int)MathF.Ceiling(diagonal) + 4;
        var luminance = new byte[side * side];
        var radians = degrees * MathF.PI / 180f;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        var reach = (int)MathF.Ceiling(Math.Abs(spreadModules)) + 1;

        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var dark = 0;
                for (var sy = 0; sy < 4; sy++)
                {
                    for (var sx = 0; sx < 4; sx++)
                    {
                        // Pixel to module coordinates: undo the turn about the canvas centre
                        var dx = x + (sx + 0.5f) / 4f - side / 2f;
                        var dy = y + (sy + 0.5f) / 4f - side / 2f;
                        var u = (cos * dx + sin * dy) / pixelsPerModule + spanX / 2f - 4f;
                        var v = (-sin * dx + cos * dy) / pixelsPerModule + spanY / 2f - 4f;
                        if (IsDarkAt(isDark, columns, rows, u, v, spreadModules, reach))
                            dark++;
                    }
                }
                luminance[y * side + x] = binarized ? (dark >= 8 ? (byte)0 : (byte)255) : (byte)((16 - dark) * 255 / 16);
            }
        }
        return (luminance, side, side);
    }

    /// <summary>Where module (u, v) lands in the rendered image, the same turn as <see cref="Render"/>.</summary>
    public static (float X, float Y) ToImage(int columns, int rows, float pixelsPerModule, float degrees, int side, float u, float v)
    {
        var radians = degrees * MathF.PI / 180f;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        var mu = (u - columns / 2f) * pixelsPerModule;
        var mv = (v - rows / 2f) * pixelsPerModule;
        return (cos * mu - sin * mv + side / 2f, sin * mu + cos * mv + side / 2f);
    }

    private static bool IsDarkAt(Func<int, int, bool> isDark, int columns, int rows, float u, float v, float spread, int reach)
    {
        var column = (int)MathF.Floor(u);
        var row = (int)MathF.Floor(v);
        var inside = Dark(isDark, columns, rows, row, column);
        // Grown: dark within the spread of any dark module. Shrunk: dark only farther than the spread from every light one.
        var nearest = float.MaxValue;
        for (var r = row - reach; r <= row + reach; r++)
        {
            for (var c = column - reach; c <= column + reach; c++)
            {
                if (Dark(isDark, columns, rows, r, c) == inside)
                    continue;
                var ex = Math.Max(Math.Max(c - u, 0f), u - (c + 1));
                var ey = Math.Max(Math.Max(r - v, 0f), v - (r + 1));
                nearest = Math.Min(nearest, MathF.Sqrt(ex * ex + ey * ey));
            }
        }
        return inside ? nearest > -spread : nearest < spread;
    }

    private static bool Dark(Func<int, int, bool> isDark, int columns, int rows, int row, int column)
        => row >= 0 && column >= 0 && row < rows && column < columns && isDark(row, column);
}
