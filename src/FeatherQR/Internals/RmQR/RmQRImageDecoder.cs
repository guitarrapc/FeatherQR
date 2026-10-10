using System.Buffers;
#if NET8_0_OR_GREATER
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
#endif
using FeatherQR.Internals.ImageDecoders;
using static FeatherQR.Internals.ImageDecoders.AttemptStatus;

namespace FeatherQR.Internals.RmQR;

/// <summary>
/// Decodes an rMQR Code from a grayscale image: clean, screen-rendered or scanned inputs, including a lighting gradient across the symbol.
/// </summary>
/// <remarks>
/// Pipeline, run in each pass until one reads the symbol: the global threshold, the inverted image, the regional binarization, then, for a polarity whose global pass found no finder, a sweep at the midpoint of its grey levels. A verdict on the content ends the sequence as a read does, except that the inverted pass still runs after one from the global threshold.
/// The global and regional passes scan with a row stride, then sweep every row unless that read a symbol or read one too long for the destination; the midpoint pass sweeps only. A scan decodes its first eight candidates, most confirmed first, less any inside a symbol that read but did not fit the destination (a finder-like pattern in that symbol's own data), each within a budget of decodes; a successful decode ends the scan and a read that did not fit ends the candidate, whose frames see only its own results; otherwise each finder scan reports the result that went furthest (<see cref="CandidateScan"/>).
/// The list gives the stages in order with their main conditions; each method states its own in full.
/// <code>
/// 1. Frames: four right angles, each also with its axes swapped (mirror); first from the axis-aligned module sizes
///    (one pixel or more), then from the finder's own outline, shear included, each whose finder-side format copy
///    decodes having its perimeter traced, then from each axis pair of an angular sweep
/// 2. Per frame of the first and the last kind:
///    a. Low density (module under 1.75 px, along the image axes): module boundaries, whose count names the version
///    b. Finder-side format copy at the measured scale, then, under 6 px per module, at scales a few percent off
///       (an exact codeword there); a copy that reads names the version, so the width and height
///    c. Sub-finder near the corner that version predicts: the isotropic grid and the anisotropic grid
///    d. The perimeter traced from the frame, when its copy read an exact codeword
///    e. Once an anchored grid gets past its format information, the perspective search, each candidate gated by
///       the sub-finder-side format copy and the edge timing rows
///    f. The unrefined frame when no anchored grid got past its format information; the scales stop once one did
/// 3. Each grid: the matrix level, then, on an image with grey levels, a coverage re-read of a grid past its format
///    information that neither read nor read too long for the destination
/// </code>
/// </remarks>
internal static partial class RmQRImageDecoder
{
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
        var pass = new SymbolPass();
        return ImageDecodePasses.Decode<SymbolPass, RmQRCodeDecodeInfo>(ref pass, luminance, width, height, destination, out charsWritten, out info);
    }

    private static RmQRCodeDecodeInfo NotDetected() => new(DecodeStatus.NotDetected, default, default, 0);

    /// <summary>
    /// This decoder's pass through the shared image decode passes: the strided scan and the sweep (<see cref="CandidateScan.Decode"/>), and at the midpoint the sweep alone, its edges located at the midpoint too.
    /// </summary>
    private readonly struct SymbolPass : ISymbolPass<RmQRCodeDecodeInfo>
    {
        public bool HasMidpointPass => true;

        public bool HasReducedScaleSearch => false;

        public RmQRCodeDecodeInfo AtFullScale(in RmQRCodeDecodeInfo info, int scale) => info.WithCorners(SymbolGeometry.Scaled(info.Corners, scale));

        public RmQRCodeDecodeInfo NotDetected => RmQRImageDecoder.NotDetected();

        public DecodeStatus DecodeGlobal(ReadOnlySpan<byte> luminance, ReadOnlySpan<int> histogram, int width, int height, Span<char> destination, out int charsWritten, out RmQRCodeDecodeInfo info, out bool noFinder, out byte threshold, out GreyLevels grey)
        {
            var decoder = new CandidateDecoder();
            return CandidateScan.Decode<CandidateDecoder, RmQRCodeDecodeInfo>(ref decoder, luminance, histogram, width, height, destination, out charsWritten, out info, out noFinder, out threshold, out grey);
        }

        public DecodeStatus Decode(ReadOnlySpan<byte> luminance, ReadOnlySpan<int> histogram, int width, int height, Span<char> destination, out int charsWritten, out RmQRCodeDecodeInfo info)
            => DecodeLuminanceCore(luminance, histogram, width, height, destination, out charsWritten, out info);

        public DecodeStatus DecodeAtMidpoint(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, Span<char> destination, out int charsWritten, out RmQRCodeDecodeInfo info)
        {
            var decoder = new CandidateDecoder();
            return CandidateScan.Scan<CandidateDecoder, RmQRCodeDecodeInfo>(ref decoder, new ImageView(luminance, width, height, threshold, grey), destination, out charsWritten, out info, fullSweep: true, skip: default, tried: default, out _, out _);
        }
    }

    /// <summary>This decoder's part of the shared candidate scan: a candidate's decode (<see cref="DecodeCandidate"/>) and the corners of its results.</summary>
    private readonly struct CandidateDecoder : ICandidateDecoder<RmQRCodeDecodeInfo>
    {
        public RmQRCodeDecodeInfo NotDetected => RmQRImageDecoder.NotDetected();

        /// <summary>The largest symbol, R17x139, whose 2.3 KB the scan rents rather than take from the stack.</summary>
        public int ModuleBufferLength => MaxModules;

        public SymbolCorners Corners(in RmQRCodeDecodeInfo info) => info.Corners;

        public RmQRCodeDecodeInfo WithoutCorners(in RmQRCodeDecodeInfo info) => info.WithCorners(default);

        public DecodeStatus DecodeCandidate(in ImageView image, FinderPattern candidate, Span<byte> modules, Span<char> destination, out int charsWritten, out RmQRCodeDecodeInfo info, ref SearchResult<RmQRCodeDecodeInfo> result)
            => RmQRImageDecoder.DecodeCandidate(image, candidate, modules, destination, out charsWritten, out info, ref result);
    }

    /// <summary>The strided finder scan and, unless it read a symbol or read one too long for the destination, the full sweep, at the global threshold of <paramref name="histogram"/> (<see cref="CandidateScan.Decode"/>).</summary>
    internal static DecodeStatus DecodeLuminanceCore(ReadOnlySpan<byte> luminance, ReadOnlySpan<int> histogram, int width, int height, Span<char> destination, out int charsWritten, out RmQRCodeDecodeInfo info)
    {
        var decoder = new CandidateDecoder();
        return CandidateScan.Decode<CandidateDecoder, RmQRCodeDecodeInfo>(ref decoder, luminance, histogram, width, height, destination, out charsWritten, out info, out _, out _, out _);
    }

    /// <summary>
    /// One finder candidate within its budget of decodes: the axis-aligned frames, then those of the finder's outline, then those of each axis pair from the angular sweep.
    /// Ends at a read, or at a read that did not fit, since its symbol was read up to the caller's buffer and no other frame of this finder can change that; a sized call reads it, its content permitting (<see cref="AttemptStatus.Progress"/>).
    /// </summary>
    /// <remarks>
    /// A read that did not fit went through the format information and every Reed-Solomon block, the evidence a read rests on; the perspective search, this finder's other frames and the inverted pass would only find the same symbol again at hundreds of times the cost.
    /// The other candidates of the finder scan that made it are still tried, so a second symbol among them that does fit is found whatever their order; a fitting symbol that only the full sweep or the inverted pass would find is not, the trade-off of neither running after it (<see cref="CandidateScan.Decode"/>).
    /// It also ranks above every other failure (<see cref="AttemptStatus.Progress"/>), a verdict on the content included, so it reaches the caller even when an earlier attempt failed at Reed-Solomon or another symbol's content gave a verdict.
    /// </remarks>
    private static DecodeStatus DecodeCandidate(
        in ImageView image,
        FinderPattern finder,
        Span<byte> modules,
        Span<char> destination,
        out int charsWritten,
        out RmQRCodeDecodeInfo info,
        ref SearchResult<RmQRCodeDecodeInfo> best)
    {
        Span<OrientationCandidate> orientations = stackalloc OrientationCandidate[FinderAxisEstimator.MaxOrientationCandidates];
        var attemptsRemaining = MaxDecodeAttemptsPerCandidate;

        // Fast path: right-angle frames from the axis-aligned module sizes.
        FinderAxisEstimator.RefineModuleSize(image.Luminance, image.Width, image.Height, image.Threshold, finder, out var horizontalModuleSize, out var verticalModuleSize);
        if (horizontalModuleSize >= 1f && verticalModuleSize >= 1f)
        {
            ConcentricCentroid.TryRefine(image.Luminance, image.Width, image.Height, image.Grey, horizontalModuleSize, 0f, 0f, verticalModuleSize, 2f, 9f, 0.75f, ref finder.X, ref finder.Y, out _);
            var status = TryFrames(
                image, finder,
                horizontalModuleSize, 0f, 0f, verticalModuleSize,
                modules, destination, out charsWritten, out info,
                ref best, ref attemptsRemaining);
            if (IsTerminal(status))
                return status;
        }

        // Arbitrary rotation: recover the finder's local axes from the angular sweep.
        var orientationCount = FinderAxisEstimator.FindOrientationCandidates(image.Luminance, image.Width, image.Height, image.Threshold, finder, orientations);

        // The finder's own frame, shear included, before the sweep's square ones: under perspective the finder is not the square they assume, and a rotated one reads through its outline as soon as through them.
        // Seeded from a square frame, the first measurement's chords cross the ring at a slant; along its own axes they cross it square
        if (TryMeasureOutline(image.Luminance, image.Width, image.Height, image.EdgeLevel, finder, orientations.Slice(0, orientationCount), out var outline))
        {
            if (outline.TryRefine(image.Luminance, image.Width, image.Height, image.EdgeLevel, out var refined))
                outline = refined;
            var status = TryOutlineFrames(image, outline, modules, destination, out charsWritten, out info, ref best, ref attemptsRemaining);
            if (IsTerminal(status))
                return status;
        }

        for (var o = 0; o < orientationCount && attemptsRemaining > 0; o++)
        {
            ref readonly var frame = ref orientations[o];
            // The centre square's window has to lie along the symbol's axes, which only this frame knows
            var candidate = finder;
            ConcentricCentroid.TryRefine(image.Luminance, image.Width, image.Height, image.Grey, frame.UX, frame.UY, frame.VX, frame.VY, 2f, 9f, 0.75f, ref candidate.X, ref candidate.Y, out _);
            var status = TryFrames(
                image, candidate,
                frame.UX, frame.UY, frame.VX, frame.VY,
                modules, destination, out charsWritten, out info,
                ref best, ref attemptsRemaining);
            if (IsTerminal(status))
                return status;
        }

        charsWritten = 0;
        info = best.Info;
        return best.Status;
    }

    /// <summary>The finder's outline, seeded from each of the sweep's frames until one measures it.</summary>
    private static bool TryMeasureOutline(ReadOnlySpan<byte> luminance, int width, int height, float level, in FinderPattern finder, ReadOnlySpan<OrientationCandidate> orientations, out FinderOutline outline)
    {
        for (var o = 0; o < orientations.Length; o++)
        {
            ref readonly var frame = ref orientations[o];
            if (FinderOutline.TryMeasure(luminance, width, height, level, finder.X, finder.Y, frame.UX, frame.UY, frame.VX, frame.VY, out outline))
                return true;
        }
        outline = default;
        return false;
    }

    /// <summary>
    /// The eight frames of the finder's outline, made as <see cref="TryFrames"/> makes a frame's: each whose finder-side format copy decodes, as at the measured scale, has its perimeter traced.
    /// </summary>
    private static DecodeStatus TryOutlineFrames(
        in ImageView image,
        in FinderOutline outline,
        Span<byte> modules,
        Span<char> destination,
        out int charsWritten,
        out RmQRCodeDecodeInfo info,
        ref SearchResult<RmQRCodeDecodeInfo> best,
        ref int attemptsRemaining)
    {
        charsWritten = 0;
        info = best.Info;
        for (var orientation = 0; orientation < 4; orientation++)
        {
            for (var mirror = 0; mirror < 2; mirror++)
            {
                if (attemptsRemaining <= 0)
                    break;
                OutlineFrame(outline, orientation, mirror, out var uX, out var uY, out var vX, out var vY);
                var affine = PerspectiveTransform.FromLocalFrame(FinderCenter, FinderCenter, outline.CenterX, outline.CenterY, uX, uY, vX, vY, 0f, 0f);
                var raw = ReadFormatCopy(image.Luminance, image.Width, image.Height, image.Threshold, affine, subFinderSide: false, 0, 0);
                if (!RmQRFormatInformationDecoder.TryDecodeCopy(raw, subFinderSide: false, out var version, out _, out _))
                    continue;

                var status = TryTracedFrame(image, outline.CenterX, outline.CenterY, uX, uY, vX, vY, version, modules, destination, out charsWritten, out info, ref best, ref attemptsRemaining);
                if (IsTerminal(status))
                    return status;
            }
        }

        charsWritten = 0;
        info = best.Info;
        return best.Status;
    }

    /// <summary>One of the eight frames of an outline's axes, in <see cref="TryFrames"/>'s order.</summary>
    private static void OutlineFrame(in FinderOutline outline, int orientation, int mirror, out float uX, out float uY, out float vX, out float vY)
        => Frame(outline.UX, outline.UY, outline.VX, outline.VY, orientation, mirror, out uX, out uY, out vX, out vY);

    /// <summary>
    /// One of the eight frames an axis pair makes: a right angle turn (<paramref name="orientation"/> 0 to 3), then for <paramref name="mirror"/> 1 its axes swapped.
    /// </summary>
    /// <remarks>
    /// A mirrored capture keeps the finder and transposes the data. rMQR reads it by sampling again with the frame's axes swapped, where Standard QR transposes its sampled grid in place and Micro QR reads it through a transposed view: a transposed rMQR grid, 43 modules tall and 7 wide, is no rMQR grid.
    /// </remarks>
    private static void Frame(float uX, float uY, float vX, float vY, int orientation, int mirror, out float frameUX, out float frameUY, out float frameVX, out float frameVY)
    {
        var (aX, aY, bX, bY) = orientation switch
        {
            0 => (uX, uY, vX, vY),
            1 => (vX, vY, -uX, -uY),
            2 => (-uX, -uY, -vX, -vY),
            _ => (-vX, -vY, uX, uY),
        };
        (frameUX, frameUY, frameVX, frameVY) = mirror == 0 ? (aX, aY, bX, bY) : (bX, bY, aX, aY);
    }

    /// <summary>
    /// The perimeter traced from a frame at the finder, sampled and decoded.
    /// <see cref="DecodeStatus.NotDetected"/> when the budget is spent or the trace fails.
    /// </summary>
    private static DecodeStatus TryTracedFrame(
        in ImageView image,
        float centerX,
        float centerY,
        float uX,
        float uY,
        float vX,
        float vY,
        RmQRVersion version,
        Span<byte> modules,
        Span<char> destination,
        out int charsWritten,
        out RmQRCodeDecodeInfo info,
        ref SearchResult<RmQRCodeDecodeInfo> best,
        ref int attemptsRemaining)
    {
        charsWritten = 0;
        info = best.Info;
        if (attemptsRemaining <= 0)
            return DecodeStatus.NotDetected;
        if (!TryTracePerimeter(image.Luminance, image.Width, image.Height, image.EdgeLevel, centerX, centerY, uX, uY, vX, vY, version, out var transform))
            return DecodeStatus.NotDetected;
        var samplingSlack = Math.Max((float)Math.Sqrt(uX * uX + uY * uY), (float)Math.Sqrt(vX * vX + vY * vY));
        return Attempt(image, transform, RmQRConstants.GetWidth(version), RmQRConstants.GetHeight(version), samplingSlack, modules, destination, out charsWritten, out info, ref best, ref attemptsRemaining);
    }

    /// <summary>
    /// Tries the eight frames one axis pair generates: four right-angle rotations, each with and without the axes swapped (a mirrored capture keeps the finder geometry and transposes the grid).
    /// </summary>
    private static DecodeStatus TryFrames(
        in ImageView image,
        in FinderPattern candidate,
        float uX,
        float uY,
        float vX,
        float vY,
        Span<byte> modules,
        Span<char> destination,
        out int charsWritten,
        out RmQRCodeDecodeInfo info,
        ref SearchResult<RmQRCodeDecodeInfo> best,
        ref int attemptsRemaining)
    {
        for (var orientation = 0; orientation < 4; orientation++)
        {
            for (var mirror = 0; mirror < 2; mirror++)
            {
                if (attemptsRemaining <= 0)
                    break;

                Frame(uX, uY, vX, vY, orientation, mirror, out var colX, out var colY, out var rowX, out var rowY);
                var status = TryFrame(
                    image, candidate,
                    colX, colY, rowX, rowY,
                    modules, destination, out charsWritten, out info,
                    ref best, ref attemptsRemaining);
                if (IsTerminal(status))
                    return status;
            }
        }

        charsWritten = 0;
        info = best.Info;
        return best.Status;
    }

    /// <summary>
    /// One local frame (finder center at grid (3.5, 3.5), <c>u</c> = column axis, <c>v</c> = row axis in pixels per module): read the finder-side format copy to learn the version, anchor the far end on the sub-finder or trace the perimeter, then decode.
    /// </summary>
    private static DecodeStatus TryFrame(
        in ImageView image,
        in FinderPattern candidate,
        float uX,
        float uY,
        float vX,
        float vY,
        Span<byte> modules,
        Span<char> destination,
        out int charsWritten,
        out RmQRCodeDecodeInfo info,
        ref SearchResult<RmQRCodeDecodeInfo> best,
        ref int attemptsRemaining)
    {
        charsWritten = 0;

        // The finder-side format copy sits within 12 modules of the finder, so the local frame reads it before any refinement, as long as the finder's scale is close.
        // A render that snaps modules to whole pixels can give the finder a scale 3-6 % off, most of a pixel at column 11 below 2 px/module, so the scales nearest the measured one are tried in turn.
        // A corrected scale must read an exact codeword:
        // within 3 bits, a quarter of random reads match one of the 64 words, so nine tries on noise would nearly always "read" a version and pay for its searches.
        // Only while the larger of the finder's two axes measures under 6 px per module, a bound taken from measurement,the search buys fewer reads as the density rises and costs every other symbology's image its failure time.
        // A trade, not a free cut: renders above the bound that read only at a corrected scale are given up.
        var moduleLength = Math.Max((float)Math.Sqrt(uX * uX + uY * uY), (float)Math.Sqrt(vX * vX + vY * vY));
        var formatRead = false;

        // Module boundaries: under about 1.5 px/module a crisp module is 1 or 2 px wide and a sample has an eighth of a pixel to spare, which no frame scaled from the finder keeps.
        // First, while the attempt budget is whole: the searches below can spend all of it on such a symbol, and two timing lines that do not read cost a few dozen pixels.
        if (moduleLength < ModuleBoundaryReader.MaxModuleSize)
        {
            var boundaryStatus = TryBoundaryFrame(image, candidate, uX, uY, vX, vY, moduleLength, modules, destination, out charsWritten, out info, ref best, ref attemptsRemaining);
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
            var finderSideRaw = ReadFormatCopy(image.Luminance, image.Width, image.Height, image.Threshold, affine, subFinderSide: false, 0, 0);
            if (!RmQRFormatInformationDecoder.TryDecodeCopy(finderSideRaw, subFinderSide: false, out var version, out _, out var distance) || (scale != 1f && distance != 0))
                continue;

            formatRead = true;
            var status = TryScaledFrame(
                image, candidate,
                scale * uX, scale * uY, scale * vX, scale * vY, version, distance == 0, affine,
                modules, destination, out charsWritten, out info,
                ref best, ref attemptsRemaining, out var anchoredStatus);

            // A copy can read exactly at a scale a few percent off, and a frame built on it fails where the next scale reads; so the search goes on while frames fail.
            // Once frames anchored on the sub-finder read format information, a failure is the data's: on a damaged symbol every later scale only repeated them.
            if (IsTerminal(status))
                return status;
            if (IsPastFormat(anchoredStatus))
                break;
        }

        charsWritten = 0;
        info = best.Info;
        return formatRead ? best.Status : DecodeStatus.NotDetected;
    }

    /// <summary>
    /// Decodes an upright or right-angle symbol drawn crisp at a low density through its module boundaries (<see cref="ModuleBoundaryReader"/>): module row 0 and module column 0 run from the finder's edge rows to the symbol's far edges, over the alignment and corner patterns that sit on them.
    /// <see cref="DecodeStatus.NotDetected"/> unless both lines read and count a size some version has.
    /// </summary>
    private static DecodeStatus TryBoundaryFrame(
        in ImageView image,
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
        ref SearchResult<RmQRCodeDecodeInfo> best,
        ref int attemptsRemaining)
    {
        charsWritten = 0;
        info = best.Info;
        Span<int> columns = stackalloc int[MaxBoundaryColumns];
        Span<int> rows = stackalloc int[MaxBoundaryRows];
        if (attemptsRemaining <= 0
            || !TryReadModuleBoundaries(image.Luminance, image.Width, image.Height, image.Threshold, candidate, uX, uY, vX, vY, moduleLength, columns, rows, out var frame, out var symbolWidth, out var symbolHeight))
        {
            return DecodeStatus.NotDetected;
        }

        attemptsRemaining--;
        var grid = modules.Slice(0, symbolWidth * symbolHeight);
        ModuleBoundaryReader.Sample(image.Luminance, image.Width, image.Height, image.Threshold, frame, columns, symbolWidth, rows, symbolHeight, grid);
        var status = RmQRMatrixDecoder.DecodeMatrix(grid, symbolWidth, symbolHeight, destination, out charsWritten, out info);
        if (IsTerminal(status))
        {
            // A read that does not fit keeps its corners too, for the scan to skip the candidates inside it; the scan reports it without them
            frame.ToImage(columns[0], rows[0], out var x0, out var y0);
            frame.ToImage(columns[symbolWidth], rows[0], out var x1, out var y1);
            frame.ToImage(columns[symbolWidth], rows[symbolHeight], out var x2, out var y2);
            frame.ToImage(columns[0], rows[symbolHeight], out var x3, out var y3);
            var outline = PerspectiveTransform.QuadrilateralToQuadrilateral(0f, 0f, symbolWidth, 0f, symbolWidth, symbolHeight, 0f, symbolHeight, x0, y0, x1, y1, x2, y2, x3, y3);
            info = info.WithCorners(SymbolGeometry.FromTransform(outline, symbolWidth, symbolHeight, transposed: false));
            if (status == DecodeStatus.Success)
                return status;
        }

        best.Other(status, 0, info);
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
    /// The frame at a scale whose finder-side format copy read <paramref name="version"/>, an exact codeword when <paramref name="exactCopy"/>: anchors the far end on the sub-finder or traces the perimeter, then decodes.
    /// </summary>
    private static DecodeStatus TryScaledFrame(
        in ImageView image,
        in FinderPattern candidate,
        float uX,
        float uY,
        float vX,
        float vY,
        RmQRVersion version,
        bool exactCopy,
        in PerspectiveTransform affine,
        Span<byte> modules,
        Span<char> destination,
        out int charsWritten,
        out RmQRCodeDecodeInfo info,
        ref SearchResult<RmQRCodeDecodeInfo> best,
        ref int attemptsRemaining,
        out DecodeStatus anchoredStatus)
    {
        charsWritten = 0;
        var symbolWidth = RmQRConstants.GetWidth(version);
        var symbolHeight = RmQRConstants.GetHeight(version);
        var samplingSlack = Math.Max((float)Math.Sqrt(uX * uX + uY * uY), (float)Math.Sqrt(vX * vX + vY * vY));

        var frameStatus = DecodeStatus.NotDetected;
        anchoredStatus = DecodeStatus.NotDetected;
        // Predicted (affine) and observed offsets from the finder center to the sub-finder center.
        var dX = symbolWidth - 2.5f - FinderCenter;
        var dY = symbolHeight - 2.5f - FinderCenter;
        var observed = false;
        var observedX = 0f;
        var observedY = 0f;
        var subFinderFound = TryLocateSubFinder(image.Luminance, image.Width, image.Height, image.Threshold, candidate, uX, uY, vX, vY, symbolWidth, symbolHeight, out var subX, out var subY);
        if (subFinderFound)
        {
            ConcentricCentroid.TryRefine(image.Luminance, image.Width, image.Height, image.Grey, uX, uY, vX, vY, 1f, 1f, 0.5f, ref subX, ref subY, out _);
            var predictedX = dX * uX + dY * vX;
            var predictedY = dX * uY + dY * vY;
            observedX = subX - candidate.X;
            observedY = subY - candidate.Y;
            var predictedLength = (float)Math.Sqrt(predictedX * predictedX + predictedY * predictedY);
            var observedLength = (float)Math.Sqrt(observedX * observedX + observedY * observedY);
            if (predictedLength > 0f && observedLength > 0f)
            {
                observed = true;
                // (a) Rotation-corrected, isotropically rescaled frame: exact for a
                // rotated or uniformly mis-scaled affine capture.
                var scale = observedLength / predictedLength;
                var cos = (predictedX * observedX + predictedY * observedY) / (predictedLength * observedLength);
                var sin = (predictedX * observedY - predictedY * observedX) / (predictedLength * observedLength);
                Rotate(uX, uY, cos, sin, out var ruX, out var ruY);
                Rotate(vX, vY, cos, sin, out var rvX, out var rvY);
                var isotropic = PerspectiveTransform.FromLocalFrame(FinderCenter, FinderCenter, candidate.X, candidate.Y, scale * ruX, scale * ruY, scale * rvX, scale * rvY, 0f, 0f);
                var status = Attempt(image, isotropic, symbolWidth, symbolHeight, samplingSlack, modules, destination, out charsWritten, out info, ref best, ref attemptsRemaining);
                if (IsTerminal(status))
                    return status;
                frameStatus = Deeper(frameStatus, status);

                // (b) Anisotropic rescale without rotation. Non-square modules already read above, since the frame scales each axis on its own.
                // This grid reads crisp rows under 2 px: there the snapped rows put the sub-finder off its predicted row, and the turn (a) makes of that misses the far rows.
                var determinant = dX * dY * (uX * vY - uY * vX);
                if (Math.Abs(determinant) > 1e-6f)
                {
                    var a = (observedX * dY * vY - observedY * dY * vX) / determinant;
                    var b = (observedY * dX * uX - observedX * dX * uY) / determinant;
                    if (a > 0.7f && a < 1.4f && b > 0.7f && b < 1.4f)
                    {
                        var anisotropic = PerspectiveTransform.FromLocalFrame(FinderCenter, FinderCenter, candidate.X, candidate.Y, a * uX, a * uY, b * vX, b * vY, 0f, 0f);
                        status = Attempt(image, anisotropic, symbolWidth, symbolHeight, samplingSlack, modules, destination, out charsWritten, out info, ref best, ref attemptsRemaining);
                        if (IsTerminal(status))
                            return status;
                        frameStatus = Deeper(frameStatus, status);
                    }
                }
            }
        }

        // (c) The perimeter traced from this frame: it needs no sub-finder, and it measures the perspective the search below can only try, so it comes first.
        // Every frame traces its own, even where another traced from the same side: under noise the start decides the trace.
        // Only from an exact copy, as at a corrected scale: within 3 bits a quarter of random reads name a version, and each would start a trace on another symbology's image.
        // A traced grid that fails leaves the frame's status to the anchored ones: at low density it can get past the format information and would stop the scales before one whose anchored grid reads
        if (exactCopy)
        {
            var status = TryTracedFrame(image, candidate.X, candidate.Y, uX, uY, vX, vY, version, modules, destination, out charsWritten, out info, ref best, ref attemptsRemaining);
            if (IsTerminal(status))
                return status;
        }

        // (d) Perspective: only after an attempt got past format decoding (the grid is roughly right, RS still fails) and with the sub-finder observed. Search the two projective coefficients; for each, the sub-finder fixes the Jacobian column scale and the frame rotation exactly, and the sub-finder-side format copy must read back consistently before the full grid is sampled.
        if (observed && IsPastFormat(frameStatus))
        {
            var status = TryPerspectiveVariants(
                image, candidate,
                uX, uY, vX, vY, version, symbolWidth, symbolHeight, samplingSlack,
                observedX, observedY, dX, dY,
                modules, destination, out charsWritten, out info,
                ref best, ref attemptsRemaining);
            if (IsTerminal(status))
                return status;
        }

        anchoredStatus = frameStatus;

        // Fallback: the unrefined local frame (small symbols, or a sub-finder hidden by damage).
        // Skipped when a refined affine grid already read the format: the coarser frame cannot do better. Run at every scale, though: each is a different grid, and on a hidden sub-finder it is the frame that reads.
        if (!IsPastFormat(frameStatus))
        {
            var status = Attempt(image, affine, symbolWidth, symbolHeight, samplingSlack, modules, destination, out charsWritten, out info, ref best, ref attemptsRemaining);
            if (IsTerminal(status))
                return status;
        }

        charsWritten = 0;
        info = best.Info;
        return best.Status;
    }

    /// <summary>
    /// Bounded search over the projective denominator coefficients.
    /// For every pair, the finder→sub-finder correspondence determines the finder-local Jacobian scale and rotation in closed form (a homography maps the grid line through both centers to the image line through both centers; only the scale along it depends on the coefficients).
    /// </summary>
    private static DecodeStatus TryPerspectiveVariants(
        in ImageView image,
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
        ref SearchResult<RmQRCodeDecodeInfo> best,
        ref int attemptsRemaining)
    {
        // Relative foreshortening at the far edge, per axis. Fine steps along the long axis: a 1% error over 139 modules is already 0.7 module at the far end.
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

        // Row-axis shear: under a keystone the finder's column axis leans away from the perpendicular by atan(shrink / height), 9° for a 2% tilt on 17 rows.
        // The column direction is pinned by the sub-finder (the finder→sub-finder line is almost a symbol row); the row axis is the free direction.
        for (var shearStep = 0; shearStep <= 2 * MaxShearDegrees; shearStep++)
        {
            var shearDegrees = (shearStep + 1) / 2 * ((shearStep & 1) == 0 ? -1 : 1); // 0, +1, -1, +2, -2, …
            var shearRadians = shearDegrees * (Math.PI / 180d);
            var shearCos = (float)Math.Cos(shearRadians);
            var shearSin = (float)Math.Sin(shearRadians);
            // The row spacing was measured perpendicular to the column axis,
            // i.e. it is the leaning axis projected onto that normal: undo the projection.
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
                        info = best.Info;
                        return best.Status;
                    }

                    var perspectiveX = xStrengths[xIndex] / symbolWidth;
                    if (perspectiveX == 0f && perspectiveY == 0f && shearDegrees == 0)
                        continue; // the affine frame was already tried

                    // Q − P = (d0 / D) · J · (dX, dY): solve the Jacobian scale along the column axis (quadratic, the axes need not be perpendicular) and the frame rotation from the observed offset.
                    var d0 = perspectiveX * FinderCenter + perspectiveY * FinderCenter + 1f;
                    var d = perspectiveX * farX + perspectiveY * farY + 1f;
                    if (d <= 0.5f || d0 <= 0.5f)
                        continue;
                    // |a·dX·u + dY·sv|² = required²: every term uses the SHEARED row axis sv (its length is |v| / cos φ), the same vector the Jacobian below applies.
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
                    if (!SymbolFitsImage(transform, symbolWidth, symbolHeight, image.Width, image.Height, samplingSlack))
                        continue;

                    // Cheap gate: the far-end format copy must agree with the version.
                    var subFinderSideRaw = ReadFormatCopy(image.Luminance, image.Width, image.Height, image.Threshold, transform, subFinderSide: true, symbolWidth, symbolHeight);
                    if (!RmQRFormatInformationDecoder.TryDecodeCopy(subFinderSideRaw, subFinderSide: true, out var farVersion, out _, out var distance)
                        || farVersion != version || distance > 1)
                    {
                        continue;
                    }
                    if (!TimingRowsAgree(image.Luminance, image.Width, image.Height, image.Threshold, transform, version, symbolWidth, symbolHeight))
                        continue;

                    var status = Attempt(image, transform, symbolWidth, symbolHeight, samplingSlack, modules, destination, out charsWritten, out info, ref best, ref attemptsRemaining);
                    if (IsTerminal(status))
                        return status;
                }
            }
        }

        charsWritten = 0;
        info = best.Info;
        return best.Status;
    }

    /// <summary>
    /// Samples the full grid through the transform and decodes it (<see cref="GridRead"/>), unless the budget is spent or the grid does not fit the image; on an image with grey levels, a grid past its format information that neither read nor read too long for the destination is read again by coverage while the budget lasts.
    /// Each decode spends one of the budget, the re-read whether or not it changes a module.
    /// </summary>
    internal static DecodeStatus Attempt(
        in ImageView image,
        in PerspectiveTransform transform,
        int symbolWidth,
        int symbolHeight,
        float samplingSlack,
        Span<byte> modules,
        Span<char> destination,
        out int charsWritten,
        out RmQRCodeDecodeInfo info,
        ref SearchResult<RmQRCodeDecodeInfo> best,
        ref int attemptsRemaining)
    {
        charsWritten = 0;
        if (attemptsRemaining <= 0 || !SymbolFitsImage(transform, symbolWidth, symbolHeight, image.Width, image.Height, samplingSlack))
        {
            info = best.Info;
            return DecodeStatus.NotDetected;
        }

        attemptsRemaining--;
        var gridModules = modules.Slice(0, symbolWidth * symbolHeight);
        SampleGrid(image.Luminance, image.Width, image.Height, image.Threshold, transform, symbolWidth, symbolHeight, gridModules);
        var grid = new TransformGrid(transform, symbolWidth, symbolHeight, budgetLeft: attemptsRemaining > 0);
        var status = GridRead.Decode<TransformGrid, RmQRCodeDecodeInfo>(ref grid, image, gridModules, destination, out charsWritten, out info, ref best, out _, out var readByCoverage);
        if (readByCoverage)
            attemptsRemaining--;
        return status;
    }

    /// <summary>
    /// A grid sampled through a transform, decoded once: the frames carry a mirrored capture in their axes, so the transform is in
    /// symbol order and never transposed. Read again by coverage while the budget lasts (<see cref="GridRead"/>).
    /// </summary>
    private struct TransformGrid(in PerspectiveTransform transform, int width, int height, bool budgetLeft) : IGridRead<RmQRCodeDecodeInfo>
    {
        private readonly PerspectiveTransform _transform = transform;
        private DecodeStatus _status;

        public readonly int Columns => width;

        public readonly int Rows => height;

        public readonly void Map(float u, float v, out float x, out float y) => _transform.Transform(u, v, out x, out y);

        public DecodeStatus Decode(ReadOnlySpan<byte> modules, in ImageView image, Span<char> destination, out int charsWritten, out RmQRCodeDecodeInfo info, ref SearchResult<RmQRCodeDecodeInfo> result)
        {
            _status = RmQRMatrixDecoder.DecodeMatrix(modules, width, height, destination, out charsWritten, out info);
            if (IsTerminal(_status))
            {
                // A read that does not fit keeps its corners too, for the scan to skip the candidates inside it
                info = info.WithCorners(SymbolGeometry.FromTransform(_transform, width, height, transposed: false));
                if (_status == DecodeStatus.Success)
                    return _status;
            }
            result.Other(_status, 0, info);
            return _status;
        }

        public readonly bool PastFormat => IsPastFormat(_status);

        public readonly bool MayReadByCoverage(ReadOnlySpan<byte> modules) => budgetLeft;
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

        // Reject before searching when the whole template can only land outside the image.
        // Every sample sits at predicted + (offU + i)·u + (offV + j)·sv with |offU| ≤ radius/2 and |i| ≤ 2,
        // and the 12° lean bounds |svX| by |vX| + tan 12°·|vY|, so |vX| + |vY| over-estimates it.
        // A frame whose prediction misses the image entirely would otherwise score 0 at all 7,803 ring positions before returning false.
        // Most wrong frames do NOT predict off-image, so this is the cheaper and rarer of the two guards here — end to end it is worth about a sixth of the failure-path win and the row-wise early exit below is worth the rest; on a frame it does catch it replaces a ~400 µs search with a few ns.
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

        // A keystone leans the column axis (see TryPerspectiveVariants); the template is matched with a few leans so its corner samples stay on their modules.
        ReadOnlySpan<float> shearDegrees = stackalloc float[] { 0f, 12f, -12f };
        Span<ulong> survivors = stackalloc ulong[MaxSubFinderScreenRows];
        foreach (var degrees in shearDegrees)
        {
            var radians = degrees * (Math.PI / 180d);
            var leanCos = (float)Math.Cos(radians);
            Rotate(vX / leanCos, vY / leanCos, leanCos, (float)Math.Sin(radians), out var svX, out var svY);
            var screenTried = false;
            var screened = false;
            // Outward by rings (Chebyshev distance): the prediction is usually within a few modules, and the first perfect match on the innermost ring is the answer (the tie-break prefers the nearest anyway), so the search stops there.
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
                        // Two certain mismatches: the position scores under the floor, and a score under the floor never becomes the answer
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

                            // Once the rows still to come cannot lift the score to the acceptance floor, this position is decided: stop sampling it.
                            // A partial score may still be recorded as the best so far, which cannot change the outcome — acceptance needs SubFinderMinScore, and any position that reaches it outranks every partial one.
                            // Checked per row of five rather than per sample: the same early exit on the failing path (which bails after one row) without adding a branch to the 25-sample inner loop that the matching path runs in full.
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

        // Every coordinate either expression forms is at most this large; each rounds a handful of times, 2^-24 of it at most, and the margin is 2^-18 of it
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
                // Written as "not inside" so a NaN point is skipped too, never cast
                if (!(x - marginX > -1f && x + marginX < width && y - marginY > -1f && y + marginY < height))
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
    /// Reads one 18-bit format copy through the transform, each bit at the centre of its module: the block <see cref="RmQRConstants.GetFormatBlock"/> starts, column by column, then the three modules <see cref="RmQRConstants.GetFormatTail"/> gives. The matrix decoder's bit order.
    /// </summary>
    internal static int ReadFormatCopy(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in PerspectiveTransform transform, bool subFinderSide, int symbolWidth, int symbolHeight)
    {
        var raw = 0;
        RmQRConstants.GetFormatBlock(subFinderSide, symbolHeight, symbolWidth, out var rowBase, out var colBase);
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
            RmQRConstants.GetFormatTail(k, subFinderSide, symbolHeight, symbolWidth, out var row, out var col);
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
                var px = PixelIndex.Clamp(x, width);
                var py = PixelIndex.Clamp(y, height);
                modules[rowBase + col] = luminance[py * width + px] < threshold ? (byte)1 : (byte)0;
            }
        }
    }

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

    /// <summary>The status that got further (<see cref="AttemptStatus.Progress"/>), <paramref name="current"/> on a tie.</summary>
    private static DecodeStatus Deeper(DecodeStatus current, DecodeStatus candidate)
        => Progress(candidate) > Progress(current) ? candidate : current;
}
