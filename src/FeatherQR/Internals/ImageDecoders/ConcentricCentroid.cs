namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// The centre of a concentric pattern's dark centre square as the centroid of the darkness around it, to a fraction of a pixel.
/// </summary>
/// <remarks>
/// A centre from thresholded runs is good to half a pixel, which below 2 px/module is a third of a module and is extrapolated across the symbol.
/// The centroid weighs every pixel by the share of it that is dark, so an edge that lands inside a pixel moves the centre by the share it covers.
/// The window is square along the symbol's axes and ends inside the light ring round the centre square, so the pixels at its edge are light and weigh nothing wherever the edge falls.
/// </remarks>
internal static class ConcentricCentroid
{
    /// <summary>The second pass centres the window on the square: below 1.6 px/module the light ring is under two pixels wide and a window off centre takes in more of the dark ring on one side.</summary>
    private const int Passes = 2;

    /// <summary>
    /// Refines (<paramref name="x"/>, <paramref name="y"/>) to the centroid of the darkness inside a window of ± <paramref name="halfModules"/> along the per-module axis vectors (<paramref name="uX"/>, <paramref name="uY"/>) and (<paramref name="vX"/>, <paramref name="vY"/>).
    /// False, with the point unchanged, when the image has no grey levels, when the dark area in the window is not about <paramref name="expectedArea"/> modules, or when the centre would move by more than <paramref name="maxShiftModules"/>.
    /// </summary>
    public static bool TryRefine(ReadOnlySpan<byte> luminance, int width, int height, in GreyLevels grey, float uX, float uY, float vX, float vY, float halfModules, float expectedArea, float maxShiftModules, ref float x, ref float y, out float darkArea)
    {
        darkArea = 0f;
        var uLength2 = uX * uX + uY * uY;
        var vLength2 = vX * vX + vY * vY;
        var moduleArea = Math.Abs(uX * vY - uY * vX);
        if (!grey.IsEnabled || uLength2 < 0.25f || vLength2 < 0.25f || moduleArea < 0.25f)
            return false;

        // The window is two slabs |d · u| ≤ half · |u|² and |d · v| ≤ half · |v|², each an interval of dx on a row
        var along = new Slab(uX, uY, halfModules * uLength2);
        var across = new Slab(vX, vY, halfModules * vLength2);
        var reachY = halfModules * (Math.Abs(uY) + Math.Abs(vY)) + 1f;
        // Darkness in whole levels: the centroid does not depend on the scale, and integer sums are exact in any order
        var light = grey.LightLevel;
        var range = light - grey.DarkLevel;

        var centreX = x;
        var centreY = y;
        var area = 0f;
        for (var pass = 0; pass < Passes; pass++)
        {
            var top = Math.Max(0, (int)(centreY - reachY));
            var bottom = Math.Min(height - 1, (int)(centreY + reachY));

            float weight = 0f, sumX = 0f, sumY = 0f;
            for (var py = top; py <= bottom; py++)
            {
                var dy = py + 0.5f - centreY;
                if (!along.TryRow(dy, float.NegativeInfinity, float.PositiveInfinity, out var low, out var high)
                    || !across.TryRow(dy, low, high, out low, out high))
                {
                    continue;
                }
                // Pixel centres px + 0.5 within [centre + low, centre + high]
                var left = Math.Max(0, (int)Math.Ceiling(centreX - 0.5f + low));
                var right = Math.Min(width - 1, (int)Math.Floor(centreX - 0.5f + high));
                if (left > right)
                    continue;
                SumRow(luminance.Slice(py * width + left, right - left + 1), light, range, out var rowWeight, out var rowMoment);
                weight += rowWeight;
                sumX += rowMoment + rowWeight * (left + 0.5f - centreX);
                sumY += rowWeight * dy;
            }
            if (weight <= 0f)
                return false;
            centreX += sumX / weight;
            centreY += sumY / weight;
            area = weight / range;
        }

        var modules = area / moduleArea;
        if (modules < expectedArea * 0.5f || modules > expectedArea * 1.5f)
            return false;
        var shiftX = centreX - x;
        var shiftY = centreY - y;
        var maxShift = maxShiftModules * maxShiftModules * Math.Min(uLength2, vLength2);
        if (shiftX * shiftX + shiftY * shiftY > maxShift)
            return false;

        darkArea = area;
        x = centreX;
        y = centreY;
        return true;
    }

    /// <summary>Σ d and Σ d · i over the row, d = min(max(light − luminance, 0), range).</summary>
    internal static void SumRow(ReadOnlySpan<byte> row, int light, int range, out int weight, out int moment)
    {
        weight = 0;
        moment = 0;
        for (var i = 0; i < row.Length; i++)
        {
            var darkness = light - row[i];
            if (darkness <= 0)
                continue;
            if (darkness > range)
                darkness = range;
            weight += darkness;
            moment += darkness * i;
        }
    }

    /// <summary>The offsets d with |d · (aX, aY)| ≤ bound, as an interval of dx for a given dy.</summary>
    private readonly struct Slab
    {
        private readonly float _slope;
        private readonly float _halfWidth;
        private readonly float _aY;
        private readonly float _bound;
        private readonly bool _level;

        public Slab(float aX, float aY, float bound)
        {
            _aY = aY;
            _bound = bound;
            // A slab parallel to the rows holds a row whole or not at all
            _level = Math.Abs(aX) < 1e-6f;
            if (!_level)
            {
                _slope = -aY / aX;
                _halfWidth = bound / Math.Abs(aX);
            }
        }

        public bool TryRow(float dy, float low, float high, out float rowLow, out float rowHigh)
        {
            rowLow = low;
            rowHigh = high;
            if (_level)
                return Math.Abs(_aY * dy) <= _bound;
            var middle = _slope * dy;
            rowLow = Math.Max(low, middle - _halfWidth);
            rowHigh = Math.Min(high, middle + _halfWidth);
            return rowLow <= rowHigh;
        }
    }
}
