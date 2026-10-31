using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Internals.StandardQR;

internal static partial class QRImageDecoder
{
    /// <summary>The dimension the plane is fitted below: version 7, the first with an alignment lattice for the mesh to follow.</summary>
    private const int OutlineFitBelowDimension = 17 + 4 * 7;

    /// <summary>
    /// Fits a grid-to-pixel plane to the inner outlines of three finders, optionally including the alignment anchor.
    /// </summary>
    /// <remarks>
    /// <paramref name="anchoredFit"/> includes the alignment point when available.
    /// Returns false from version 7 onward or when an outline cannot be measured, including round or inferred finders.
    /// </remarks>
    internal static bool TryFitToFinderOutlines(in ImageView image, in FinderFrame frame, int dimension, bool anchored, in PerspectiveTransform anchoredTransform, out PerspectiveTransform fitted, out PerspectiveTransform anchoredFit)
    {
        // Each inner outline encloses a five-module square. Its four corners provide local perspective evidence.
        // Fitting these shapes avoids extrapolating a fourth corner from three finder centers alone.
        // Anchored and unanchored fits each read symbols the other misses.
        fitted = default;
        anchoredFit = default;
        if (dimension >= OutlineFitBelowDimension)
            return false;
        var topRight = frame.TopRight;
        var bottomLeft = frame.BottomLeft;
        var fit = new ProjectiveFit((topRight.X + bottomLeft.X) / 2f, (topRight.Y + bottomLeft.Y) / 2f, Math.Max(1f, Distance(topRight, bottomLeft)));
        var far = dimension - 3.5f;
        if (!TryAddOutline(ref fit, image, frame, frame.TopLeft, 3.5f, 3.5f, dimension)
            || !TryAddOutline(ref fit, image, frame, topRight, far, 3.5f, dimension)
            || !TryAddOutline(ref fit, image, frame, bottomLeft, 3.5f, far, dimension))
        {
            return false;
        }

        if (!fit.TrySolve(out fitted))
            return false;
        if (!anchored)
            return true;

        var anchor = dimension - 6.5f;
        anchoredTransform.Transform(anchor, anchor, out var x, out var y);
        fit.Add(anchor, anchor, x, y);
        return fit.TrySolve(out anchoredFit);
    }

    /// <summary>The four corners of the inner outline of the finder at grid point (<paramref name="u"/>, <paramref name="v"/>), two and a half modules from its centre along both of its own axes, measured from the frame's axes there and again along its own.</summary>
    private static bool TryAddOutline(ref ProjectiveFit fit, in ImageView image, in FinderFrame frame, in FinderPattern finder, float u, float v, int dimension)
    {
        frame.Axes(u, v, dimension, out var axisX, out var axisY);
        if (!FinderOutline.TryMeasure(image.Luminance, image.Width, image.Height, image.EdgeLevel, finder.X, finder.Y, axisX.X, axisX.Y, axisY.X, axisY.Y, out var outline))
            return false;
        if (outline.TryRefine(image.Luminance, image.Width, image.Height, image.EdgeLevel, out var refined))
            outline = refined;

        const float Reach = 2.5f;
        for (var j = -1; j <= 1; j += 2)
        {
            for (var i = -1; i <= 1; i += 2)
            {
                var su = i * Reach;
                var sv = j * Reach;
                fit.Add(u + su, v + sv, outline.CenterX + su * outline.UX + sv * outline.VX, outline.CenterY + su * outline.UY + sv * outline.VY);
            }
        }
        return true;
    }
}
