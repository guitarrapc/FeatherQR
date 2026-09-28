namespace FeatherQR.Tests;

/// <summary>
/// Renders a styled symbol: every dark module outside the finders a dot of <c>diameter</c> modules, its centre shifted by (<c>shiftU</c>, <c>shiftV</c>) modules off the module's centre, and each finder three concentric rings (centre disc, dark ring) on the grid; turned about the centre of a square canvas, 4×4 supersampled.
/// </summary>
internal static class DottedRenderer
{
    /// <param name="qr">The symbol with its quiet zone.</param>
    public static (byte[] Luminance, int Side) Render(QRCodeData qr, float pixelsPerModule, float degrees, float diameter, float shiftU, float shiftV)
    {
        var size = qr.Size;
        var side = (int)(size * pixelsPerModule * 1.45f) + 4;
        var luminance = new byte[side * side];
        var radians = degrees * Math.PI / 180;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        var centre = side / 2.0;
        var half = size * pixelsPerModule / 2.0;
        var quietZone = (size - (17 + 4 * qr.Version)) / 2;
        (double U, double V)[] finders = [(quietZone + 3.5, quietZone + 3.5), (size - quietZone - 3.5, quietZone + 3.5), (quietZone + 3.5, size - quietZone - 3.5)];
        var radiusSquared = diameter * diameter / 4.0;
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var dark = 0;
                for (var sy = 0; sy < 4; sy++)
                {
                    for (var sx = 0; sx < 4; sx++)
                    {
                        double px = x + (sx + 0.5) / 4 - centre, py = y + (sy + 0.5) / 4 - centre;
                        var u = (px * cos + py * sin + half) / pixelsPerModule;
                        var v = (-px * sin + py * cos + half) / pixelsPerModule;
                        if (IsDark(qr, finders, u, v, shiftU, shiftV, radiusSquared))
                            dark++;
                    }
                }
                luminance[y * side + x] = (byte)(255 - dark * 255 / 16);
            }
        }
        return (luminance, side);
    }

    private static bool IsDark(QRCodeData qr, (double U, double V)[] finders, double u, double v, float shiftU, float shiftV, double radiusSquared)
    {
        foreach (var (fu, fv) in finders)
        {
            if (Math.Abs(u - fu) < 4 && Math.Abs(v - fv) < 4)
            {
                var r = Math.Sqrt((u - fu) * (u - fu) + (v - fv) * (v - fv));
                return r <= 1.5 || (r >= 2.5 && r <= 3.5);
            }
        }
        var column = (int)Math.Floor(u - shiftU);
        var row = (int)Math.Floor(v - shiftV);
        if (row < 0 || column < 0 || row >= qr.Size || column >= qr.Size || !qr[row, column])
            return false;
        var du = u - shiftU - (column + 0.5);
        var dv = v - shiftV - (row + 0.5);
        return du * du + dv * dv <= radiusSquared;
    }
}
