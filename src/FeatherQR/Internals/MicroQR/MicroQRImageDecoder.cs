using System.Buffers;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
#endif
using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Internals.MicroQR;

/// <summary>
/// Decodes a Micro QR code from a grayscale image: clean, screen-rendered or scanned inputs, including a lighting gradient across the symbol.
/// </summary>
/// <remarks>
/// Pipeline:
/// <code>
/// 1. Global binarization threshold (Otsu, shared with Standard QR)
/// 2. Finder pattern candidates (shared 1:1:3:1:1 scan; ALL candidates, not best three)
/// 3. Fast axis-aligned grid sampling anchored on the single finder
/// 4. Failure-path angular finder-axis recovery, local center/scale refinement and
///    a bounded projective search using the shared Standard QR sampler
/// 5. Every version size (M4..M1) × orientation × transpose (mirror) is tried
/// 6. Matrix decoding arbitrates: format info is cross-checked against the matrix
///    size, and a grid may use only the corrections its timing patterns, format
///    word and quiet zone earn (MicroQRGridEvidence), at most the ISO Table 9 cap
/// </code>
/// A Micro QR symbol has a single finder pattern, so orientation cannot be derived from finder geometry the way three finders allow for Standard QR.
/// The detector therefore recovers the finder's local axes from angular dark-light-dark runs and searches the two projective coefficients that remain unknown.
/// This supports arbitrary rotation and mild perspective; strong perspective remains out of scope.
/// </remarks>
internal static class MicroQRImageDecoder
{
    /// <summary>Candidates actually tried, most-confirmed first (false hits rank behind).</summary>
    private const int MaxCandidatesToTry = 8;

    /// <summary>
    /// Maximum matrix decode attempts in the arbitrary-orientation failure path for one finder candidate.
    /// This bounds the multiplicative frame, orientation, size, scale and perspective searches while leaving enough for one complete orientation frame (all four axis assignments and four symbol sizes), and without making the result CPU-speed dependent.
    /// </summary>
    private const int MaxArbitraryOrientationDecodeAttempts = 10_000;

    /// <summary>
    /// Decodes a Micro QR code from grayscale pixels.
    /// Reflectance-reversed symbols (light modules on a dark background) are handled by one inverted retry when the normal attempt fails. A symbol lit unevenly, which no one threshold splits, is retried on a regional binarization of each polarity when both fail.
    /// </summary>
    public static DecodeStatus DecodeLuminance(ReadOnlySpan<byte> luminance, int width, int height, Span<char> destination, out int charsWritten, out MicroQRCodeDecodeInfo info)
    {
        var status = DecodeLuminanceAttempts(luminance, width, height, destination, out charsWritten, out info);
        // No text unless it decoded: a failing decode can stop after a segment was written
        if (status != DecodeStatus.Success)
            charsWritten = 0;
        return status;
    }

    private static DecodeStatus DecodeLuminanceAttempts(ReadOnlySpan<byte> luminance, int width, int height, Span<char> destination, out int charsWritten, out MicroQRCodeDecodeInfo info)
    {
        if (!ImageDimensions.TryGetPixelCount(width, height, out var pixelCount) || luminance.Length < pixelCount)
        {
            charsWritten = 0;
            info = new MicroQRCodeDecodeInfo(DecodeStatus.NotDetected, 0, default, -1, 0);
            return DecodeStatus.NotDetected;
        }

        luminance = luminance.Slice(0, pixelCount);
        // One count serves both polarities: the negative's histogram is this one mirrored
        Span<int> histogram = stackalloc int[Binarizer.HistogramBins];
        Binarizer.FillHistogram(luminance, histogram);
        var status = DecodeLuminanceCore(luminance, histogram, width, height, destination, out charsWritten, out info, out var noFinder, out var positiveThreshold, out var positiveGrey);
        if (IsTerminal(status))
            return status;

        // Reflectance reversal: if no symbol was read, invert into a rented buffer and
        // retry once. Taken only on that failure path, so success and a genuinely short
        // destination stay allocation-free.
        var rented = ArrayPool<byte>.Shared.Rent(pixelCount);
        try
        {
            var inverted = rented.AsSpan(0, pixelCount);
            LuminanceInverter.Invert(luminance, inverted);
            Binarizer.InvertHistogram(histogram);

            var invertedStatus = DecodeLuminanceCore(inverted, histogram, width, height, destination, out charsWritten, out var invertedInfo, out var invertedNoFinder, out var negativeThreshold, out var negativeGrey);
            if (IsTerminal(invertedStatus))
            {
                info = invertedInfo;
                return invertedStatus;
            }

            // A verdict skips the regional pass: the symbol was seen
            if (RegionalRetry.IsContentVerdict(status))
                return status;
            if (RegionalRetry.IsContentVerdict(invertedStatus))
            {
                info = invertedInfo;
                return invertedStatus;
            }

            // Uneven lighting: each polarity binarized again against each region's own level
            var regional = new RegionalAttempt();
            var regionalStatus = RegionalRetry.Decode<RegionalAttempt, MicroQRCodeDecodeInfo>(ref regional, luminance, inverted, histogram, width, height, destination, out charsWritten, out var regionalInfo);
            if (IsTerminal(regionalStatus) || RegionalRetry.IsContentVerdict(regionalStatus))
            {
                info = regionalInfo;
                return regionalStatus;
            }

            // Last, and only for a polarity whose global threshold found no finder at all: a symbol the regional pass reads never pays for it
            if (noFinder)
            {
                var midpointStatus = DecodeAtMidpoint(luminance, positiveThreshold, positiveGrey, width, height, destination, out charsWritten, out var midpointInfo);
                if (IsSettled(midpointStatus))
                {
                    info = midpointInfo;
                    return midpointStatus;
                }
            }
            if (invertedNoFinder)
            {
                // The regional pass wrote its binarization into this buffer
                LuminanceInverter.Invert(luminance, inverted);
                var midpointStatus = DecodeAtMidpoint(inverted, negativeThreshold, negativeGrey, width, height, destination, out charsWritten, out var midpointInfo);
                if (IsSettled(midpointStatus))
                {
                    info = midpointInfo;
                    return midpointStatus;
                }
            }

            // Report the first attempt's diagnostics: every attempt failed short of the content
            return status;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }
    }

    /// <summary>The regional retry's decode: this decoder's attempt on a binarized image.</summary>
    private readonly struct RegionalAttempt : ILuminanceAttempt<MicroQRCodeDecodeInfo>
    {
        public DecodeStatus Decode(ReadOnlySpan<byte> luminance, ReadOnlySpan<int> histogram, int width, int height, Span<char> destination, out int charsWritten, out MicroQRCodeDecodeInfo info)
            => DecodeLuminanceCore(luminance, histogram, width, height, destination, out charsWritten, out info);
    }

    /// <summary>
    /// Strided finder scan first, then a full sweep when nothing decoded.
    /// </summary>
    /// <remarks>
    /// Mirrors the rMQR image decoder: the widening trigger has to be a question about the symbol, and only the caller can ask it.
    /// See <see cref="FinderPatternFinder.FindCandidates"/>.
    /// </remarks>
    internal static DecodeStatus DecodeLuminanceCore(ReadOnlySpan<byte> luminance, ReadOnlySpan<int> histogram, int width, int height, Span<char> destination, out int charsWritten, out MicroQRCodeDecodeInfo info)
        => DecodeLuminanceCore(luminance, histogram, width, height, destination, out charsWritten, out info, out _, out _, out _);

    /// <summary>The strided scan and the sweep at the global threshold; <paramref name="noFinder"/> when neither found a finder candidate, with the threshold and levels they used.</summary>
    private static DecodeStatus DecodeLuminanceCore(ReadOnlySpan<byte> luminance, ReadOnlySpan<int> histogram, int width, int height, Span<char> destination, out int charsWritten, out MicroQRCodeDecodeInfo info, out bool noFinder, out byte threshold, out GreyLevels grey)
    {
        // Hoisted: the two scans binarize the same buffer
        threshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out grey);

        Span<FinderPattern> tried = stackalloc FinderPattern[MaxCandidatesToTry];
        var status = DecodeLuminanceScan(luminance, width, height, threshold, grey, destination, out charsWritten, out info, fullSweep: false, skip: default, tried, out var triedCount, out var stridedFound);
        noFinder = false;
        // Terminal, not just successful: DestinationTooSmall is only reached after the
        // symbol has been located, sampled, RS-corrected and its segment found to fit
        // the bitstream, so the buffer is the only thing missing and a wider finder scan
        // cannot change it. (That ordering is a precondition, not a given: the segment
        // decoders check bitstream sufficiency before destination sufficiency precisely
        // so a malformed count cannot masquerade as a short buffer here.) A verdict on
        // the content does not end it: the sweep can find another symbol that reads.
        if (IsTerminal(status))
            return status;

        // A candidate the strided scan tried decodes the same way in the sweep, so it is not
        // tried again; unless that scan settled, when every candidate stays
        var skip = IsSettled(status) ? default : tried.Slice(0, triedCount);
        var sweptStatus = DecodeLuminanceScan(luminance, width, height, threshold, grey, destination, out var sweptChars, out var sweptInfo, fullSweep: true, skip, tried: default, out _, out var sweptFound);
        noFinder = stridedFound == 0 && sweptFound == 0;
        // Settled, not just successful: when the sweep is the pass that reads the symbol,
        // its DestinationTooSmall or its verdict on the content is the answer.
        if (IsSettled(sweptStatus))
        {
            charsWritten = sweptChars;
            info = sweptInfo;
            return sweptStatus;
        }

        return status;
    }

    /// <summary>
    /// The sweep at the midpoint of the two levels, for a polarity where the global threshold found no finder at all.
    /// Edge greys pull the global threshold toward light, and a blurred finder's light ring can keep too few pixels above it; halfway between the levels the ring reads its width.
    /// </summary>
    private static DecodeStatus DecodeAtMidpoint(ReadOnlySpan<byte> luminance, byte threshold, in GreyLevels grey, int width, int height, Span<char> destination, out int charsWritten, out MicroQRCodeDecodeInfo info)
    {
        charsWritten = 0;
        info = new MicroQRCodeDecodeInfo(DecodeStatus.NotDetected, 0, default, -1, 0);
        if (!grey.IsEnabled)
            return DecodeStatus.NotDetected;
        var midpoint = (byte)Math.Round(grey.Midpoint);
        if (midpoint == threshold)
            return DecodeStatus.NotDetected;
        return DecodeLuminanceScan(luminance, width, height, midpoint, grey, destination, out charsWritten, out info, fullSweep: true, skip: default, tried: default, out _, out _);
    }

    /// <summary>
    /// One finder scan and the candidates it ranks first; those equal to one in <paramref name="skip"/> are not decoded, and each one decoded is written to <paramref name="tried"/>.
    /// </summary>
    /// <remarks>
    /// A candidate's decode depends on its position and module size, the image and the threshold, and not on the other candidates; so a candidate tried by a scan that settled on nothing settles on nothing again, and skipping it changes only a failure the caller does not report.
    /// </remarks>
    private static DecodeStatus DecodeLuminanceScan(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, Span<char> destination, out int charsWritten, out MicroQRCodeDecodeInfo info, bool fullSweep, ReadOnlySpan<FinderPattern> skip, Span<FinderPattern> tried, out int triedCount, out int found)
    {
        charsWritten = 0;
        triedCount = 0;

        Span<FinderPattern> candidates = stackalloc FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var candidateCount = fullSweep
            ? FinderPatternFinder.FindCandidatesFullSweep(luminance, width, height, threshold, candidates, grey)
            : FinderPatternFinder.FindCandidates(luminance, width, height, threshold, candidates, grey);
        found = candidateCount;
        if (candidateCount == 0)
        {
            info = new MicroQRCodeDecodeInfo(DecodeStatus.NotDetected, 0, default, -1, 0);
            return DecodeStatus.NotDetected;
        }

        // Most-confirmed candidates first: repeated row hits separate real finder
        // patterns from data-area false positives.
        // Insertion sort: netstandard2.0 has no Span.Sort, and the list is tiny (≤ 32).
        for (var i = 1; i < candidateCount; i++)
        {
            var current = candidates[i];
            var j = i - 1;
            while (j >= 0 && candidates[j].Count < current.Count)
            {
                candidates[j + 1] = candidates[j];
                j--;
            }
            candidates[j + 1] = current;
        }

        // The furthest-progressing failure is the most useful diagnostic: an attempt
        // that passed format decoding but failed RS says more than "not detected".
        var bestStatus = DecodeStatus.NotDetected;
        var bestInfo = new MicroQRCodeDecodeInfo(DecodeStatus.NotDetected, 0, default, -1, 0);

        Span<byte> modules = stackalloc byte[17 * 17];
        Span<int> boundaryColumns = stackalloc int[18];
        Span<int> boundaryRows = stackalloc int[18];

        var ranked = Math.Min(candidateCount, MaxCandidatesToTry);
        for (var c = 0; c < ranked; c++)
        {
            // Not replaced by the next in rank: the candidates tried stay the first eight
            if (FinderPatternFinder.ContainsCandidate(skip, candidates[c]))
                continue;
            if (!tried.IsEmpty)
                tried[triedCount++] = candidates[c];
            var candidate = candidates[c];
            FinderAxisEstimator.RefineModuleSize(luminance, width, height, threshold, candidate, out var horizontalModuleSize, out var verticalModuleSize);
            if (horizontalModuleSize < 1f || verticalModuleSize < 1f)
                continue; // below one pixel per module nothing can be sampled reliably
            ConcentricCentroid.TryRefine(luminance, width, height, grey, horizontalModuleSize, 0f, 0f, verticalModuleSize, 2f, 9f, 0.75f, ref candidate.X, ref candidate.Y, out _);
            var samplingSlack = Math.Max(horizontalModuleSize, verticalModuleSize);

            // Right-angle orientations as grid axis pairs (u = grid column axis,
            // v = grid row axis, in pixels per module).
            for (var orientation = 0; orientation < 4; orientation++)
            {
                var (uX, uY, vX, vY) = orientation switch
                {
                    0 => (horizontalModuleSize, 0f, 0f, verticalModuleSize),   // finder at symbol top-left
                    1 => (0f, verticalModuleSize, -horizontalModuleSize, 0f),  // rotated 90° clockwise
                    2 => (-horizontalModuleSize, 0f, 0f, -verticalModuleSize), // rotated 180°
                    _ => (0f, -verticalModuleSize, horizontalModuleSize, 0f),  // rotated 270°
                };

                // Grid origin: the finder center sits at grid (3.5, 3.5)
                var originX = candidate.X - 3.5f * (uX + vX);
                var originY = candidate.Y - 3.5f * (uY + vY);

                // Larger sizes first: a real M4 sampled as M2 reads a garbled
                // sub-grid, while trying real sizes first exits at the first success.
                for (var size = 17; size >= 11; size -= 2)
                {
                    if (!SymbolFitsImage(originX, originY, uX, uY, vX, vY, size, width, height, samplingSlack))
                        continue;

                    SampleGrid(luminance, width, height, threshold, originX, originY, uX, uY, vX, vY, size, modules);
                    var quietZone = new AffineQuietZone(originX, originY, uX, uY, vX, vY);

                    var status = DecodeGrid(modules, new MatrixModules(size), size, luminance, width, height, threshold, quietZone, destination, out charsWritten, out var attemptInfo);
                    if (status == DecodeStatus.Success)
                    {
                        info = attemptInfo.WithCorners(SymbolGeometry.FromAffine(originX, originY, uX, uY, vX, vY, size, transposed: false));
                        return status;
                    }
                    TrackBestFailure(status, attemptInfo, ref bestStatus, ref bestInfo);

                    // Mirrored capture (e.g. front camera): finder geometry is
                    // identical but the data grid is transposed.
                    var mirroredStatus = DecodeGrid(modules, new TransposedModules<MatrixModules>(new MatrixModules(size)), size, luminance, width, height, threshold, quietZone, destination, out charsWritten, out var mirroredInfo);
                    if (mirroredStatus == DecodeStatus.Success)
                    {
                        info = mirroredInfo.WithCorners(SymbolGeometry.FromAffine(originX, originY, uX, uY, vX, vY, size, transposed: true));
                        return mirroredStatus;
                    }
                    TrackBestFailure(mirroredStatus, mirroredInfo, ref bestStatus, ref bestInfo);

                    if (ShouldReadByCoverage(grey, status, mirroredStatus, modules, size))
                    {
                        var coverageStatus = DecodeByCoverage(luminance, width, height, threshold, grey, new AffineGrid(originX, originY, uX, uY, vX, vY), size, modules, destination, out charsWritten, out var coverageInfo, ref bestStatus, ref bestInfo);
                        if (coverageStatus == DecodeStatus.Success)
                        {
                            info = coverageInfo;
                            return coverageStatus;
                        }
                    }
                }

                // Timing frame: the finder's module size is extrapolated across the whole
                // symbol, and a render that snaps modules to whole pixels can give the
                // finder a size a few percent off. The timing patterns reach the far edge,
                // so they measure the symbol itself. Failure-path cost only.
                if (TryTimingFrame(luminance, width, height, threshold, candidate, uX, uY, vX, vY, out var timingOriginX, out var timingOriginY, out var tuX, out var tuY, out var tvX, out var tvY, out var timingSize)
                    && SymbolFitsImage(timingOriginX, timingOriginY, tuX, tuY, tvX, tvY, timingSize, width, height, samplingSlack))
                {
                    SampleGrid(luminance, width, height, threshold, timingOriginX, timingOriginY, tuX, tuY, tvX, tvY, timingSize, modules);
                    var quietZone = new AffineQuietZone(timingOriginX, timingOriginY, tuX, tuY, tvX, tvY);
                    var timingStatus = DecodeGrid(modules, new MatrixModules(timingSize), timingSize, luminance, width, height, threshold, quietZone, destination, out charsWritten, out var timingInfo);
                    if (timingStatus == DecodeStatus.Success)
                    {
                        info = timingInfo.WithCorners(SymbolGeometry.FromAffine(timingOriginX, timingOriginY, tuX, tuY, tvX, tvY, timingSize, transposed: false));
                        return timingStatus;
                    }
                    TrackBestFailure(timingStatus, timingInfo, ref bestStatus, ref bestInfo);

                    var mirroredTimingStatus = DecodeGrid(modules, new TransposedModules<MatrixModules>(new MatrixModules(timingSize)), timingSize, luminance, width, height, threshold, quietZone, destination, out charsWritten, out var mirroredTimingInfo);
                    if (mirroredTimingStatus == DecodeStatus.Success)
                    {
                        info = mirroredTimingInfo.WithCorners(SymbolGeometry.FromAffine(timingOriginX, timingOriginY, tuX, tuY, tvX, tvY, timingSize, transposed: true));
                        return mirroredTimingStatus;
                    }
                    TrackBestFailure(mirroredTimingStatus, mirroredTimingInfo, ref bestStatus, ref bestInfo);
                }

                // Module boundaries: under about 1.5 px/module a crisp module is 1 or 2 px wide
                // and a sample has an eighth of a pixel to spare, which neither fitted frame keeps.
                if (samplingSlack < ModuleBoundaryReader.MaxModuleSize
                    && TryReadModuleBoundaries(luminance, width, height, threshold, candidate, Math.Sign(uX), Math.Sign(uY), Math.Sign(vX), Math.Sign(vY), samplingSlack, boundaryColumns, boundaryRows, out var frame, out var boundarySize))
                {
                    ModuleBoundaryReader.Sample(luminance, width, height, threshold, frame, boundaryColumns, boundarySize, boundaryRows, boundarySize, modules);
                    var quietZone = new CountedQuietZone(ModuleBoundaryReader.CountQuietZoneDark(luminance, width, height, threshold, frame, boundaryColumns, boundarySize, boundaryRows, boundarySize));
                    frame.ToImage(boundaryColumns[0], boundaryRows[0], out var boundaryOriginX, out var boundaryOriginY);
                    var pitchU = (boundaryColumns[boundarySize] - boundaryColumns[0]) / (float)boundarySize;
                    var pitchV = (boundaryRows[boundarySize] - boundaryRows[0]) / (float)boundarySize;

                    var boundaryStatus = DecodeGrid(modules, new MatrixModules(boundarySize), boundarySize, luminance, width, height, threshold, quietZone, destination, out charsWritten, out var boundaryInfo);
                    if (boundaryStatus == DecodeStatus.Success)
                    {
                        info = boundaryInfo.WithCorners(SymbolGeometry.FromAffine(boundaryOriginX, boundaryOriginY, pitchU * frame.UX, pitchU * frame.UY, pitchV * frame.VX, pitchV * frame.VY, boundarySize, transposed: false));
                        return boundaryStatus;
                    }
                    TrackBestFailure(boundaryStatus, boundaryInfo, ref bestStatus, ref bestInfo);

                    var mirroredBoundaryStatus = DecodeGrid(modules, new TransposedModules<MatrixModules>(new MatrixModules(boundarySize)), boundarySize, luminance, width, height, threshold, quietZone, destination, out charsWritten, out var mirroredBoundaryInfo);
                    if (mirroredBoundaryStatus == DecodeStatus.Success)
                    {
                        info = mirroredBoundaryInfo.WithCorners(SymbolGeometry.FromAffine(boundaryOriginX, boundaryOriginY, pitchU * frame.UX, pitchU * frame.UY, pitchV * frame.VX, pitchV * frame.VY, boundarySize, transposed: true));
                        return mirroredBoundaryStatus;
                    }
                    TrackBestFailure(mirroredBoundaryStatus, mirroredBoundaryInfo, ref bestStatus, ref bestInfo);
                }
            }

            // The fast path above covers the overwhelmingly common axis-aligned
            // case. On failure, recover the finder square's local axes by sweeping
            // directions through 90 degrees, then sample along those rotated axes.
            // A square finder repeats every 90 degrees; the four sign/axis
            // assignments below recover the symbol orientation.
            var rotatedStatus = TryDecodeArbitraryOrientation(
                luminance,
                width,
                height,
                threshold,
                grey,
                candidate,
                modules,
                destination,
                out charsWritten,
                out var rotatedInfo,
                ref bestStatus,
                ref bestInfo);
            if (rotatedStatus == DecodeStatus.Success)
            {
                info = rotatedInfo;
                return rotatedStatus;
            }
        }

        charsWritten = 0;
        info = bestInfo;
        return bestStatus;
    }

    /// <summary>
    /// Recovers arbitrary image rotation from the single finder pattern.
    /// For a concentric square finder, a center ray crosses the shortest dark-light-dark span when it follows one of the square's local axes.
    /// An angular sweep therefore supplies the two grid-axis directions without the three finder centers available to Standard QR.
    /// </summary>
    private static DecodeStatus TryDecodeArbitraryOrientation(
        ReadOnlySpan<byte> luminance,
        int width,
        int height,
        byte threshold,
        in GreyLevels grey,
        in FinderPattern finder,
        Span<byte> modules,
        Span<char> destination,
        out int charsWritten,
        out MicroQRCodeDecodeInfo info,
        ref DecodeStatus bestStatus,
        ref MicroQRCodeDecodeInfo bestInfo)
    {
        Span<OrientationCandidate> orientations = stackalloc OrientationCandidate[FinderAxisEstimator.MaxOrientationCandidates];
        var orientationCount = FinderAxisEstimator.FindOrientationCandidates(luminance, width, height, threshold, finder, orientations);
        var attemptsRemaining = MaxArbitraryOrientationDecodeAttempts;

        for (var frameIndex = 0; frameIndex < orientationCount; frameIndex++)
        {
            ref readonly var frame = ref orientations[frameIndex];
            // The centre square's window has to lie along the symbol's axes, which only this frame knows
            var candidate = finder;
            ConcentricCentroid.TryRefine(luminance, width, height, grey, frame.UX, frame.UY, frame.VX, frame.VY, 2f, 9f, 0.75f, ref candidate.X, ref candidate.Y, out _);
            for (var orientation = 0; orientation < 4; orientation++)
            {
                var (uX, uY, vX, vY) = orientation switch
                {
                    0 => (frame.UX, frame.UY, frame.VX, frame.VY),
                    1 => (frame.VX, frame.VY, -frame.UX, -frame.UY),
                    2 => (-frame.UX, -frame.UY, -frame.VX, -frame.VY),
                    _ => (-frame.VX, -frame.VY, frame.UX, frame.UY),
                };

                var originX = candidate.X - 3.5f * (uX + vX);
                var originY = candidate.Y - 3.5f * (uY + vY);
                var samplingSlack = Math.Max(frame.USize, frame.VSize);

                for (var size = 17; size >= 11; size -= 2)
                {
                    if (attemptsRemaining == 0)
                    {
                        charsWritten = 0;
                        info = bestInfo;
                        return bestStatus;
                    }

                    if (!SymbolFitsImage(originX, originY, uX, uY, vX, vY, size, width, height, samplingSlack))
                        continue;

                    SampleGrid(luminance, width, height, threshold, originX, originY, uX, uY, vX, vY, size, modules);
                    var quietZone = new AffineQuietZone(originX, originY, uX, uY, vX, vY);
                    attemptsRemaining--;
                    var status = DecodeGrid(modules, new MatrixModules(size), size, luminance, width, height, threshold, quietZone, destination, out charsWritten, out var attemptInfo);
                    if (status == DecodeStatus.Success)
                    {
                        info = attemptInfo.WithCorners(SymbolGeometry.FromAffine(originX, originY, uX, uY, vX, vY, size, transposed: false));
                        return status;
                    }
                    TrackBestFailure(status, attemptInfo, ref bestStatus, ref bestInfo);

                    if (attemptsRemaining == 0)
                        continue;

                    attemptsRemaining--;
                    var mirroredStatus = DecodeGrid(modules, new TransposedModules<MatrixModules>(new MatrixModules(size)), size, luminance, width, height, threshold, quietZone, destination, out charsWritten, out var mirroredInfo);
                    if (mirroredStatus == DecodeStatus.Success)
                    {
                        info = mirroredInfo.WithCorners(SymbolGeometry.FromAffine(originX, originY, uX, uY, vX, vY, size, transposed: true));
                        return mirroredStatus;
                    }
                    TrackBestFailure(mirroredStatus, mirroredInfo, ref bestStatus, ref bestInfo);

                    if (ShouldReadByCoverage(grey, status, mirroredStatus, modules, size))
                    {
                        var coverageStatus = DecodeByCoverage(luminance, width, height, threshold, grey, new AffineGrid(originX, originY, uX, uY, vX, vY), size, modules, destination, out charsWritten, out var coverageInfo, ref bestStatus, ref bestInfo);
                        if (coverageStatus == DecodeStatus.Success)
                        {
                            info = coverageInfo;
                            return coverageStatus;
                        }
                    }

                    // Scale and perspective searches multiply this affine attempt
                    // by hundreds. Enter them only after either polarity decoded
                    // valid format information; wrong grids overwhelmingly fail
                    // before that point.
                    if (!IsPlausibleRefinement(status) && !IsPlausibleRefinement(mirroredStatus))
                        continue;

                    var scaledStatus = TryDecodeScaleVariants(
                        luminance, width, height, threshold, grey, candidate,
                        uX, uY, vX, vY, size, modules, destination,
                        out charsWritten, out var scaledInfo,
                        ref bestStatus, ref bestInfo, ref attemptsRemaining);
                    if (scaledStatus == DecodeStatus.Success)
                    {
                        info = scaledInfo;
                        return scaledStatus;
                    }

                    var projectiveStatus = TryDecodePerspectiveVariants(
                        luminance,
                        width,
                        height,
                        threshold,
                        grey,
                        candidate,
                        uX,
                        uY,
                        vX,
                        vY,
                        size,
                        samplingSlack,
                        modules,
                        destination,
                        out charsWritten,
                        out var projectiveInfo,
                        ref bestStatus,
                        ref bestInfo,
                        ref attemptsRemaining);
                    if (projectiveStatus == DecodeStatus.Success)
                    {
                        info = projectiveInfo;
                        return projectiveStatus;
                    }
                }
            }
        }

        charsWritten = 0;
        info = bestInfo;
        return bestStatus;
    }

    /// <summary>
    /// Refines the two local module scales independently around the pixel-quantized finder-run estimate while keeping the finder center fixed.
    /// </summary>
    private static DecodeStatus TryDecodeScaleVariants(
        ReadOnlySpan<byte> luminance,
        int width,
        int height,
        byte threshold,
        in GreyLevels grey,
        in FinderPattern candidate,
        float uX,
        float uY,
        float vX,
        float vY,
        int size,
        Span<byte> modules,
        Span<char> destination,
        out int charsWritten,
        out MicroQRCodeDecodeInfo info,
        ref DecodeStatus bestStatus,
        ref MicroQRCodeDecodeInfo bestInfo,
        ref int attemptsRemaining)
    {
        ReadOnlySpan<float> centerOffsets = stackalloc float[] { 0f, -0.5f, 0.5f };
        ReadOnlySpan<float> factors = stackalloc float[] { 0.94f, 0.97f, 1f, 1.03f, 1.06f };
        var uSize = (float)Math.Sqrt(uX * uX + uY * uY);
        var vSize = (float)Math.Sqrt(vX * vX + vY * vY);
        foreach (var centerYOffset in centerOffsets)
        {
            foreach (var centerXOffset in centerOffsets)
            {
                foreach (var vFactor in factors)
                {
                    foreach (var uFactor in factors)
                    {
                        if (attemptsRemaining == 0)
                        {
                            charsWritten = 0;
                            info = bestInfo;
                            return bestStatus;
                        }

                        if (centerXOffset == 0f && centerYOffset == 0f && uFactor == 1f && vFactor == 1f)
                            continue;

                        var scaledUX = uX * uFactor;
                        var scaledUY = uY * uFactor;
                        var scaledVX = vX * vFactor;
                        var scaledVY = vY * vFactor;
                        var centerX = candidate.X + centerXOffset;
                        var centerY = candidate.Y + centerYOffset;
                        var originX = centerX - 3.5f * (scaledUX + scaledVX);
                        var originY = centerY - 3.5f * (scaledUY + scaledVY);
                        var samplingSlack = Math.Max(uSize * uFactor, vSize * vFactor);
                        if (!SymbolFitsImage(originX, originY, scaledUX, scaledUY, scaledVX, scaledVY, size, width, height, samplingSlack))
                            continue;

                        SampleGrid(luminance, width, height, threshold, originX, originY, scaledUX, scaledUY, scaledVX, scaledVY, size, modules);
                        var quietZone = new AffineQuietZone(originX, originY, scaledUX, scaledUY, scaledVX, scaledVY);
                        attemptsRemaining--;
                        var status = DecodeGrid(modules, new MatrixModules(size), size, luminance, width, height, threshold, quietZone, destination, out charsWritten, out var attemptInfo);
                        if (status == DecodeStatus.Success)
                        {
                            info = attemptInfo.WithCorners(SymbolGeometry.FromAffine(originX, originY, scaledUX, scaledUY, scaledVX, scaledVY, size, transposed: false));
                            return status;
                        }
                        TrackBestFailure(status, attemptInfo, ref bestStatus, ref bestInfo);

                        if (attemptsRemaining == 0)
                            continue;

                        attemptsRemaining--;
                        var mirroredStatus = DecodeGrid(modules, new TransposedModules<MatrixModules>(new MatrixModules(size)), size, luminance, width, height, threshold, quietZone, destination, out charsWritten, out var mirroredInfo);
                        if (mirroredStatus == DecodeStatus.Success)
                        {
                            info = mirroredInfo.WithCorners(SymbolGeometry.FromAffine(originX, originY, scaledUX, scaledUY, scaledVX, scaledVY, size, transposed: true));
                            return mirroredStatus;
                        }
                        TrackBestFailure(mirroredStatus, mirroredInfo, ref bestStatus, ref bestInfo);

                        if (ShouldReadByCoverage(grey, status, mirroredStatus, modules, size))
                        {
                            var coverageStatus = DecodeByCoverage(luminance, width, height, threshold, grey, new AffineGrid(originX, originY, scaledUX, scaledUY, scaledVX, scaledVY), size, modules, destination, out charsWritten, out var coverageInfo, ref bestStatus, ref bestInfo);
                            if (coverageStatus == DecodeStatus.Success)
                            {
                                info = coverageInfo;
                                return coverageStatus;
                            }
                        }
                    }
                }
            }
        }

        charsWritten = 0;
        info = bestInfo;
        return bestStatus;
    }

    /// <summary>
    /// A single finder determines a homography's image point and local Jacobian, leaving only the two projective denominator coefficients unknown.
    /// Search a bounded Tier-2 range for those two values; matrix format and RS validation select the correct transform without image-specific heuristics.
    /// </summary>
    private static DecodeStatus TryDecodePerspectiveVariants(
        ReadOnlySpan<byte> luminance,
        int width,
        int height,
        byte threshold,
        in GreyLevels grey,
        in FinderPattern candidate,
        float uX,
        float uY,
        float vX,
        float vY,
        int size,
        float samplingSlack,
        Span<byte> modules,
        Span<char> destination,
        out int charsWritten,
        out MicroQRCodeDecodeInfo info,
        ref DecodeStatus bestStatus,
        ref MicroQRCodeDecodeInfo bestInfo,
        ref int attemptsRemaining)
    {
        ReadOnlySpan<float> strengths = stackalloc float[] { -0.12f, -0.08f, -0.04f, -0.02f, 0f, 0.02f, 0.04f, 0.08f, 0.12f };
        for (var pyIndex = 0; pyIndex < strengths.Length; pyIndex++)
        {
            var perspectiveY = strengths[pyIndex] / size;
            for (var pxIndex = 0; pxIndex < strengths.Length; pxIndex++)
            {
                if (attemptsRemaining == 0)
                {
                    charsWritten = 0;
                    info = bestInfo;
                    return bestStatus;
                }

                var perspectiveX = strengths[pxIndex] / size;
                if (perspectiveX == 0f && perspectiveY == 0f)
                    continue; // the affine transform was already tried

                var transform = PerspectiveTransform.FromLocalFrame(
                    3.5f,
                    3.5f,
                    candidate.X,
                    candidate.Y,
                    uX,
                    uY,
                    vX,
                    vY,
                    perspectiveX,
                    perspectiveY);
                if (!ProjectiveSymbolFitsImage(transform, size, width, height, samplingSlack))
                    continue;

                QRImageDecoder.SampleGrid(luminance, width, height, threshold, transform, size, modules);
                var quietZone = new ProjectiveQuietZone(transform);
                attemptsRemaining--;
                var status = DecodeGrid(modules, new MatrixModules(size), size, luminance, width, height, threshold, quietZone, destination, out charsWritten, out var attemptInfo);
                if (status == DecodeStatus.Success)
                {
                    info = attemptInfo.WithCorners(SymbolGeometry.FromTransform(transform, size, size, transposed: false));
                    return status;
                }
                TrackBestFailure(status, attemptInfo, ref bestStatus, ref bestInfo);

                if (attemptsRemaining == 0)
                    continue;

                attemptsRemaining--;
                var mirroredStatus = DecodeGrid(modules, new TransposedModules<MatrixModules>(new MatrixModules(size)), size, luminance, width, height, threshold, quietZone, destination, out charsWritten, out var mirroredInfo);
                if (mirroredStatus == DecodeStatus.Success)
                {
                    info = mirroredInfo.WithCorners(SymbolGeometry.FromTransform(transform, size, size, transposed: true));
                    return mirroredStatus;
                }
                TrackBestFailure(mirroredStatus, mirroredInfo, ref bestStatus, ref bestInfo);

                if (ShouldReadByCoverage(grey, status, mirroredStatus, modules, size))
                {
                    var coverageStatus = DecodeByCoverage(luminance, width, height, threshold, grey, new ProjectiveGrid(transform), size, modules, destination, out charsWritten, out var coverageInfo, ref bestStatus, ref bestInfo);
                    if (coverageStatus == DecodeStatus.Success)
                    {
                        info = coverageInfo;
                        return coverageStatus;
                    }
                }
            }
        }

        charsWritten = 0;
        info = bestInfo;
        return bestStatus;
    }

    /// <summary>
    /// A grid is read again by coverage only when the image has grey levels and an orientation that got past its format information read that word exactly.
    /// A real symbol's format modules sit next to the finder, where the frame is best; texture reads a word within 3 bits about half the time and an exact one about 1 in 1,000.
    /// </summary>
    private static bool ShouldReadByCoverage(in GreyLevels grey, DecodeStatus status, DecodeStatus mirroredStatus, ReadOnlySpan<byte> modules, int size)
        => grey.IsEnabled
            && ((IsPlausibleRefinement(status) && MicroQRMatrixDecoder.HasExactFormat(modules, new MatrixModules(size), size))
                || (IsPlausibleRefinement(mirroredStatus) && MicroQRMatrixDecoder.HasExactFormat(modules, new TransposedModules<MatrixModules>(new MatrixModules(size)), size)));

    /// <summary>
    /// A grid read again with each module's luminance interpolated at its centre and split halfway between the two grey levels, in both orientations.
    /// </summary>
    private static DecodeStatus DecodeByCoverage<TGrid>(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, in TGrid grid, int size, Span<byte> modules, Span<char> destination, out int charsWritten, out MicroQRCodeDecodeInfo info, ref DecodeStatus bestStatus, ref MicroQRCodeDecodeInfo bestInfo)
        where TGrid : struct, ICoverageGrid
    {
        var midpoint = grey.Midpoint;
        var changed = false;
        for (var v = 0; v < size; v++)
        {
            for (var u = 0; u < size; u++)
            {
                grid.Map(u + 0.5f, v + 0.5f, out var x, out var y);
                var dark = LuminanceSampler.Bilinear(luminance, width, height, x, y) < midpoint ? (byte)1 : (byte)0;
                changed |= modules[v * size + u] != dark;
                modules[v * size + u] = dark;
            }
        }

        // The grid that already failed decodes the same way again
        if (!changed)
        {
            charsWritten = 0;
            info = bestInfo;
            return bestStatus;
        }

        var status = grid.Decode(modules, new MatrixModules(size), size, luminance, width, height, threshold, destination, out charsWritten, out var attemptInfo);
        if (status == DecodeStatus.Success)
        {
            info = attemptInfo.WithCorners(grid.Corners(size, transposed: false));
            return status;
        }
        TrackBestFailure(status, attemptInfo, ref bestStatus, ref bestInfo);

        var mirroredStatus = grid.Decode(modules, new TransposedModules<MatrixModules>(new MatrixModules(size)), size, luminance, width, height, threshold, destination, out charsWritten, out var mirroredInfo);
        if (mirroredStatus == DecodeStatus.Success)
        {
            info = mirroredInfo.WithCorners(grid.Corners(size, transposed: true));
            return mirroredStatus;
        }
        TrackBestFailure(mirroredStatus, mirroredInfo, ref bestStatus, ref bestInfo);
        info = bestInfo;
        return mirroredStatus;
    }

    /// <summary>Grid coordinates to image coordinates, with the quiet zone and corners of the same mapping.</summary>
    private interface ICoverageGrid
    {
        void Map(float u, float v, out float x, out float y);

        DecodeStatus Decode<TModules>(ReadOnlySpan<byte> modules, in TModules grid, int size, ReadOnlySpan<byte> luminance, int width, int height, byte threshold, Span<char> destination, out int charsWritten, out MicroQRCodeDecodeInfo info)
            where TModules : struct, IMicroQRModules;

        SymbolCorners Corners(int size, bool transposed);
    }

    private readonly struct AffineGrid(float originX, float originY, float uX, float uY, float vX, float vY) : ICoverageGrid
    {
        public void Map(float u, float v, out float x, out float y)
        {
            x = originX + u * uX + v * vX;
            y = originY + u * uY + v * vY;
        }

        public DecodeStatus Decode<TModules>(ReadOnlySpan<byte> modules, in TModules grid, int size, ReadOnlySpan<byte> luminance, int width, int height, byte threshold, Span<char> destination, out int charsWritten, out MicroQRCodeDecodeInfo info)
            where TModules : struct, IMicroQRModules
            => DecodeGrid(modules, grid, size, luminance, width, height, threshold, new AffineQuietZone(originX, originY, uX, uY, vX, vY), destination, out charsWritten, out info);

        public SymbolCorners Corners(int size, bool transposed) => SymbolGeometry.FromAffine(originX, originY, uX, uY, vX, vY, size, transposed);
    }

    private readonly struct ProjectiveGrid(in PerspectiveTransform transform) : ICoverageGrid
    {
        private readonly PerspectiveTransform _transform = transform;

        public void Map(float u, float v, out float x, out float y) => _transform.Transform(u, v, out x, out y);

        public DecodeStatus Decode<TModules>(ReadOnlySpan<byte> modules, in TModules grid, int size, ReadOnlySpan<byte> luminance, int width, int height, byte threshold, Span<char> destination, out int charsWritten, out MicroQRCodeDecodeInfo info)
            where TModules : struct, IMicroQRModules
            => DecodeGrid(modules, grid, size, luminance, width, height, threshold, new ProjectiveQuietZone(_transform), destination, out charsWritten, out info);

        public SymbolCorners Corners(int size, bool transposed) => SymbolGeometry.FromTransform(_transform, size, size, transposed);
    }

    /// <summary>One grid's matrix decode: the corrections its read may use are what its structure earns (<see cref="MicroQRMatrixDecoder.DecodeSampledGrid"/>).</summary>
    private static DecodeStatus DecodeGrid<TModules, TQuietZone>(ReadOnlySpan<byte> modules, in TModules grid, int size, ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in TQuietZone quietZone, Span<char> destination, out int charsWritten, out MicroQRCodeDecodeInfo info)
        where TModules : struct, IMicroQRModules
        where TQuietZone : struct, IMicroQRQuietZone
        => MicroQRMatrixDecoder.DecodeSampledGrid(modules, grid, size, luminance, width, height, threshold, quietZone, destination, out charsWritten, out info);

    /// <summary>
    /// Ranks decode failures by how far the attempt progressed; keeps the deepest.
    /// Wrong-grid samples overwhelmingly die at format decoding, so anything past it almost certainly hit the real grid.
    /// </summary>
    private static void TrackBestFailure(DecodeStatus status, in MicroQRCodeDecodeInfo attemptInfo, ref DecodeStatus bestStatus, ref MicroQRCodeDecodeInfo bestInfo)
    {
        if (Rank(status) > Rank(bestStatus))
        {
            bestStatus = status;
            bestInfo = attemptInfo;
        }

        static int Rank(DecodeStatus s) => s switch
        {
            DecodeStatus.NotDetected => 0,
            DecodeStatus.InvalidMatrix => 1,
            DecodeStatus.FormatInformationInvalid => 1,
            // The symbol was read (format + RS) and only the caller's buffer is short:
            // this outranks every failure short of the content, so a wrong-size attempt that reaches RS
            // first — sizes are tried 17 down to 11 — cannot mask it. Matches the rMQR
            // decoder's ranking; without it M3-L reported DataUncorrectable for a short
            // buffer while every other version reported DestinationTooSmall.
            DecodeStatus.DestinationTooSmall => 3,
            // A verdict on the content comes after error correction too, so a wrong grid's
            // correction failure tried before the right one cannot mask it
            DecodeStatus.UnmappedCharacter or DecodeStatus.UnsupportedContent => 3,
            _ => 2, // got past format decoding
        };
    }

    private static bool IsPlausibleRefinement(DecodeStatus status)
        => status is not DecodeStatus.NotDetected
            and not DecodeStatus.InvalidMatrix
            and not DecodeStatus.FormatInformationInvalid;

    private static bool IsTerminal(DecodeStatus status)
        => status is DecodeStatus.Success or DecodeStatus.DestinationTooSmall;

    /// <summary>A result no other grid or pass for the same symbol improves on: read, too long for the destination, or a verdict on its content.</summary>
    private static bool IsSettled(DecodeStatus status)
        => IsTerminal(status) || RegionalRetry.IsContentVerdict(status);

    /// <summary>
    /// The module boundaries of an axis-aligned symbol along both axes, <c>size + 1</c> each (<see cref="ModuleBoundaryReader"/>): module row 0 and module column 0 run from the finder's edge rows to the symbol's far edges.
    /// False unless both lines read as timing patterns and count the same valid size.
    /// </summary>
    internal static bool TryReadModuleBoundaries(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in FinderPattern candidate, int uX, int uY, int vX, int vY, float moduleSize, Span<int> columns, Span<int> rows, out AxisAlignedFrame frame, out int size)
    {
        frame = default;
        var centerX = (int)candidate.X;
        var centerY = (int)candidate.Y;

        // Rows and columns 0 are the finder's first edge rows, so its last dark run walking back from the centre
        var maxPosition = (int)(16f * 1.6f * moduleSize);
        if (!ModuleBoundaryReader.TryFinderEdgeRow(luminance, width, height, threshold, centerX, centerY, -vX, -vY, out var rowOffset)
            || !ModuleBoundaryReader.TryFinderEdgeRow(luminance, width, height, threshold, centerX, centerY, -uX, -uY, out var columnOffset)
            || !ModuleBoundaryReader.TryReadTimingLine(luminance, width, height, threshold, centerX - rowOffset * vX, centerY - rowOffset * vY, uX, uY, moduleSize, endsOnFinder: false, allowTriples: false, maxPosition, columns, out size)
            || !ModuleBoundaryReader.TryReadTimingLine(luminance, width, height, threshold, centerX - columnOffset * uX, centerY - columnOffset * uY, vX, vY, moduleSize, endsOnFinder: false, allowTriples: false, maxPosition, rows, out var rowSize)
            || size != rowSize
            || size < 11 || size > 17 || size % 2 == 0)
        {
            size = 0;
            return false;
        }

        ModuleBoundaryReader.ReadFinderLine(luminance, width, height, threshold, centerX, centerY, uX, uY, 0, columns, 0);
        ModuleBoundaryReader.ReadFinderLine(luminance, width, height, threshold, centerX, centerY, vX, vY, 0, rows, 0);
        ModuleBoundaryReader.ReadRunInteriors(luminance, width, height, threshold, centerX, centerY, uX, uY, vX, vY, columns, size, rows, size);
        ModuleBoundaryReader.ReadRunInteriors(luminance, width, height, threshold, centerX, centerY, vX, vY, uX, uY, rows, size, columns, size);
        if (!ModuleBoundaryReader.TryFillBoundaries(columns, size) || !ModuleBoundaryReader.TryFillBoundaries(rows, size))
            return false;

        frame = new AxisAlignedFrame(centerX, centerY, uX, uY, vX, vY);
        return true;
    }

    /// <summary>
    /// The grid frame measured on the timing patterns, row 0 and column 0 from the finder to the symbol's far edge, instead of extrapolated from the finder's module size.
    /// <paramref name="uX"/>…<paramref name="vY"/> give the orientation and a first scale; the result's origin is the symbol's corner and its axes are one module long.
    /// </summary>
    /// <remarks>
    /// Each line is fitted by least squares over every module boundary it crosses (the finder's outer edge, then one boundary per module from column 7 to the far edge), so the half-pixel quantization of each boundary averages out; the dark runs give the size directly.
    /// </remarks>
    private static bool TryTimingFrame(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in FinderPattern candidate, float uX, float uY, float vX, float vY, out float originX, out float originY, out float tuX, out float tuY, out float tvX, out float tvY, out int size)
    {
        originX = originY = tuX = tuY = tvX = tvY = 0f;
        size = 0;
        var uLength = (float)Math.Sqrt(uX * uX + uY * uY);
        var vLength = (float)Math.Sqrt(vX * vX + vY * vY);
        if (uLength < 1f || vLength < 1f)
            return false;
        var unitUX = uX / uLength;
        var unitUY = uY / uLength;
        var unitVX = vX / vLength;
        var unitVY = vY / vLength;

        // Row 0 is 3 modules from the finder's center toward -v, column 0 toward -u
        if (!TryFitTimingLine(luminance, width, height, threshold, candidate.X - 3f * vX, candidate.Y - 3f * vY, unitUX, unitUY, uLength, out var uStart, out var uPitch, out var uSize)
            || !TryFitTimingLine(luminance, width, height, threshold, candidate.X - 3f * uX, candidate.Y - 3f * uY, unitVX, unitVY, vLength, out var vStart, out var vPitch, out var vSize)
            || uSize != vSize)
            return false;

        size = uSize;
        originX = candidate.X + uStart * unitUX + vStart * unitVX;
        originY = candidate.Y + uStart * unitUY + vStart * unitVY;
        tuX = unitUX * uPitch;
        tuY = unitUY * uPitch;
        tvX = unitVX * vPitch;
        tvY = unitVY * vPitch;
        return true;
    }

    /// <summary>
    /// Walks a timing line from the finder's edge row: back to the finder's outer edge, then forward across the separator and the alternating timing modules to the symbol's far edge.
    /// Fits position = start + pitch · module index over every boundary crossed; false unless the line reads as a timing pattern (runs about one module, a size of 11-17, a pitch near the finder's).
    /// </summary>
    internal static bool TryFitTimingLine(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, float lineX, float lineY, float dirX, float dirY, float module, out float start, out float pitch, out int size)
    {
        start = pitch = 0f;
        size = 0;
        var step = module / 4f;
        if (!IsDarkAt(luminance, width, height, threshold, lineX, lineY, out var dark) || !dark)
            return false;

        // Boundaries as (module index, position along the line); the finder's outer edge is index 0
        Span<float> indices = stackalloc float[16];
        Span<float> positions = stackalloc float[16];
        var count = 0;

        // Back to the finder's outer edge, about 3.5 modules away; the image edge counts as one
        for (var t = -step; ; t -= step)
        {
            if (t < -5f * module)
                return false;
            if (!IsDarkAt(luminance, width, height, threshold, lineX + t * dirX, lineY + t * dirY, out dark) || !dark)
            {
                indices[count] = 0f;
                positions[count++] = t + step / 2f;
                break;
            }
        }

        // Forward: the finder's row ends at column 7, then one boundary per module
        var nextIndex = 7;
        var previousDark = true;
        var runStart = 0f;
        var darkRuns = 1;
        for (var t = step; ; t += step)
        {
            var inside = IsDarkAt(luminance, width, height, threshold, lineX + t * dirX, lineY + t * dirY, out dark);
            if (!inside)
                dark = false; // the image edge ends the symbol like a quiet zone

            if (dark == previousDark)
            {
                // A light run longer than any module is the quiet zone: the symbol has ended
                if (!dark && t - runStart > 1.6f * module)
                    break;
                if (!inside)
                    break;
                continue;
            }

            var boundary = t - step / 2f;
            var runLength = boundary - runStart;
            var isFinderRun = runStart == 0f;
            var (minRun, maxRun) = isFinderRun ? (2f * module, 5f * module) : (0.4f * module, 1.6f * module);
            if (runLength < minRun || runLength > maxRun)
                return false;
            if (count == indices.Length)
                return false;
            indices[count] = nextIndex++;
            positions[count++] = boundary;
            if (dark)
                darkRuns++;
            previousDark = dark;
            runStart = boundary;
            if (!inside)
                break;
        }

        // The far edge closes the last dark timing module: size = its index, 11 to 17, odd,
        // with the dark runs to match (the finder's row, then columns 8, 10, …, size − 1)
        size = (int)indices[count - 1];
        if (previousDark || size is < 11 or > 17 || size % 2 == 0 || darkRuns != 1 + (size - 7) / 2)
            return false;

        // Least squares: position = start + pitch · index
        float sumI = 0f, sumP = 0f, sumII = 0f, sumIP = 0f;
        for (var i = 0; i < count; i++)
        {
            sumI += indices[i];
            sumP += positions[i];
            sumII += indices[i] * indices[i];
            sumIP += indices[i] * positions[i];
        }
        var denominator = count * sumII - sumI * sumI;
        if (denominator <= 0f)
            return false;
        pitch = (count * sumIP - sumI * sumP) / denominator;
        start = (sumP - pitch * sumI) / count;
        return pitch > 0.7f * module && pitch < 1.4f * module;
    }

    /// <summary>Whether the pixel containing the point is dark; false (and not dark) outside the image.</summary>
    private static bool IsDarkAt(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, float x, float y, out bool dark)
    {
        dark = false;
        if (x < 0f || y < 0f)
            return false;
        var px = (int)x;
        var py = (int)y;
        if (px >= width || py >= height)
            return false;
        dark = luminance[py * width + px] < threshold;
        return true;
    }

    /// <summary>
    /// All four grid corners must land inside the image (with one module of slack for sampling clamp tolerance); orientations pointing off the image cannot contain the symbol and are skipped before sampling.
    /// </summary>
    private static bool SymbolFitsImage(float originX, float originY, float uX, float uY, float vX, float vY, int size, int width, int height, float moduleSize)
    {
        var slack = moduleSize;
        for (var corner = 0; corner < 4; corner++)
        {
            var gu = (corner & 1) == 0 ? 0f : size;
            var gv = (corner & 2) == 0 ? 0f : size;
            var x = originX + gu * uX + gv * vX;
            var y = originY + gu * uY + gv * vY;
            if (x < -slack || x > width + slack || y < -slack || y > height + slack)
                return false;
        }
        return true;
    }

    private static bool ProjectiveSymbolFitsImage(in PerspectiveTransform transform, int size, int width, int height, float samplingSlack)
    {
        for (var corner = 0; corner < 4; corner++)
        {
            var gridX = (corner & 1) == 0 ? 0f : size;
            var gridY = (corner & 2) == 0 ? 0f : size;
            transform.Transform(gridX, gridY, out var x, out var y);
            if (float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(y) || float.IsInfinity(y)
                || x < -samplingSlack || x > width + samplingSlack
                || y < -samplingSlack || y > height + samplingSlack)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Samples every module center on the axis-aligned (per orientation) grid.
    /// Out-of-range positions clamp to the nearest edge pixel, mild inaccuracy at the outermost modules must not read out of bounds.
    /// </summary>
    /// <remarks>
    /// The grid searches sample hundreds of grids a finder, so the vector path takes four module centres a step with the scalar op sequence (no FMA), lane for lane the same pixels.
    /// Every caller has checked the corners against the image first, so no centre is outside the int range, where a vector conversion and a scalar cast could part.
    /// </remarks>
    private static void SampleGrid(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, float originX, float originY, float uX, float uY, float vX, float vY, int size, Span<byte> modules)
    {
#if NET8_0_OR_GREATER
        if (Vector128.IsHardwareAccelerated)
        {
            SampleGridVector128(luminance, width, height, threshold, originX, originY, uX, uY, vX, vY, size, modules);
            return;
        }
#endif
        SampleGridScalar(luminance, width, height, threshold, originX, originY, uX, uY, vX, vY, size, modules);
    }

#if NET8_0_OR_GREATER
    internal static void SampleGridVector128(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, float originX, float originY, float uX, float uY, float vX, float vY, int size, Span<byte> modules)
    {
        var laneCentres = Vector128.Create(0.5f, 1.5f, 2.5f, 3.5f);
        var columnX = Vector128.Create(uX);
        var columnY = Vector128.Create(uY);
        var maxPx = Vector128.Create(width - 1);
        var maxPy = Vector128.Create(height - 1);
        var stride = Vector128.Create(width);
        Span<int> indices = stackalloc int[4];
        for (var v = 0; v < size; v++)
        {
            var gridV = v + 0.5f;
            var rowX = originX + gridV * vX;
            var rowY = originY + gridV * vY;
            var rowXs = Vector128.Create(rowX);
            var rowYs = Vector128.Create(rowY);
            var rowBase = v * size;

            var u = 0;
            for (; u + 4 <= size; u += 4)
            {
                // u + lane + 0.5 is exact, as the scalar gridU is
                var gridU = laneCentres + Vector128.Create((float)u);
                var px = Vector128.ConvertToInt32(rowXs + gridU * columnX);
                var py = Vector128.ConvertToInt32(rowYs + gridU * columnY);
                px = Vector128.Max(Vector128.Min(px, maxPx), Vector128<int>.Zero);
                py = Vector128.Max(Vector128.Min(py, maxPy), Vector128<int>.Zero);
                (py * stride + px).CopyTo(indices);
                modules[rowBase + u] = luminance[indices[0]] < threshold ? (byte)1 : (byte)0;
                modules[rowBase + u + 1] = luminance[indices[1]] < threshold ? (byte)1 : (byte)0;
                modules[rowBase + u + 2] = luminance[indices[2]] < threshold ? (byte)1 : (byte)0;
                modules[rowBase + u + 3] = luminance[indices[3]] < threshold ? (byte)1 : (byte)0;
            }

            for (; u < size; u++)
            {
                var gridU = u + 0.5f;
                var px = Math.Min(Math.Max((int)(rowX + gridU * uX), 0), width - 1);
                var py = Math.Min(Math.Max((int)(rowY + gridU * uY), 0), height - 1);
                modules[rowBase + u] = luminance[py * width + px] < threshold ? (byte)1 : (byte)0;
            }
        }
    }
#endif

    internal static void SampleGridScalar(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, float originX, float originY, float uX, float uY, float vX, float vY, int size, Span<byte> modules)
    {
        for (var v = 0; v < size; v++)
        {
            var gridV = v + 0.5f;
            var rowX = originX + gridV * vX;
            var rowY = originY + gridV * vY;
            var rowBase = v * size;

            for (var u = 0; u < size; u++)
            {
                var gridU = u + 0.5f;
                // Pixel edges sit on integers, so the pixel containing a point is its floor
                var px = (int)(rowX + gridU * uX);
                var py = (int)(rowY + gridU * uY);

                if (px < 0)
                    px = 0;
                else if (px >= width)
                    px = width - 1;
                if (py < 0)
                    py = 0;
                else if (py >= height)
                    py = height - 1;

                modules[rowBase + u] = luminance[py * width + px] < threshold ? (byte)1 : (byte)0;
            }
        }
    }
}
