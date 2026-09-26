using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>The geometry a <see cref="SupersampledRenderer"/> render was drawn with.</summary>
internal static class SupersampledGeometry
{
    /// <summary>Grid point (u, v) of the symbol, quiet zone excluded, to its pixel position; module (c, r) spans u from c to c + 1 and v from r to r + 1.</summary>
    public static PerspectiveTransform GridToPixel(int columns, int rows, float pixelsPerModule, float degrees, float keystone)
        => GridToPixel(columns, rows, pixelsPerModule, degrees, keystone, tiltDegrees: 0f);

    /// <summary><see cref="GridToPixel(int, int, float, float, float)"/> for a plane tilted about both axes (<see cref="SupersampledRenderer.SpanCorners(int, int, float, float, float, float, out int)"/>).</summary>
    public static PerspectiveTransform GridToPixel(int columns, int rows, float pixelsPerModule, float degrees, float keystone, float tiltDegrees)
    {
        var corners = SupersampledRenderer.SpanCorners(columns, rows, pixelsPerModule, degrees, keystone, tiltDegrees, out _);
        return PerspectiveTransform.QuadrilateralToQuadrilateral(
            -4f, -4f, columns + 4f, -4f, columns + 4f, rows + 4f, -4f, rows + 4f,
            corners[0], corners[1], corners[2], corners[3], corners[4], corners[5], corners[6], corners[7]);
    }
}
