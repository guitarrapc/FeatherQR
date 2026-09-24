namespace FeatherQR.Internals.ImageDecoders;

/// <summary>Luminance at a point between pixel centres.</summary>
internal static class LuminanceSampler
{
    /// <summary>
    /// Bilinear interpolation between the four pixel centres around (<paramref name="x"/>, <paramref name="y"/>), in image coordinates where pixel edges sit on integers and centres at half-integers.
    /// Points past the outer centres take the edge pixels.
    /// </summary>
    public static float Bilinear(ReadOnlySpan<byte> luminance, int width, int height, float x, float y)
    {
        x -= 0.5f;
        y -= 0.5f;
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var fx = x - x0;
        var fy = y - y0;
        var left = Clamp(x0, width);
        var right = Clamp(x0 + 1, width);
        var top = Clamp(y0, height) * width;
        var bottom = Clamp(y0 + 1, height) * width;
        var upper = luminance[top + left] + (luminance[top + right] - luminance[top + left]) * fx;
        var lower = luminance[bottom + left] + (luminance[bottom + right] - luminance[bottom + left]) * fx;
        return upper + (lower - upper) * fy;

        static int Clamp(int value, int length) => value < 0 ? 0 : value >= length ? length - 1 : value;
    }
}
