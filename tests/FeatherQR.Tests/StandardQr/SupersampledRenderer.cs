namespace FeatherQR.Tests;

/// <summary>
/// Renders a symbol into a grayscale buffer under an arbitrary rotation and keystone, with 2×2 supersampling: an edge pixel comes out grey in proportion to the dark area it covers, which is what an anti-aliased render or a camera gives and what a whole-pixel render never does.
/// The symbol is centered in a canvas 1.45 times its side, so its edges fall at fractional pixel positions.
/// </summary>
internal static class SupersampledRenderer
{
    public static (byte[] Luminance, int Side) Render(QRCodeData qr, float pixelsPerModule, float degrees, float keystone = 0f)
    {
        var modules = qr.Size;
        var span = modules + 8f; // 4-module quiet zone each side
        var symbolSide = span * pixelsPerModule;
        var side = (int)(symbolSide * 1.45f) + 8;
        var luminance = new byte[side * side];
        Array.Fill(luminance, (byte)255);

        // Destination corners (TL, TR, BR, BL); the top edge shrinks by the keystone
        var half = symbolSide / 2f;
        var top = half * (1f - keystone);
        float[] corners = [-top, -half, top, -half, half, half, -half, half];
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

        var toModules = Homography(corners, [0, 0, span, 0, span, span, 0, span]);
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
                        if (u < 0 || v < 0 || u >= span || v >= span)
                            continue;
                        inside++;
                        var column = (int)u - 4;
                        var row = (int)v - 4;
                        if (row >= 0 && column >= 0 && row < modules && column < modules && qr[row, column])
                            dark++;
                    }
                }
                if (inside > 0)
                    luminance[y * side + x] = (byte)((4 - dark) * 255 / 4);
            }
        }

        return (luminance, side);
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
