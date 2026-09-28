using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Internals.RmQR;

internal static partial class RmQRImageDecoder
{
    /// <summary>Consecutive runs an edge may miss before its trace is given up: a damaged module or two, not a lost line.</summary>
    private const int MaxMissedRuns = 2;

    /// <summary>
    /// The symbol's grid from its edges: the top row traced from the finder rightward, the left column downward and the bottom row rightward, module by module, and one homography fitted to every edge met.
    /// The trace starts from the finder centre (<paramref name="centerX"/>, <paramref name="centerY"/>), grid (3.5, 3.5), and one module toward increasing column (<paramref name="uX"/>, <paramref name="uY"/>) and row (<paramref name="vX"/>, <paramref name="vY"/>) there; pixels at or above <paramref name="level"/> are light.
    /// </summary>
    /// <remarks>
    /// Every edge of an rMQR symbol is a timing pattern, so each dark run along it has two ends at known columns and faces the quiet zone.
    /// A trace predicts the next run from the step the last ones measured, so it follows perspective however strong: over a wide symbol the step changes by a quarter from one end to the other and the columns lean up to 40° either way, which no frame measured at the finder extrapolates.
    /// The runs expected are the ones the encoder paints (<see cref="RmQRModulePlacer.GetFunctionTemplate"/>).
    /// False when a line misses more than <see cref="MaxMissedRuns"/> runs in a row or reads none, or the points do not determine a plane.
    /// </remarks>
    internal static bool TryTracePerimeter(ReadOnlySpan<byte> luminance, int width, int height, float level, float centerX, float centerY, float uX, float uY, float vX, float vY, RmQRVersion version, out PerspectiveTransform transform)
    {
        transform = default;
        var symbolWidth = RmQRConstants.GetWidth(version);
        var symbolHeight = RmQRConstants.GetHeight(version);
        var template = RmQRModulePlacer.GetFunctionTemplate(version);

        // The image side about where the finder frame puts the grid's centre, at the symbol's half width
        var halfWidth = symbolWidth * 0.5f;
        var halfHeight = symbolHeight * 0.5f;
        var moduleLength = (float)Math.Sqrt(uX * uX + uY * uY);
        var fit = new ProjectiveFit(
            centerX + (halfWidth - FinderCenter) * uX + (halfHeight - FinderCenter) * vX,
            centerY + (halfWidth - FinderCenter) * uY + (halfHeight - FinderCenter) * vY,
            moduleLength * halfWidth);

        // Top row, from the first column past the separator; the quiet zone is up
        var top = EdgeWalk.At(centerX, centerY, uX, uY, vX, vY, 8f, 0.5f, 8f, uX, uY, -vX, -vY);
        if (!TryTraceEdge(luminance, width, height, level, template, symbolWidth, alongRow: true, line: 0, outward: -1, first: 8, last: symbolWidth - 1, ref top, ref fit))
            return false;

        // The bottom row starts under the finder on a height-7 symbol, whose finder spans it; otherwise at the corner the left column leads down to
        EdgeWalk bottom;
        int bottomFirst;
        if (symbolHeight > 7)
        {
            var left = EdgeWalk.At(centerX, centerY, uX, uY, vX, vY, 0.5f, 8f, 8f, vX, vY, -uX, -uY);
            if (!TryTraceEdge(luminance, width, height, level, template, symbolWidth, alongRow: false, line: 0, outward: -1, first: 8, last: symbolHeight - 1, ref left, ref fit))
                return false;
            // Grid (0.5, symbolHeight), then half a module left and up to the bottom row's centre line at column 0
            var reach = symbolHeight - left.Index;
            var cornerX = left.X + reach * left.StepX;
            var cornerY = left.Y + reach * left.StepY;
            bottom = new EdgeWalk(cornerX - 0.5f * (uX + left.StepX), cornerY - 0.5f * (uY + left.StepY), 0f, uX, uY, vX, vY);
            bottomFirst = 0;
        }
        else
        {
            bottom = EdgeWalk.At(centerX, centerY, uX, uY, vX, vY, 8f, symbolHeight - 0.5f, 8f, uX, uY, vX, vY);
            bottomFirst = 8;
        }
        if (!TryTraceEdge(luminance, width, height, level, template, symbolWidth, alongRow: true, line: symbolHeight - 1, outward: 1, first: bottomFirst, last: symbolWidth - 1, ref bottom, ref fit))
            return false;

        return fit.TrySolve(out transform);
    }

    /// <summary>A trace along one edge line: where the line's centre is at boundary <see cref="Index"/>, a module along it, and a module outward, in pixels.</summary>
    private struct EdgeWalk
    {
        public float X;
        public float Y;
        public float Index;
        public float StepX;
        public float StepY;
        public float OutX;
        public float OutY;

        public EdgeWalk(float x, float y, float index, float stepX, float stepY, float outX, float outY)
        {
            X = x;
            Y = y;
            Index = index;
            StepX = stepX;
            StepY = stepY;
            OutX = outX;
            OutY = outY;
        }

        /// <summary>Starting at grid (<paramref name="gridX"/>, <paramref name="gridY"/>) as the finder's frame predicts it.</summary>
        public static EdgeWalk At(float centerX, float centerY, float uX, float uY, float vX, float vY, float gridX, float gridY, float index, float stepX, float stepY, float outX, float outY)
            => new(
                centerX + (gridX - FinderCenter) * uX + (gridY - FinderCenter) * vX,
                centerY + (gridX - FinderCenter) * uY + (gridY - FinderCenter) * vY,
                index, stepX, stepY, outX, outY);
    }

    /// <summary>
    /// Traces the dark runs of row or column <paramref name="line"/> from module <paramref name="first"/> to <paramref name="last"/>: each run's two ends, found near where the step puts them, and the symbol's outer edge beside it, found from the quiet zone inward.
    /// Both ends of every run go into <paramref name="fit"/>, and a row's outer edges too, since the corners are read off that boundary; the step is measured again from each run, and the line's centre put back half a module inside the outer edge.
    /// </summary>
    private static bool TryTraceEdge(ReadOnlySpan<byte> luminance, int width, int height, float level, ReadOnlySpan<byte> template, int symbolWidth, bool alongRow, int line, int outward, int first, int last, ref EdgeWalk walk, ref ProjectiveFit fit)
    {
        // The outward scan runs square to the line, at the module height across it the frame gives: along a sheared finder's column it would cross a run's side, not the symbol's edge
        var stepLength = (float)Math.Sqrt(walk.StepX * walk.StepX + walk.StepY * walk.StepY);
        var cross = walk.StepX * walk.OutY - walk.StepY * walk.OutX;
        var outHeight = Math.Abs(cross) / stepLength;
        var side = cross < 0f ? -1f : 1f;
        walk.OutX = -walk.StepY / stepLength * outHeight * side;
        walk.OutY = walk.StepX / stepLength * outHeight * side;
        var centre = line + 0.5f;
        var edge = line + 0.5f + 0.5f * outward;

        var measured = 0;
        var misses = 0;
        var k = first;
        while (k <= last)
        {
            if (!ExpectedDark(template, symbolWidth, alongRow, line, k))
            {
                k++;
                continue;
            }
            var start = k;
            while (k < last && ExpectedDark(template, symbolWidth, alongRow, line, k + 1))
                k++;
            var end = k + 1;
            k++;

            var modules = end - start;
            var predictedX = walk.X + (start - walk.Index) * walk.StepX;
            var predictedY = walk.Y + (start - walk.Index) * walk.StepY;
            // Each end within a module of where it is expected: the runs either side start and end two modules off or more.
            // The far end is expected from the near one, a little further out along a longer run: the step's error grows with the run, not with the distance walked
            if (!TryNearestCrossing(luminance, width, height, level, predictedX, predictedY, walk.StepX, walk.StepY, 1f, toDark: true, out var nearX, out var nearY)
                || !TryNearestCrossing(luminance, width, height, level, nearX + modules * walk.StepX, nearY + modules * walk.StepY, walk.StepX, walk.StepY, 1f + 0.05f * modules, toDark: false, out var farX, out var farY))
            {
                if (++misses > MaxMissedRuns)
                    return false;
                continue;
            }
            misses = 0;
            measured++;

            var middleX = (nearX + farX) * 0.5f;
            var middleY = (nearY + farY) * 0.5f;
            var outer = TryNearestCrossing(luminance, width, height, level, middleX + 0.5f * walk.OutX, middleY + 0.5f * walk.OutY, -walk.OutX, -walk.OutY, 0.45f, toDark: true, out var outerX, out var outerY);
            if (alongRow)
            {
                fit.Add(start, centre, nearX, nearY);
                fit.Add(end, centre, farX, farY);
                if (outer)
                    fit.Add((start + end) * 0.5f, edge, outerX, outerY);
            }
            else
            {
                fit.Add(centre, start, nearX, nearY);
                fit.Add(centre, end, farX, farY);
            }

            // The step moves halfway to this run's own, which spreads one edge's error over the runs after it
            walk.StepX = 0.5f * (walk.StepX + (farX - nearX) / modules);
            walk.StepY = 0.5f * (walk.StepY + (farY - nearY) / modules);
            var length = (float)Math.Sqrt(walk.StepX * walk.StepX + walk.StepY * walk.StepY);
            walk.OutX = -walk.StepY / length * outHeight * side;
            walk.OutY = walk.StepX / length * outHeight * side;

            // On from the far end, moved across the line to half a module inside the outer edge
            walk.X = farX;
            walk.Y = farY;
            walk.Index = end;
            if (outer)
            {
                var offX = outerX - 0.5f * walk.OutX - middleX;
                var offY = outerY - 0.5f * walk.OutY - middleY;
                var along = (offX * walk.StepX + offY * walk.StepY) / (length * length);
                walk.X += offX - along * walk.StepX;
                walk.Y += offY - along * walk.StepY;
            }
        }
        return measured > 0;
    }

    private static bool ExpectedDark(ReadOnlySpan<byte> template, int symbolWidth, bool alongRow, int line, int k)
        => (alongRow ? template[line * symbolWidth + k] : template[k * symbolWidth + line]) != 0;

    /// <summary>
    /// The crossing of <paramref name="level"/> nearest (<paramref name="x"/>, <paramref name="y"/>) between it ∓ <paramref name="half"/> of (<paramref name="dx"/>, <paramref name="dy"/>), light to dark or dark to light in that direction; interpolated between the samples either side.
    /// Of two crossings as near, the one toward −(<paramref name="dx"/>, <paramref name="dy"/>).
    /// </summary>
    /// <remarks>
    /// The samples are taken outward from the middle, and stop once no pair further out can hold a nearer crossing: a trace predicts most edges within a sample or two.
    /// </remarks>
    internal static bool TryNearestCrossing(ReadOnlySpan<byte> luminance, int width, int height, float level, float x, float y, float dx, float dy, float half, bool toDark, out float crossingX, out float crossingY)
    {
        crossingX = 0f;
        crossingY = 0f;
        var length = (float)Math.Sqrt(dx * dx + dy * dy);
        if (!(length >= 0.5f))
            return false;
        // A pixel at most, and a third of a module where modules are under three pixels
        var spacing = Math.Min(1f, 0.34f * length);
        var steps = Math.Max(2, (int)Math.Ceiling(2f * half * length / spacing));
        var startX = x - half * dx;
        var startY = y - half * dy;
        var stepX = 2f * half * dx / steps;
        var stepY = 2f * half * dy / steps;
        if (startX < 0f || startY < 0f || startX >= width || startY >= height
            || startX + steps * stepX < 0f || startY + steps * stepY < 0f || startX + steps * stepX >= width || startY + steps * stepY >= height)
        {
            return false;
        }

        // Samples lo..hi are taken; pair i is samples i − 1 and i, its crossing at i − 1 plus a fraction
        var middle = steps * 0.5f;
        var lo = steps / 2;
        var hi = lo;
        var valueLo = LuminanceSampler.Bilinear(luminance, width, height, startX + lo * stepX, startY + lo * stepY);
        var valueHi = valueLo;
        var nearest = float.MaxValue;
        var nearestPair = 0;
        var nearestPosition = 0f;
        if ((steps & 1) != 0)
        {
            hi++;
            valueHi = LuminanceSampler.Bilinear(luminance, width, height, startX + hi * stepX, startY + hi * stepY);
            Consider(valueLo, valueHi, hi, level, toDark, middle, ref nearest, ref nearestPair, ref nearestPosition);
        }
        while (lo > 0 || hi < steps)
        {
            if (hi < steps)
            {
                var value = LuminanceSampler.Bilinear(luminance, width, height, startX + (hi + 1) * stepX, startY + (hi + 1) * stepY);
                Consider(valueHi, value, hi + 1, level, toDark, middle, ref nearest, ref nearestPair, ref nearestPosition);
                hi++;
                valueHi = value;
            }
            if (lo > 0)
            {
                var value = LuminanceSampler.Bilinear(luminance, width, height, startX + (lo - 1) * stepX, startY + (lo - 1) * stepY);
                Consider(value, valueLo, lo, level, toDark, middle, ref nearest, ref nearestPair, ref nearestPosition);
                lo--;
                valueLo = value;
            }
            // No pair further out comes nearer than this, and one exactly this near toward the start would win the tie
            if (nearestPair != 0 && nearest < hi - middle)
                break;
        }
        if (nearestPair == 0)
            return false;

        crossingX = startX + nearestPosition * stepX;
        crossingY = startY + nearestPosition * stepY;
        return true;

        static void Consider(float previous, float value, int pair, float level, bool toDark, float middle, ref float nearest, ref int nearestPair, ref float nearestPosition)
        {
            if (!(toDark ? previous >= level && value < level : previous < level && value >= level))
                return;
            var position = pair - 1 + (previous - level) / (previous - value);
            var offset = Math.Abs(position - middle);
            if (offset < nearest || offset == nearest && pair < nearestPair)
            {
                nearest = offset;
                nearestPair = pair;
                nearestPosition = position;
            }
        }
    }
}
