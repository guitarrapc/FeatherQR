namespace FeatherQR.Tests;

/// <summary>How the light falls on an <see cref="UnevenLightingRenderer"/> render.</summary>
public enum UnevenLight
{
    /// <summary>Full light on one side, dimming linearly to <c>1 - depth</c> on the other.</summary>
    Ramp,
    /// <summary>Full light on one half, <c>1 - depth</c> on the other, across an edge a few modules wide.</summary>
    Shadow,
}

/// <summary>
/// Renders a module grid printed on paper and lit unevenly: each pixel is the reflectance of what it covers (dark ink or light paper) times the light that falls there.
/// A ramp dims the light steadily across the image; a shadow drops it across a soft edge through the middle. Either way one global threshold puts a whole side of the symbol in one class, which is what a photograph with a shading gradient does.
/// The whole-scale form draws the grid as given, so its quiet zone has to be part of it; the turned form adds a light margin of two modules.
/// </summary>
internal static class UnevenLightingRenderer
{
    public const byte DarkReflectance = 30;
    public const byte LightReflectance = 220;

    /// <summary>Crisp at a whole scale, the image exactly the grid.</summary>
    /// <param name="degrees">Direction the light falls off towards: 0 is to the right, 90 downwards.</param>
    /// <param name="depth">How much light the dimmest part loses, 0 to 1.</param>
    public static (byte[] Luminance, int Width, int Height) Render(Func<int, int, bool> isDark, int columns, int rows, int pixelsPerModule, UnevenLight light, float degrees, float depth)
    {
        var width = columns * pixelsPerModule;
        var height = rows * pixelsPerModule;
        var illumination = Illumination(width, height, pixelsPerModule, light, degrees, depth);
        var luminance = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var reflectance = isDark(y / pixelsPerModule, x / pixelsPerModule) ? DarkReflectance : LightReflectance;
                luminance[y * width + x] = (byte)MathF.Round(reflectance * illumination[y * width + x]);
            }
        }
        return (luminance, width, height);
    }

    /// <summary>
    /// The same light over a symbol turned by any angle and moved by an offset in pixels from a point 1.5 px above the image centre, crisp or anti-aliased (4 × 4 samples a pixel), in a light margin of two modules while the offset is within half a pixel across and -0.5 to 3.5 px down.
    /// </summary>
    /// <param name="turnDegrees">The symbol's rotation.</param>
    /// <param name="offsetX">Pixels right, whole or fractional: a fractional offset moves where module edges fall inside pixels.</param>
    /// <param name="offsetY">Pixels down, whole or fractional: a whole offset also moves a finder's rows against a row-strided scan.</param>
    /// <param name="lightDegrees">Direction the light falls off towards: 0 is to the right, 90 downwards.</param>
    public static (byte[] Luminance, int Width, int Height) Render(Func<int, int, bool> isDark, int columns, int rows, float pixelsPerModule, float turnDegrees, float offsetX, float offsetY, bool antiAliased, UnevenLight light, float lightDegrees, float depth)
    {
        var radians = turnDegrees * MathF.PI / 180f;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        var symbolWidth = columns * pixelsPerModule;
        var symbolHeight = rows * pixelsPerModule;
        var margin = 2 * pixelsPerModule;
        var width = (int)MathF.Ceiling(MathF.Abs(symbolWidth * cos) + MathF.Abs(symbolHeight * sin) + 2 * margin + 1);
        var height = (int)MathF.Ceiling(MathF.Abs(symbolWidth * sin) + MathF.Abs(symbolHeight * cos) + 2 * margin + 4);
        var centreX = width / 2f + offsetX;
        var centreY = height / 2f + offsetY - 1.5f;
        var samples = antiAliased ? 4 : 1;
        var illumination = Illumination(width, height, pixelsPerModule, light, lightDegrees, depth);

        var luminance = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var reflectance = 0f;
                for (var sy = 0; sy < samples; sy++)
                {
                    for (var sx = 0; sx < samples; sx++)
                    {
                        var px = x + (sx + 0.5f) / samples - centreX;
                        var py = y + (sy + 0.5f) / samples - centreY;
                        // Back into the symbol's own axes
                        var column = (px * cos + py * sin + symbolWidth / 2f) / pixelsPerModule;
                        var row = (-px * sin + py * cos + symbolHeight / 2f) / pixelsPerModule;
                        var dark = column >= 0 && row >= 0 && column < columns && row < rows && isDark((int)row, (int)column);
                        reflectance += dark ? DarkReflectance : LightReflectance;
                    }
                }
                luminance[y * width + x] = (byte)MathF.Round(reflectance / (samples * samples) * illumination[y * width + x]);
            }
        }
        return (luminance, width, height);
    }

    /// <summary>The light at each pixel centre, 1 at full light.</summary>
    private static float[] Illumination(int width, int height, float pixelsPerModule, UnevenLight light, float degrees, float depth)
    {
        var radians = degrees * MathF.PI / 180f;
        var dx = MathF.Cos(radians);
        var dy = MathF.Sin(radians);

        // Projections of the four corners bound the light's axis over the image
        var min = float.MaxValue;
        var max = float.MinValue;
        foreach (var (cx, cy) in new[] { (0f, 0f), (width, 0f), (0f, height), ((float)width, (float)height) })
        {
            var p = cx * dx + cy * dy;
            min = MathF.Min(min, p);
            max = MathF.Max(max, p);
        }
        var softness = 1.5f * pixelsPerModule;

        var illumination = new float[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var along = ((x + 0.5f) * dx + (y + 0.5f) * dy - min) / (max - min); // 0 to 1
                float dimming;
                if (light == UnevenLight.Ramp)
                {
                    dimming = along;
                }
                else
                {
                    var t = Math.Clamp(((along - 0.5f) * (max - min) + softness) / (2 * softness), 0f, 1f);
                    dimming = t * t * (3 - 2 * t);
                }
                illumination[y * width + x] = 1f - depth * dimming;
            }
        }
        return illumination;
    }
}
