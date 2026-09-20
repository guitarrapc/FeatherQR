using SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// The corners a decode should report for an axis-aligned render, read off the pixels: the
/// finders and timing patterns reach every edge of the module area, so the bounding box of the
/// dark pixels is the symbol, whatever widths the renderer snapped its modules to.
/// </summary>
internal static class DrawnCorners
{
    /// <summary>Asserts every corner within <paramref name="toleranceModules"/> of a module, in the symbol's own order.</summary>
    /// <param name="modules">The symbol's width in modules, quiet zone excluded.</param>
    /// <param name="mirrored">The render was flipped left to right after drawing: the symbol's top-left is the box's top-right.</param>
    public static async Task AssertMatch(SKBitmap bitmap, SymbolCorners actual, int modules, float toleranceModules, bool mirrored = false)
    {
        var (left, top, right, bottom) = DarkBounds(bitmap);
        var tolerance = toleranceModules * (right - left) / modules;
        var (topLeft, topRight, bottomRight, bottomLeft) = mirrored
            ? ((right, top), (left, top), (left, bottom), (right, bottom))
            : ((left, top), (right, top), (right, bottom), (left, bottom));

        await Assert.That(actual.IsEmpty).IsFalse();
        await AssertNear(actual.TopLeft, topLeft, tolerance, "TopLeft");
        await AssertNear(actual.TopRight, topRight, tolerance, "TopRight");
        await AssertNear(actual.BottomRight, bottomRight, tolerance, "BottomRight");
        await AssertNear(actual.BottomLeft, bottomLeft, tolerance, "BottomLeft");
    }

    /// <summary>Continuous coordinates: (0, 0) is the top-left corner of the top-left pixel.</summary>
    public static (float Left, float Top, float Right, float Bottom) DarkBounds(SKBitmap bitmap)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Red >= 128)
                    continue;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }
        return (minX, minY, maxX + 1, maxY + 1);
    }

    public static SKBitmap MirrorLeftToRight(SKBitmap source)
    {
        var mirrored = new SKBitmap(source.Width, source.Height);
        using var canvas = new SKCanvas(mirrored);
        canvas.Clear(SKColors.White);
        canvas.Scale(-1, 1, source.Width / 2f, 0);
        canvas.DrawBitmap(source, 0, 0);
        return mirrored;
    }

    private static async Task AssertNear(ImagePoint actual, (float X, float Y) expected, float tolerance, string corner)
    {
        var dx = actual.X - expected.X;
        var dy = actual.Y - expected.Y;
        var distance = MathF.Sqrt(dx * dx + dy * dy);
        await Assert.That(distance).IsLessThanOrEqualTo(tolerance)
            .Because($"{corner}: got ({actual.X:F2}, {actual.Y:F2}), expected ({expected.X:F2}, {expected.Y:F2}), off by {distance:F2}px");
    }
}
