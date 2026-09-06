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
/// Local module-scale and axis recovery around a single 7×7 finder pattern, for symbologies whose orientation cannot be derived from three finder centers (Micro QR, rMQR).
/// Measures dark-light-dark runs from the finder center: the center square (3) + light ring (1) + dark ring (1) on each side spans exactly 7 modules of the 1:1:3:1:1 structure.
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

        // The pixel-grid minimum can be a few degrees away from the true finder
        // axis, particularly for small rotated symbols. Select several separated
        // minima rather than filling the result with adjacent samples of one dip.
        Span<int> selectedDegrees = stackalloc int[MaxOrientationCandidates];
        var count = 0;
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
            var radians = bestDegree * (Math.PI / 180d);
            var cos = (float)Math.Cos(radians);
            var sin = (float)Math.Sin(radians);
            var bestUSize = uSizes[bestDegree];
            var bestVSize = vSizes[bestDegree];
            destination[count++] = new OrientationCandidate(
                cos * bestUSize,
                sin * bestUSize,
                -sin * bestVSize,
                cos * bestVSize,
                bestUSize,
                bestVSize);
        }

        return count;
    }

    /// <summary>
    /// Module size along one axis through the finder center: forward and backward dark-light-dark runs together span 7 modules.
    /// NaN when either run clips.
    /// </summary>
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
        var forward = DarkLightDarkRun(luminance, width, height, threshold, centerX, centerY, dirX, dirY, maxRunLength);
        var backward = DarkLightDarkRun(luminance, width, height, threshold, centerX, centerY, -dirX, -dirY, maxRunLength);
        if (float.IsNaN(forward) || float.IsNaN(backward))
            return float.NaN;

        return (forward + backward) / 7f;
    }

    /// <summary>
    /// Walks from the finder center along a direction until the dark-light-dark sequence completes (center square → light ring → dark ring → out), returning the traveled distance (≈ 3.5 modules).
    /// NaN when the image edge or the caller's maximum run length interrupts the sequence.
    /// Returning step − 0.5 centers the one-pixel overshoot of the integer-step walk (same correction as the Standard QR measurement).
    /// </summary>
    public static float DarkLightDarkRun(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, float startX, float startY, float dirX, float dirY, float maxRunLength)
    {
        var phase = 0;
        for (var step = 1f; step <= maxRunLength; step += 1f)
        {
            var x = (int)(startX + dirX * step + 0.5f);
            var y = (int)(startY + dirY * step + 0.5f);
            if (x < 0 || x >= width || y < 0 || y >= height)
            {
                // The outer dark ring may end exactly at the image edge (zero or
                // cropped quiet zone): the run is complete, not clipped.
                return phase == 2 ? step - 0.5f : float.NaN;
            }

            var dark = luminance[y * width + x] < threshold;
            switch (phase)
            {
                case 0: // inside the 3-module center square
                    if (!dark)
                        phase = 1;
                    break;
                case 1: // light ring
                    if (dark)
                        phase = 2;
                    break;
                default: // dark ring; run ends at the transition out of it
                    if (!dark)
                        return step - 0.5f;
                    break;
            }
        }

        return float.NaN;
    }
}
