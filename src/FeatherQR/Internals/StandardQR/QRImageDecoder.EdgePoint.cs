using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Internals.StandardQR;

internal static partial class QRImageDecoder
{
    /// <summary>Edge points taken across a finder when its edge is followed: every half module over the ring's 7, the corners left out.</summary>
    private const int EdgePoints = 13;

    /// <summary>
    /// The fourth point of a symbol nothing anchored, from its finders' edges: the bottom-left finder's top edge and the top-right finder's left edge, each the ring's outer edge followed across the finder and fitted as a line, meet where the symbol's module row and column 7 from the far corner do.
    /// </summary>
    /// <remarks>
    /// The parallelogram and the frame place that point from the three centres alone, flat or by the finders' foreshortening. A symbol bent round a cylinder is neither: its centres are measured within a fifth of a module and its fourth point lands a module out, and with the point measured on the image the same centres read. The edges are what the image holds of the symbol's far side.
    /// </remarks>
    internal static bool TryFourthPointFromEdges(in ImageView image, in FinderPattern topLeft, in FinderPattern topRight, in FinderPattern bottomLeft, float moduleSize, out float x, out float y)
    {
        x = y = 0f;
        var uLength = Distance(topLeft, topRight);
        var vLength = Distance(topLeft, bottomLeft);
        if (uLength < 1f || vLength < 1f)
            return false;
        var uX = (topRight.X - topLeft.X) / uLength;
        var uY = (topRight.Y - topLeft.Y) / uLength;
        var vX = (bottomLeft.X - topLeft.X) / vLength;
        var vY = (bottomLeft.Y - topLeft.Y) / vLength;
        return TryTraceEdge(image, bottomLeft, uX, uY, -vX, -vY, moduleSize, out var bottomLine)
            && TryTraceEdge(image, topRight, vX, vY, -uX, -uY, moduleSize, out var rightLine)
            && TryIntersect(bottomLine, rightLine, out x, out y);
    }

    /// <summary>A line as a point on it and a unit direction.</summary>
    private readonly record struct EdgeLine(float X, float Y, float DirectionX, float DirectionY);

    /// <summary>
    /// The line along a finder's outer ring edge on the side (<paramref name="outX"/>, <paramref name="outY"/>) points to: found on the centre line by the dark-light-dark walk, then followed at half-module steps along (<paramref name="alongX"/>, <paramref name="alongY"/>) by a short walk out of the ring at each step, and fitted by least squares.
    /// </summary>
    private static bool TryTraceEdge(in ImageView image, in FinderPattern finder, float alongX, float alongY, float outX, float outY, float module, out EdgeLine line)
    {
        line = default;
        if (!(module >= 1f) || !FinderAxisEstimator.TryDarkLightDarkRun(image.Luminance, image.Width, image.Height, image.Threshold, finder.X, finder.Y, outX, outY, 6f * module, out _, out var outer))
            return false;

        Span<float> xs = stackalloc float[EdgePoints];
        Span<float> ys = stackalloc float[EdgePoints];
        var count = 0;
        for (var k = -(EdgePoints / 2); k <= EdgePoints / 2; k++)
        {
            // From the middle of the ring, half a module inside its outer edge, out to the first light pixel, two modules at most
            var offset = k * 0.5f * module;
            var startX = finder.X + alongX * offset + outX * (outer - 0.5f * module);
            var startY = finder.Y + alongY * offset + outY * (outer - 0.5f * module);
            if (TryEdgeCrossing(image, startX, startY, outX, outY, 2f * module, out var distance))
            {
                xs[count] = startX + outX * distance;
                ys[count] = startY + outY * distance;
                count++;
            }
        }
        if (count < EdgePoints / 2)
            return false;

        var meanX = 0f;
        var meanY = 0f;
        for (var i = 0; i < count; i++)
        {
            meanX += xs[i];
            meanY += ys[i];
        }
        meanX /= count;
        meanY /= count;
        float sxx = 0f, sxy = 0f, syy = 0f;
        for (var i = 0; i < count; i++)
        {
            sxx += (xs[i] - meanX) * (xs[i] - meanX);
            sxy += (xs[i] - meanX) * (ys[i] - meanY);
            syy += (ys[i] - meanY) * (ys[i] - meanY);
        }
        // The points' principal direction
        var angle = 0.5f * (float)Math.Atan2(2f * sxy, sxx - syy);
        line = new EdgeLine(meanX, meanY, (float)Math.Cos(angle), (float)Math.Sin(angle));
        return true;
    }

    /// <summary>The distance along (<paramref name="dirX"/>, <paramref name="dirY"/>) from a dark start to the first light pixel, at half-pixel steps, the crossing taken halfway. False when the start is light or no light pixel lies within <paramref name="reach"/>.</summary>
    private static bool TryEdgeCrossing(in ImageView image, float startX, float startY, float dirX, float dirY, float reach, out float distance)
    {
        distance = 0f;
        for (var step = 0f; step <= reach; step += 0.5f)
        {
            var px = (int)(startX + dirX * step);
            var py = (int)(startY + dirY * step);
            if (px < 0 || py < 0 || px >= image.Width || py >= image.Height)
                return false;
            if (image.Luminance[py * image.Width + px] >= image.Threshold)
            {
                if (step == 0f)
                    return false;
                distance = step - 0.25f;
                return true;
            }
        }
        return false;
    }

    private static bool TryIntersect(in EdgeLine a, in EdgeLine b, out float x, out float y)
    {
        x = y = 0f;
        var determinant = a.DirectionX * b.DirectionY - a.DirectionY * b.DirectionX;
        if (Math.Abs(determinant) < 1e-6f)
            return false;
        var t = ((b.X - a.X) * b.DirectionY - (b.Y - a.Y) * b.DirectionX) / determinant;
        x = a.X + a.DirectionX * t;
        y = a.Y + a.DirectionY * t;
        return true;
    }

    /// <summary>The grid-to-pixel transform through the three finder centres and the fourth point from the edges, at module (dimension − 7, dimension − 7).</summary>
    private static PerspectiveTransform BuildEdgePointTransform(in FinderPattern topLeft, in FinderPattern topRight, in FinderPattern bottomLeft, float x, float y, int dimension)
    {
        var far = dimension - 3.5f;
        return PerspectiveTransform.QuadrilateralToQuadrilateral(
            3.5f, 3.5f,
            far, 3.5f,
            dimension - 7f, dimension - 7f,
            3.5f, far,
            topLeft.X, topLeft.Y,
            topRight.X, topRight.Y,
            x, y,
            bottomLeft.X, bottomLeft.Y);
    }
}
