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
/// Renders a module grid crisp at a whole scale, printed on paper and lit unevenly: each pixel is the reflectance of its module (dark ink or light paper) times the light that falls there.
/// A ramp dims the light steadily across the image; a shadow drops it across a soft edge through the middle. Either way one global threshold puts a whole side of the symbol in one class, which is what a photograph with a shading gradient does.
/// The grid is drawn as given, so the quiet zone has to be part of it.
/// </summary>
internal static class UnevenLightingRenderer
{
    public const byte DarkReflectance = 30;
    public const byte LightReflectance = 220;

    /// <param name="degrees">Direction the light falls off towards: 0 is to the right, 90 downwards.</param>
    /// <param name="depth">How much light the dimmest part loses, 0 to 1.</param>
    public static (byte[] Luminance, int Width, int Height) Render(Func<int, int, bool> isDark, int columns, int rows, int pixelsPerModule, UnevenLight light, float degrees, float depth)
    {
        var width = columns * pixelsPerModule;
        var height = rows * pixelsPerModule;
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

        var luminance = new byte[width * height];
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
                var illumination = 1f - depth * dimming;
                var reflectance = isDark(y / pixelsPerModule, x / pixelsPerModule) ? DarkReflectance : LightReflectance;
                luminance[y * width + x] = (byte)MathF.Round(reflectance * illumination);
            }
        }
        return (luminance, width, height);
    }
}
