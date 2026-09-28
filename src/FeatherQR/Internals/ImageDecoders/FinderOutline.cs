namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// A 7 × 7 finder pattern's own frame, measured from the outline of its light ring: the centre and one module along each of its axes, which need not be perpendicular.
/// </summary>
/// <remarks>
/// The frames the angular sweep gives a single finder are square, and a finder in perspective is a parallelogram: at the near end of a wide symbol narrowed along its short axis the columns lean up to 40° off the rows, and a square frame puts the format information a module or two off.
/// The dark ring's inner edges are the finder's own: they bound the light ring on both sides whatever lies round the pattern, so neither a quiet zone nor a separator is assumed.
/// Chords along one axis meet the two inner edges it crosses, and under any affine map the edges' lines run along the other axis, so fitting them gives that axis with the shear in it; chords along it then meet the other two edges.
/// </remarks>
internal readonly struct FinderOutline
{
    /// <summary>
    /// The seed frame's module lengths within which the centre chord meets the inner edges: 2.5 modules along a square finder's axis, up to twice that along a sheared one's long diagonal.
    /// The lower bound refuses a 5 × 5 pattern of rings, a sub-finder or an alignment pattern, whose inner edge lies 1.5 modules out.
    /// </summary>
    private const float MinCentreReach = 1.8f;
    private const float MaxCentreReach = 5.5f;

    /// <summary>
    /// How far, in modules, the centre chord's two inner edges may differ: a seed up to half a module off the centre along the chord.
    /// It cuts the time seeds on noise take, and refuses no finder the other checks accept.
    /// </summary>
    private const float MaxCentreAsymmetry = 1f;

    /// <summary>The largest distance, in modules, an inner edge may lie off its fitted line.</summary>
    internal const float MaxResidual = 0.25f;

    /// <summary>Chords either side of the centre chord, on each side.</summary>
    private const int ChordReach = 2;

    /// <summary>
    /// From a seed, chords half a module apart: all five cross the centre square, a module and a half inside the corners of the ring's inner edge, so a seed 15° off the finder's axes still meets the two edges it aims at.
    /// Not 0.75: the outer chords would run along the centre square's own edge, whose grey pixels cross the level.
    /// </summary>
    private const float SeedChordSpacing = 0.5f;

    /// <summary>Along the outline's own axes, chords a module apart, down the middle of the light ring at the ends: twice the span, so half the error in each axis.</summary>
    private const float RefineChordSpacing = 1f;

    public readonly float CenterX;
    public readonly float CenterY;

    /// <summary>One module along the finder's rows, toward increasing column.</summary>
    public readonly float UX;
    public readonly float UY;

    /// <summary>One module along the finder's columns, toward increasing row.</summary>
    public readonly float VX;
    public readonly float VY;

    private FinderOutline(float centerX, float centerY, float uX, float uY, float vX, float vY)
    {
        CenterX = centerX;
        CenterY = centerY;
        UX = uX;
        UY = uY;
        VX = vX;
        VY = vY;
    }

    /// <summary>
    /// The outline round (<paramref name="x"/>, <paramref name="y"/>), which has to lie in the finder's centre square, from a seed frame whose axes are within reach of the finder's.
    /// Pixels at or above <paramref name="level"/> are light. The result's axes point the seed's way.
    /// False when the centre chord misses an inner edge or is not symmetric about the seed, when fewer than two chords meet an edge, or when the edges do not lie on four lines to within <see cref="MaxResidual"/> of a module.
    /// </summary>
    public static bool TryMeasure(ReadOnlySpan<byte> luminance, int width, int height, float level, float x, float y, float uX, float uY, float vX, float vY, out FinderOutline outline)
        => TryMeasure(luminance, width, height, level, x, y, uX, uY, vX, vY, SeedChordSpacing, out outline);

    /// <summary>The outline measured again along its own axes, where the chords meet the edges square on and can reach into the light ring.</summary>
    public bool TryRefine(ReadOnlySpan<byte> luminance, int width, int height, float level, out FinderOutline refined)
        => TryMeasure(luminance, width, height, level, CenterX, CenterY, UX, UY, VX, VY, RefineChordSpacing, out refined);

    private static bool TryMeasure(ReadOnlySpan<byte> luminance, int width, int height, float level, float x, float y, float uX, float uY, float vX, float vY, float chordSpacing, out FinderOutline outline)
    {
        outline = default;
        var uLength = (float)Math.Sqrt(uX * uX + uY * uY);
        var vLength = (float)Math.Sqrt(vX * vX + vY * vY);
        if (!(uLength >= 1f) || !(vLength >= 1f))
            return false;
        // A pixel at most, and a third of a module where modules are under three pixels: a sample in each run the light ring leaves
        var step = Math.Min(1f, 0.34f * Math.Min(uLength, vLength));

        // The two edges the u axis crosses, from chords along u
        Span<float> nearX = stackalloc float[2 * ChordReach + 1];
        Span<float> nearY = stackalloc float[2 * ChordReach + 1];
        Span<float> farX = stackalloc float[2 * ChordReach + 1];
        Span<float> farY = stackalloc float[2 * ChordReach + 1];
        var dx = uX / uLength * step;
        var dy = uY / uLength * step;
        var maxSteps = (int)(MaxCentreReach * uLength / step);
        if (!TryInnerEdge(luminance, width, height, level, x, y, -dx, -dy, maxSteps, out nearX[0], out nearY[0])
            || !TryInnerEdge(luminance, width, height, level, x, y, dx, dy, maxSteps, out farX[0], out farY[0]))
        {
            return false;
        }
        var nearReach = Distance(nearX[0] - x, nearY[0] - y) / uLength;
        var farReach = Distance(farX[0] - x, farY[0] - y) / uLength;
        if (nearReach < MinCentreReach || farReach < MinCentreReach || Math.Abs(nearReach - farReach) > MaxCentreAsymmetry)
            return false;
        var nearCount = 1;
        var farCount = 1;
        for (var t = -ChordReach; t <= ChordReach; t++)
        {
            if (t == 0)
                continue;
            var sx = x + t * chordSpacing * vX;
            var sy = y + t * chordSpacing * vY;
            if (TryInnerEdge(luminance, width, height, level, sx, sy, -dx, -dy, maxSteps, out nearX[nearCount], out nearY[nearCount]))
                nearCount++;
            if (TryInnerEdge(luminance, width, height, level, sx, sy, dx, dy, maxSteps, out farX[farCount], out farY[farCount]))
                farCount++;
        }
        if (nearCount < 2 || farCount < 2
            || !TryFitParallel(nearX.Slice(0, nearCount), nearY.Slice(0, nearCount), farX.Slice(0, farCount), farY.Slice(0, farCount), out var vDirX, out var vDirY, out var leftX, out var leftY, out var rightX, out var rightY, out var vResidual))
        {
            return false;
        }
        if (vDirX * vX + vDirY * vY < 0f)
        {
            vDirX = -vDirX;
            vDirY = -vDirY;
        }
        // The seed's module height is measured across its u axis, so a module along the leaning axis is longer by the lean
        var cos = (vDirX * vX + vDirY * vY) / vLength;
        if (!(cos > 0f))
            return false;
        var vModule = vLength / cos;

        // The other two edges, from chords along the measured axis
        dx = vDirX * step;
        dy = vDirY * step;
        maxSteps = (int)(MaxCentreReach * vModule / step);
        nearCount = 0;
        farCount = 0;
        for (var s = -ChordReach; s <= ChordReach; s++)
        {
            var sx = x + s * chordSpacing * uX;
            var sy = y + s * chordSpacing * uY;
            if (TryInnerEdge(luminance, width, height, level, sx, sy, -dx, -dy, maxSteps, out nearX[nearCount], out nearY[nearCount]))
                nearCount++;
            if (TryInnerEdge(luminance, width, height, level, sx, sy, dx, dy, maxSteps, out farX[farCount], out farY[farCount]))
                farCount++;
        }
        if (nearCount < 2 || farCount < 2
            || !TryFitParallel(nearX.Slice(0, nearCount), nearY.Slice(0, nearCount), farX.Slice(0, farCount), farY.Slice(0, farCount), out var uDirX, out var uDirY, out var topX, out var topY, out var bottomX, out var bottomY, out var uResidual))
        {
            return false;
        }
        if (uDirX * uX + uDirY * uY < 0f)
        {
            uDirX = -uDirX;
            uDirY = -uDirY;
        }
        // Distances off a line are across it: a module is the seed's length on the other axis, measured square to it
        if (Math.Max(vResidual / uLength, uResidual / vLength) > MaxResidual)
            return false;

        // The 5 × 5 outline's corners
        if (!TryIntersect(leftX, leftY, vDirX, vDirY, topX, topY, uDirX, uDirY, out var x0, out var y0)
            || !TryIntersect(rightX, rightY, vDirX, vDirY, topX, topY, uDirX, uDirY, out var x1, out var y1)
            || !TryIntersect(rightX, rightY, vDirX, vDirY, bottomX, bottomY, uDirX, uDirY, out var x2, out var y2)
            || !TryIntersect(leftX, leftY, vDirX, vDirY, bottomX, bottomY, uDirX, uDirY, out var x3, out var y3))
        {
            return false;
        }
        outline = new FinderOutline(
            (x0 + x1 + x2 + x3) * 0.25f,
            (y0 + y1 + y2 + y3) * 0.25f,
            (x1 - x0 + x2 - x3) * 0.1f,
            (y1 - y0 + y2 - y3) * 0.1f,
            (x3 - x0 + x2 - x1) * 0.1f,
            (y3 - y0 + y2 - y1) * 0.1f);
        return true;
    }

    /// <summary>
    /// The first crossing from light to dark after at least one light sample, walking (<paramref name="dx"/>, <paramref name="dy"/>) a step at a time from (<paramref name="x"/>, <paramref name="y"/>): the dark ring's inner edge from a start in the centre square or the light ring.
    /// The crossing is interpolated between the two samples either side of it.
    /// </summary>
    internal static bool TryInnerEdge(ReadOnlySpan<byte> luminance, int width, int height, float level, float x, float y, float dx, float dy, int maxSteps, out float edgeX, out float edgeY)
    {
        edgeX = 0f;
        edgeY = 0f;
        var previous = LuminanceSampler.Bilinear(luminance, width, height, x, y);
        var seenLight = previous >= level;
        for (var i = 1; i <= maxSteps; i++)
        {
            var px = x + i * dx;
            var py = y + i * dy;
            if (px < 0f || py < 0f || px >= width || py >= height)
                return false;
            var value = LuminanceSampler.Bilinear(luminance, width, height, px, py);
            if (value >= level)
            {
                seenLight = true;
            }
            else if (seenLight)
            {
                var fraction = (previous - level) / (previous - value);
                edgeX = x + (i - 1 + fraction) * dx;
                edgeY = y + (i - 1 + fraction) * dy;
                return true;
            }
            previous = value;
        }
        return false;
    }

    /// <summary>
    /// Two parallel lines through two point sets: their shared direction (the principal axis of both sets about their own means), a point on each, and the largest distance of a point off its line.
    /// </summary>
    internal static bool TryFitParallel(ReadOnlySpan<float> aX, ReadOnlySpan<float> aY, ReadOnlySpan<float> bX, ReadOnlySpan<float> bY, out float dirX, out float dirY, out float aMeanX, out float aMeanY, out float bMeanX, out float bMeanY, out float residual)
    {
        Mean(aX, aY, out aMeanX, out aMeanY);
        Mean(bX, bY, out bMeanX, out bMeanY);
        double sxx = 0d, sxy = 0d, syy = 0d;
        Scatter(aX, aY, aMeanX, aMeanY, ref sxx, ref sxy, ref syy);
        Scatter(bX, bY, bMeanX, bMeanY, ref sxx, ref sxy, ref syy);
        var angle = 0.5d * Math.Atan2(2d * sxy, sxx - syy);
        dirX = (float)Math.Cos(angle);
        dirY = (float)Math.Sin(angle);
        residual = Math.Max(MaxOffLine(aX, aY, aMeanX, aMeanY, dirX, dirY), MaxOffLine(bX, bY, bMeanX, bMeanY, dirX, dirY));
        return sxx + syy > 0d;
    }

    private static void Mean(ReadOnlySpan<float> xs, ReadOnlySpan<float> ys, out float meanX, out float meanY)
    {
        float sumX = 0f, sumY = 0f;
        for (var i = 0; i < xs.Length; i++)
        {
            sumX += xs[i];
            sumY += ys[i];
        }
        meanX = sumX / xs.Length;
        meanY = sumY / xs.Length;
    }

    private static void Scatter(ReadOnlySpan<float> xs, ReadOnlySpan<float> ys, float meanX, float meanY, ref double sxx, ref double sxy, ref double syy)
    {
        for (var i = 0; i < xs.Length; i++)
        {
            double dx = xs[i] - meanX;
            double dy = ys[i] - meanY;
            sxx += dx * dx;
            sxy += dx * dy;
            syy += dy * dy;
        }
    }

    private static float MaxOffLine(ReadOnlySpan<float> xs, ReadOnlySpan<float> ys, float meanX, float meanY, float dirX, float dirY)
    {
        var worst = 0f;
        for (var i = 0; i < xs.Length; i++)
            worst = Math.Max(worst, Math.Abs((ys[i] - meanY) * dirX - (xs[i] - meanX) * dirY));
        return worst;
    }

    private static bool TryIntersect(float p1X, float p1Y, float d1X, float d1Y, float p2X, float p2Y, float d2X, float d2Y, out float x, out float y)
    {
        var denominator = d1X * d2Y - d1Y * d2X;
        x = 0f;
        y = 0f;
        if (Math.Abs(denominator) < 1e-4f)
            return false;
        var t = ((p2X - p1X) * d2Y - (p2Y - p1Y) * d2X) / denominator;
        x = p1X + t * d1X;
        y = p1Y + t * d1Y;
        return true;
    }

    private static float Distance(float dx, float dy) => (float)Math.Sqrt(dx * dx + dy * dy);
}
