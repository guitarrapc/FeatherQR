namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// One local grid frame recovered around a finder pattern: the column axis (U) and row axis (V) in pixels per module, plus their lengths.
/// </summary>
internal readonly struct OrientationCandidate(
    float uX,
    float uY,
    float vX,
    float vY,
    float uSize,
    float vSize)
{
    public float UX { get; } = uX;
    public float UY { get; } = uY;
    public float VX { get; } = vX;
    public float VY { get; } = vY;
    public float USize { get; } = uSize;
    public float VSize { get; } = vSize;
}

/// <summary>
/// Local module-scale measurement through a 7×7 finder pattern, shared by all three symbologies, and axis recovery around a single finder for those whose orientation cannot be derived from three finder centers (Micro QR, rMQR).
/// Measures dark-light-dark runs from the finder center and pairs the dark ring's inner edge on one side with its outer edge on the other (6 modules), so grey edge pixels on either side of the threshold do not scale the result.
/// </summary>
internal static class FinderAxisEstimator
{
    /// <summary>Best local finder-axis estimates retained from the angular sweep.</summary>
    public const int MaxOrientationCandidates = 16;

    /// <summary>
    /// Maximum angular-sweep ray length relative to the row-scan module estimate.
    /// A finder center-to-edge ray is at most 3.5√2 modules; the extra margin tolerates pixel quantization and mild perspective.
    /// </summary>
    private const float MaxAngularSweepRunModules = 8f;

    /// <summary>
    /// Refines the horizontal and vertical module sizes independently by walking dark-light-dark runs from the finder center.
    /// Keeping both estimates lets the decoder read symbols rendered into a non-square rectangle.
    /// A clipped axis falls back to the other axis, then to the row-scan estimate when both clip.
    /// </summary>
    public static void RefineModuleSize(
        ReadOnlySpan<byte> luminance,
        int width,
        int height,
        byte threshold,
        in FinderPattern candidate,
        out float horizontalModuleSize,
        out float verticalModuleSize)
    {
        horizontalModuleSize = MeasureAxis(luminance, width, height, threshold, candidate.X, candidate.Y, 1f, 0f);
        verticalModuleSize = MeasureAxis(luminance, width, height, threshold, candidate.X, candidate.Y, 0f, 1f);

        if (float.IsNaN(horizontalModuleSize))
            horizontalModuleSize = float.IsNaN(verticalModuleSize) ? candidate.ModuleSize : verticalModuleSize;
        if (float.IsNaN(verticalModuleSize))
            verticalModuleSize = horizontalModuleSize;
    }

    /// <summary>
    /// Sweeps one quadrant because finder axes repeat every 90 degrees, retaining separated low-score directions.
    /// For a concentric square finder, a center ray crosses the shortest dark-light-dark span when it follows one of the square's local axes.
    /// Pixel quantization can shift the shortest measured run several degrees away from the true finder axis, so adjacent samples of one minimum must not consume every candidate slot.
    /// </summary>
    public static int FindOrientationCandidates(
        ReadOnlySpan<byte> luminance,
        int width,
        int height,
        byte threshold,
        in FinderPattern candidate,
        Span<OrientationCandidate> destination)
    {
        Span<float> uSizes = stackalloc float[90];
        Span<float> vSizes = stackalloc float[90];
        var maxRunLength = candidate.ModuleSize * MaxAngularSweepRunModules;
        for (var degrees = 0; degrees < 90; degrees++)
        {
            var radians = degrees * (Math.PI / 180d);
            var cos = (float)Math.Cos(radians);
            var sin = (float)Math.Sin(radians);
            var uSize = MeasureAxis(luminance, width, height, threshold, candidate.X, candidate.Y, cos, sin, maxRunLength);
            var vSize = MeasureAxis(luminance, width, height, threshold, candidate.X, candidate.Y, -sin, cos, maxRunLength);
            uSizes[degrees] = uSize;
            vSizes[degrees] = vSize;
        }

        Span<float> selectedDegrees = stackalloc float[MaxOrientationCandidates];
        var count = 0;

        // First candidate: the axis fitted to the whole sweep. Near the axis the span
        // grows only as 1/cos, so its minimum is flat and pixel noise picks it (measured
        // 5-11 degrees off at 3 px/module); toward the corners it peaks sharply, and a
        // fit of every direction uses both.
        if (destination.Length > 0 && TryFitAxis(uSizes, vSizes, out var fittedDegrees, out var fittedSize))
        {
            if (TryMeasureFrame(luminance, width, height, threshold, candidate, fittedDegrees, fittedSize, fittedSize, maxRunLength, out destination[count]))
                selectedDegrees[count++] = fittedDegrees;
        }

        // Then separated minima: the pixel-grid minimum can be a few degrees away from
        // the true finder axis, particularly for small rotated symbols, so several are
        // kept rather than filling the result with adjacent samples of one dip.
        while (count < destination.Length && count < MaxOrientationCandidates)
        {
            var bestDegree = -1;
            var bestScore = float.MaxValue;
            for (var degrees = 0; degrees < 90; degrees++)
            {
                var uSize = uSizes[degrees];
                var vSize = vSizes[degrees];
                if (float.IsNaN(uSize) || float.IsNaN(vSize) || uSize < 1f || vSize < 1f)
                    continue;

                var separated = true;
                for (var i = 0; i < count; i++)
                {
                    var distance = Math.Abs(degrees - selectedDegrees[i]);
                    if (Math.Min(distance, 90 - distance) < 2)
                    {
                        separated = false;
                        break;
                    }
                }
                if (!separated || uSize + vSize >= bestScore)
                    continue;

                bestDegree = degrees;
                bestScore = uSize + vSize;
            }

            if (bestDegree < 0)
                break;

            selectedDegrees[count] = bestDegree;
            if (TryMeasureFrame(luminance, width, height, threshold, candidate, bestDegree, uSizes[bestDegree], vSizes[bestDegree], maxRunLength, out destination[count]))
                count++;
            else
                break;
        }

        return count;
    }

    /// <summary>
    /// The grid frame along <paramref name="degrees"/>, with each axis measured on rays that took no part in choosing the direction.
    /// </summary>
    /// <remarks>
    /// A direction chosen as the smallest of 90 noisy measurements came with a measurement that came out low, and a grid scaled from it shrinks toward the far side of the symbol.
    /// </remarks>
    private static bool TryMeasureFrame(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in FinderPattern candidate, float degrees, float uFallback, float vFallback, float maxRunLength, out OrientationCandidate frame)
    {
        var radians = degrees * (Math.PI / 180d);
        var cos = (float)Math.Cos(radians);
        var sin = (float)Math.Sin(radians);
        var uSize = MeasureAxisOffCenter(luminance, width, height, threshold, candidate.X, candidate.Y, cos, sin, uFallback, maxRunLength);
        var vSize = MeasureAxisOffCenter(luminance, width, height, threshold, candidate.X, candidate.Y, -sin, cos, vFallback, maxRunLength);
        frame = new OrientationCandidate(cos * uSize, sin * uSize, -sin * vSize, cos * vSize, uSize, vSize);
        return uSize >= 1f && vSize >= 1f;
    }

    /// <summary>Quarter-degree steps of the fitted axis angle.</summary>
    private const int FitStepsPerDegree = 4;

    /// <summary>
    /// Span shape of a unit square crossed through its center at a quarter-degree offset from its axis, 1 / max(|cos|, |sin|); it repeats every 90 degrees.
    /// </summary>
    private static readonly float[] SquareSpanShape = CreateSquareSpanShape();

    private static float[] CreateSquareSpanShape()
    {
        var shape = new float[90 * FitStepsPerDegree];
        for (var i = 0; i < shape.Length; i++)
        {
            var radians = i * Math.PI / (180d * FitStepsPerDegree);
            shape[i] = (float)(1d / Math.Max(Math.Abs(Math.Cos(radians)), Math.Abs(Math.Sin(radians))));
        }
        return shape;
    }

    /// <summary>
    /// Least-squares fit of the square's span shape to every direction of the sweep (u at d degrees, v at d + 90), coarse whole degrees first and then quarter degrees around the best.
    /// False when fewer than half the directions measured.
    /// </summary>
    private static bool TryFitAxis(ReadOnlySpan<float> uSizes, ReadOnlySpan<float> vSizes, out float degrees, out float size)
    {
        degrees = 0f;
        size = 0f;
        var valid = 0;
        for (var d = 0; d < 90; d++)
        {
            if (IsMeasured(uSizes[d]))
                valid++;
            if (IsMeasured(vSizes[d]))
                valid++;
        }
        if (valid < 90)
            return false;

        var bestStep = 0;
        var bestResidual = float.MaxValue;
        var bestSize = 0f;
        for (var step = 0; step < 90 * FitStepsPerDegree; step += FitStepsPerDegree)
            Evaluate(uSizes, vSizes, step, ref bestStep, ref bestResidual, ref bestSize);

        var coarse = bestStep;
        for (var offset = -FitStepsPerDegree + 1; offset < FitStepsPerDegree; offset++)
        {
            if (offset != 0)
                Evaluate(uSizes, vSizes, (coarse + offset + 90 * FitStepsPerDegree) % (90 * FitStepsPerDegree), ref bestStep, ref bestResidual, ref bestSize);
        }

        degrees = bestStep / (float)FitStepsPerDegree;
        size = bestSize;
        return true;

        static void Evaluate(ReadOnlySpan<float> uSizes, ReadOnlySpan<float> vSizes, int step, ref int bestStep, ref float bestResidual, ref float bestSize)
        {
            var period = 90 * FitStepsPerDegree;
            float sumFG = 0f, sumGG = 0f, sumFF = 0f;
            for (var d = 0; d < 90; d++)
            {
                // v at d + 90 has the same shape offset as u at d (period 90)
                var g = SquareSpanShape[(d * FitStepsPerDegree - step + period) % period];
                Accumulate(uSizes[d], g, ref sumFG, ref sumGG, ref sumFF);
                Accumulate(vSizes[d], g, ref sumFG, ref sumGG, ref sumFF);
            }
            if (sumGG <= 0f)
                return;

            var residual = sumFF - sumFG * sumFG / sumGG;
            if (residual < bestResidual)
            {
                bestResidual = residual;
                bestStep = step;
                bestSize = sumFG / sumGG;
            }
        }

        static void Accumulate(float value, float g, ref float sumFG, ref float sumGG, ref float sumFF)
        {
            if (!IsMeasured(value))
                return;
            sumFG += value * g;
            sumGG += g * g;
            sumFF += value * value;
        }
    }

    private static bool IsMeasured(float size) => !float.IsNaN(size) && size >= 1f;

    /// <summary>
    /// Module size along one axis from four rays parallel to it, offset sideways by ±0.5 and ±1 module: inside the 3-module center square they cross the same ring edges at right angles as the center ray.
    /// Falls back to <paramref name="centerSize"/> when every offset ray clips.
    /// </summary>
    private static float MeasureAxisOffCenter(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, float centerX, float centerY, float dirX, float dirY, float centerSize, float maxRunLength)
    {
        var sum = 0f;
        var count = 0;
        for (var i = 0; i < 4; i++)
        {
            var offset = (i < 2 ? 0.5f : 1f) * (i % 2 == 0 ? 1f : -1f) * centerSize;
            var size = MeasureAxis(luminance, width, height, threshold, centerX - dirY * offset, centerY + dirX * offset, dirX, dirY, maxRunLength);
            if (!float.IsNaN(size) && size >= 1f)
            {
                sum += size;
                count++;
            }
        }
        return count == 0 ? centerSize : sum / count;
    }

    /// <summary>
    /// Module size along one axis through the finder center, from the dark-light-dark runs walked in both directions.
    /// NaN when either run clips.
    /// </summary>
    /// <remarks>
    /// Only edges of the same polarity are paired: the dark ring's inner edge on one side and its outer edge on the other are 6 modules apart, twice (12 in total).
    /// A dark-to-light edge and a light-to-dark edge move in opposite directions when grey edge pixels fall on one side of the threshold, so pairing the two outer edges (7 modules) carries that shift into the module size: one pixel of ink spread at 3 px/module is +4.8 %.
    /// </remarks>
    public static float MeasureAxis(
        ReadOnlySpan<byte> luminance,
        int width,
        int height,
        byte threshold,
        float centerX,
        float centerY,
        float dirX,
        float dirY,
        float maxRunLength = float.PositiveInfinity)
    {
        if (!TryDarkLightDarkRun(luminance, width, height, threshold, centerX, centerY, dirX, dirY, maxRunLength, out var forwardInner, out var forwardOuter)
            || !TryDarkLightDarkRun(luminance, width, height, threshold, centerX, centerY, -dirX, -dirY, maxRunLength, out var backwardInner, out var backwardOuter))
            return float.NaN;

        return (forwardInner + backwardOuter + backwardInner + forwardOuter) / 12f;
    }

    /// <summary>
    /// <see cref="MeasureAxis"/> from sub-pixel edges: where the luminance interpolated along the line crosses <paramref name="level"/>, halfway between the dark and light levels.
    /// NaN when either walk leaves the image or passes <paramref name="maxRunLength"/> before the dark ring ends.
    /// </summary>
    /// <remarks>
    /// A grey edge pixel's level is the share of it the module covers, so the crossing places the edge within a small fraction of a pixel where a threshold walk places it within half a step.
    /// </remarks>
    public static float MeasureAxisAtLevel(ReadOnlySpan<byte> luminance, int width, int height, float level, float centerX, float centerY, float dirX, float dirY, float maxRunLength)
    {
        if (!TryDarkLightDarkEdges(luminance, width, height, level, centerX, centerY, dirX, dirY, maxRunLength, out var forwardInner, out var forwardOuter)
            || !TryDarkLightDarkEdges(luminance, width, height, level, centerX, centerY, -dirX, -dirY, maxRunLength, out var backwardInner, out var backwardOuter))
            return float.NaN;

        return (forwardInner + backwardOuter + backwardInner + forwardOuter) / 12f;
    }

    private static bool TryDarkLightDarkEdges(ReadOnlySpan<byte> luminance, int width, int height, float level, float startX, float startY, float dirX, float dirY, float maxRunLength, out float inner, out float outer)
    {
        const float Step = 0.5f;
        inner = 0f;
        outer = 0f;
        var previous = LuminanceSampler.Bilinear(luminance, width, height, startX, startY);
        if (previous >= level)
            return false;

        var phase = 0;
        for (var t = Step; t <= maxRunLength; t += Step)
        {
            var x = startX + dirX * t;
            var y = startY + dirY * t;
            if (x < 0f || x >= width || y < 0f || y >= height)
                return false;

            var value = LuminanceSampler.Bilinear(luminance, width, height, x, y);
            // Out of the light ring the edge is light to dark (the dark ring's inner edge); the other two are dark to light
            var crossed = phase == 1 ? value < level : value >= level;
            if (crossed)
            {
                var edge = t - Step + Step * (level - previous) / (value - previous);
                if (phase == 1)
                    inner = edge;
                else if (phase == 2)
                {
                    outer = edge;
                    return true;
                }
                phase++;
            }
            previous = value;
        }

        return false;
    }

    /// <summary>
    /// Walks from the finder center along a direction through the dark-light-dark sequence (center square → light ring → dark ring → out), returning the distances to the dark ring's inner edge (≈ 2.5 modules) and outer edge (≈ 3.5 modules).
    /// False when the image edge or the caller's maximum run length interrupts the sequence.
    /// </summary>
    /// <remarks>
    /// The walk samples at integer pixel steps, so the first pixel past an edge overshoots it by up to one pixel.
    /// Reporting step − 0.5 centers that error: without the correction the module size is systematically overestimated (~+0.07..+0.25 px measured), which at small pixels-per-module snaps a Standard QR dimension estimate one whole version low (e.g. a 512 px version 14 render read as version 13).
    /// </remarks>
    public static bool TryDarkLightDarkRun(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, float startX, float startY, float dirX, float dirY, float maxRunLength, out float inner, out float outer)
    {
        inner = 0f;
        outer = 0f;
        var phase = 0;
        for (var step = 1f; step <= maxRunLength; step += 1f)
        {
            var x = (int)(startX + dirX * step);
            var y = (int)(startY + dirY * step);
            if (x < 0 || x >= width || y < 0 || y >= height)
            {
                // The outer dark ring may end exactly at the image edge (zero or
                // cropped quiet zone): the run is complete, not clipped.
                if (phase != 2)
                    return false;
                outer = step - 0.5f;
                return true;
            }

            var dark = luminance[y * width + x] < threshold;
            switch (phase)
            {
                case 0: // inside the 3-module center square
                    if (!dark)
                        phase = 1;
                    break;
                case 1: // light ring; ends at the dark ring's inner edge
                    if (dark)
                    {
                        inner = step - 0.5f;
                        phase = 2;
                    }
                    break;
                default: // dark ring; run ends at the transition out of it
                    if (!dark)
                    {
                        outer = step - 0.5f;
                        return true;
                    }
                    break;
            }
        }

        return false;
    }
}
