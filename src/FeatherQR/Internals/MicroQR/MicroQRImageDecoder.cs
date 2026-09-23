using System.Buffers;
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
///    size and RS + the ISO Table 9 capacity cap reject wrong-grid samples
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
        var status = DecodeLuminanceCore(luminance, histogram, width, height, destination, out charsWritten, out info);
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

            var invertedStatus = DecodeLuminanceCore(inverted, histogram, width, height, destination, out charsWritten, out var invertedInfo);
            if (IsTerminal(invertedStatus))
            {
                info = invertedInfo;
                return invertedStatus;
            }

            // Uneven lighting: no one threshold splits the symbol, so each polarity is
            // binarized again against each region's own level
            var regional = new RegionalAttempt();
            var regionalStatus = RegionalRetry.Decode<RegionalAttempt, MicroQRCodeDecodeInfo>(ref regional, luminance, inverted, histogram, width, height, destination, out charsWritten, out var regionalInfo);
            if (IsTerminal(regionalStatus))
            {
                info = regionalInfo;
                return regionalStatus;
            }

            // Every attempt failed: report the first one's diagnostics
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
    {
        // Hoisted: the two scans binarize the same buffer
        var threshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out var grey);

        var status = DecodeLuminanceScan(luminance, width, height, threshold, grey, destination, out charsWritten, out info, fullSweep: false);
        // Terminal, not just successful: DestinationTooSmall is only reached after the
        // symbol has been located, sampled, RS-corrected and its segment found to fit
        // the bitstream, so the buffer is the only thing missing and a wider finder scan
        // cannot change it. (That ordering is a precondition, not a given: the segment
        // decoders check bitstream sufficiency before destination sufficiency precisely
        // so a malformed count cannot masquerade as a short buffer here.)
        if (IsTerminal(status))
            return status;

        var sweptStatus = DecodeLuminanceScan(luminance, width, height, threshold, grey, destination, out var sweptChars, out var sweptInfo, fullSweep: true);
        // Terminal, not just successful, for the same reason as above: when the sweep
        // is the pass that reads the symbol, its DestinationTooSmall is the answer.
        if (IsTerminal(sweptStatus))
        {
            charsWritten = sweptChars;
            info = sweptInfo;
            return sweptStatus;
        }

        return status;
    }

    private static DecodeStatus DecodeLuminanceScan(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, Span<char> destination, out int charsWritten, out MicroQRCodeDecodeInfo info, bool fullSweep)
    {
        charsWritten = 0;

        Span<FinderPattern> candidates = stackalloc FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var candidateCount = fullSweep
            ? FinderPatternFinder.FindCandidatesFullSweep(luminance, width, height, threshold, candidates, grey)
            : FinderPatternFinder.FindCandidates(luminance, width, height, threshold, candidates, grey);
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

        var tried = Math.Min(candidateCount, MaxCandidatesToTry);
        for (var c = 0; c < tried; c++)
        {
            ref readonly var candidate = ref candidates[c];
            FinderAxisEstimator.RefineModuleSize(luminance, width, height, threshold, candidate, out var horizontalModuleSize, out var verticalModuleSize);
            if (horizontalModuleSize < 1f || verticalModuleSize < 1f)
                continue; // below one pixel per module nothing can be sampled reliably
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

                    var status = MicroQRMatrixDecoder.DecodeMatrix(modules.Slice(0, size * size), size, destination, out charsWritten, out var attemptInfo);
                    if (status == DecodeStatus.Success)
                    {
                        info = attemptInfo.WithCorners(SymbolGeometry.FromAffine(originX, originY, uX, uY, vX, vY, size, transposed: false));
                        return status;
                    }
                    TrackBestFailure(status, attemptInfo, ref bestStatus, ref bestInfo);

                    // Mirrored capture (e.g. front camera): finder geometry is
                    // identical but the data grid is transposed.
                    TransposeInPlace(modules, size);
                    var mirroredStatus = MicroQRMatrixDecoder.DecodeMatrix(modules.Slice(0, size * size), size, destination, out charsWritten, out var mirroredInfo);
                    if (mirroredStatus == DecodeStatus.Success)
                    {
                        info = mirroredInfo.WithCorners(SymbolGeometry.FromAffine(originX, originY, uX, uY, vX, vY, size, transposed: true));
                        return mirroredStatus;
                    }
                    TrackBestFailure(mirroredStatus, mirroredInfo, ref bestStatus, ref bestInfo);
                }

                // Timing frame: the finder's module size is extrapolated across the whole
                // symbol, and a render that snaps modules to whole pixels can give the
                // finder a size a few percent off. The timing patterns reach the far edge,
                // so they measure the symbol itself. Failure-path cost only.
                if (TryTimingFrame(luminance, width, height, threshold, candidate, uX, uY, vX, vY, out var timingOriginX, out var timingOriginY, out var tuX, out var tuY, out var tvX, out var tvY, out var timingSize)
                    && SymbolFitsImage(timingOriginX, timingOriginY, tuX, tuY, tvX, tvY, timingSize, width, height, samplingSlack))
                {
                    SampleGrid(luminance, width, height, threshold, timingOriginX, timingOriginY, tuX, tuY, tvX, tvY, timingSize, modules);
                    var timingStatus = MicroQRMatrixDecoder.DecodeMatrix(modules.Slice(0, timingSize * timingSize), timingSize, destination, out charsWritten, out var timingInfo);
                    if (timingStatus == DecodeStatus.Success)
                    {
                        info = timingInfo.WithCorners(SymbolGeometry.FromAffine(timingOriginX, timingOriginY, tuX, tuY, tvX, tvY, timingSize, transposed: false));
                        return timingStatus;
                    }
                    TrackBestFailure(timingStatus, timingInfo, ref bestStatus, ref bestInfo);

                    TransposeInPlace(modules, timingSize);
                    var mirroredTimingStatus = MicroQRMatrixDecoder.DecodeMatrix(modules.Slice(0, timingSize * timingSize), timingSize, destination, out charsWritten, out var mirroredTimingInfo);
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
                    frame.ToImage(boundaryColumns[0], boundaryRows[0], out var boundaryOriginX, out var boundaryOriginY);
                    var pitchU = (boundaryColumns[boundarySize] - boundaryColumns[0]) / (float)boundarySize;
                    var pitchV = (boundaryRows[boundarySize] - boundaryRows[0]) / (float)boundarySize;

                    var boundaryStatus = MicroQRMatrixDecoder.DecodeMatrix(modules.Slice(0, boundarySize * boundarySize), boundarySize, destination, out charsWritten, out var boundaryInfo);
                    if (boundaryStatus == DecodeStatus.Success)
                    {
                        info = boundaryInfo.WithCorners(SymbolGeometry.FromAffine(boundaryOriginX, boundaryOriginY, pitchU * frame.UX, pitchU * frame.UY, pitchV * frame.VX, pitchV * frame.VY, boundarySize, transposed: false));
                        return boundaryStatus;
                    }
                    TrackBestFailure(boundaryStatus, boundaryInfo, ref bestStatus, ref bestInfo);

                    TransposeInPlace(modules, boundarySize);
                    var mirroredBoundaryStatus = MicroQRMatrixDecoder.DecodeMatrix(modules.Slice(0, boundarySize * boundarySize), boundarySize, destination, out charsWritten, out var mirroredBoundaryInfo);
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
        in FinderPattern candidate,
        Span<byte> modules,
        Span<char> destination,
        out int charsWritten,
        out MicroQRCodeDecodeInfo info,
        ref DecodeStatus bestStatus,
        ref MicroQRCodeDecodeInfo bestInfo)
    {
        Span<OrientationCandidate> orientations = stackalloc OrientationCandidate[FinderAxisEstimator.MaxOrientationCandidates];
        var orientationCount = FinderAxisEstimator.FindOrientationCandidates(luminance, width, height, threshold, candidate, orientations);
        var attemptsRemaining = MaxArbitraryOrientationDecodeAttempts;

        for (var frameIndex = 0; frameIndex < orientationCount; frameIndex++)
        {
            ref readonly var frame = ref orientations[frameIndex];
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
                    attemptsRemaining--;
                    var status = MicroQRMatrixDecoder.DecodeMatrix(modules.Slice(0, size * size), size, destination, out charsWritten, out var attemptInfo);
                    if (status == DecodeStatus.Success)
                    {
                        info = attemptInfo.WithCorners(SymbolGeometry.FromAffine(originX, originY, uX, uY, vX, vY, size, transposed: false));
                        return status;
                    }
                    TrackBestFailure(status, attemptInfo, ref bestStatus, ref bestInfo);

                    if (attemptsRemaining == 0)
                        continue;

                    TransposeInPlace(modules, size);
                    attemptsRemaining--;
                    var mirroredStatus = MicroQRMatrixDecoder.DecodeMatrix(modules.Slice(0, size * size), size, destination, out charsWritten, out var mirroredInfo);
                    if (mirroredStatus == DecodeStatus.Success)
                    {
                        info = mirroredInfo.WithCorners(SymbolGeometry.FromAffine(originX, originY, uX, uY, vX, vY, size, transposed: true));
                        return mirroredStatus;
                    }
                    TrackBestFailure(mirroredStatus, mirroredInfo, ref bestStatus, ref bestInfo);

                    // Scale and perspective searches multiply this affine attempt
                    // by hundreds. Enter them only after either polarity decoded
                    // valid format information; wrong grids overwhelmingly fail
                    // before that point.
                    if (!IsPlausibleRefinement(status) && !IsPlausibleRefinement(mirroredStatus))
                        continue;

                    var scaledStatus = TryDecodeScaleVariants(
                        luminance, width, height, threshold, candidate,
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
                        attemptsRemaining--;
                        var status = MicroQRMatrixDecoder.DecodeMatrix(modules.Slice(0, size * size), size, destination, out charsWritten, out var attemptInfo);
                        if (status == DecodeStatus.Success)
                        {
                            info = attemptInfo.WithCorners(SymbolGeometry.FromAffine(originX, originY, scaledUX, scaledUY, scaledVX, scaledVY, size, transposed: false));
                            return status;
                        }
                        TrackBestFailure(status, attemptInfo, ref bestStatus, ref bestInfo);

                        if (attemptsRemaining == 0)
                            continue;

                        TransposeInPlace(modules, size);
                        attemptsRemaining--;
                        var mirroredStatus = MicroQRMatrixDecoder.DecodeMatrix(modules.Slice(0, size * size), size, destination, out charsWritten, out var mirroredInfo);
                        if (mirroredStatus == DecodeStatus.Success)
                        {
                            info = mirroredInfo.WithCorners(SymbolGeometry.FromAffine(originX, originY, scaledUX, scaledUY, scaledVX, scaledVY, size, transposed: true));
                            return mirroredStatus;
                        }
                        TrackBestFailure(mirroredStatus, mirroredInfo, ref bestStatus, ref bestInfo);
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
                attemptsRemaining--;
                var status = MicroQRMatrixDecoder.DecodeMatrix(modules.Slice(0, size * size), size, destination, out charsWritten, out var attemptInfo);
                if (status == DecodeStatus.Success)
                {
                    info = attemptInfo.WithCorners(SymbolGeometry.FromTransform(transform, size, size, transposed: false));
                    return status;
                }
                TrackBestFailure(status, attemptInfo, ref bestStatus, ref bestInfo);

                if (attemptsRemaining == 0)
                    continue;

                TransposeInPlace(modules, size);
                attemptsRemaining--;
                var mirroredStatus = MicroQRMatrixDecoder.DecodeMatrix(modules.Slice(0, size * size), size, destination, out charsWritten, out var mirroredInfo);
                if (mirroredStatus == DecodeStatus.Success)
                {
                    info = mirroredInfo.WithCorners(SymbolGeometry.FromTransform(transform, size, size, transposed: true));
                    return mirroredStatus;
                }
                TrackBestFailure(mirroredStatus, mirroredInfo, ref bestStatus, ref bestInfo);
            }
        }

        charsWritten = 0;
        info = bestInfo;
        return bestStatus;
    }

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
            // this outranks every other failure, so a wrong-size attempt that reaches RS
            // first — sizes are tried 17 down to 11 — cannot mask it. Matches the rMQR
            // decoder's ranking; without it M3-L reported DataUncorrectable for a short
            // buffer while every other version reported DestinationTooSmall.
            DecodeStatus.DestinationTooSmall => 3,
            _ => 2, // got past format decoding
        };
    }

    private static bool IsPlausibleRefinement(DecodeStatus status)
        => status is not DecodeStatus.NotDetected
            and not DecodeStatus.InvalidMatrix
            and not DecodeStatus.FormatInformationInvalid;

    private static bool IsTerminal(DecodeStatus status)
        => status is DecodeStatus.Success or DecodeStatus.DestinationTooSmall;

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
    private static void SampleGrid(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, float originX, float originY, float uX, float uY, float vX, float vY, int size, Span<byte> modules)
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

    private static void TransposeInPlace(Span<byte> modules, int size)
    {
        for (var y = 0; y < size; y++)
        {
            for (var x = y + 1; x < size; x++)
            {
                var a = y * size + x;
                var b = x * size + y;
                (modules[a], modules[b]) = (modules[b], modules[a]);
            }
        }
    }
}
