using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Internals.StandardQR;

internal static partial class QRImageDecoder
{
    /// <summary>The best confirmed candidates paired for a third finder. The pairs of four are six, each with six hypotheses.</summary>
    private const int MaxInferenceFinders = 4;

    private const int MaxHypotheses = MaxInferenceFinders * (MaxInferenceFinders - 1) / 2 * 6;

    /// <summary>
    /// The closest two finders of one symbol can be, in modules of their mean size: version 1's are 14 apart, foreshortened by a strong keystone to about two thirds.
    /// </summary>
    private const float MinPairModules = 9f;

    /// <summary>How many hypotheses are decoded, the best read timing patterns first: a third is rarely worth a decode.</summary>
    private const int MaxInferredDecodes = 2;

    /// <summary>How far the inferred centre is searched from its construction, in modules, a module at a time, then how far from there at the fine step.</summary>
    private const float InferredSearchModules = 2f;

    private const float InferredFineSearchModules = 0.5f;

    private const float InferredFineStep = 0.25f;

    /// <summary>A finder triple with one finder inferred from the other two.</summary>
    private struct Hypothesis
    {
        public FinderPattern TopLeft;
        public FinderPattern TopRight;
        public FinderPattern BottomLeft;

        /// <summary>Which finder is inferred: 0 top-left, 1 top-right, 2 bottom-left.</summary>
        public int Inferred;

        /// <summary>The share of timing modules read wrong through the frame at the centre the search settled on, the least first.</summary>
        public float Wrong;

        public readonly FinderPattern InferredFinder => Inferred == 0 ? TopLeft : Inferred == 1 ? TopRight : BottomLeft;

        public void SetInferred(in FinderPattern finder)
        {
            if (Inferred == 0)
                TopLeft = finder;
            else if (Inferred == 1)
                TopRight = finder;
            else
                BottomLeft = finder;
        }
    }

    /// <summary>
    /// Decodes with a third finder inferred from two, after the candidate list selected no triple that read: a finder under glare, damage or an odd drawing leaves two, and two finders of one symbol leave the third in one of six places.
    /// </summary>
    /// <remarks>
    /// Two candidates that share an edge of the symbol put the third at the perpendicular of their edge from either of them, on either side. Two on the diagonal put it half the perpendicular from their midpoint, on either side.
    /// The construction is exact for a flat symbol seen square on and a module or two out under perspective, so the centre is searched around it for the position whose two timing patterns read best through the frame, a module at a time to two modules out and then a quarter module within half a module.
    /// Only candidates confirmed on more than one row are paired, the best confirmed four, and a hypothesis is decoded only where its timing patterns read.
    /// The lines through the inferred finder are taken flat, since its end cannot be measured: a line's foreshortening is lost, which bounds the keystone the read holds under and leaves the corner reported at the inferred finder where that flat line extrapolates it.
    /// </remarks>
    private static DecodeStatus DecodeInferred(in ImageView image, ReadOnlySpan<FinderPattern> candidates, ref ModuleWorkspace modules, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info)
        => DecodeInferred(image, candidates, ref modules, destination, out charsWritten, out info, out _);

    /// <summary><see cref="DecodeInferred(in ImageView, ReadOnlySpan{FinderPattern}, ref ModuleWorkspace, Span{char}, out int, out QRCodeDecodeInfo)"/>, with the hypothesis whose decode settled in <paramref name="settled"/>: its two finders from the list and the inferred one.</summary>
    private static DecodeStatus DecodeInferred(in ImageView image, ReadOnlySpan<FinderPattern> candidates, ref ModuleWorkspace modules, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info, out SelectedTriple settled)
    {
        charsWritten = 0;
        info = NotDetectedInfo;
        settled = default;

        Span<FinderPattern> strong = stackalloc FinderPattern[MaxInferenceFinders];
        var strongCount = SelectStrong(candidates, strong);
        if (strongCount < 2)
            return DecodeStatus.NotDetected;

        Span<Hypothesis> hypotheses = stackalloc Hypothesis[MaxHypotheses];
        var count = 0;
        for (var i = 0; i < strongCount; i++)
        {
            for (var j = i + 1; j < strongCount; j++)
                AddHypotheses(image, strong[i], strong[j], hypotheses, ref count);
        }

        for (var h = 0; h < count; h++)
            hypotheses[h].Wrong = SearchInferredCentre(image, ref hypotheses[h]);

        var result = NewResult();
        var decoded = 0;
        while (decoded < MaxInferredDecodes)
        {
            // The best hypothesis left, taken out of the list by marking it decoded
            var best = -1;
            for (var h = 0; h < count; h++)
            {
                if (hypotheses[h].Wrong < MaxInferredTimingWrong && (best < 0 || hypotheses[h].Wrong < hypotheses[best].Wrong))
                    best = h;
            }
            if (best < 0)
                break;
            ref var hypothesis = ref hypotheses[best];
            hypothesis.Wrong = 1f;
            decoded++;

            var status = DecodeInferredCorners(image, hypothesis, ref modules, destination, out var attemptCharsWritten, out var attemptInfo);
            if (decoded == 1 ? result.Main(status, attemptCharsWritten, attemptInfo) : result.Other(status, attemptCharsWritten, attemptInfo))
            {
                settled = new SelectedTriple(hypothesis.TopLeft, hypothesis.TopRight, hypothesis.BottomLeft);
                break;
            }
        }
        return result.Report(out charsWritten, out info);
    }

    /// <summary>The share of timing modules read wrong past which a hypothesis is not decoded: the bar the timing match holds a dimension to.</summary>
    private const float MaxInferredTimingWrong = 1f / 8f;

    /// <summary>The best confirmed candidates, by the height they were seen over in modules, at most <see cref="MaxInferenceFinders"/>.</summary>
    private static int SelectStrong(ReadOnlySpan<FinderPattern> candidates, Span<FinderPattern> strong)
    {
        var count = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.Count < 2 || !(candidate.ModuleSize > 0f))
                continue;
            var modules = candidate.Count / candidate.ModuleSize;
            // Insertion into a list kept in order, dropping the last when full
            var at = count;
            while (at > 0 && strong[at - 1].Count / strong[at - 1].ModuleSize < modules)
                at--;
            if (at == strong.Length)
                continue;
            if (count < strong.Length)
                count++;
            for (var k = count - 1; k > at; k--)
                strong[k] = strong[k - 1];
            strong[at] = candidate;
        }
        return count;
    }

    /// <summary>The six triples a pair of finders implies, those whose inferred centre lies inside the image.</summary>
    private static void AddHypotheses(in ImageView image, in FinderPattern a, in FinderPattern b, Span<Hypothesis> hypotheses, ref int count)
    {
        var module = (a.ModuleSize + b.ModuleSize) / 2f;
        if (Math.Max(a.ModuleSize, b.ModuleSize) > 2f * Math.Min(a.ModuleSize, b.ModuleSize) || Distance(a, b) < MinPairModules * module)
            return;

        // The perpendicular of the pair's edge, as long as it
        var perpX = -(b.Y - a.Y);
        var perpY = b.X - a.X;
        var inferred = new FinderPattern { ModuleSize = module };

        // The pair shares an edge: either is the corner, and the third lies on either side
        for (var corner = 0; corner < 2; corner++)
        {
            var topLeft = corner == 0 ? a : b;
            var other = corner == 0 ? b : a;
            for (var sign = -1; sign <= 1; sign += 2)
            {
                inferred.X = topLeft.X + sign * perpX;
                inferred.Y = topLeft.Y + sign * perpY;
                if (!Inside(image, inferred))
                    continue;
                // Image y points down, so the cross product is positive when the first point is top-right of the corner
                var cross = (other.X - topLeft.X) * (inferred.Y - topLeft.Y) - (other.Y - topLeft.Y) * (inferred.X - topLeft.X);
                hypotheses[count++] = cross > 0f
                    ? new Hypothesis { TopLeft = topLeft, TopRight = other, BottomLeft = inferred, Inferred = 2 }
                    : new Hypothesis { TopLeft = topLeft, TopRight = inferred, BottomLeft = other, Inferred = 1 };
            }
        }

        // The pair is the diagonal: the top-left corner lies half the perpendicular from its midpoint, on either side
        var midX = (a.X + b.X) / 2f;
        var midY = (a.Y + b.Y) / 2f;
        for (var sign = -1; sign <= 1; sign += 2)
        {
            inferred.X = midX + sign * perpX / 2f;
            inferred.Y = midY + sign * perpY / 2f;
            if (!Inside(image, inferred))
                continue;
            var cross = (a.X - inferred.X) * (b.Y - inferred.Y) - (a.Y - inferred.Y) * (b.X - inferred.X);
            hypotheses[count++] = cross > 0f
                ? new Hypothesis { TopLeft = inferred, TopRight = a, BottomLeft = b, Inferred = 0 }
                : new Hypothesis { TopLeft = inferred, TopRight = b, BottomLeft = a, Inferred = 0 };
        }

        static bool Inside(in ImageView image, in FinderPattern finder)
            => finder.X >= 0f && finder.X < image.Width && finder.Y >= 0f && finder.Y < image.Height;
    }

    /// <summary>
    /// Moves the inferred centre to the position near its construction whose timing patterns read best, and returns the share of timing modules wrong there, 1 when nothing could be measured.
    /// </summary>
    private static float SearchInferredCentre(in ImageView image, ref Hypothesis hypothesis)
    {
        var sizes = MeasureInferredSizes(image, hypothesis);
        if (!(sizes.Mean >= 1f))
            return 1f;

        var origin = hypothesis.InferredFinder;
        var module = origin.ModuleSize;
        var bestWrong = TimingWrongShare(image, hypothesis, sizes);
        var bestDistance = 0f;
        SearchPosition(image, ref hypothesis, sizes, origin, module, (int)InferredSearchModules, ref bestWrong, ref bestDistance);
        SearchPosition(image, ref hypothesis, sizes, origin, InferredFineStep * module, (int)(InferredFineSearchModules / InferredFineStep), ref bestWrong, ref bestDistance);
        return bestWrong;
    }

    /// <summary>One pass of the centre search: every position within <paramref name="reach"/> steps of the current centre, keeping the one whose timing patterns read best and, on a tie, the one nearer the construction, which a flat symbol's exact construction keeps.</summary>
    private static void SearchPosition(in ImageView image, ref Hypothesis hypothesis, in FinderModuleSizes sizes, in FinderPattern origin, float step, int reach, ref float bestWrong, ref float bestDistance)
    {
        var centre = hypothesis.InferredFinder;
        var best = centre;
        for (var dy = -reach; dy <= reach; dy++)
        {
            for (var dx = -reach; dx <= reach; dx++)
            {
                if (dx == 0 && dy == 0)
                    continue;
                var moved = origin;
                moved.X = centre.X + dx * step;
                moved.Y = centre.Y + dy * step;
                hypothesis.SetInferred(moved);
                var wrong = TimingWrongShare(image, hypothesis, sizes);
                var distance = (moved.X - origin.X) * (moved.X - origin.X) + (moved.Y - origin.Y) * (moved.Y - origin.Y);
                if (wrong < bestWrong || (wrong == bestWrong && distance < bestDistance))
                {
                    bestWrong = wrong;
                    best = moved;
                    bestDistance = distance;
                }
            }
        }
        hypothesis.SetInferred(best);
    }

    /// <summary>The share of both timing patterns' modules read wrong through the frame of the hypothesis, at the dimension the frame's sizes estimate.</summary>
    private static float TimingWrongShare(in ImageView image, in Hypothesis hypothesis, in FinderModuleSizes sizes)
    {
        var frame = FinderFrame.Create(hypothesis.TopLeft, hypothesis.TopRight, hypothesis.BottomLeft, sizes);
        var dimension = NearestDimension(EstimateModules(hypothesis.TopLeft, hypothesis.TopRight, hypothesis.BottomLeft, sizes, frame));
        var transform = frame.Transform(dimension);
        var wrong = 0;
        for (var i = 8; i < dimension - 8; i++)
        {
            var dark = (i & 1) == 0;
            if (IsDarkAt(image.Luminance, image.Width, image.Height, image.Threshold, image.Grey, transform, i + 0.5f, 6.5f) != dark)
                wrong++;
            if (IsDarkAt(image.Luminance, image.Width, image.Height, image.Threshold, image.Grey, transform, 6.5f, i + 0.5f) != dark)
                wrong++;
        }
        return wrong / (2f * (dimension - 16));
    }

    /// <summary>The valid dimension nearest a module count, within versions 1 to 40.</summary>
    private static int NearestDimension(float modules)
    {
        var version = (int)Math.Round((modules - 17f) / 4f);
        return 17 + 4 * Math.Max(1, Math.Min(40, version));
    }

    /// <summary>
    /// The module sizes along the finder lines with one finder inferred: its ends are not measured, and take the size of the finder across the symbol's edge from it, as a flat line. The two real finders are measured as always.
    /// </summary>
    private static FinderModuleSizes MeasureInferredSizes(in ImageView image, in Hypothesis hypothesis)
    {
        var topLeft = hypothesis.TopLeft;
        var topRight = hypothesis.TopRight;
        var bottomLeft = hypothesis.BottomLeft;
        float topLeftAlongU, topLeftAlongV, topRightSize, bottomLeftSize;
        bool subPixelU, subPixelV;
        switch (hypothesis.Inferred)
        {
            case 0:
                (topRightSize, subPixelU) = MeasureEnd(image, topRight, topLeft);
                (bottomLeftSize, subPixelV) = MeasureEnd(image, bottomLeft, topLeft);
                topLeftAlongU = topRightSize;
                topLeftAlongV = bottomLeftSize;
                break;
            case 1:
                (topLeftAlongU, subPixelU) = MeasureEnd(image, topLeft, topRight);
                (topLeftAlongV, bottomLeftSize, subPixelV) = MeasureLine(image.Luminance, image.Width, image.Height, image.Threshold, image.Grey, topLeft, bottomLeft);
                topRightSize = topLeftAlongU;
                break;
            default:
                (topLeftAlongU, topRightSize, subPixelU) = MeasureLine(image.Luminance, image.Width, image.Height, image.Threshold, image.Grey, topLeft, topRight);
                (topLeftAlongV, subPixelV) = MeasureEnd(image, topLeft, bottomLeft);
                bottomLeftSize = topLeftAlongV;
                break;
        }

        var sum = 0f;
        var count = 0;
        Accumulate(topLeftAlongU, ref sum, ref count);
        Accumulate(topLeftAlongV, ref sum, ref count);
        Accumulate(topRightSize, ref sum, ref count);
        Accumulate(bottomLeftSize, ref sum, ref count);
        var mean = count > 0 ? sum / count : float.NaN;
        return new FinderModuleSizes(topLeftAlongU, topLeftAlongV, topRightSize, bottomLeftSize, mean, subPixelU, subPixelV);

        static void Accumulate(float value, ref float sum, ref int count)
        {
            if (!float.IsNaN(value))
            {
                sum += value;
                count++;
            }
        }
    }

    /// <summary>One real finder's module size along its line toward <paramref name="toward"/>, from sub-pixel edges where the image has grey levels, else from whole-pixel runs.</summary>
    private static (float Size, bool SubPixel) MeasureEnd(in ImageView image, in FinderPattern finder, in FinderPattern toward)
    {
        var dx = toward.X - finder.X;
        var dy = toward.Y - finder.Y;
        var length = (float)Math.Sqrt(dx * dx + dy * dy);
        if (length < 1f)
            return (float.NaN, false);
        var dirX = dx / length;
        var dirY = dy / length;
        if (image.Grey.IsEnabled)
        {
            var size = FinderAxisEstimator.MeasureAxisAtLevel(image.Luminance, image.Width, image.Height, image.Grey.Midpoint, finder.X, finder.Y, dirX, dirY, 6f * finder.ModuleSize);
            if (!float.IsNaN(size))
                return (size, true);
        }
        return (FinderAxisEstimator.MeasureAxis(image.Luminance, image.Width, image.Height, image.Threshold, finder.X, finder.Y, dirX, dirY), false);
    }

    /// <summary>A hypothesis decoded as a triple is, but for the inferred finder, which is neither measured nor refined on the image.</summary>
    private static DecodeStatus DecodeInferredCorners(in ImageView image, in Hypothesis hypothesis, ref ModuleWorkspace modules, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info)
    {
        var topLeft = hypothesis.TopLeft;
        var topRight = hypothesis.TopRight;
        var bottomLeft = hypothesis.BottomLeft;
        var sizes = MeasureInferredSizes(image, hypothesis);
        if (!(sizes.Mean >= 1f))
        {
            charsWritten = 0;
            info = NotDetectedInfo;
            return DecodeStatus.NotDetected;
        }

        var uLength = Distance(topLeft, topRight);
        var vLength = Distance(topLeft, bottomLeft);
        if (image.Grey.IsEnabled && uLength >= 1f && vLength >= 1f)
        {
            var uX = (topRight.X - topLeft.X) / uLength;
            var uY = (topRight.Y - topLeft.Y) / uLength;
            var vX = (bottomLeft.X - topLeft.X) / vLength;
            var vY = (bottomLeft.Y - topLeft.Y) / vLength;
            if (hypothesis.Inferred != 0)
                RefineCentre(image.Luminance, image.Width, image.Height, image.Grey, uX, uY, vX, vY, sizes.TopLeft, ref topLeft);
            if (hypothesis.Inferred != 1)
                RefineCentre(image.Luminance, image.Width, image.Height, image.Grey, uX, uY, vX, vY, sizes.TopRight, ref topRight);
            if (hypothesis.Inferred != 2)
                RefineCentre(image.Luminance, image.Width, image.Height, image.Grey, uX, uY, vX, vY, sizes.BottomLeft, ref bottomLeft);
            var refined = hypothesis;
            refined.TopLeft = topLeft;
            refined.TopRight = topRight;
            refined.BottomLeft = bottomLeft;
            sizes = MeasureInferredSizes(image, refined);
        }
        return DecodeFromFinders(image, topLeft, topRight, bottomLeft, sizes, ref modules, destination, out charsWritten, out info);
    }
}
