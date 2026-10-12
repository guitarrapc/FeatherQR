using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Internals.StandardQR;

internal static partial class QRImageDecoder
{
    /// <summary>The dimension the plane is fitted below: version 7, the first with an alignment lattice for the mesh to follow.</summary>
    private const int OutlineFitBelowDimension = 17 + 4 * 7;

    /// <summary>
    /// The grid-to-pixel plane that best fits the corners of the three finders' inner outlines, the square five modules a side each dark ring encloses.
    /// A grid through three centres and a fourth point takes each finder as a point and leans on the fourth; this one takes each finder as a shape, whose four corners carry the perspective where the finder stands, so nothing is extrapolated from a single point across the symbol.
    /// Where the alignment search anchored, <paramref name="anchoredFit"/> is the plane fitted to those corners and the anchor as one more point; each reads symbols the other does not.
    /// False from version 7, and when an outline cannot be measured, as on a finder drawn round or an inferred one.
    /// </summary>
    internal static bool TryFitToFinderOutlines(in ImageView image, in FinderFrame frame, int dimension, bool anchored, in PerspectiveTransform anchoredTransform, out PerspectiveTransform fitted, out PerspectiveTransform anchoredFit)
    {
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
