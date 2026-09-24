using System.Buffers;
#if NET8_0_OR_GREATER
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
#endif
using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Internals.RmQR;

/// <summary>
/// Decodes an rMQR Code from a grayscale image: clean, screen-rendered or scanned inputs, including a lighting gradient across the symbol.
/// </summary>
/// <remarks>
/// Pipeline:
/// <code>
/// 1. Global binarization threshold (Otsu, shared)
/// 2. Finder pattern candidates (shared 1:1:3:1:1 scan; every candidate is tried)
/// 3. Local grid frames around the finder: four right angles × transpose (mirror),
///    then the angular finder-axis sweep (shared with Micro QR) for arbitrary rotation
/// 4. Per frame: the finder-side format copy is sampled first (18 modules next to the
///    finder), which yields the version and therefore the symbol width and height
///    before any full grid is sampled
/// 5. The sub-finder (5×5, bottom-right) is located near its predicted position and
///    anchors the far end of the symbol: it fixes global scale and rotation exactly
///    (the finder-local estimates are only pixel-accurate over 7 modules, far too
///    coarse over 139), and a bounded projective search around that anchor recovers
///    mild perspective; the sub-finder-side format copy gates each projective
///    candidate cheaply before a full sample
/// 6. Matrix decoding arbitrates (format cross-check, RS); reflectance reversal is
///    handled by one inverted retry when the normal attempt fails, and a symbol lit unevenly by a
///    regional binarization of each polarity when both fail
/// </code>
/// </remarks>
internal static class RmQRImageDecoder
{
    /// <summary>Candidates actually tried, most-confirmed first (false hits rank behind).</summary>
    private const int MaxCandidatesToTry = 8;

    /// <summary>Full-grid decode attempts per finder candidate (all frames together).</summary>
    private const int MaxDecodeAttemptsPerCandidate = 256;

    /// <summary>Largest symbol: R17x139.</summary>
    internal const int MaxModules = 17 * 139;

    /// <summary>
    /// Sub-finder search radius around the predicted center, in half modules: a fixed part plus a width-proportional part, since the finder-local scale estimate (±2 %) and a mild keystone both displace the far corner in proportion to the width.
    /// </summary>
    private const int SubFinderSearchRadiusHalfModulesBase = 12;
    private const int SubFinderSearchRadiusHalfModulesPerTenModules = 1;

    /// <summary>Template matches (of 25) required to accept a sub-finder location.</summary>
    private const int SubFinderMinScore = 24;

    /// <summary>Rings of the sub-finder search scored without the lattice screen: a sub-finder near its prediction is found before the screen would pay for itself.</summary>
    private const int SubFinderUnscreenedRings = 3;

    /// <summary>Rows of positions the lattice screen can hold: the lattice is <c>2 · radius + 9</c> points a side, one 64-bit mask a row.</summary>
    internal const int MaxSubFinderScreenRows = 64;

    /// <summary>Row-axis shear searched on the perspective path, ± degrees.</summary>
    private const int MaxShearDegrees = 20;

    /// <summary>Grid coordinates of the finder center.</summary>
    private const float FinderCenter = 3.5f;

    /// <summary>Finder-scale corrections tried for the finder-side format read, nearest the measured scale first.</summary>
    private static readonly float[] FormatReadScales = [1f, 0.98f, 1.02f, 0.96f, 1.04f, 0.94f, 1.06f, 0.92f, 1.08f];

    /// <summary>Finder module length, in pixels, from which only the measured scale is read.</summary>
    private const float FormatScaleSearchMaxModule = 6f;

    /// <summary>
    /// Decodes an rMQR Code from grayscale pixels.
    /// Reflectance-reversed symbols (light modules on a dark background) are handled by one inverted retry when the normal attempt fails. A symbol lit unevenly, which no one threshold splits, is retried on a regional binarization of each polarity when both fail.
    /// </summary>
    public static DecodeStatus DecodeLuminance(ReadOnlySpan<byte> luminance, int width, int height, Span<char> destination, out int charsWritten, out RmQRCodeDecodeInfo info)
    {
        var status = DecodeLuminanceAttempts(luminance, width, height, destination, out charsWritten, out info);
        // No text unless it decoded: a failing decode can stop after a segment was written
        if (status != DecodeStatus.Success)
            charsWritten = 0;
        return status;
    }

    private static DecodeStatus DecodeLuminanceAttempts(ReadOnlySpan<byte> luminance, int width, int height, Span<char> destination, out int charsWritten, out RmQRCodeDecodeInfo info)
    {
        if (!ImageDimensions.TryGetPixelCount(width, height, out var pixelCount) || luminance.Length < pixelCount)
        {
            charsWritten = 0;
            info = NotDetected();
            return DecodeStatus.NotDetected;
        }

        luminance = luminance.Slice(0, pixelCount);
        // One count serves both polarities: the negative's histogram is this one mirrored
        Span<int> histogram = stackalloc int[Binarizer.HistogramBins];
        Binarizer.FillHistogram(luminance, histogram);
        var status = DecodeLuminanceCore(luminance, histogram, width, height, destination, out charsWritten, out info, out var noFinder, out var positiveThreshold, out var positiveGrey);
        if (IsTerminal(status))
            return status;

        // Reflectance reversal: invert into a rented buffer and retry once.
        // Taken only on the failure path, so the normal case stays allocation-free.
        var rented = ArrayPool<byte>.Shared.Rent(pixelCount);
        try
        {
            var inverted = rented.AsSpan(0, pixelCount);
            LuminanceInverter.Invert(luminance, inverted);
            Binarizer.InvertHistogram(histogram);

            var invertedStatus = DecodeLuminanceCore(inverted, histogram, width, height, destination, out charsWritten, out var invertedInfo, out var invertedNoFinder, out var negativeThreshold, out var negativeGrey);
            if (IsTerminal(invertedStatus))
            {
                // Success, or the symbol was read but the caller's destination is too
                // small: both polarities report the same way.
                info = invertedInfo;
                return invertedStatus;
            }

            // A verdict skips the regional pass, which looks for a symbol the global threshold did not see
            if (RegionalRetry.IsContentVerdict(status))
                return status;
            if (RegionalRetry.IsContentVerdict(invertedStatus))
            {
                info = invertedInfo;
                return invertedStatus;
            }

            // Uneven lighting: each polarity binarized again against each region's own level
            var regional = new RegionalAttempt();
            var regionalStatus = RegionalRetry.Decode<RegionalAttempt, RmQRCodeDecodeInfo>(ref regional, luminance, inverted, histogram, width, height, destination, out charsWritten, out var regionalInfo);
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

            // Every attempt failed short of the content: report the first one's diagnostics
            return status;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }
    }

    private static RmQRCodeDecodeInfo NotDetected() => new(DecodeStatus.NotDetected, default, default, 0);

    /// <summary>The regional retry's decode: this decoder's attempt on a binarized image.</summary>
    private readonly struct RegionalAttempt : ILuminanceAttempt<RmQRCodeDecodeInfo>
    {
        public DecodeStatus Decode(ReadOnlySpan<byte> luminance, ReadOnlySpan<int> histogram, int width, int height, Span<char> destination, out int charsWritten, out RmQRCodeDecodeInfo info)
            => DecodeLuminanceCore(luminance, histogram, width, height, destination, out charsWritten, out info);
    }

    /// <summary>
    /// Strided finder scan first, then a full sweep when nothing decoded.
    /// </summary>
    /// <remarks>
    /// The widening trigger has to be a question about the symbol, and "did anything decode" is the only one available.
    /// The scan itself cannot ask it: every signal inside a flat candidate list is a statement about the image, so a second QR code or a noise artefact would answer it in the real symbol's place and suppress the sweep the symbol needed.
    /// Paid only on images that fail, and it makes the detection envelope a superset of a full sweep's: the symbol is read if either pass reads it.
    /// </remarks>
    internal static DecodeStatus DecodeLuminanceCore(ReadOnlySpan<byte> luminance, ReadOnlySpan<int> histogram, int width, int height, Span<char> destination, out int charsWritten, out RmQRCodeDecodeInfo info)
        => DecodeLuminanceCore(luminance, histogram, width, height, destination, out charsWritten, out info, out _, out _, out _);

    /// <summary>The strided scan and the sweep at the global threshold; <paramref name="noFinder"/> when neither found a finder candidate, with the threshold and levels they used.</summary>
    private static DecodeStatus DecodeLuminanceCore(ReadOnlySpan<byte> luminance, ReadOnlySpan<int> histogram, int width, int height, Span<char> destination, out int charsWritten, out RmQRCodeDecodeInfo info, out bool noFinder, out byte threshold, out GreyLevels grey)
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

        // Both failed: keep the strided pass's diagnostic, which is the one whose
        // candidate ranking the caller would have seen before this retry existed.
        return status;
    }

    /// <summary>
    /// The sweep at the midpoint of the two levels, for a polarity where the global threshold found no finder at all.
    /// Edge greys pull the global threshold toward light, and a blurred finder's light ring can keep too few pixels above it; halfway between the levels the ring reads its width.
    /// </summary>
    private static DecodeStatus DecodeAtMidpoint(ReadOnlySpan<byte> luminance, byte threshold, in GreyLevels grey, int width, int height, Span<char> destination, out int charsWritten, out RmQRCodeDecodeInfo info)
    {
        charsWritten = 0;
        info = NotDetected();
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
    /// A candidate's decode depends on its position and module size, the image and the threshold, and on the scan's best failure only once that is terminal; so a candidate tried by a scan that settled on nothing settles on nothing again, and skipping it changes only a failure the caller does not report.
    /// </remarks>
    private static DecodeStatus DecodeLuminanceScan(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, Span<char> destination, out int charsWritten, out RmQRCodeDecodeInfo info, bool fullSweep, ReadOnlySpan<FinderPattern> skip, Span<FinderPattern> tried, out int triedCount, out int found)
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
            info = NotDetected();
            return DecodeStatus.NotDetected;
        }

        // Most-confirmed candidates first (insertion sort: tiny list, netstandard2.0 has no Span.Sort).
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

        var bestStatus = DecodeStatus.NotDetected;
        var bestInfo = NotDetected();

        // The module buffer is sized for the largest symbol; rented rather than
        // stack-allocated (2.3 KB) since this sits under the public image entry point.
        var rentedModules = ArrayPool<byte>.Shared.Rent(MaxModules);
        try
        {
            var modules = rentedModules.AsSpan(0, MaxModules);
            Span<OrientationCandidate> orientations = stackalloc OrientationCandidate[FinderAxisEstimator.MaxOrientationCandidates];
            var ranked = Math.Min(candidateCount, MaxCandidatesToTry);
            for (var c = 0; c < ranked; c++)
            {
                // Not replaced by the next in rank: the candidates tried stay the first eight
                if (FinderPatternFinder.ContainsCandidate(skip, candidates[c]))
                    continue;
                if (!tried.IsEmpty)
                    tried[triedCount++] = candidates[c];
                var candidate = candidates[c];
                var attemptsRemaining = MaxDecodeAttemptsPerCandidate;

                // Fast path: right-angle frames from the axis-aligned module sizes.
                FinderAxisEstimator.RefineModuleSize(luminance, width, height, threshold, candidate, out var horizontalModuleSize, out var verticalModuleSize);
                if (horizontalModuleSize >= 1f && verticalModuleSize >= 1f)
                {
                    ConcentricCentroid.TryRefine(luminance, width, height, grey, horizontalModuleSize, 0f, 0f, verticalModuleSize, 2f, 9f, 0.75f, ref candidate.X, ref candidate.Y, out _);
                    var status = TryFrames(
                        luminance, width, height, threshold, grey, candidate,
                        horizontalModuleSize, 0f, 0f, verticalModuleSize,
                        modules, destination, out charsWritten, out info,
                        ref bestStatus, ref bestInfo, ref attemptsRemaining);
                    if (status == DecodeStatus.Success)
                        return status;
                    // Terminal for THIS finder (its symbol was read; no other frame can
                    // change that); another finder in the frame may still carry a symbol
                    // that fits, so the candidate loop continues.
                    if (IsTerminal(status))
                        continue;
                }

                // Arbitrary rotation: recover the finder's local axes from the angular sweep.
                var orientationCount = FinderAxisEstimator.FindOrientationCandidates(luminance, width, height, threshold, candidate, orientations);
                for (var o = 0; o < orientationCount && attemptsRemaining > 0; o++)
                {
                    ref readonly var frame = ref orientations[o];
                    var status = TryFrames(
                        luminance, width, height, threshold, grey, candidate,
                        frame.UX, frame.UY, frame.VX, frame.VY,
                        modules, destination, out charsWritten, out info,
                        ref bestStatus, ref bestInfo, ref attemptsRemaining);
                    if (status == DecodeStatus.Success)
                        return status;
                    if (IsTerminal(status))
                        break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedModules, clearArray: false);
        }

        charsWritten = 0;
        info = bestInfo;
        return bestStatus;
    }

    /// <summary>
    /// Tries the eight frames one axis pair generates: four right-angle rotations, each with and without the axes swapped (a mirrored capture keeps the finder geometry and transposes the grid).
    /// </summary>
    private static DecodeStatus TryFrames(
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
        Span<byte> modules,
        Span<char> destination,
        out int charsWritten,
        out RmQRCodeDecodeInfo info,
        ref DecodeStatus bestStatus,
        ref RmQRCodeDecodeInfo bestInfo,
        ref int attemptsRemaining)
    {
        for (var orientation = 0; orientation < 4; orientation++)
        {
            var (aX, aY, bX, bY) = orientation switch
            {
                0 => (uX, uY, vX, vY),
                1 => (vX, vY, -uX, -uY),
                2 => (-uX, -uY, -vX, -vY),
                _ => (-vX, -vY, uX, uY),
            };

            for (var mirror = 0; mirror < 2; mirror++)
            {
                if (attemptsRemaining <= 0)
                    break;

                var (colX, colY, rowX, rowY) = mirror == 0 ? (aX, aY, bX, bY) : (bX, bY, aX, aY);
                var status = TryFrame(
                    luminance, width, height, threshold, grey, candidate,
                    colX, colY, rowX, rowY,
                    modules, destination, out charsWritten, out info,
                    ref bestStatus, ref bestInfo, ref attemptsRemaining);
                if (IsTerminal(status))
                    return status;
            }
        }

        charsWritten = 0;
        info = bestInfo;
        return bestStatus;
    }

    /// <summary>
    /// One local frame (finder center at grid (3.5, 3.5), <c>u</c> = column axis, <c>v</c> = row axis in pixels per module): read the finder-side format copy to learn the version, anchor the far end on the sub-finder, then decode.
    /// </summary>
    private static DecodeStatus TryFrame(
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
        Span<byte> modules,
        Span<char> destination,
        out int charsWritten,
        out RmQRCodeDecodeInfo info,
        ref DecodeStatus bestStatus,
        ref RmQRCodeDecodeInfo bestInfo,
        ref int attemptsRemaining)
    {
        charsWritten = 0;

        // The finder-side format copy sits within 12 modules of the finder, so the local
        // frame reads it before any refinement, as long as the finder's scale is close.
        // A render that snaps modules to whole pixels can give the finder a scale 3-6 %
        // off, most of a pixel at column 11 below 2 px/module, so the scales nearest the
        // measured one are tried in turn. A corrected scale must read an exact codeword:
        // within 3 bits, a quarter of random reads match one of the 64 words, so nine
        // tries on noise would nearly always "read" a version and pay for its searches.
        // Only while the larger of the finder's two axes measures under 6 px per module,
        // a bound taken from measurement: the search buys fewer reads as the density rises
        // and costs every other symbology's image its failure time. A trade, not a free cut:
        // renders above the bound that read only at a corrected scale are given up.
        var moduleLength = Math.Max((float)Math.Sqrt(uX * uX + uY * uY), (float)Math.Sqrt(vX * vX + vY * vY));
        var formatRead = false;

        // Module boundaries: under about 1.5 px/module a crisp module is 1 or 2 px wide and a
        // sample has an eighth of a pixel to spare, which no frame scaled from the finder keeps.
        // First, while the attempt budget is whole: the searches below can spend all of it on
        // such a symbol, and two timing lines that do not read cost a few dozen pixels.
        if (moduleLength < ModuleBoundaryReader.MaxModuleSize)
        {
            var boundaryStatus = TryBoundaryFrame(luminance, width, height, threshold, candidate, uX, uY, vX, vY, moduleLength, modules, destination, out charsWritten, out info, ref bestStatus, ref bestInfo, ref attemptsRemaining);
            if (IsTerminal(boundaryStatus))
                return boundaryStatus;
            formatRead |= boundaryStatus != DecodeStatus.NotDetected;
        }

        foreach (var scale in FormatReadScales)
        {
            if (scale != 1f && moduleLength >= FormatScaleSearchMaxModule)
                break;
            if (attemptsRemaining <= 0)
                break; // nothing left to decode with: reading and searching would be waste
            var affine = PerspectiveTransform.FromLocalFrame(FinderCenter, FinderCenter, candidate.X, candidate.Y, scale * uX, scale * uY, scale * vX, scale * vY, 0f, 0f);
            var finderSideRaw = ReadFormatCopy(luminance, width, height, threshold, affine, subFinderSide: false, 0, 0);
            if (!RmQRFormatInformationDecoder.TryDecodeCopy(finderSideRaw, subFinderSide: false, out var version, out _, out var distance) || (scale != 1f && distance != 0))
                continue;

            formatRead = true;
            var status = TryScaledFrame(
                luminance, width, height, threshold, grey, candidate,
                scale * uX, scale * uY, scale * vX, scale * vY, version, affine,
                modules, destination, out charsWritten, out info,
                ref bestStatus, ref bestInfo, ref attemptsRemaining, out var anchoredStatus);

            // A copy can read exactly at a scale a few percent off, and a frame built on
            // it fails where the next scale reads; so the search goes on while frames fail.
            // Once frames anchored on the sub-finder read format information, a failure is
            // the data's: on a damaged symbol every later scale only repeated them.
            if (IsTerminal(status))
                return status;
            if (IsPlausibleRefinement(anchoredStatus))
                break;
        }

        charsWritten = 0;
        info = bestInfo;
        return formatRead ? bestStatus : DecodeStatus.NotDetected;
    }

    /// <summary>
    /// Decodes an upright or right-angle symbol drawn crisp at a low density through its module boundaries (<see cref="ModuleBoundaryReader"/>): module row 0 and module column 0 run from the finder's edge rows to the symbol's far edges, over the alignment and corner patterns that sit on them.
    /// <see cref="DecodeStatus.NotDetected"/> unless both lines read and count a size some version has.
    /// </summary>
    private static DecodeStatus TryBoundaryFrame(
        ReadOnlySpan<byte> luminance,
        int width,
        int height,
        byte threshold,
        in FinderPattern candidate,
        float uX,
        float uY,
        float vX,
        float vY,
        float moduleLength,
        Span<byte> modules,
        Span<char> destination,
        out int charsWritten,
        out RmQRCodeDecodeInfo info,
        ref DecodeStatus bestStatus,
        ref RmQRCodeDecodeInfo bestInfo,
        ref int attemptsRemaining)
    {
        charsWritten = 0;
        info = bestInfo;
        Span<int> columns = stackalloc int[MaxBoundaryColumns];
        Span<int> rows = stackalloc int[MaxBoundaryRows];
        if (attemptsRemaining <= 0
            || !TryReadModuleBoundaries(luminance, width, height, threshold, candidate, uX, uY, vX, vY, moduleLength, columns, rows, out var frame, out var symbolWidth, out var symbolHeight))
        {
            return DecodeStatus.NotDetected;
        }

        attemptsRemaining--;
        var grid = modules.Slice(0, symbolWidth * symbolHeight);
        ModuleBoundaryReader.Sample(luminance, width, height, threshold, frame, columns, symbolWidth, rows, symbolHeight, grid);
        var status = RmQRMatrixDecoder.DecodeMatrix(grid, symbolWidth, symbolHeight, destination, out charsWritten, out info);
        if (status == DecodeStatus.Success)
        {
            frame.ToImage(columns[0], rows[0], out var x0, out var y0);
            frame.ToImage(columns[symbolWidth], rows[0], out var x1, out var y1);
            frame.ToImage(columns[symbolWidth], rows[symbolHeight], out var x2, out var y2);
            frame.ToImage(columns[0], rows[symbolHeight], out var x3, out var y3);
            var outline = PerspectiveTransform.QuadrilateralToQuadrilateral(0f, 0f, symbolWidth, 0f, symbolWidth, symbolHeight, 0f, symbolHeight, x0, y0, x1, y1, x2, y2, x3, y3);
            info = info.WithCorners(SymbolGeometry.FromTransform(outline, symbolWidth, symbolHeight, transposed: false));
            return status;
        }

        TrackBestFailure(status, info, ref bestStatus, ref bestInfo);
        return status;
    }

    /// <summary>Boundaries along the widest symbol, 139 modules, and the tallest, 17.</summary>
    private const int MaxBoundaryColumns = 140;
    private const int MaxBoundaryRows = 18;

    /// <summary>
    /// The module boundaries of an axis-aligned symbol, <c>width + 1</c> columns and <c>height + 1</c> rows.
    /// False unless the frame lies along the image axes, both lines read, and they count the size of a version.
    /// </summary>
    internal static bool TryReadModuleBoundaries(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in FinderPattern candidate, float columnX, float columnY, float rowX, float rowY, float moduleSize, Span<int> columns, Span<int> rows, out AxisAlignedFrame frame, out int symbolWidth, out int symbolHeight)
    {
        frame = default;
        symbolWidth = symbolHeight = 0;
        if (!ModuleBoundaryReader.TryAxisDirection(columnX, columnY, out var uX, out var uY)
            || !ModuleBoundaryReader.TryAxisDirection(rowX, rowY, out var vX, out var vY)
            || uX * vX + uY * vY != 0)
        {
            return false;
        }

        var centerX = (int)candidate.X;
        var centerY = (int)candidate.Y;

        // Rows and columns 0 are the finder's first edge rows, so its last dark run walking back from the centre
        var maxColumn = (int)(MaxBoundaryColumns * 1.6f * moduleSize);
        var maxRow = (int)(MaxBoundaryRows * 1.6f * moduleSize);
        if (!ModuleBoundaryReader.TryFinderEdgeRow(luminance, width, height, threshold, centerX, centerY, -vX, -vY, out var rowOffset)
            || !ModuleBoundaryReader.TryFinderEdgeRow(luminance, width, height, threshold, centerX, centerY, -uX, -uY, out var columnOffset)
            || !ModuleBoundaryReader.TryReadTimingLine(luminance, width, height, threshold, centerX - rowOffset * vX, centerY - rowOffset * vY, uX, uY, moduleSize, endsOnFinder: false, allowTriples: true, maxColumn, columns, out symbolWidth)
            || !ModuleBoundaryReader.TryReadTimingLine(luminance, width, height, threshold, centerX - columnOffset * uX, centerY - columnOffset * uY, vX, vY, moduleSize, endsOnFinder: false, allowTriples: true, maxRow, rows, out symbolHeight)
            || !RmQRConstants.TryGetVersion(symbolHeight, symbolWidth, out _))
        {
            return false;
        }

        ModuleBoundaryReader.ReadFinderLine(luminance, width, height, threshold, centerX, centerY, uX, uY, 0, columns, 0);
        ModuleBoundaryReader.ReadFinderLine(luminance, width, height, threshold, centerX, centerY, vX, vY, 0, rows, 0);
        ModuleBoundaryReader.ReadRunInteriors(luminance, width, height, threshold, centerX, centerY, uX, uY, vX, vY, columns, symbolWidth, rows, symbolHeight);
        ModuleBoundaryReader.ReadRunInteriors(luminance, width, height, threshold, centerX, centerY, vX, vY, uX, uY, rows, symbolHeight, columns, symbolWidth);
        if (!ModuleBoundaryReader.TryFillBoundaries(columns, symbolWidth) || !ModuleBoundaryReader.TryFillBoundaries(rows, symbolHeight))
            return false;

        frame = new AxisAlignedFrame(centerX, centerY, uX, uY, vX, vY);
        return true;
    }

    /// <summary>
    /// The frame at a scale whose finder-side format copy read <paramref name="version"/>: anchors the far end on the sub-finder, then decodes.
    /// </summary>
    private static DecodeStatus TryScaledFrame(
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
        RmQRVersion version,
        in PerspectiveTransform affine,
        Span<byte> modules,
        Span<char> destination,
        out int charsWritten,
        out RmQRCodeDecodeInfo info,
        ref DecodeStatus bestStatus,
        ref RmQRCodeDecodeInfo bestInfo,
        ref int attemptsRemaining,
        out DecodeStatus anchoredStatus)
    {
        charsWritten = 0;
        var symbolWidth = RmQRConstants.GetWidth(version);
        var symbolHeight = RmQRConstants.GetHeight(version);
        var samplingSlack = Math.Max((float)Math.Sqrt(uX * uX + uY * uY), (float)Math.Sqrt(vX * vX + vY * vY));

        var frameStatus = DecodeStatus.NotDetected;
        anchoredStatus = DecodeStatus.NotDetected;
        var subFinderFound = TryLocateSubFinder(luminance, width, height, threshold, candidate, uX, uY, vX, vY, symbolWidth, symbolHeight, out var subX, out var subY);
        if (subFinderFound)
        {
            ConcentricCentroid.TryRefine(luminance, width, height, grey, uX, uY, vX, vY, 1f, 1f, 0.5f, ref subX, ref subY, out _);
            // Predicted (affine) and observed offsets from the finder center to the sub-finder center.
            var dX = symbolWidth - 2.5f - FinderCenter;
            var dY = symbolHeight - 2.5f - FinderCenter;
            var predictedX = dX * uX + dY * vX;
            var predictedY = dX * uY + dY * vY;
            var observedX = subX - candidate.X;
            var observedY = subY - candidate.Y;
            var predictedLength = (float)Math.Sqrt(predictedX * predictedX + predictedY * predictedY);
            var observedLength = (float)Math.Sqrt(observedX * observedX + observedY * observedY);
            if (predictedLength > 0f && observedLength > 0f)
            {
                // (a) Rotation-corrected, isotropically rescaled frame: exact for a
                // rotated or uniformly mis-scaled affine capture.
                var scale = observedLength / predictedLength;
                var cos = (predictedX * observedX + predictedY * observedY) / (predictedLength * observedLength);
                var sin = (predictedX * observedY - predictedY * observedX) / (predictedLength * observedLength);
                Rotate(uX, uY, cos, sin, out var ruX, out var ruY);
                Rotate(vX, vY, cos, sin, out var rvX, out var rvY);
                var isotropic = PerspectiveTransform.FromLocalFrame(FinderCenter, FinderCenter, candidate.X, candidate.Y, scale * ruX, scale * ruY, scale * rvX, scale * rvY, 0f, 0f);
                var status = Attempt(luminance, width, height, threshold, grey, isotropic, symbolWidth, symbolHeight, samplingSlack, modules, destination, out charsWritten, out info, ref bestStatus, ref bestInfo, ref attemptsRemaining);
                if (IsTerminal(status))
                    return status;
                frameStatus = Deeper(frameStatus, status);

                // (b) Anisotropic rescale without rotation: exact for a symbol rendered
                // with non-square modules (independent per-axis scale error).
                var determinant = dX * dY * (uX * vY - uY * vX);
                if (Math.Abs(determinant) > 1e-6f)
                {
                    var a = (observedX * dY * vY - observedY * dY * vX) / determinant;
                    var b = (observedY * dX * uX - observedX * dX * uY) / determinant;
                    if (a > 0.7f && a < 1.4f && b > 0.7f && b < 1.4f)
                    {
                        var anisotropic = PerspectiveTransform.FromLocalFrame(FinderCenter, FinderCenter, candidate.X, candidate.Y, a * uX, a * uY, b * vX, b * vY, 0f, 0f);
                        status = Attempt(luminance, width, height, threshold, grey, anisotropic, symbolWidth, symbolHeight, samplingSlack, modules, destination, out charsWritten, out info, ref bestStatus, ref bestInfo, ref attemptsRemaining);
                        if (IsTerminal(status))
                            return status;
                        frameStatus = Deeper(frameStatus, status);
                    }
                }

                // (c) Perspective: only after an affine attempt got past format decoding
                // (the grid is roughly right, RS still fails). Search the two projective
                // coefficients; for each, the sub-finder fixes the Jacobian column scale and the
                // frame rotation exactly, and the sub-finder-side format copy must read
                // back consistently before the full grid is sampled.
                if (IsPlausibleRefinement(frameStatus))
                {
                    status = TryPerspectiveVariants(
                        luminance, width, height, threshold, grey, candidate,
                        uX, uY, vX, vY, version, symbolWidth, symbolHeight, samplingSlack,
                        observedX, observedY, dX, dY,
                        modules, destination, out charsWritten, out info,
                        ref bestStatus, ref bestInfo, ref attemptsRemaining);
                    if (IsTerminal(status))
                        return status;
                }
            }
        }

        anchoredStatus = frameStatus;

        // Fallback: the unrefined local frame (small symbols, or a sub-finder hidden
        // by damage). Skipped when a refined affine grid already read the format:
        // the coarser frame cannot do better. Run at every scale, though: each is a
        // different grid, and on a hidden sub-finder it is the frame that reads.
        if (!IsPlausibleRefinement(frameStatus))
        {
            var status = Attempt(luminance, width, height, threshold, grey, affine, symbolWidth, symbolHeight, samplingSlack, modules, destination, out charsWritten, out info, ref bestStatus, ref bestInfo, ref attemptsRemaining);
            if (IsTerminal(status))
                return status;
        }

        charsWritten = 0;
        info = bestInfo;
        return bestStatus;
    }

    /// <summary>
    /// Bounded search over the projective denominator coefficients.
    /// For every pair, the finder→sub-finder correspondence determines the finder-local Jacobian scale and rotation in closed form (a homography maps the grid line through both centers to the image line through both centers; only the scale along it depends on the coefficients).
    /// </summary>
    private static DecodeStatus TryPerspectiveVariants(
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
        RmQRVersion version,
        int symbolWidth,
        int symbolHeight,
        float samplingSlack,
        float observedX,
        float observedY,
        float dX,
        float dY,
        Span<byte> modules,
        Span<char> destination,
        out int charsWritten,
        out RmQRCodeDecodeInfo info,
        ref DecodeStatus bestStatus,
        ref RmQRCodeDecodeInfo bestInfo,
        ref int attemptsRemaining)
    {
        // Relative foreshortening at the far edge, per axis. Fine steps along the
        // long axis: a 1% error over 139 modules is already 0.7 module at the far end.
        ReadOnlySpan<float> xStrengths = stackalloc float[]
        {
            0f, -0.01f, 0.01f, -0.02f, 0.02f, -0.03f, 0.03f, -0.04f, 0.04f, -0.05f, 0.05f, -0.06f, 0.06f,
            -0.07f, 0.07f, -0.08f, 0.08f, -0.09f, 0.09f, -0.10f, 0.10f, -0.11f, 0.11f, -0.12f, 0.12f,
        };
        ReadOnlySpan<float> yStrengths = stackalloc float[] { 0f, -0.02f, 0.02f, -0.04f, 0.04f, -0.06f, 0.06f, -0.08f, 0.08f, -0.10f, 0.10f, -0.12f, 0.12f };

        var uLength = (float)Math.Sqrt(uX * uX + uY * uY);
        var observedLength = (float)Math.Sqrt(observedX * observedX + observedY * observedY);
        var farX = symbolWidth - 2.5f;
        var farY = symbolHeight - 2.5f;

        // Row-axis shear: under a keystone the finder's column axis leans away from
        // the perpendicular by atan(shrink / height), 9° for a 2% tilt on 17 rows.
        // The column direction is pinned by the sub-finder (the finder→sub-finder
        // line is almost a symbol row); the row axis is the free direction.
        for (var shearStep = 0; shearStep <= 2 * MaxShearDegrees; shearStep++)
        {
            var shearDegrees = (shearStep + 1) / 2 * ((shearStep & 1) == 0 ? -1 : 1); // 0, +1, -1, +2, -2, …
            var shearRadians = shearDegrees * (Math.PI / 180d);
            var shearCos = (float)Math.Cos(shearRadians);
            var shearSin = (float)Math.Sin(shearRadians);
            // The row spacing was measured perpendicular to the column axis, i.e. it is
            // the leaning axis projected onto that normal: undo the projection.
            Rotate(vX / shearCos, vY / shearCos, shearCos, shearSin, out var svX, out var svY);
            var uDotV = uX * svX + uY * svY;

            for (var yIndex = 0; yIndex < yStrengths.Length; yIndex++)
            {
                var perspectiveY = yStrengths[yIndex] / symbolHeight;
                for (var xIndex = 0; xIndex < xStrengths.Length; xIndex++)
                {
                    if (attemptsRemaining <= 0)
                    {
                        charsWritten = 0;
                        info = bestInfo;
                        return bestStatus;
                    }

                    var perspectiveX = xStrengths[xIndex] / symbolWidth;
                    if (perspectiveX == 0f && perspectiveY == 0f && shearDegrees == 0)
                        continue; // the affine frame was already tried

                    // Q − P = (d0 / D) · J · (dX, dY): solve the Jacobian scale along the
                    // column axis (quadratic, the axes need not be perpendicular) and the
                    // frame rotation from the observed offset.
                    var d0 = perspectiveX * FinderCenter + perspectiveY * FinderCenter + 1f;
                    var d = perspectiveX * farX + perspectiveY * farY + 1f;
                    if (d <= 0.5f || d0 <= 0.5f)
                        continue;
                    // |a·dX·u + dY·sv|² = required²: every term uses the SHEARED row axis
                    // sv (its length is |v| / cos φ), the same vector the Jacobian below applies.
                    var required = observedLength * d / d0;
                    var qa = dX * dX * uLength * uLength;
                    var qb = 2f * dX * dY * uDotV;
                    var qc = dY * dY * (svX * svX + svY * svY) - required * required;
                    var discriminant = qb * qb - 4f * qa * qc;
                    if (discriminant <= 0f)
                        continue;
                    var a = (-qb + (float)Math.Sqrt(discriminant)) / (2f * qa);
                    if (a < 0.7f || a > 1.4f)
                        continue;

                    var jX = a * dX * uX + dY * svX;
                    var jY = a * dX * uY + dY * svY;
                    var jLength = (float)Math.Sqrt(jX * jX + jY * jY);
                    if (jLength <= 0f)
                        continue;
                    var cos = (jX * observedX + jY * observedY) / (jLength * observedLength);
                    var sin = (jX * observedY - jY * observedX) / (jLength * observedLength);
                    Rotate(uX, uY, cos, sin, out var ruX, out var ruY);
                    Rotate(svX, svY, cos, sin, out var rvX, out var rvY);

                    var transform = PerspectiveTransform.FromLocalFrame(FinderCenter, FinderCenter, candidate.X, candidate.Y, a * ruX, a * ruY, rvX, rvY, perspectiveX, perspectiveY);
                    if (!SymbolFitsImage(transform, symbolWidth, symbolHeight, width, height, samplingSlack))
                        continue;

                    // Cheap gate: the far-end format copy must agree with the version.
                    var subFinderSideRaw = ReadFormatCopy(luminance, width, height, threshold, transform, subFinderSide: true, symbolWidth, symbolHeight);
                    if (!RmQRFormatInformationDecoder.TryDecodeCopy(subFinderSideRaw, subFinderSide: true, out var farVersion, out _, out var distance)
                        || farVersion != version || distance > 1)
                    {
                        continue;
                    }
                    if (!TimingRowsAgree(luminance, width, height, threshold, transform, version, symbolWidth, symbolHeight))
                        continue;

                    var status = Attempt(luminance, width, height, threshold, grey, transform, symbolWidth, symbolHeight, samplingSlack, modules, destination, out charsWritten, out info, ref bestStatus, ref bestInfo, ref attemptsRemaining);
                    if (IsTerminal(status))
                        return status;
                }
            }
        }

        charsWritten = 0;
        info = bestInfo;
        return bestStatus;
    }

    /// <summary>Samples the full grid through the transform and runs the matrix decoder.</summary>
    private static DecodeStatus Attempt(
        ReadOnlySpan<byte> luminance,
        int width,
        int height,
        byte threshold,
        in GreyLevels grey,
        in PerspectiveTransform transform,
        int symbolWidth,
        int symbolHeight,
        float samplingSlack,
        Span<byte> modules,
        Span<char> destination,
        out int charsWritten,
        out RmQRCodeDecodeInfo info,
        ref DecodeStatus bestStatus,
        ref RmQRCodeDecodeInfo bestInfo,
        ref int attemptsRemaining)
    {
        charsWritten = 0;
        if (attemptsRemaining <= 0 || !SymbolFitsImage(transform, symbolWidth, symbolHeight, width, height, samplingSlack))
        {
            info = bestInfo;
            return DecodeStatus.NotDetected;
        }

        attemptsRemaining--;
        var grid = modules.Slice(0, symbolWidth * symbolHeight);
        SampleGrid(luminance, width, height, threshold, transform, symbolWidth, symbolHeight, grid);
        var status = RmQRMatrixDecoder.DecodeMatrix(grid, symbolWidth, symbolHeight, destination, out charsWritten, out info);
        if (status == DecodeStatus.Success)
        {
            // The frames already carry a mirrored capture in their axes, so the
            // transform is in symbol order and never transposed.
            info = info.WithCorners(SymbolGeometry.FromTransform(transform, symbolWidth, symbolHeight, transposed: false));
            return status;
        }

        TrackBestFailure(status, info, ref bestStatus, ref bestInfo);
        if (!grey.IsEnabled || !IsPlausibleRefinement(status) || attemptsRemaining <= 0)
            return status;

        // Grey edges: the same grid read by coverage, decoded only where it differs from the one that failed
        attemptsRemaining--;
        var midpoint = grey.Midpoint;
        var changed = false;
        for (var row = 0; row < symbolHeight; row++)
        {
            for (var column = 0; column < symbolWidth; column++)
            {
                transform.Transform(column + 0.5f, row + 0.5f, out var x, out var y);
                var dark = LuminanceSampler.Bilinear(luminance, width, height, x, y) < midpoint ? (byte)1 : (byte)0;
                changed |= grid[row * symbolWidth + column] != dark;
                grid[row * symbolWidth + column] = dark;
            }
        }
        if (!changed)
            return status;
        var coverageStatus = RmQRMatrixDecoder.DecodeMatrix(grid, symbolWidth, symbolHeight, destination, out charsWritten, out var coverageInfo);
        if (coverageStatus == DecodeStatus.Success)
        {
            info = coverageInfo.WithCorners(SymbolGeometry.FromTransform(transform, symbolWidth, symbolHeight, transposed: false));
            return coverageStatus;
        }
        TrackBestFailure(coverageStatus, coverageInfo, ref bestStatus, ref bestInfo);
        if (Rank(coverageStatus) <= Rank(status))
            return status;
        info = coverageInfo;
        return coverageStatus;
    }

    /// <summary>
    /// Locates the 5×5 sub-finder (dark ring, light ring, dark center) around its predicted position (center at grid (w − 2.5, h − 2.5)) by template matching on a half-module lattice, then refines the center to the middle of the center dark module along both axes.
    /// </summary>
    internal static bool TryLocateSubFinder(
        ReadOnlySpan<byte> luminance,
        int width,
        int height,
        byte threshold,
        in FinderPattern candidate,
        float uX,
        float uY,
        float vX,
        float vY,
        int symbolWidth,
        int symbolHeight,
        out float centerX,
        out float centerY)
    {
        var dX = symbolWidth - 2.5f - FinderCenter;
        var dY = symbolHeight - 2.5f - FinderCenter;
        var predictedX = candidate.X + dX * uX + dY * vX;
        var predictedY = candidate.Y + dX * uY + dY * vY;

        var radius = SubFinderSearchRadiusHalfModulesBase + symbolWidth / 10 * SubFinderSearchRadiusHalfModulesPerTenModules;

        // Reject before searching when the whole template can only land outside the
        // image. Every sample sits at predicted + (offU + i)·u + (offV + j)·sv with
        // |offU| ≤ radius/2 and |i| ≤ 2, and the 12° lean bounds |svX| by
        // |vX| + tan 12°·|vY|, so |vX| + |vY| over-estimates it. A frame whose prediction
        // misses the image entirely would otherwise score 0 at all 7,803 ring positions
        // before returning false. Most wrong frames do NOT predict off-image, so this is
        // the cheaper and rarer of the two guards here — end to end it is worth about a
        // sixth of the failure-path win and the row-wise early exit below is worth the
        // rest; on a frame it does catch it replaces a ~400 µs search with a few ns.
        var reach = radius * 0.5f + 2f;
        var extentX = reach * (Math.Abs(uX) + Math.Abs(vX) + Math.Abs(vY)) + 1f;
        var extentY = reach * (Math.Abs(uY) + Math.Abs(vX) + Math.Abs(vY)) + 1f;
        if (predictedX + extentX < 0f || predictedX - extentX >= width
            || predictedY + extentY < 0f || predictedY - extentY >= height)
        {
            centerX = 0f;
            centerY = 0f;
            return false;
        }

        var bestScore = -1;
        var bestDistance = int.MaxValue;
        var bestX = 0f;
        var bestY = 0f;
        var bestVX = vX;
        var bestVY = vY;

        // A keystone leans the column axis (see TryPerspectiveVariants); the template
        // is matched with a few leans so its corner samples stay on their modules.
        ReadOnlySpan<float> shearDegrees = stackalloc float[] { 0f, 12f, -12f };
        Span<ulong> survivors = stackalloc ulong[MaxSubFinderScreenRows];
        foreach (var degrees in shearDegrees)
        {
            var radians = degrees * (Math.PI / 180d);
            var leanCos = (float)Math.Cos(radians);
            Rotate(vX / leanCos, vY / leanCos, leanCos, (float)Math.Sin(radians), out var svX, out var svY);
            var screenTried = false;
            var screened = false;
            // Outward by rings (Chebyshev distance): the prediction is usually within a
            // few modules, and the first perfect match on the innermost ring is the
            // answer (the tie-break prefers the nearest anyway), so the search stops there.
            for (var ring = 0; ring <= radius && bestScore < 25; ring++)
            {
                if (ring >= SubFinderUnscreenedRings && !screenTried)
                {
                    screenTried = true;
                    screened = TryScreenSubFinderPositions(luminance, width, height, threshold, predictedX, predictedY, uX, uY, svX, svY, radius, survivors);
                }

                for (var ov = -ring; ov <= ring; ov++)
                {
                    var offV = ov * 0.5f;
                    // Between the top and bottom rows only the two ends: smaller rings scanned the interior
                    var step = ov == -ring || ov == ring ? 1 : 2 * ring;
                    for (var ou = -ring; ou <= ring; ou += step)
                    {
                        // Two certain mismatches: the position scores under the floor, and a
                        // score under the floor never becomes the answer
                        if (screened && (survivors[ov + radius] >> (ou + radius) & 1) == 0)
                            continue;

                        var offU = ou * 0.5f;
                        var cx = predictedX + offU * uX + offV * svX;
                        var cy = predictedY + offU * uY + offV * svY;
                        var score = 0;
                        var remaining = 25;
                        for (var j = -2; j <= 2; j++)
                        {
                            for (var i = -2; i <= 2; i++)
                            {
                                var expectedDark = i == -2 || i == 2 || j == -2 || j == 2 || (i == 0 && j == 0);
                                var px = (int)(cx + i * uX + j * svX);
                                var py = (int)(cy + i * uY + j * svY);
                                if ((uint)px >= (uint)width || (uint)py >= (uint)height)
                                    continue; // outside the image counts as a mismatch
                                var dark = luminance[py * width + px] < threshold;
                                if (dark == expectedDark)
                                    score++;
                            }

                            // Once the rows still to come cannot lift the score to the
                            // acceptance floor, this position is decided: stop sampling
                            // it. A partial score may still be recorded as the best so
                            // far, which cannot change the outcome — acceptance needs
                            // SubFinderMinScore, and any position that reaches it
                            // outranks every partial one. Checked per row of five rather
                            // than per sample: the same early exit on the failing path
                            // (which bails after one row) without adding a branch to the
                            // 25-sample inner loop that the matching path runs in full.
                            remaining -= 5;
                            if (score + remaining < SubFinderMinScore)
                                break;
                        }

                        var distance = ou * ou + ov * ov;
                        if (score > bestScore || (score == bestScore && distance < bestDistance))
                        {
                            bestScore = score;
                            bestDistance = distance;
                            bestX = cx;
                            bestY = cy;
                            bestVX = svX;
                            bestVY = svY;
                        }
                    }
                }
            }

            if (bestScore == 25)
                break; // a perfect match needs no other lean
        }

        if (bestScore < SubFinderMinScore)
        {
            centerX = 0f;
            centerY = 0f;
            return false;
        }

        // Sub-pixel refinement: center of the center dark module along each axis.
        centerX = bestX;
        centerY = bestY;
        var uLength = (float)Math.Sqrt(uX * uX + uY * uY);
        var vLength = (float)Math.Sqrt(bestVX * bestVX + bestVY * bestVY);
        if (uLength > 0f && vLength > 0f)
        {
            RefineAlongAxis(luminance, width, height, threshold, ref centerX, ref centerY, uX / uLength, uY / uLength, uLength);
            RefineAlongAxis(luminance, width, height, threshold, ref centerX, ref centerY, bestVX / vLength, bestVY / vLength, vLength);
        }
        return true;
    }

    /// <summary>
    /// Screens the sub-finder search's positions on the half-module lattice they sample: position (ou, ov) samples lattice points (ou + 2i, ov + 2j), so one read of each point serves every position, and a position with two certain mismatches of its 25 cannot reach <see cref="SubFinderMinScore"/>.
    /// Bit <c>ou + radius</c> of <c>survivors[ov + radius]</c> stays set unless the position is dropped.
    /// </summary>
    /// <remarks>
    /// The search computes each sample in another float expression, so the two can fall in different pixels next to a pixel edge or the image border.
    /// A point counts only when a margin around it, several times the rounding of both expressions at its magnitude, stays in one pixel or wholly outside the image; otherwise it matches either way.
    /// False, with nothing screened, when the lattice is wider than a mask or a coordinate is not finite.
    /// </remarks>
    internal static bool TryScreenSubFinderPositions(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, float predictedX, float predictedY, float uX, float uY, float svX, float svY, int radius, Span<ulong> survivors)
    {
        Span<ulong> mismatchIfDark = stackalloc ulong[MaxSubFinderScreenRows];
        Span<ulong> mismatchIfLight = stackalloc ulong[MaxSubFinderScreenRows];
        if (!TryClassifySubFinderLattice(luminance, width, height, threshold, predictedX, predictedY, uX, uY, svX, svY, radius, mismatchIfDark, mismatchIfLight))
            return false;

        // Per position row, all positions at once: a bit reaches "two" at its second mismatch
        var positions = 2 * radius + 1;
        var positionMask = (1UL << positions) - 1;
        for (var p = 0; p < positions; p++)
        {
            var one = 0UL;
            var two = 0UL;
            for (var j = 0; j < 5; j++)
            {
                // Lattice row of template row j - 2 for position row p - radius
                var ifDark = mismatchIfDark[p + 2 * j];
                var ifLight = mismatchIfLight[p + 2 * j];
                for (var i = 0; i < 5; i++)
                {
                    // Dark ring, light ring, dark centre
                    var expectedDark = i == 0 || i == 4 || j == 0 || j == 4 || (i == 2 && j == 2);
                    var mismatch = (expectedDark ? ifDark : ifLight) >> (2 * i);
                    two |= one & mismatch;
                    one |= mismatch;
                }
            }
            survivors[p] = ~two & positionMask;
        }
        return true;
    }

    /// <summary>
    /// The lattice <see cref="TryScreenSubFinderPositions"/> screens on, <c>2 · radius + 9</c> points a side from (-(radius + 4), -(radius + 4)) half modules off the prediction.
    /// Bit <c>a</c> of row <c>b</c> is set in <paramref name="mismatchIfDark"/> for a certain light pixel, in <paramref name="mismatchIfLight"/> for a certain dark one, in both when the point is certainly outside the image, and in neither next to an edge.
    /// </summary>
    internal static bool TryClassifySubFinderLattice(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, float predictedX, float predictedY, float uX, float uY, float svX, float svY, int radius, Span<ulong> mismatchIfDark, Span<ulong> mismatchIfLight)
    {
        var side = 2 * radius + 9;
        if (radius < 0 || side > MaxSubFinderScreenRows)
            return false;

        // Every coordinate either expression forms is at most this large; each rounds a
        // handful of times, 2^-24 of it at most, and the margin is 2^-18 of it
        var reach = radius * 0.5f + 2f;
        var marginX = (Math.Abs(predictedX) + reach * (Math.Abs(uX) + Math.Abs(svX))) * (1f / 262144f);
        var marginY = (Math.Abs(predictedY) + reach * (Math.Abs(uY) + Math.Abs(svY))) * (1f / 262144f);
        if (!(marginX < float.PositiveInfinity) || !(marginY < float.PositiveInfinity))
            return false; // NaN fails both comparisons

#if NET8_0_OR_GREATER
        if (Vector128.IsHardwareAccelerated)
        {
            ClassifySubFinderLatticeVector128(luminance, width, height, threshold, predictedX, predictedY, uX, uY, svX, svY, side, marginX, marginY, mismatchIfDark, mismatchIfLight);
            return true;
        }
#endif
        ClassifySubFinderLatticeScalar(luminance, width, height, threshold, predictedX, predictedY, uX, uY, svX, svY, side, marginX, marginY, mismatchIfDark, mismatchIfLight);
        return true;
    }

    internal static void ClassifySubFinderLatticeScalar(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, float predictedX, float predictedY, float uX, float uY, float svX, float svY, int side, float marginX, float marginY, Span<ulong> mismatchIfDark, Span<ulong> mismatchIfLight)
    {
        var first = -(side - 1) / 2;
        for (var row = 0; row < side; row++)
        {
            var halfV = (first + row) * 0.5f;
            var rowX = predictedX + halfV * svX;
            var rowY = predictedY + halfV * svY;
            var ifDark = 0UL;
            var ifLight = 0UL;
            for (var column = 0; column < side; column++)
            {
                var halfU = (first + column) * 0.5f;
                var x = rowX + halfU * uX;
                var y = rowY + halfU * uY;
                var bit = 1UL << column;

                // The search truncates, so a coordinate in (-1, size) lands inside
                if (x + marginX <= -1f || x - marginX >= width || y + marginY <= -1f || y - marginY >= height)
                {
                    ifDark |= bit;
                    ifLight |= bit;
                    continue;
                }
                if (x - marginX <= -1f || x + marginX >= width || y - marginY <= -1f || y + marginY >= height)
                    continue;
                var px = (int)(x - marginX);
                var py = (int)(y - marginY);
                if (px != (int)(x + marginX) || py != (int)(y + marginY))
                    continue;

                if (luminance[py * width + px] < threshold)
                    ifLight |= bit;
                else
                    ifDark |= bit;
            }
            mismatchIfDark[row] = ifDark;
            mismatchIfLight[row] = ifLight;
        }
    }

#if NET8_0_OR_GREATER
    /// <summary>The scalar classification four points at a time: the same expressions and comparisons per lane, the pixel read only where the lane is certain.</summary>
    internal static void ClassifySubFinderLatticeVector128(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, float predictedX, float predictedY, float uX, float uY, float svX, float svY, int side, float marginX, float marginY, Span<ulong> mismatchIfDark, Span<ulong> mismatchIfLight)
    {
        var first = -(side - 1) / 2;
        var laneHalves = Vector128.Create(0f, 0.5f, 1f, 1.5f);
        var columnU = Vector128.Create(uX);
        var columnV = Vector128.Create(uY);
        var marginXs = Vector128.Create(marginX);
        var marginYs = Vector128.Create(marginY);
        var minusOne = Vector128.Create(-1f);
        var widths = Vector128.Create((float)width);
        var heights = Vector128.Create((float)height);
        var stride = Vector128.Create(width);
        var sideMask = side == 64 ? ulong.MaxValue : (1UL << side) - 1;
        Span<int> indices = stackalloc int[4];
        for (var row = 0; row < side; row++)
        {
            var halfV = (first + row) * 0.5f;
            var rowX = Vector128.Create(predictedX + halfV * svX);
            var rowY = Vector128.Create(predictedY + halfV * svY);
            var ifDark = 0UL;
            var ifLight = 0UL;
            // Lanes past the side are classified and masked off below; a certain lane is inside the image whatever its column
            for (var column = 0; column < side; column += 4)
            {
                // (first + column) / 2 plus a lane's half is exact, as the scalar half is
                var halfU = Vector128.Create((first + column) * 0.5f) + laneHalves;
                var x = rowX + halfU * columnU;
                var y = rowY + halfU * columnV;
                var xLow = x - marginXs;
                var xHigh = x + marginXs;
                var yLow = y - marginYs;
                var yHigh = y + marginYs;

                var outside = Vector128.LessThanOrEqual(xHigh, minusOne) | Vector128.GreaterThanOrEqual(xLow, widths)
                    | Vector128.LessThanOrEqual(yHigh, minusOne) | Vector128.GreaterThanOrEqual(yLow, heights);
                var nearBorder = Vector128.LessThanOrEqual(xLow, minusOne) | Vector128.GreaterThanOrEqual(xHigh, widths)
                    | Vector128.LessThanOrEqual(yLow, minusOne) | Vector128.GreaterThanOrEqual(yHigh, heights);
                // Truncating, as the scalar cast does; a lane out of range is near the border and not read
                var pxLow = Vector128.ConvertToInt32(xLow);
                var pyLow = Vector128.ConvertToInt32(yLow);
                var onePixel = Vector128.Equals(pxLow, Vector128.ConvertToInt32(xHigh)) & Vector128.Equals(pyLow, Vector128.ConvertToInt32(yHigh));
                var certain = Vector128.AndNot(onePixel, nearBorder.AsInt32());

                var outsideBits = (ulong)outside.ExtractMostSignificantBits() << column;
                ifDark |= outsideBits;
                ifLight |= outsideBits;
                var certainBits = certain.ExtractMostSignificantBits();
                if (certainBits == 0)
                    continue;
                (pyLow * stride + pxLow).CopyTo(indices);
                while (certainBits != 0)
                {
                    var lane = BitOperations.TrailingZeroCount(certainBits);
                    certainBits &= certainBits - 1;
                    // Branch-free: on texture the pixel's class is a coin toss
                    var dark = (ulong)(luminance[indices[lane]] - threshold) >> 63;
                    ifLight |= dark << (column + lane);
                    ifDark |= (dark ^ 1) << (column + lane);
                }
            }
            mismatchIfDark[row] = ifDark & sideMask;
            mismatchIfLight[row] = ifLight & sideMask;
        }
    }
#endif

    /// <summary>
    /// Moves the point to the midpoint of the dark run it sits in, along a unit direction; left unchanged when the run is not a plausible single module.
    /// </summary>
    private static void RefineAlongAxis(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, ref float x, ref float y, float dirX, float dirY, float moduleLength)
    {
        var maxRun = moduleLength * 1.6f;
        var forward = DarkRun(luminance, width, height, threshold, x, y, dirX, dirY, maxRun);
        var backward = DarkRun(luminance, width, height, threshold, x, y, -dirX, -dirY, maxRun);
        if (float.IsNaN(forward) || float.IsNaN(backward))
            return;
        var shift = (forward - backward) / 2f;
        x += dirX * shift;
        y += dirY * shift;
    }

    /// <summary>Distance from the start to the first light pixel along a direction; NaN when clipped or too long.</summary>
    private static float DarkRun(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, float startX, float startY, float dirX, float dirY, float maxRun)
    {
        for (var step = 0.5f; step <= maxRun; step += 0.5f)
        {
            var px = (int)(startX + dirX * step);
            var py = (int)(startY + dirY * step);
            if ((uint)px >= (uint)width || (uint)py >= (uint)height)
                return float.NaN;
            if (luminance[py * width + px] >= threshold)
                return step - 0.25f;
        }
        return float.NaN;
    }

    /// <summary>
    /// Cheap far-from-anchor consistency check: the edge timing patterns (rows 0 and h−1, dark at even columns) sampled between the finder and the sub-finder, skipping the alignment patterns.
    /// A grid that is right at both anchors but bent in between (wrong projective coefficient or shear) fails here long before a full sample and RS decode would reject it.
    /// </summary>
    internal static bool TimingRowsAgree(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in PerspectiveTransform transform, RmQRVersion version, int symbolWidth, int symbolHeight)
    {
        var alignment = RmQRConstants.GetAlignmentColumns(version);
        var samples = 0;
        var mismatches = 0;
        for (var col = 8; col <= symbolWidth - 6; col++)
        {
            var nearAlignment = false;
            for (var i = 0; i < alignment.Length; i++)
            {
                if (Math.Abs(col - alignment[i]) <= 1)
                {
                    nearAlignment = true;
                    break;
                }
            }
            if (nearAlignment)
                continue;

            var expectedDark = (col & 1) == 0;
            samples += 2;
            if (SampleDark(luminance, width, height, threshold, transform, col + 0.5f, 0.5f) != expectedDark)
                mismatches++;
            if (SampleDark(luminance, width, height, threshold, transform, col + 0.5f, symbolHeight - 0.5f) != expectedDark)
                mismatches++;
        }

        // A half-module drift flips about half the alternating samples; noise flips a few.
        return mismatches * 8 <= samples;
    }

    /// <summary>
    /// Reads one 18-bit format copy through the transform: finder side (rows 1-5 × cols 8-10 column-major, then col 11 rows 1-3) or sub-finder side (rows h−6..h−2 × cols w−8..w−6, then row h−6 cols w−5..w−3), same bit order as the matrix decoder.
    /// </summary>
    internal static int ReadFormatCopy(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in PerspectiveTransform transform, bool subFinderSide, int symbolWidth, int symbolHeight)
    {
        var raw = 0;
        var rowBase = subFinderSide ? symbolHeight - 6 : 1;
        var colBase = subFinderSide ? symbolWidth - 8 : 8;
        for (var c = 0; c < 3; c++)
        {
            for (var r = 0; r < 5; r++)
            {
                if (SampleDark(luminance, width, height, threshold, transform, colBase + c + 0.5f, rowBase + r + 0.5f))
                    raw |= 1 << (c * 5 + r);
            }
        }
        for (var k = 0; k < 3; k++)
        {
            var col = subFinderSide ? symbolWidth - 5 + k : 11;
            var row = subFinderSide ? symbolHeight - 6 : k + 1;
            if (SampleDark(luminance, width, height, threshold, transform, col + 0.5f, row + 0.5f))
                raw |= 1 << (15 + k);
        }
        return raw;
    }

    private static bool SampleDark(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in PerspectiveTransform transform, float gridX, float gridY)
    {
        transform.Transform(gridX, gridY, out var x, out var y);
        if (float.IsNaN(x) || float.IsNaN(y))
            return false;
        var px = (int)x;
        var py = (int)y;
        if (px < 0)
            px = 0;
        else if (px >= width)
            px = width - 1;
        if (py < 0)
            py = 0;
        else if (py >= height)
            py = height - 1;
        return luminance[py * width + px] < threshold;
    }

    /// <summary>
    /// Narrowest column count the Vector128 samplers accept: one whole lane group.
    /// No rMQR width comes near it (the narrowest symbol is 27 columns wide), and RmQRSampleGridParityTest asserts that, so this is the one place the threshold lives — a test that repeated the literal would pin nothing.
    /// </summary>
    internal const int Simd128MinColumns = 8;

    /// <summary>
    /// Samples every module center of the rectangular grid through the transform.
    /// Out-of-range positions clamp to the nearest edge pixel.
    /// </summary>
    /// <remarks>
    /// The Vector128 kernel samples the exact same pixels as the scalar loop, so the two tiers are interchangeable (pinned by RmQRSampleGridParityTest).
    /// That is stricter than it looks: rMQR's scalar form divides each numerator by the denominator, and Standard QR's row kernel — which multiplies by one reciprocal instead — is NOT bit-equivalent to it, which is why this kernel is its own implementation rather than a shared one.
    ///
    /// Measured on Apple M2 (RmQrSampleArm findings log), 2.4-3.8x over the scalar loop.
    /// Two thirds of that comes from vectorizing the convert/clamp/gather; the rest from the affine special case, which is not a heuristic — every first attempt at a clean capture goes through a frame built with perspectiveX = perspectiveY = 0, and there the denominator is exactly 1f, so both divisions can be skipped without changing a sampled byte.
    /// </remarks>
    internal static void SampleGrid(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in PerspectiveTransform transform, int columns, int rows, Span<byte> modules)
    {
#if NET8_0_OR_GREATER
        // Every rMQR width is >= 27, so the vector path takes every real symbol; the
        // guard only covers direct callers (tests) below one full 8-module block.
        // RmQRSampleGridParityTest pins that against the version table, so a widened
        // threshold fails there instead of silently retiring the kernel.
        if (Vector128.IsHardwareAccelerated && columns >= Simd128MinColumns)
        {
            SampleGridSimd128(luminance, width, height, threshold, transform, columns, rows, modules);
            return;
        }
#endif
        SampleGridScalar(luminance, width, height, threshold, transform, columns, rows, modules);
    }

    /// <summary>
    /// Scalar reference sampler: one <see cref="PerspectiveTransform.Transform"/> per module.
    /// Kept as the parity reference for the vector tier as well as the fallback for platforms without Vector128.
    /// </summary>
    internal static void SampleGridScalar(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in PerspectiveTransform transform, int columns, int rows, Span<byte> modules)
    {
        for (var row = 0; row < rows; row++)
        {
            var gridY = row + 0.5f;
            var rowBase = row * columns;
            for (var col = 0; col < columns; col++)
            {
                transform.Transform(col + 0.5f, gridY, out var x, out var y);
                // Pixel edges sit on integers, so the pixel containing a point is its floor
                var px = (int)x;
                var py = (int)y;
                if (px < 0)
                    px = 0;
                else if (px >= width)
                    px = width - 1;
                if (py < 0)
                    py = 0;
                else if (py >= height)
                    py = height - 1;
                modules[rowBase + col] = luminance[py * width + px] < threshold ? (byte)1 : (byte)0;
            }
        }
    }

#if NET8_0_OR_GREATER
    /// <summary>
    /// Vector128 grid sampler: 8 module centres per step, with a projective and an affine variant.
    /// Byte-identical to <see cref="SampleGridScalar"/>.
    /// </summary>
    /// <remarks>
    /// Exactness is by construction, and each step is chosen to preserve it: lanes keep the scalar's own association <c>((a1x*gridX) + a2x*gridY) + a3x</c> (folding the last two into a row constant would re-associate), there is no FMA contraction and no reciprocal multiply, and the affine variant only skips a division by exactly <c>1f</c>.
    /// That is also why this is a separate implementation from the Standard QR row kernel, which computes <c>1/d</c> once and multiplies twice: it rounds differently.
    /// The overlapping tail block re-samples up to three already-written modules; it reads the same inputs and writes the same values.
    /// <para>
    /// The clamp is what makes the unchecked gather safe: px and py are pinned to [0, width-1] and [0, height-1].
    /// That relies on the caller having sliced <paramref name="luminance"/> to exactly width * height, as DecodeLuminance does — this method skips the bounds checks the scalar loop keeps, so a short span reads out of bounds here where it would throw there.
    /// </para>
    /// </remarks>
    internal static void SampleGridSimd128(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in PerspectiveTransform transform, int columns, int rows, Span<byte> modules)
    {
        if (!Vector128.IsHardwareAccelerated || columns < Simd128MinColumns)
        {
            SampleGridScalar(luminance, width, height, threshold, transform, columns, rows, modules);
            return;
        }

        // Affine frames have an exactly unit denominator, so both divisions drop out.
        // This is not a heuristic about typical input: TryDecodeFrame builds the
        // isotropic and anisotropic frames with perspectiveX = perspectiveY = 0, so
        // every attempt before the perspective search lands here.
        if (transform.a13 == 0f && transform.a23 == 0f && transform.a33 == 1f)
        {
            SampleGridSimd128Affine(luminance, width, height, threshold, transform, columns, rows, modules);
            return;
        }

        var laneOffsetsLo = Vector128.Create(0.5f, 1.5f, 2.5f, 3.5f);
        var laneOffsetsHi = Vector128.Create(4.5f, 5.5f, 6.5f, 7.5f);
        var a11 = Vector128.Create(transform.a11);
        var a12 = Vector128.Create(transform.a12);
        var a13 = Vector128.Create(transform.a13);
        var a31 = Vector128.Create(transform.a31);
        var a32 = Vector128.Create(transform.a32);
        var a33 = Vector128.Create(transform.a33);
        var zero = Vector128<int>.Zero;
        var maxPx = Vector128.Create(width - 1);
        var maxPy = Vector128.Create(height - 1);
        var widthVector = Vector128.Create(width);

        ref var luminanceRef = ref MemoryMarshal.GetReference(luminance);
        ref var moduleRef = ref MemoryMarshal.GetReference(modules);

        for (var row = 0; row < rows; row++)
        {
            var gridY = row + 0.5f;
            var rowBase = row * columns;
            var rowX = Vector128.Create(transform.a21 * gridY);
            var rowY = Vector128.Create(transform.a22 * gridY);
            var rowDenominator = Vector128.Create(transform.a23 * gridY);

            var column = 0;
            for (; column + 8 <= columns; column += 8)
            {
                var columnVector = Vector128.Create((float)column);
                var gridXLo = laneOffsetsLo + columnVector;
                var gridXHi = laneOffsetsHi + columnVector;
                var denominatorLo = a13 * gridXLo + rowDenominator + a33;
                var denominatorHi = a13 * gridXHi + rowDenominator + a33;
                var xLo = (a11 * gridXLo + rowX + a31) / denominatorLo;
                var yLo = (a12 * gridXLo + rowY + a32) / denominatorLo;
                var xHi = (a11 * gridXHi + rowX + a31) / denominatorHi;
                var yHi = (a12 * gridXHi + rowY + a32) / denominatorHi;

                // ConvertToInt32 truncates toward zero like the scalar cast: the pixel
                // containing the point, not the nearest one.
                var indexLo = Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(yLo), maxPy), zero) * widthVector
                    + Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(xLo), maxPx), zero);
                var indexHi = Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(yHi), maxPy), zero) * widthVector
                    + Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(xHi), maxPx), zero);

                // Lane extraction beats spilling the index vector to the stack: the
                // reload was measured on the critical path of every gather.
                ref var destination = ref Unsafe.Add(ref moduleRef, rowBase + column);
                Unsafe.Add(ref destination, 0) = Unsafe.Add(ref luminanceRef, indexLo.GetElement(0)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 1) = Unsafe.Add(ref luminanceRef, indexLo.GetElement(1)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 2) = Unsafe.Add(ref luminanceRef, indexLo.GetElement(2)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 3) = Unsafe.Add(ref luminanceRef, indexLo.GetElement(3)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 4) = Unsafe.Add(ref luminanceRef, indexHi.GetElement(0)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 5) = Unsafe.Add(ref luminanceRef, indexHi.GetElement(1)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 6) = Unsafe.Add(ref luminanceRef, indexHi.GetElement(2)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 7) = Unsafe.Add(ref luminanceRef, indexHi.GetElement(3)) < threshold ? (byte)1 : (byte)0;
            }

            while (column < columns)
            {
                var start = Math.Min(column, columns - 4);
                var gridX = laneOffsetsLo + Vector128.Create((float)start);
                var denominator = a13 * gridX + rowDenominator + a33;
                var x = (a11 * gridX + rowX + a31) / denominator;
                var y = (a12 * gridX + rowY + a32) / denominator;

                var index = Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(y), maxPy), zero) * widthVector
                    + Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(x), maxPx), zero);

                ref var destination = ref Unsafe.Add(ref moduleRef, rowBase + start);
                Unsafe.Add(ref destination, 0) = Unsafe.Add(ref luminanceRef, index.GetElement(0)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 1) = Unsafe.Add(ref luminanceRef, index.GetElement(1)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 2) = Unsafe.Add(ref luminanceRef, index.GetElement(2)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 3) = Unsafe.Add(ref luminanceRef, index.GetElement(3)) < threshold ? (byte)1 : (byte)0;
                column = start + 4;
            }
        }
    }

    /// <summary>
    /// Affine tier of <see cref="SampleGridSimd128"/>: the denominator is exactly 1f, so x / 1f == x and both divisions are dropped.
    /// Everything else — the hoisted products, the clamp, the overlapping tail — matches the projective kernel, and the sampled bytes match <see cref="SampleGridScalar"/>.
    /// </summary>
    /// <remarks>
    /// A separate method rather than a branch inside the shared loop: this is the shape that was measured, and it keeps the projective kernel's constants out of the affine loop's register budget.
    /// </remarks>
    private static void SampleGridSimd128Affine(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in PerspectiveTransform transform, int columns, int rows, Span<byte> modules)
    {
        var laneOffsetsLo = Vector128.Create(0.5f, 1.5f, 2.5f, 3.5f);
        var laneOffsetsHi = Vector128.Create(4.5f, 5.5f, 6.5f, 7.5f);
        var a11 = Vector128.Create(transform.a11);
        var a12 = Vector128.Create(transform.a12);
        var a31 = Vector128.Create(transform.a31);
        var a32 = Vector128.Create(transform.a32);
        var zero = Vector128<int>.Zero;
        var maxPx = Vector128.Create(width - 1);
        var maxPy = Vector128.Create(height - 1);
        var widthVector = Vector128.Create(width);

        ref var luminanceRef = ref MemoryMarshal.GetReference(luminance);
        ref var moduleRef = ref MemoryMarshal.GetReference(modules);

        for (var row = 0; row < rows; row++)
        {
            var gridY = row + 0.5f;
            var rowBase = row * columns;
            var rowX = Vector128.Create(transform.a21 * gridY);
            var rowY = Vector128.Create(transform.a22 * gridY);

            var column = 0;
            for (; column + 8 <= columns; column += 8)
            {
                var columnVector = Vector128.Create((float)column);
                var gridXLo = laneOffsetsLo + columnVector;
                var gridXHi = laneOffsetsHi + columnVector;
                var xLo = a11 * gridXLo + rowX + a31;
                var yLo = a12 * gridXLo + rowY + a32;
                var xHi = a11 * gridXHi + rowX + a31;
                var yHi = a12 * gridXHi + rowY + a32;

                // ConvertToInt32 truncates toward zero like the scalar cast: the pixel
                // containing the point, not the nearest one.
                var indexLo = Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(yLo), maxPy), zero) * widthVector
                    + Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(xLo), maxPx), zero);
                var indexHi = Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(yHi), maxPy), zero) * widthVector
                    + Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(xHi), maxPx), zero);

                ref var destination = ref Unsafe.Add(ref moduleRef, rowBase + column);
                Unsafe.Add(ref destination, 0) = Unsafe.Add(ref luminanceRef, indexLo.GetElement(0)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 1) = Unsafe.Add(ref luminanceRef, indexLo.GetElement(1)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 2) = Unsafe.Add(ref luminanceRef, indexLo.GetElement(2)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 3) = Unsafe.Add(ref luminanceRef, indexLo.GetElement(3)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 4) = Unsafe.Add(ref luminanceRef, indexHi.GetElement(0)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 5) = Unsafe.Add(ref luminanceRef, indexHi.GetElement(1)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 6) = Unsafe.Add(ref luminanceRef, indexHi.GetElement(2)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 7) = Unsafe.Add(ref luminanceRef, indexHi.GetElement(3)) < threshold ? (byte)1 : (byte)0;
            }

            while (column < columns)
            {
                var start = Math.Min(column, columns - 4);
                var gridX = laneOffsetsLo + Vector128.Create((float)start);
                var x = a11 * gridX + rowX + a31;
                var y = a12 * gridX + rowY + a32;

                var index = Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(y), maxPy), zero) * widthVector
                    + Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(x), maxPx), zero);

                ref var destination = ref Unsafe.Add(ref moduleRef, rowBase + start);
                Unsafe.Add(ref destination, 0) = Unsafe.Add(ref luminanceRef, index.GetElement(0)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 1) = Unsafe.Add(ref luminanceRef, index.GetElement(1)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 2) = Unsafe.Add(ref luminanceRef, index.GetElement(2)) < threshold ? (byte)1 : (byte)0;
                Unsafe.Add(ref destination, 3) = Unsafe.Add(ref luminanceRef, index.GetElement(3)) < threshold ? (byte)1 : (byte)0;
                column = start + 4;
            }
        }
    }
#endif

    private static bool SymbolFitsImage(in PerspectiveTransform transform, int symbolWidth, int symbolHeight, int width, int height, float samplingSlack)
    {
        for (var corner = 0; corner < 4; corner++)
        {
            var gridX = (corner & 1) == 0 ? 0f : symbolWidth;
            var gridY = (corner & 2) == 0 ? 0f : symbolHeight;
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

    private static void Rotate(float x, float y, float cos, float sin, out float rx, out float ry)
    {
        rx = cos * x - sin * y;
        ry = sin * x + cos * y;
    }

    private static DecodeStatus Deeper(DecodeStatus current, DecodeStatus candidate)
        => Rank(candidate) > Rank(current) ? candidate : current;

    /// <summary>
    /// Ranks decode failures by how far the attempt progressed; keeps the deepest.
    /// Wrong-grid samples overwhelmingly die at format decoding, so anything past it almost certainly hit the real grid.
    /// </summary>
    private static void TrackBestFailure(DecodeStatus status, in RmQRCodeDecodeInfo attemptInfo, ref DecodeStatus bestStatus, ref RmQRCodeDecodeInfo bestInfo)
    {
        if (Rank(status) > Rank(bestStatus))
        {
            bestStatus = status;
            bestInfo = attemptInfo;
        }
    }

    private static int Rank(DecodeStatus s) => s switch
    {
        DecodeStatus.NotDetected => 0,
        DecodeStatus.InvalidMatrix => 1,
        DecodeStatus.FormatInformationInvalid => 1,
        // The symbol was read (format + RS) and only the caller's buffer is short:
        // this outranks every failure short of the content so an earlier same-finder RS failure
        // (the usual prelude to the perspective search) can never mask it.
        DecodeStatus.DestinationTooSmall => 3,
        // A verdict on the content comes after error correction too, so a wrong grid's
        // correction failure tried before the right one cannot mask it
        DecodeStatus.UnmappedCharacter or DecodeStatus.UnsupportedContent => 3,
        _ => 2, // got past format decoding
    };

    private static bool IsPlausibleRefinement(DecodeStatus status)
        => status is not DecodeStatus.NotDetected
            and not DecodeStatus.InvalidMatrix
            and not DecodeStatus.FormatInformationInvalid;

    /// <summary>
    /// Outcomes no further geometry around the SAME finder can change: success, and a caller destination too small for the payload (the symbol was already read through format decode and RS on every block, the same evidence a success rests on; the perspective search, the remaining frames of that finder and the inverted retry would only rediscover the same symbol at hundreds of times the cost).
    /// Other finder candidates of the same polarity are still tried, so a second symbol in the frame that does fit the destination is found regardless of candidate order; a fitting symbol of the OPPOSITE polarity next to a too-large one is the accepted trade-off of skipping the inverted retry.
    /// The status also outranks every failure short of the content in <see cref="Rank"/>, so it reaches the caller even when an earlier attempt around the same finder failed at RS.
    /// </summary>
    private static bool IsTerminal(DecodeStatus status)
        => status is DecodeStatus.Success or DecodeStatus.DestinationTooSmall;

    /// <summary>A result no other grid or pass for the same symbol improves on: read, too long for the destination, or a verdict on its content.</summary>
    private static bool IsSettled(DecodeStatus status)
        => IsTerminal(status) || RegionalRetry.IsContentVerdict(status);
}
