using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Internals.MicroQR;

/// <summary>
/// Counts the dark modules on the quiet-zone line of a sampled grid, the ring next to the symbol: column <c>size</c> from row 0 to row <c>size</c>, and row <c>size</c> from column 0 to column <c>size − 1</c>.
/// This ring, not the one outside it: a grid a little off the real symbol puts it on the symbol's own edge modules, which the outer ring would not see.
/// A point outside the image counts as light, as an image cropped at the symbol has no quiet zone to show.
/// </summary>
/// <remarks>
/// Asked only when the rest of the grid's structure cannot earn the corrections its read needs, so most grids never sample it.
/// </remarks>
internal interface IMicroQRQuietZone
{
    int CountDark(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, int size);
}

/// <summary>The quiet zone of a grid sampled through an affine frame (origin at the symbol's corner, one module per axis step).</summary>
internal readonly struct AffineQuietZone(float originX, float originY, float uX, float uY, float vX, float vY) : IMicroQRQuietZone
{
    public int CountDark(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, int size)
    {
        var dark = 0;
        var outside = size + 0.5f;
        for (var i = 0; i <= size; i++)
        {
            var along = i + 0.5f;
            dark += IsDark(luminance, width, height, threshold, originX + outside * uX + along * vX, originY + outside * uY + along * vY);
            if (i < size)
                dark += IsDark(luminance, width, height, threshold, originX + along * uX + outside * vX, originY + along * uY + outside * vY);
        }
        return dark;
    }

    internal static int IsDark(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, float x, float y)
    {
        // Negated so a NaN coordinate reads as outside
        if (!(x >= 0f && y >= 0f && x < width && y < height))
            return 0;
        return luminance[(int)y * width + (int)x] < threshold ? 1 : 0;
    }
}

/// <summary>The quiet zone of a grid sampled through a perspective transform from module coordinates.</summary>
internal readonly struct ProjectiveQuietZone(in PerspectiveTransform transform) : IMicroQRQuietZone
{
    private readonly PerspectiveTransform _transform = transform;

    public int CountDark(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, int size)
    {
        var dark = 0;
        var outside = size + 0.5f;
        for (var i = 0; i <= size; i++)
        {
            var along = i + 0.5f;
            _transform.Transform(outside, along, out var x, out var y);
            dark += AffineQuietZone.IsDark(luminance, width, height, threshold, x, y);
            if (i < size)
            {
                _transform.Transform(along, outside, out x, out y);
                dark += AffineQuietZone.IsDark(luminance, width, height, threshold, x, y);
            }
        }
        return dark;
    }
}

/// <summary>A quiet zone counted when the grid was sampled.</summary>
internal readonly struct CountedQuietZone(int dark) : IMicroQRQuietZone
{
    public int CountDark(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, int size) => dark;
}
