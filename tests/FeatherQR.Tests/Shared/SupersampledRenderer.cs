namespace FeatherQR.Tests;

/// <summary>
/// Renders a module grid into a grayscale buffer under an arbitrary rotation and keystone, with 2×2 supersampling: an edge pixel comes out grey in proportion to the dark area it covers, which is what an anti-aliased render or a camera gives and what a whole-pixel render never does.
/// The symbol is centered in a canvas 1.45 times its side, so its edges fall at fractional pixel positions.
/// </summary>
internal static class SupersampledRenderer
{
    public static (byte[] Luminance, int Side) Render(QRCodeData qr, float pixelsPerModule, float degrees, float keystone = 0f)
    {
        var (luminance, side, _) = Render((row, col) => qr[row, col], qr.Size, qr.Size, pixelsPerModule, degrees, keystone);
        return (luminance, side);
    }

    public static (byte[] Luminance, int Width, int Height) Render(Func<int, int, bool> isDark, int columns, int rows, float pixelsPerModule, float degrees, float keystone = 0f)
        => Render(isDark, columns, rows, pixelsPerModule, degrees, keystone, tiltDegrees: 0f);

    /// <summary>
    /// The plane tilted about both of the symbol's axes: the keystone narrows the plane along a direction <paramref name="tiltDegrees"/> from the symbol's own vertical, so at 45° the near and far points are two of the symbol's corners and every finder is drawn stretched off its own axes.
    /// </summary>
    public static (byte[] Luminance, int Width, int Height) Render(Func<int, int, bool> isDark, int columns, int rows, float pixelsPerModule, float degrees, float keystone, float tiltDegrees)
    {
        var spanX = columns + 8f; // 4-module quiet zone each side
        var spanY = rows + 8f;
        var corners = SpanCorners(columns, rows, pixelsPerModule, degrees, keystone, tiltDegrees, out var side);
        var luminance = new byte[side * side];
        Array.Fill(luminance, (byte)255);

        var toModules = Homography(corners, [0, 0, spanX, 0, spanX, spanY, 0, spanY]);
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                int dark = 0, inside = 0;
                for (var sy = 0; sy < 2; sy++)
                {
                    for (var sx = 0; sx < 2; sx++)
                    {
                        double px = x + 0.25 + sx * 0.5, py = y + 0.25 + sy * 0.5;
                        var d = toModules[6] * px + toModules[7] * py + 1;
                        var u = (toModules[0] * px + toModules[1] * py + toModules[2]) / d;
                        var v = (toModules[3] * px + toModules[4] * py + toModules[5]) / d;
                        if (u < 0 || v < 0 || u >= spanX || v >= spanY)
                            continue;
                        inside++;
                        var column = (int)u - 4;
                        var row = (int)v - 4;
                        if (row >= 0 && column >= 0 && row < rows && column < columns && isDark(row, column))
                            dark++;
                    }
                }
                if (inside > 0)
                    luminance[y * side + x] = (byte)((4 - dark) * 255 / 4);
            }
        }

        return (luminance, side, side);
    }

    /// <summary>
    /// Where <see cref="Render(Func{int, int, bool}, int, int, float, float, float)"/> puts the corners of the symbol and its 4-module quiet zone (top-left, top-right, bottom-right, bottom-left, x then y), and the side of its square canvas: module column <c>c</c> of the grid lies at <c>c + 4</c> of that span.
    /// </summary>
    public static float[] SpanCorners(int columns, int rows, float pixelsPerModule, float degrees, float keystone, out int side)
        => SpanCorners(columns, rows, pixelsPerModule, degrees, keystone, tiltDegrees: 0f, out side);

    /// <summary>
    /// <see cref="SpanCorners(int, int, float, float, float, out int)"/> with the keystone along a direction <paramref name="tiltDegrees"/> from the symbol's own vertical.
    /// The span is turned by the tilt, the box round it is narrowed at the top by the keystone as a plane in perspective, and the result is turned by <paramref name="degrees"/>; the box's height and bottom edge keep their length, so the depth ratio between the span's nearest and farthest points is 1/(1 − keystone) at every tilt.
    /// </summary>
    public static float[] SpanCorners(int columns, int rows, float pixelsPerModule, float degrees, float keystone, float tiltDegrees, out int side)
    {
        var symbolWidth = (columns + 8f) * pixelsPerModule;
        var symbolHeight = (rows + 8f) * pixelsPerModule;
        var halfWidth = symbolWidth / 2f;
        var halfHeight = symbolHeight / 2f;
        float[] corners;
        if (tiltDegrees == 0f)
        {
            side = (int)(Math.Max(symbolWidth, symbolHeight) * 1.45f) + 8;

            // Destination corners (TL, TR, BR, BL); the top edge shrinks by the keystone
            var top = halfWidth * (1f - keystone);
            corners = [-top, -halfHeight, top, -halfHeight, halfWidth, halfHeight, -halfWidth, halfHeight];
        }
        else
        {
            var tilt = tiltDegrees * Math.PI / 180.0;
            double tiltCos = Math.Cos(tilt), tiltSin = Math.Sin(tilt);
            var boxHalfWidth = halfWidth * Math.Abs(tiltCos) + halfHeight * Math.Abs(tiltSin);
            var boxHalfHeight = halfWidth * Math.Abs(tiltSin) + halfHeight * Math.Abs(tiltCos);
            side = (int)(2.0 * Math.Max(boxHalfWidth, boxHalfHeight) * 1.45) + 8;

            // The box's top edge narrowed to 1 − keystone of its bottom by the plane map x' = a·x / (1 + b·y), y' = (y + b·B²) / (1 + b·y), which keeps y = ±B
            var bB = -keystone / (2.0 - keystone);
            var a = (2.0 - 2.0 * keystone) / (2.0 - keystone);
            var b = bB / boxHalfHeight;
            corners = new float[8];
            double[] span = [-halfWidth, -halfHeight, halfWidth, -halfHeight, halfWidth, halfHeight, -halfWidth, halfHeight];
            for (var i = 0; i < 8; i += 2)
            {
                var x = span[i] * tiltCos - span[i + 1] * tiltSin;
                var y = span[i] * tiltSin + span[i + 1] * tiltCos;
                var w = 1.0 + b * y;
                corners[i] = (float)(a * x / w);
                corners[i + 1] = (float)((y + bB * boxHalfHeight) / w);
            }
        }
        var radians = degrees * MathF.PI / 180f;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        for (var i = 0; i < 8; i += 2)
        {
            var x = corners[i];
            var y = corners[i + 1];
            corners[i] = side / 2f + x * cos - y * sin;
            corners[i + 1] = side / 2f + x * sin + y * cos;
        }
        return corners;
    }

    /// <summary>Solves the 4-point homography mapping <paramref name="source"/> to <paramref name="destination"/> (8 coefficients, h33 = 1).</summary>
    private static double[] Homography(float[] source, float[] destination)
    {
        var a = new double[8, 9];
        for (var i = 0; i < 4; i++)
        {
            double x = source[2 * i], y = source[2 * i + 1], u = destination[2 * i], v = destination[2 * i + 1];
            double[] first = [x, y, 1, 0, 0, 0, -u * x, -u * y, u];
            double[] second = [0, 0, 0, x, y, 1, -v * x, -v * y, v];
            for (var k = 0; k < 9; k++)
            {
                a[2 * i, k] = first[k];
                a[2 * i + 1, k] = second[k];
            }
        }
        for (var column = 0; column < 8; column++)
        {
            var pivot = column;
            for (var row = column + 1; row < 8; row++)
            {
                if (Math.Abs(a[row, column]) > Math.Abs(a[pivot, column]))
                    pivot = row;
            }
            for (var k = 0; k < 9; k++)
                (a[column, k], a[pivot, k]) = (a[pivot, k], a[column, k]);
            for (var row = 0; row < 8; row++)
            {
                if (row == column)
                    continue;
                var factor = a[row, column] / a[column, column];
                for (var k = column; k < 9; k++)
                    a[row, k] -= factor * a[column, k];
            }
        }
        var h = new double[8];
        for (var i = 0; i < 8; i++)
            h[i] = a[i, 8] / a[i, i];
        return h;
    }
}
