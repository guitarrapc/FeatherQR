#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
#endif
using FeatherQR.Internals.ImageDecoders;
using static FeatherQR.Internals.ImageDecoders.AttemptStatus;

namespace FeatherQR.Internals.MicroQR;

/// <summary>
/// Decodes a Micro QR code from a grayscale image: clean, screen-rendered or scanned inputs, including a lighting gradient across the symbol.
/// </summary>
/// <remarks>
/// Pipeline, run in each pass until one reads the symbol: the global threshold, the inverted image, the regional binarization, then, for a polarity whose global pass found no finder, a sweep at the midpoint of its grey levels. A verdict on the content ends the sequence as a read does, except that the inverted pass still runs after one from the global threshold.
/// The global and regional passes scan with a row stride, then sweep every row unless that read a symbol or read one too long for the destination; the midpoint pass sweeps only. A scan decodes its first eight candidates, most confirmed first, less any inside a symbol that read but did not fit the destination (a finder-like pattern in that symbol's own data); a successful decode ends the scan and a read that did not fit ends the candidate, whose attempts see only its own results; otherwise each finder scan reports the result that went furthest (<see cref="CandidateScan"/>).
/// Each grid is decoded through the matrix level as soon as it is sampled, keeping only the corrections its structure earns (<see cref="MicroQRGridEvidence"/>), then transposed unless it read, a read that did not fit included.
/// The list gives the stages in order with their main conditions; each method states its own in full.
/// <code>
/// 1. Module sizes and centre of the candidate (dropped under one pixel per module)
/// 2. Axis-aligned path, per right-angle orientation:
///    a. Each size, M4 down to M1: affine grid; coverage re-read (grey levels, exact format word)
///    b. Timing frame: the grid fitted to the timing patterns
///    c. Low density (larger module size under 1.75 px): module boundaries read off the timing patterns
/// 3. Arbitrary orientation, per axis frame from an angular sweep and per right-angle orientation, within an attempt budget:
///    each size, M4 down to M1: affine grid; coverage re-read; once a grid gets past its format information,
///    the scale search, then the perspective search, each of their grids with its coverage re-read
/// </code>
/// A Micro QR symbol has a single finder pattern, so orientation cannot be derived from finder geometry the way three finders allow for Standard QR.
/// The detector therefore recovers the finder's local axes from angular dark-light-dark runs and searches the two projective coefficients that remain unknown.
/// This supports arbitrary rotation and mild perspective; strong perspective remains out of scope.
/// </remarks>
internal static partial class MicroQRImageDecoder
{
    /// <summary>
    /// Maximum matrix decode attempts in the arbitrary-orientation failure path for one finder candidate.
    /// A grid spends its two, as sampled and transposed, together, so the budget ends the path between grids.
    /// This bounds the multiplicative frame, orientation, size, scale and perspective searches while leaving enough for one complete orientation frame (all four axis assignments and four symbol sizes), and without making the result CPU-speed dependent.
    /// </summary>
    private const int MaxArbitraryOrientationDecodeAttempts = 10_000;

    /// <summary>
    /// Decodes a Micro QR code from grayscale pixels.
    /// Reflectance-reversed symbols (light modules on a dark background) are handled by one inverted retry when the normal attempt fails. A symbol lit unevenly, which no one threshold splits, is retried on a regional binarization of each polarity when both fail.
    /// </summary>
    public static DecodeStatus DecodeLuminance(ReadOnlySpan<byte> luminance, int width, int height, Span<char> destination, out int charsWritten, out MicroQRCodeDecodeInfo info)
    {
        var pass = new SymbolPass();
        return ImageDecodePasses.Decode<SymbolPass, MicroQRCodeDecodeInfo>(ref pass, luminance, width, height, destination, out charsWritten, out info);
    }

    /// <summary>
    /// This decoder's pass through the shared image decode passes: the strided scan and the sweep (<see cref="CandidateScan.Decode"/>), and at the midpoint the sweep alone.
    /// </summary>
    private readonly struct SymbolPass : ISymbolPass<MicroQRCodeDecodeInfo>
    {
        public bool HasMidpointPass => true;

        public MicroQRCodeDecodeInfo NotDetected => new CandidateDecoder().NotDetected;

        public DecodeStatus DecodeGlobal(ReadOnlySpan<byte> luminance, ReadOnlySpan<int> histogram, int width, int height, Span<char> destination, out int charsWritten, out MicroQRCodeDecodeInfo info, out bool noFinder, out byte threshold, out GreyLevels grey)
        {
            var decoder = new CandidateDecoder();
            return CandidateScan.Decode<CandidateDecoder, MicroQRCodeDecodeInfo>(ref decoder, luminance, histogram, width, height, destination, out charsWritten, out info, out noFinder, out threshold, out grey);
        }

        public DecodeStatus Decode(ReadOnlySpan<byte> luminance, ReadOnlySpan<int> histogram, int width, int height, Span<char> destination, out int charsWritten, out MicroQRCodeDecodeInfo info)
            => DecodeLuminanceCore(luminance, histogram, width, height, destination, out charsWritten, out info);

        public DecodeStatus DecodeAtMidpoint(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, Span<char> destination, out int charsWritten, out MicroQRCodeDecodeInfo info)
        {
            var decoder = new CandidateDecoder();
            return CandidateScan.Scan<CandidateDecoder, MicroQRCodeDecodeInfo>(ref decoder, new ImageView(luminance, width, height, threshold, grey), destination, out charsWritten, out info, fullSweep: true, skip: default, tried: default, out _, out _);
        }
    }

    /// <summary>This decoder's part of the shared candidate scan: a candidate's decode (<see cref="DecodeCandidate"/>) and the corners of its results.</summary>
    private readonly struct CandidateDecoder : ICandidateDecoder<MicroQRCodeDecodeInfo>
    {
        public MicroQRCodeDecodeInfo NotDetected => new(DecodeStatus.NotDetected, 0, default, -1, 0);

        /// <summary>The largest grid, M4's 17 × 17.</summary>
        public int ModuleBufferLength => 17 * 17;

        public SymbolCorners Corners(in MicroQRCodeDecodeInfo info) => info.Corners;

        public MicroQRCodeDecodeInfo WithoutCorners(in MicroQRCodeDecodeInfo info) => info.WithCorners(default);

        public DecodeStatus DecodeCandidate(in ImageView image, FinderPattern candidate, Span<byte> modules, Span<char> destination, out int charsWritten, out MicroQRCodeDecodeInfo info, ref SearchResult<MicroQRCodeDecodeInfo> result)
            => MicroQRImageDecoder.DecodeCandidate(image, candidate, modules, destination, out charsWritten, out info, ref result);
    }

    /// <summary>The strided finder scan and, unless it read a symbol or read one too long for the destination, the full sweep, at the global threshold of <paramref name="histogram"/> (<see cref="CandidateScan.Decode"/>).</summary>
    internal static DecodeStatus DecodeLuminanceCore(ReadOnlySpan<byte> luminance, ReadOnlySpan<int> histogram, int width, int height, Span<char> destination, out int charsWritten, out MicroQRCodeDecodeInfo info)
    {
        var decoder = new CandidateDecoder();
        return CandidateScan.Decode<CandidateDecoder, MicroQRCodeDecodeInfo>(ref decoder, luminance, histogram, width, height, destination, out charsWritten, out info, out _, out _, out _);
    }

    /// <summary>
    /// One finder candidate: the frames along the image axes at each right angle, then the arbitrary-orientation path.
    /// A terminal result ends the candidate, a read that does not fit the destination as well as one that does: its grids are
    /// tried in the same order whatever the destination, so the read that does not fit is the one a sized call returns, its content permitting (<see cref="AttemptStatus.Progress"/>), and a
    /// later grid could only read something else.
    /// </summary>
    private static DecodeStatus DecodeCandidate(in ImageView image, FinderPattern candidate, Span<byte> modules, Span<char> destination, out int charsWritten, out MicroQRCodeDecodeInfo info, ref SearchResult<MicroQRCodeDecodeInfo> best)
    {
        Span<int> boundaryColumns = stackalloc int[18];
        Span<int> boundaryRows = stackalloc int[18];
        FinderAxisEstimator.RefineModuleSize(image.Luminance, image.Width, image.Height, image.Threshold, candidate, out var horizontalModuleSize, out var verticalModuleSize);
        if (horizontalModuleSize < 1f || verticalModuleSize < 1f)
            return best.Report(out charsWritten, out info); // below one pixel per module nothing can be sampled reliably
        ConcentricCentroid.TryRefine(image.Luminance, image.Width, image.Height, image.Grey, horizontalModuleSize, 0f, 0f, verticalModuleSize, 2f, 9f, 0.75f, ref candidate.X, ref candidate.Y, out _);
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
                if (!SymbolFitsImage(originX, originY, uX, uY, vX, vY, size, image.Width, image.Height, samplingSlack))
                    continue;

                SampleGrid(image.Luminance, image.Width, image.Height, image.Threshold, originX, originY, uX, uY, vX, vY, size, modules);
                var grid = new BothWays<AffineGrid>(new AffineGrid(originX, originY, uX, uY, vX, vY), size);
                var status = GridRead.Decode<BothWays<AffineGrid>, MicroQRCodeDecodeInfo>(ref grid, image, modules, destination, out charsWritten, out info, ref best, out _, out _);
                if (IsTerminal(status))
                    return status;
            }

            // Timing frame: the finder's module size is extrapolated across the whole
            // symbol, and a render that snaps modules to whole pixels can give the
            // finder a size a few percent off. The timing patterns reach the far edge,
            // so they measure the symbol itself. Failure-path cost only.
            if (TryTimingFrame(image.Luminance, image.Width, image.Height, image.Threshold, candidate, uX, uY, vX, vY, out var timingOriginX, out var timingOriginY, out var tuX, out var tuY, out var tvX, out var tvY, out var timingSize)
                && SymbolFitsImage(timingOriginX, timingOriginY, tuX, tuY, tvX, tvY, timingSize, image.Width, image.Height, samplingSlack))
            {
                SampleGrid(image.Luminance, image.Width, image.Height, image.Threshold, timingOriginX, timingOriginY, tuX, tuY, tvX, tvY, timingSize, modules);
                var grid = new AffineGrid(timingOriginX, timingOriginY, tuX, tuY, tvX, tvY);
                var timingStatus = DecodeBothWays(modules, grid, timingSize, image, destination, out charsWritten, out info, ref best, out _, out _);
                if (IsTerminal(timingStatus))
                    return timingStatus;
            }

            // Module boundaries: under about 1.5 px/module a crisp module is 1 or 2 px wide
            // and a sample has an eighth of a pixel to spare, which neither fitted frame keeps.
            if (samplingSlack < ModuleBoundaryReader.MaxModuleSize
                && TryReadModuleBoundaries(image.Luminance, image.Width, image.Height, image.Threshold, candidate, Math.Sign(uX), Math.Sign(uY), Math.Sign(vX), Math.Sign(vY), samplingSlack, boundaryColumns, boundaryRows, out var frame, out var boundarySize))
            {
                ModuleBoundaryReader.Sample(image.Luminance, image.Width, image.Height, image.Threshold, frame, boundaryColumns, boundarySize, boundaryRows, boundarySize, modules);
                var quietZoneDark = ModuleBoundaryReader.CountQuietZoneDark(image.Luminance, image.Width, image.Height, image.Threshold, frame, boundaryColumns, boundarySize, boundaryRows, boundarySize);
                frame.ToImage(boundaryColumns[0], boundaryRows[0], out var boundaryOriginX, out var boundaryOriginY);
                var pitchU = (boundaryColumns[boundarySize] - boundaryColumns[0]) / (float)boundarySize;
                var pitchV = (boundaryRows[boundarySize] - boundaryRows[0]) / (float)boundarySize;

                var grid = new BoundaryGrid(quietZoneDark, boundaryOriginX, boundaryOriginY, pitchU * frame.UX, pitchU * frame.UY, pitchV * frame.VX, pitchV * frame.VY);
                var boundaryStatus = DecodeBothWays(modules, grid, boundarySize, image, destination, out charsWritten, out info, ref best, out _, out _);
                if (IsTerminal(boundaryStatus))
                    return boundaryStatus;
            }
        }

        // The fast path above covers the overwhelmingly common axis-aligned
        // case. On failure, recover the finder square's local axes by sweeping
        // directions through 90 degrees, then sample along those rotated axes.
        // A square finder repeats every 90 degrees; the four sign/axis
        // assignments below recover the symbol orientation.
        return TryDecodeArbitraryOrientation(image, candidate, modules, destination, out charsWritten, out info, ref best);
    }

    /// <summary>
    /// Recovers arbitrary image rotation from the single finder pattern.
    /// For a concentric square finder, a center ray crosses the shortest dark-light-dark span when it follows one of the square's local axes.
    /// An angular sweep therefore supplies the two grid-axis directions without the three finder centers available to Standard QR.
    /// </summary>
    private static DecodeStatus TryDecodeArbitraryOrientation(
        in ImageView image,
        in FinderPattern finder,
        Span<byte> modules,
        Span<char> destination,
        out int charsWritten,
        out MicroQRCodeDecodeInfo info,
        ref SearchResult<MicroQRCodeDecodeInfo> best)
    {
        Span<OrientationCandidate> orientations = stackalloc OrientationCandidate[FinderAxisEstimator.MaxOrientationCandidates];
        var orientationCount = FinderAxisEstimator.FindOrientationCandidates(image.Luminance, image.Width, image.Height, image.Threshold, finder, orientations);
        var attemptsRemaining = MaxArbitraryOrientationDecodeAttempts;

        for (var frameIndex = 0; frameIndex < orientationCount; frameIndex++)
        {
            ref readonly var frame = ref orientations[frameIndex];
            // The centre square's window has to lie along the symbol's axes, which only this frame knows
            var candidate = finder;
            ConcentricCentroid.TryRefine(image.Luminance, image.Width, image.Height, image.Grey, frame.UX, frame.UY, frame.VX, frame.VY, 2f, 9f, 0.75f, ref candidate.X, ref candidate.Y, out _);
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
                    if (attemptsRemaining < 2)
                        return best.Report(out charsWritten, out info);

                    if (!SymbolFitsImage(originX, originY, uX, uY, vX, vY, size, image.Width, image.Height, samplingSlack))
                        continue;

                    SampleGrid(image.Luminance, image.Width, image.Height, image.Threshold, originX, originY, uX, uY, vX, vY, size, modules);
                    var grid = new BothWays<AffineGrid>(new AffineGrid(originX, originY, uX, uY, vX, vY), size);
                    attemptsRemaining -= 2;
                    var status = GridRead.Decode<BothWays<AffineGrid>, MicroQRCodeDecodeInfo>(ref grid, image, modules, destination, out charsWritten, out info, ref best, out var pastFormat, out _);
                    if (IsTerminal(status))
                        return status;

                    // Scale and perspective searches multiply this affine attempt
                    // by hundreds. Enter them only after either polarity decoded
                    // valid format information, which a random grid does 14 to 38 %
                    // of the time by version: the gate thins the grids searched
                    // rather than ruling texture out.
                    if (!pastFormat)
                        continue;

                    var scaledStatus = TryDecodeScaleVariants(
                        image, candidate,
                        uX, uY, vX, vY, size, modules, destination,
                        out charsWritten, out var scaledInfo,
                        ref best, ref attemptsRemaining);
                    if (IsTerminal(scaledStatus))
                    {
                        info = scaledInfo;
                        return scaledStatus;
                    }

                    var projectiveStatus = TryDecodePerspectiveVariants(
                        image,
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
                        ref best,
                        ref attemptsRemaining);
                    if (IsTerminal(projectiveStatus))
                    {
                        info = projectiveInfo;
                        return projectiveStatus;
                    }
                }
            }
        }

        return best.Report(out charsWritten, out info);
    }

    /// <summary>
    /// Refines the two local module scales independently around the pixel-quantized finder-run estimate while keeping the finder center fixed.
    /// </summary>
    private static DecodeStatus TryDecodeScaleVariants(
        in ImageView image,
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
        ref SearchResult<MicroQRCodeDecodeInfo> best,
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
                        if (attemptsRemaining < 2)
                            return best.Report(out charsWritten, out info);

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
                        if (!SymbolFitsImage(originX, originY, scaledUX, scaledUY, scaledVX, scaledVY, size, image.Width, image.Height, samplingSlack))
                            continue;

                        SampleGrid(image.Luminance, image.Width, image.Height, image.Threshold, originX, originY, scaledUX, scaledUY, scaledVX, scaledVY, size, modules);
                        var grid = new BothWays<AffineGrid>(new AffineGrid(originX, originY, scaledUX, scaledUY, scaledVX, scaledVY), size);
                        attemptsRemaining -= 2;
                        var status = GridRead.Decode<BothWays<AffineGrid>, MicroQRCodeDecodeInfo>(ref grid, image, modules, destination, out charsWritten, out info, ref best, out _, out _);
                        if (IsTerminal(status))
                            return status;
                    }
                }
            }
        }

        return best.Report(out charsWritten, out info);
    }

    /// <summary>
    /// A single finder determines a homography's image point and local Jacobian, leaving only the two projective denominator coefficients unknown.
    /// Search a small grid of mild-perspective values for those two; matrix format and RS validation select the correct transform without image-specific heuristics.
    /// </summary>
    private static DecodeStatus TryDecodePerspectiveVariants(
        in ImageView image,
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
        ref SearchResult<MicroQRCodeDecodeInfo> best,
        ref int attemptsRemaining)
    {
        ReadOnlySpan<float> strengths = stackalloc float[] { -0.12f, -0.08f, -0.04f, -0.02f, 0f, 0.02f, 0.04f, 0.08f, 0.12f };
        for (var pyIndex = 0; pyIndex < strengths.Length; pyIndex++)
        {
            var perspectiveY = strengths[pyIndex] / size;
            for (var pxIndex = 0; pxIndex < strengths.Length; pxIndex++)
            {
                if (attemptsRemaining < 2)
                    return best.Report(out charsWritten, out info);

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
                if (!ProjectiveSymbolFitsImage(transform, size, image.Width, image.Height, samplingSlack))
                    continue;

                PerspectiveGridSampler.Sample(image.Luminance, image.Width, image.Height, image.Threshold, transform, size, modules);
                var grid = new BothWays<ProjectiveGrid>(new ProjectiveGrid(transform), size);
                attemptsRemaining -= 2;
                var status = GridRead.Decode<BothWays<ProjectiveGrid>, MicroQRCodeDecodeInfo>(ref grid, image, modules, destination, out charsWritten, out info, ref best, out _, out _);
                if (IsTerminal(status))
                    return status;
            }
        }

        return best.Report(out charsWritten, out info);
    }

    /// <summary>
    /// A size × size grid read as sampled and transposed (<see cref="DecodeBothWays"/>), for the shared grid read and its re-read by
    /// coverage (<see cref="GridRead"/>).
    /// </summary>
    /// <remarks>
    /// It is read again by coverage only when an orientation that got past its format information read that word exactly. A real
    /// symbol's format modules sit next to the finder, where the frame is best; texture reads a word within 3 bits about half the
    /// time and an exact one about 1 in 1,000.
    /// </remarks>
    internal struct BothWays<TGrid>(TGrid grid, int size) : IGridRead<MicroQRCodeDecodeInfo>
        where TGrid : struct, ICoverageGrid
    {
        private DecodeStatus _straight;
        private DecodeStatus _mirrored;

        public readonly int Columns => size;

        public readonly int Rows => size;

        public readonly void Map(float u, float v, out float x, out float y) => grid.Map(u, v, out x, out y);

        public DecodeStatus Decode(ReadOnlySpan<byte> modules, in ImageView image, Span<char> destination, out int charsWritten, out MicroQRCodeDecodeInfo info, ref SearchResult<MicroQRCodeDecodeInfo> result)
            => DecodeBothWays(modules, grid, size, image, destination, out charsWritten, out info, ref result, out _straight, out _mirrored);

        public readonly bool PastFormat => IsPastFormat(_straight) || IsPastFormat(_mirrored);

        public readonly bool MayReadByCoverage(ReadOnlySpan<byte> modules)
            => (IsPastFormat(_straight) && MicroQRMatrixDecoder.HasExactFormat(modules, new MatrixModules(size), size))
                || (IsPastFormat(_mirrored) && MicroQRMatrixDecoder.HasExactFormat(modules, new TransposedModules<MatrixModules>(new MatrixModules(size)), size));
    }

    /// <summary>
    /// A sampled grid read as sampled, then transposed for a mirrored capture (a front camera), whose finder is the same and whose data is transposed.
    /// </summary>
    /// <remarks>
    /// The transpose is a view over the same grid (<see cref="TransposedModules{TModules}"/>), since the matrix level reads every module through one.
    /// The other decoders mirror differently, each for a reason of its own: Standard QR transposes its grid in place, because its matrix level reads the modules two at a time along the placement runs, which a view would not keep contiguous; rMQR samples its grid again with the frame's axes swapped, because a transposed rMQR grid is no rMQR grid.
    /// </remarks>
    /// <returns>
    /// <see cref="DecodeStatus.Success"/> with the corners of the read that made it; the grid as sampled's <see cref="DecodeStatus.DestinationTooSmall"/>, which a sized call would have stopped at as a read; otherwise the transposed grid's status, both failures kept in <paramref name="best"/>.
    /// <paramref name="straight"/> and <paramref name="mirrored"/> are each grid's own status, the transposed one <see cref="DecodeStatus.NotDetected"/> when the grid as sampled was terminal.
    /// </returns>
    private static DecodeStatus DecodeBothWays<TGrid>(ReadOnlySpan<byte> modules, in TGrid grid, int size, in ImageView image, Span<char> destination, out int charsWritten, out MicroQRCodeDecodeInfo info, ref SearchResult<MicroQRCodeDecodeInfo> best, out DecodeStatus straight, out DecodeStatus mirrored)
        where TGrid : struct, ISampledGrid
    {
        // A read that does not fit keeps its corners too, for the scan to skip the candidates inside it; the scan reports it without them
        straight = grid.Decode(modules, new MatrixModules(size), size, image, destination, out charsWritten, out info);
        if (IsTerminal(straight))
        {
            info = info.WithCorners(grid.Corners(size, transposed: false));
            mirrored = DecodeStatus.NotDetected;
            if (straight != DecodeStatus.Success)
                best.Other(straight, 0, info);
            return straight;
        }
        best.Other(straight, 0, info);

        mirrored = grid.Decode(modules, new TransposedModules<MatrixModules>(new MatrixModules(size)), size, image, destination, out charsWritten, out info);
        if (IsTerminal(mirrored))
            info = info.WithCorners(grid.Corners(size, transposed: true));
        if (mirrored != DecodeStatus.Success)
            best.Other(mirrored, 0, info);
        return mirrored;
    }

    /// <summary>A sampled grid's matrix decode, with the quiet zone and the corners of the mapping it was sampled through.</summary>
    internal interface ISampledGrid
    {
        DecodeStatus Decode<TModules>(ReadOnlySpan<byte> modules, in TModules grid, int size, in ImageView image, Span<char> destination, out int charsWritten, out MicroQRCodeDecodeInfo info)
            where TModules : struct, IMicroQRModules;

        SymbolCorners Corners(int size, bool transposed);
    }

    /// <summary>A sampled grid that can be sampled again by coverage: grid coordinates to image coordinates.</summary>
    internal interface ICoverageGrid : ISampledGrid
    {
        void Map(float u, float v, out float x, out float y);
    }

    internal readonly struct AffineGrid(float originX, float originY, float uX, float uY, float vX, float vY) : ICoverageGrid
    {
        public void Map(float u, float v, out float x, out float y)
        {
            x = originX + u * uX + v * vX;
            y = originY + u * uY + v * vY;
        }

        public DecodeStatus Decode<TModules>(ReadOnlySpan<byte> modules, in TModules grid, int size, in ImageView image, Span<char> destination, out int charsWritten, out MicroQRCodeDecodeInfo info)
            where TModules : struct, IMicroQRModules
            => DecodeGrid(modules, grid, size, image, new AffineQuietZone(originX, originY, uX, uY, vX, vY), destination, out charsWritten, out info);

        public SymbolCorners Corners(int size, bool transposed) => SymbolGeometry.FromAffine(originX, originY, uX, uY, vX, vY, size, transposed);
    }

    private readonly struct ProjectiveGrid(in PerspectiveTransform transform) : ICoverageGrid
    {
        private readonly PerspectiveTransform _transform = transform;

        public void Map(float u, float v, out float x, out float y) => _transform.Transform(u, v, out x, out y);

        public DecodeStatus Decode<TModules>(ReadOnlySpan<byte> modules, in TModules grid, int size, in ImageView image, Span<char> destination, out int charsWritten, out MicroQRCodeDecodeInfo info)
            where TModules : struct, IMicroQRModules
            => DecodeGrid(modules, grid, size, image, new ProjectiveQuietZone(_transform), destination, out charsWritten, out info);

        public SymbolCorners Corners(int size, bool transposed) => SymbolGeometry.FromTransform(_transform, size, size, transposed);
    }

    /// <summary>A grid read off its module boundaries (<see cref="ModuleBoundaryReader"/>): its quiet zone counted along the boundary table, its corners those of the mean pitch from its first boundaries.</summary>
    private readonly struct BoundaryGrid(int quietZoneDark, float originX, float originY, float uX, float uY, float vX, float vY) : ISampledGrid
    {
        public DecodeStatus Decode<TModules>(ReadOnlySpan<byte> modules, in TModules grid, int size, in ImageView image, Span<char> destination, out int charsWritten, out MicroQRCodeDecodeInfo info)
            where TModules : struct, IMicroQRModules
            => DecodeGrid(modules, grid, size, image, new CountedQuietZone(quietZoneDark), destination, out charsWritten, out info);

        public SymbolCorners Corners(int size, bool transposed) => SymbolGeometry.FromAffine(originX, originY, uX, uY, vX, vY, size, transposed);
    }

    /// <summary>One grid's matrix decode: the corrections its read may use are what its structure earns (<see cref="MicroQRMatrixDecoder.DecodeSampledGrid"/>).</summary>
    private static DecodeStatus DecodeGrid<TModules, TQuietZone>(ReadOnlySpan<byte> modules, in TModules grid, int size, in ImageView image, in TQuietZone quietZone, Span<char> destination, out int charsWritten, out MicroQRCodeDecodeInfo info)
        where TModules : struct, IMicroQRModules
        where TQuietZone : struct, IMicroQRQuietZone
        => MicroQRMatrixDecoder.DecodeSampledGrid(modules, grid, size, image.Luminance, image.Width, image.Height, image.Threshold, quietZone, destination, out charsWritten, out info);

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
                var px = PixelIndex.Clamp(rowX + gridU * uX, width);
                var py = PixelIndex.Clamp(rowY + gridU * uY, height);

                modules[rowBase + u] = luminance[py * width + px] < threshold ? (byte)1 : (byte)0;
            }
        }
    }
}
