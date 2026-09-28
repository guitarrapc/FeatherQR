namespace FeatherQR.Tests;

/// <summary>
/// Renders a symbol bowed off the plane: with a 4-module quiet zone, its rows bent into arcs that sag <c>bowModules</c> at the middle column, then turned about the centre of a square canvas; 2×2 supersampled.
/// No single projective map reads such a symbol; a grid that follows the alignment lattice does.
/// </summary>
internal static class BowedRenderer
{
    public static (byte[] Luminance, int Side) Render(QRCodeData qr, float pixelsPerModule, float degrees, float bowModules)
        => Render((row, column) => qr[row, column], qr.Size, pixelsPerModule, degrees, bowModules);

    /// <param name="isDark">Module colours of the symbol, quiet zone excluded.</param>
    /// <param name="size">Modules a side, quiet zone excluded.</param>
    public static (byte[] Luminance, int Side) Render(Func<int, int, bool> isDark, int size, float pixelsPerModule, float degrees, float bowModules)
    {
        var span = size + 8;
        var side = (int)((span + 2 * bowModules) * pixelsPerModule * 1.45f) + 8;
        var luminance = new byte[side * side];
        Array.Fill(luminance, (byte)255);
        var radians = degrees * Math.PI / 180.0;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        var centre = side / 2.0;
        var half = span * pixelsPerModule / 2.0;
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var dark = 0;
                for (var sy = 0; sy < 2; sy++)
                {
                    for (var sx = 0; sx < 2; sx++)
                    {
                        double px = x + 0.25 + sx * 0.5 - centre, py = y + 0.25 + sy * 0.5 - centre;
                        // Undo the turn, then the bow
                        var s = (px * cos + py * sin + half) / pixelsPerModule;
                        var t = (-px * sin + py * cos + half) / pixelsPerModule - bowModules * Math.Sin(Math.PI * s / span);
                        var column = (int)Math.Floor(s) - 4;
                        var row = (int)Math.Floor(t) - 4;
                        if (row >= 0 && column >= 0 && row < size && column < size && isDark(row, column))
                            dark++;
                    }
                }
                luminance[y * side + x] = (byte)((4 - dark) * 255 / 4);
            }
        }
        return (luminance, side);
    }

    /// <summary>Where <see cref="Render(QRCodeData, float, float, float)"/> draws grid point (<paramref name="u"/>, <paramref name="v"/>) of the symbol, quiet zone excluded.</summary>
    public static (float X, float Y) ToPixel(int size, float pixelsPerModule, float degrees, float bowModules, int side, float u, float v)
    {
        var span = size + 8;
        var s = u + 4.0;
        var fx = s * pixelsPerModule - span * pixelsPerModule / 2.0;
        var fy = (v + 4.0 + bowModules * Math.Sin(Math.PI * s / span)) * pixelsPerModule - span * pixelsPerModule / 2.0;
        var radians = degrees * Math.PI / 180.0;
        return ((float)(side / 2.0 + fx * Math.Cos(radians) - fy * Math.Sin(radians)), (float)(side / 2.0 + fx * Math.Sin(radians) + fy * Math.Cos(radians)));
    }
}
