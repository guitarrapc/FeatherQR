using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using FeatherQR.Internals.ImageDecoders;
using static FeatherQR.Internals.ImageDecoders.AttemptStatus;

namespace FeatherQR.Internals.StandardQR;

/// <summary>
/// Decodes a QR code from a grayscale image: a rendered symbol, a scan or a photograph, at any rotation, mirrored, reflectance-reversed, under a lighting gradient across the symbol or in perspective.
/// </summary>
/// <remarks>
/// Pipeline, run in each pass until one reads the symbol: the global threshold, the inverted image, then the regional binarization, which a verdict on the content from either earlier pass also skips.
/// When no pass settles at full size, an image with grey levels is read again halved, and halved again, through the same passes (<see cref="ImageDecodePasses"/>).
/// Each grid is decoded through the matrix level as soon as it is sampled, then transposed unless that settles it. The first grid that settles ends the pass.
/// The list gives the stages in order with their main conditions. Each method states its own in full.
/// <code>
/// 1. Finder triple
/// 2. Corner from the triangle, and from that corner:
///    a. Low density, upright or at a right angle: a grid of module boundaries read off the timing patterns
///    b. Finder centres and module sizes
///    c. Finders' frame and dimension candidates, first the one the version information beside the far finders names
///    d. Per dimension: alignment search, then grids in turn: mesh (version 7+, half its interior nodes found)
///       when nothing anchored, four-point transform (alignment-anchored, else the parallelogram), coverage
///       re-read (grey levels), mesh when the search anchored, module-space re-read (a grey image in hand, the
///       timing patterns alternating), then, each with both re-reads, the parallelogram after an anchored grid
///       or, when nothing anchored, the frame grid where the frame foreshortens and the grid through the fourth
///       point from the finders' edges, and last the planes fitted to the finders' outlines, with the anchor
///       and without it. None at the runner-up.
/// 3. When that does not settle, a-d from each other corner whose timing patterns read
/// 4. When the selected triple does not settle and more than three candidates were found, the next two triples: each a-d once, from the first of its corners whose timing patterns read, or skipped when none does
/// </code>
/// </remarks>
internal static partial class QRImageDecoder
{
    /// <summary>
    /// Decodes a QR code from grayscale pixels.
    /// Reflectance-reversed codes (light modules on a dark background, common in dark-mode UIs) are handled by one inverted retry when the normal attempt fails. A symbol lit unevenly, which no one threshold splits, is retried on a regional binarization of each polarity when both fail.
    /// A symbol under noise or a texture finer than its modules is read at reduced scale when all of those fail.
    /// </summary>
    /// <param name="luminance">Grayscale pixels, row-major, width × height bytes.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="destination">Destination buffer for decoded characters.</param>
    /// <param name="charsWritten">Number of characters written.</param>
    /// <param name="info">Diagnostic information.</param>
    public static DecodeStatus DecodeLuminance(ReadOnlySpan<byte> luminance, int width, int height, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info)
    {
        var pass = new SymbolPass();
        return ImageDecodePasses.Decode<SymbolPass, QRCodeDecodeInfo>(ref pass, luminance, width, height, destination, out charsWritten, out info);
    }

    /// <summary>
    /// This decoder's pass through the shared image decode passes: the finder triple and the stages from it (<see cref="DecodeLuminanceCore(ReadOnlySpan{byte}, ReadOnlySpan{byte}, bool, ReadOnlySpan{int}, int, int, Span{char}, out int, out QRCodeDecodeInfo)"/>).
    /// No midpoint pass: the finder sweep reads the blurred finders that pass is for.
    /// </summary>
    private readonly struct SymbolPass : ISymbolPass<QRCodeDecodeInfo>
    {
        public bool HasMidpointPass => false;

        public bool HasReducedScaleSearch => true;

        public QRCodeDecodeInfo AtFullScale(in QRCodeDecodeInfo info, int scale) => info.WithCorners(SymbolGeometry.Scaled(info.Corners, scale));

        public QRCodeDecodeInfo NotDetected => NotDetectedInfo;

        public DecodeStatus DecodeGlobal(ReadOnlySpan<byte> luminance, ReadOnlySpan<int> histogram, int width, int height, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info, out bool noFinder, out byte threshold, out GreyLevels grey)
        {
            noFinder = false;
            threshold = 0;
            grey = default;
            return DecodeLuminanceCore(luminance, histogram, width, height, destination, out charsWritten, out info);
        }

        public DecodeStatus Decode(ReadOnlySpan<byte> luminance, ReadOnlySpan<byte> source, bool negative, ReadOnlySpan<int> histogram, int width, int height, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info)
            => DecodeLuminanceCore(luminance, source, negative, histogram, width, height, destination, out charsWritten, out info);

        public DecodeStatus DecodeAtMidpoint(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info)
        {
            charsWritten = 0;
            info = NotDetected;
            return DecodeStatus.NotDetected;
        }
    }

    internal static DecodeStatus DecodeLuminanceCore(ReadOnlySpan<byte> luminance, ReadOnlySpan<int> histogram, int width, int height, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info)
        => DecodeLuminanceCore(luminance, default, false, histogram, width, height, destination, out charsWritten, out info);

    /// <summary>The stages from the finder triple on <paramref name="luminance"/>. <paramref name="source"/> is the grey image it was binarized from, empty when it is the image itself, and <paramref name="negative"/> says it was binarized from that image's negative.</summary>
    internal static DecodeStatus DecodeLuminanceCore(ReadOnlySpan<byte> luminance, ReadOnlySpan<byte> source, bool negative, ReadOnlySpan<int> histogram, int width, int height, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info)
    {
        charsWritten = 0;

        var threshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out var grey);

        Span<FinderPattern> candidates = stackalloc FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        Span<FinderPattern> patterns = stackalloc FinderPattern[3];
        var found = FinderPatternFinder.TryFind(luminance, width, height, threshold, patterns, grey, candidates, out var candidateCount, out var everyRowScanned);
        if (!found && (!everyRowScanned || candidateCount < 2))
        {
            info = NotDetectedInfo;
            return DecodeStatus.NotDetected;
        }

        var image = new ImageView(luminance, source, negative, width, height, threshold, grey);
        var modules = new ModuleWorkspace();
        try
        {
            if (!found)
                return DecodeInferred(image, candidates.Slice(0, candidateCount), ref modules, destination, out charsWritten, out info);

            var corners = new ImageCorners();
            var status = DecodeTriple(ref corners, image, patterns, ref modules, destination, out charsWritten, out info);
            if (IsSettled(status))
                return status;
            if (candidateCount > 3)
            {
                status = DecodeAlternativeTriples(ref corners, image, candidates.Slice(0, candidateCount), patterns, ref modules, destination, status, ref charsWritten, ref info);
                if (IsSettled(status))
                    return status;
            }

            var result = NewResult();
            result.Main(status, charsWritten, info);

            // The strided scan's triple failed. Where the stride could not have confirmed its finders, or the list is crowded, the rows between are scanned and the completed list selected from again. The stages below are judged on how many rows confirmed each candidate, which only a list of every row's says.
            if (!everyRowScanned)
            {
                if (!FinderPatternFinder.IsUndersampled(patterns, height) && !IsCrowded(candidates.Slice(0, candidateCount)))
                {
                    // A triple whose timing patterns read from none of its corners can be the finders of two symbols that each lack one. The stride confirmed them, so the inferred stage takes the list as it is.
                    if (TryCornerByTimingPatterns(ref corners, image, patterns, out _, out _, out _))
                        return status;
                    var strideInferredStatus = DecodeInferred(image, candidates.Slice(0, candidateCount), ref modules, destination, out var strideInferredCharsWritten, out var strideInferredInfo);
                    result.Other(strideInferredStatus, strideInferredCharsWritten, strideInferredInfo);
                    return result.Report(out charsWritten, out info);
                }
                Span<FinderPattern> swept = stackalloc FinderPattern[3];
                var sweptFound = FinderPatternFinder.TryFindSkippedRows(luminance, width, height, threshold, grey, candidates, ref candidateCount, swept);
                if (sweptFound && !SameTriple(swept, patterns))
                {
                    var sweptStatus = DecodeTriple(ref corners, image, swept, ref modules, destination, out var sweptCharsWritten, out var sweptInfo);
                    if (result.Other(sweptStatus, sweptCharsWritten, sweptInfo))
                        return result.Report(out charsWritten, out info);
                    if (candidateCount > 3)
                    {
                        sweptStatus = DecodeAlternativeTriples(ref corners, image, candidates.Slice(0, candidateCount), swept, ref modules, destination, sweptStatus, ref sweptCharsWritten, ref sweptInfo);
                        if (result.Other(sweptStatus, sweptCharsWritten, sweptInfo))
                            return result.Report(out charsWritten, out info);
                    }
                    swept.CopyTo(patterns);
                }
            }

            var crowdedStatus = DecodeCrowded(ref corners, image, candidates.Slice(0, candidateCount), patterns, ref modules, destination, out var crowdedCharsWritten, out var crowdedInfo);
            if (result.Other(crowdedStatus, crowdedCharsWritten, crowdedInfo))
                return result.Report(out charsWritten, out info);

            var inferredStatus = DecodeInferred(image, candidates.Slice(0, candidateCount), ref modules, destination, out var inferredCharsWritten, out var inferredInfo);
            result.Other(inferredStatus, inferredCharsWritten, inferredInfo);
            return result.Report(out charsWritten, out info);
        }
        finally
        {
            modules.Dispose();
        }
    }

    /// <summary>Whether two triples hold the same three finders, each within a module of its match: a completed list selects the strided scan's triple again when it was right, averaged over more rows.</summary>
    private static bool SameTriple(ReadOnlySpan<FinderPattern> triple, ReadOnlySpan<FinderPattern> other)
    {
        foreach (var finder in triple)
        {
            var matched = false;
            foreach (var candidate in other)
            {
                if (Math.Abs(candidate.X - finder.X) <= finder.ModuleSize && Math.Abs(candidate.Y - finder.Y) <= finder.ModuleSize)
                {
                    matched = true;
                    break;
                }
            }
            if (!matched)
                return false;
        }
        return true;
    }

    /// <summary>
    /// The module buffers of one scan: one rented array holding three grids of the largest dimension reserved so far, so a scan has one buffer out at a time.
    /// </summary>
    /// <remarks>
    /// A dimension needs three grids at once: the first sampling, another grid compared against it, and a coverage re-read compared against either.
    /// The thread's slot of the array pool holds one array a size, and a second of the same size would come from the per-core stacks every thread shares, where a thread decoding beside this one can take it and the rent allocates.
    /// </remarks>
    internal struct ModuleWorkspace
    {
        private byte[]? _rented;

        /// <summary>Modules each grid has room for.</summary>
        private int _capacity;

        /// <summary>Makes room for three grids of <paramref name="dimension"/>, renting a larger array when the one held is too small. Any grid taken before is no longer valid then.</summary>
        public void Reserve(int dimension)
        {
            var length = dimension * dimension;
            if (length <= _capacity)
                return;
            Dispose();
            _rented = RentModules(3 * length);
            _capacity = length;
        }

        /// <summary>The first sampling of the dimension reserved last.</summary>
        public readonly Span<byte> First(int dimension) => Grid(0, dimension);

        /// <summary>Another grid of the same dimension, compared against <see cref="First"/>.</summary>
        public readonly Span<byte> Other(int dimension) => Grid(1, dimension);

        /// <summary>A re-read of the same dimension, by coverage or in module space, compared against <see cref="First"/> or <see cref="Other"/>.</summary>
        public readonly Span<byte> Coverage(int dimension) => Grid(2, dimension);

        private readonly Span<byte> Grid(int index, int dimension)
        {
            Debug.Assert(_rented is not null && dimension * dimension <= _capacity, "reserve the dimension first");
            return _rented.AsSpan(index * _capacity, dimension * dimension);
        }

        public void Dispose()
        {
            if (_rented is null)
                return;
            ReturnModules(_rented);
            _rented = null;
            _capacity = 0;
        }
    }

    /// <summary>The two steps a finder triple is tried with from one of its corners: whether that corner's timing patterns read, and the decode from it.</summary>
    internal interface ICornerAttempt
    {
        bool ReadsTimingPatterns(in ImageView image, in FinderPattern topLeft, in FinderPattern topRight, in FinderPattern bottomLeft);

        DecodeStatus Decode(in ImageView image, FinderPattern topLeft, FinderPattern topRight, FinderPattern bottomLeft, ref ModuleWorkspace modules, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info);
    }

    /// <summary>The corner steps on the image: <see cref="TimingPatternsRead"/> and <see cref="DecodeCorners"/>.</summary>
    private readonly struct ImageCorners : ICornerAttempt
    {
        public bool ReadsTimingPatterns(in ImageView image, in FinderPattern topLeft, in FinderPattern topRight, in FinderPattern bottomLeft)
            => TimingPatternsRead(image.Luminance, image.Width, image.Height, image.Threshold, image.Grey, topLeft, topRight, bottomLeft);

        public DecodeStatus Decode(in ImageView image, FinderPattern topLeft, FinderPattern topRight, FinderPattern bottomLeft, ref ModuleWorkspace modules, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info)
            => DecodeCorners(image, topLeft, topRight, bottomLeft, ref modules, destination, out charsWritten, out info);
    }

    /// <summary>
    /// How many other triples a failed decode verifies, best confirmed first.
    /// </summary>
    /// <remarks>
    /// When the stride pass selected the triple and returned, the list it hands back counts each finder on a third of its rows, once or twice, and the order is coarse. Past 20 % keystone the second triple can be the symbol's, and verifying more than two reads no more symbols.
    /// Sweeping the rest of the rows first would make the first triple enough, and would cost a failing image a sweep each attempt.
    /// </remarks>
    private const int MaxAlternativeTriples = 2;

    /// <summary>
    /// After the selected triple failed, the candidate list's other triples, best confirmed first (<see cref="FinderPatternFinder.AlternativeTriples"/>): each decoded only from a corner whose timing patterns read through its own frame (<see cref="TryCornerByTimingPatterns"/>). Otherwise <paramref name="status"/> and the selected triple's diagnostics stand.
    /// </summary>
    internal static DecodeStatus DecodeAlternativeTriples<TAttempt>(ref TAttempt attempt, in ImageView image, Span<FinderPattern> candidates, ReadOnlySpan<FinderPattern> selected, ref ModuleWorkspace modules, Span<char> destination, DecodeStatus status, ref int charsWritten, ref QRCodeDecodeInfo info)
        where TAttempt : struct, ICornerAttempt
    {
        var result = NewResult();
        result.Main(status, charsWritten, info);

        var alternatives = new FinderPatternFinder.AlternativeTriples(candidates, selected);
        Span<FinderPattern> triple = stackalloc FinderPattern[3];
        for (var verified = 0; verified < MaxAlternativeTriples && alternatives.TryNext(triple); verified++)
        {
            if (!TryCornerByTimingPatterns(ref attempt, image, triple, out var topLeft, out var topRight, out var bottomLeft))
                continue;

            var alternativeStatus = attempt.Decode(image, topLeft, topRight, bottomLeft, ref modules, destination, out var alternativeCharsWritten, out var alternativeInfo);
            if (result.Other(alternativeStatus, alternativeCharsWritten, alternativeInfo))
                break;
        }
        return result.Report(out charsWritten, out info);
    }

    /// <summary>
    /// Whether a triple's two timing patterns read through the frame of its own centres and sizes: the check a triple from <see cref="FinderPatternFinder.AlternativeTriples"/>, or another corner of a triple that failed, passes before it costs a decode.
    /// </summary>
    /// <remarks>
    /// A timing pattern runs between two real finders and nowhere else, and the frame follows perspective, so this holds past 40 % keystone where the triple's shape says little. There a real triple still reads its timing modules through its own frame, and a triple holding a false candidate reads them near half wrong, as texture does.
    /// The centres are not refined for it. A grid through them misses the timing modules by under a module.
    /// </remarks>
    internal static bool TimingPatternsRead(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, in FinderPattern topLeft, in FinderPattern topRight, in FinderPattern bottomLeft)
    {
        var moduleSizes = MeasureModuleSizes(luminance, width, height, threshold, grey, topLeft, topRight, bottomLeft);
        return moduleSizes.Mean >= 1f
            && MatchTimingDimension(luminance, width, height, threshold, grey, topLeft, topRight, bottomLeft, moduleSizes, FinderFrame.Create(topLeft, topRight, bottomLeft, moduleSizes)) != 0;
    }

    /// <summary>
    /// The corner of a triple its timing patterns name: the vertex the triangle's shape names first (<see cref="OrderFinderPatterns"/>), then the other two, each taken once the two timing patterns from it read through its own frame (<see cref="TimingPatternsRead"/>). False when none do.
    /// </summary>
    /// <remarks>
    /// A plane tilted toward the top-left finder draws that finder nearest and the corner without one farthest, and past about 43 % keystone a leg of the image triangle is longer than its hypotenuse, so the vertex opposite the longest side is another finder.
    /// Three centres cannot tell, since a projective map puts a triangle's right angle at any of its vertices.
    /// What only the true corner has is the two timing patterns running from it. From either other vertex one of the two lines crosses the data.
    /// </remarks>
    internal static bool TryCornerByTimingPatterns(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, ReadOnlySpan<FinderPattern> triple, out FinderPattern topLeft, out FinderPattern topRight, out FinderPattern bottomLeft)
    {
        var corners = new ImageCorners();
        return TryCornerByTimingPatterns(ref corners, new ImageView(luminance, width, height, threshold, grey), triple, out topLeft, out topRight, out bottomLeft);
    }

    private static bool TryCornerByTimingPatterns<TAttempt>(ref TAttempt attempt, in ImageView image, ReadOnlySpan<FinderPattern> triple, out FinderPattern topLeft, out FinderPattern topRight, out FinderPattern bottomLeft)
        where TAttempt : struct, ICornerAttempt
    {
        var shapeCorner = ShapeCorner(triple);
        for (var i = 0; i < 3; i++)
        {
            OrderAroundCorner(triple, (shapeCorner + i) % 3, out topLeft, out topRight, out bottomLeft);
            if (attempt.ReadsTimingPatterns(image, topLeft, topRight, bottomLeft))
                return true;
        }
        topLeft = topRight = bottomLeft = default;
        return false;
    }

    /// <summary>
    /// One finder triple, in any order: from the corner the triangle's shape names, and when that does not settle, from each other corner whose timing patterns read (<see cref="TryCornerByTimingPatterns"/>).
    /// A symbol decodes from its shape's corner at no extra cost. The other corners are paid for only by a triple that failed.
    /// </summary>
    internal static DecodeStatus DecodeTriple<TAttempt>(ref TAttempt attempt, in ImageView image, ReadOnlySpan<FinderPattern> patterns, ref ModuleWorkspace modules, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info)
        where TAttempt : struct, ICornerAttempt
    {
        var shapeCorner = ShapeCorner(patterns);
        OrderAroundCorner(patterns, shapeCorner, out var topLeft, out var topRight, out var bottomLeft);
        var status = attempt.Decode(image, topLeft, topRight, bottomLeft, ref modules, destination, out charsWritten, out info);
        if (IsSettled(status))
            return status;

        var result = NewResult();
        result.Main(status, charsWritten, info);

        for (var i = 1; i < 3; i++)
        {
            OrderAroundCorner(patterns, (shapeCorner + i) % 3, out topLeft, out topRight, out bottomLeft);
            if (!attempt.ReadsTimingPatterns(image, topLeft, topRight, bottomLeft))
                continue;

            var cornerStatus = attempt.Decode(image, topLeft, topRight, bottomLeft, ref modules, destination, out var cornerCharsWritten, out var cornerInfo);
            if (result.Other(cornerStatus, cornerCharsWritten, cornerInfo))
                break;
        }
        return result.Report(out charsWritten, out info);
    }

    /// <summary>A triple with its corners assigned, through the timing frame and then the grids its centres and sizes give.</summary>
    private static DecodeStatus DecodeCorners(in ImageView image, FinderPattern topLeft, FinderPattern topRight, FinderPattern bottomLeft, ref ModuleWorkspace modules, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info)
    {
        // Under about 1.5 px/module a crisp module is 1 or 2 px wide and a sample has an eighth of a pixel to spare, which no grid extrapolated from the finder centres keeps. The
        // timing patterns mark every module boundary between the finders. First, because where both grids read, this one's corners are the symbol's own edges and the other's are extrapolated to within a module, and two lines that do not read cost a few hundred pixels.
        // Tried ahead of the main path, the grids from the finders, which it does not replace unless it settles
        var frameStatus = DecodeThroughTimingFrame(image, topLeft, topRight, bottomLeft, ref modules, destination, out var frameCharsWritten, out var frameInfo);
        if (IsSettled(frameStatus))
        {
            charsWritten = frameCharsWritten;
            info = frameInfo;
            return frameStatus;
        }

        var moduleSizes = MeasureModuleSizes(image.Luminance, image.Width, image.Height, image.Threshold, default, topLeft, topRight, bottomLeft);
        RefineFinderCentres(image.Luminance, image.Width, image.Height, image.Grey, moduleSizes, ref topLeft, ref topRight, ref bottomLeft);
        // Measured again where there are grey levels: outward from the centres that moved, and to sub-pixel edges
        if (image.Grey.IsEnabled)
            moduleSizes = MeasureModuleSizes(image.Luminance, image.Width, image.Height, image.Threshold, image.Grey, topLeft, topRight, bottomLeft);
        var status = DecodeFromFinders(image, topLeft, topRight, bottomLeft, moduleSizes, ref modules, destination, out charsWritten, out info);
        if (IsSettled(status))
            return status;

        var result = NewResult();
        result.Other(frameStatus, frameCharsWritten, frameInfo);
        result.Main(status, charsWritten, info);
        return result.Report(out charsWritten, out info);
    }

    /// <summary>Each finder's centre moved to the centroid of its centre square's darkness. True when any moved.</summary>
    internal static bool RefineFinderCentres(ReadOnlySpan<byte> luminance, int width, int height, in GreyLevels grey, in FinderModuleSizes moduleSizes, ref FinderPattern topLeft, ref FinderPattern topRight, ref FinderPattern bottomLeft)
    {
        var uLength = Distance(topLeft, topRight);
        var vLength = Distance(topLeft, bottomLeft);
        if (!grey.IsEnabled || uLength < 1f || vLength < 1f)
            return false;
        var uX = (topRight.X - topLeft.X) / uLength;
        var uY = (topRight.Y - topLeft.Y) / uLength;
        var vX = (bottomLeft.X - topLeft.X) / vLength;
        var vY = (bottomLeft.Y - topLeft.Y) / vLength;
        var moved = RefineCentre(luminance, width, height, grey, uX, uY, vX, vY, moduleSizes.TopLeft, ref topLeft);
        moved |= RefineCentre(luminance, width, height, grey, uX, uY, vX, vY, moduleSizes.TopRight, ref topRight);
        moved |= RefineCentre(luminance, width, height, grey, uX, uY, vX, vY, moduleSizes.BottomLeft, ref bottomLeft);
        return moved;
    }

    private static bool RefineCentre(ReadOnlySpan<byte> luminance, int width, int height, in GreyLevels grey, float uX, float uY, float vX, float vY, float moduleSize, ref FinderPattern finder)
        // NaN when the measurement left the image
        => moduleSize >= 1f
            && ConcentricCentroid.TryRefine(luminance, width, height, grey, uX * moduleSize, uY * moduleSize, vX * moduleSize, vY * moduleSize, 2f, 9f, 0.75f, ref finder.X, ref finder.Y, out _);

    /// <summary>
    /// Module sizes measured along the finder-to-finder lines: each finder's own along each line it is on, since one size does not fit a keystoned symbol, and their mean.
    /// A finder's run widths along the image rows would not do: a turn lengthens them by up to √2.
    /// <see cref="SubPixelAlongU"/> and <see cref="SubPixelAlongV"/> are true when both sizes on that line came from sub-pixel edges, fine enough to measure how the size changes along it. When false, both came from whole-pixel runs.
    /// </summary>
    internal readonly record struct FinderModuleSizes(float TopLeftAlongU, float TopLeftAlongV, float TopRight, float BottomLeft, float Mean, bool SubPixelAlongU, bool SubPixelAlongV)
    {
        public float TopLeft => float.IsNaN(TopLeftAlongU) ? TopLeftAlongV : float.IsNaN(TopLeftAlongV) ? TopLeftAlongU : (TopLeftAlongU + TopLeftAlongV) / 2f;
    }

    /// <summary>The dimension candidates in turn: the version information read at the finders where it differs from the estimate, the estimate, the timing count, the version information of the sampled grid, the timing match, the runner-up.</summary>
    private static DecodeStatus DecodeFromFinders(in ImageView image, in FinderPattern topLeft, in FinderPattern topRight, in FinderPattern bottomLeft, in FinderModuleSizes moduleSizes, ref ModuleWorkspace modules, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info)
    {
        var timingCounted = false;
        var timingDimension = 0;
        var matchedDimension = -1;
        var moduleSize = moduleSizes.Mean;
        var frame = FinderFrame.Create(topLeft, topRight, bottomLeft, moduleSizes);
        var estimated = TryEstimateDimension(topLeft, topRight, bottomLeft, moduleSizes, frame, out var dimension, out var secondaryDimension);
        var result = NewResult();

        // What the symbol states comes before what the finders measure: a lens puts the estimate versions high, and the timing count and the timing match run along lines it bends.
        // Tried ahead of the main path, the estimate's grid, which it does not replace unless it settles.
        // Not where the symbol already states the estimate's dimension. The outlines would name it again, and every symbol the estimate reads would pay for them.
        var namedDimension = 0;
        if (moduleSize >= 1f && (!estimated || (dimension >= VersionInformationFromDimension && !StatesDimension(image, frame, dimension))))
        {
            namedDimension = ReadVersionAtFinders(image, topLeft, topRight, bottomLeft, moduleSizes);
            if (namedDimension == dimension)
                namedDimension = 0;
        }
        if (namedDimension != 0)
        {
            var namedStatus = SampleAndDecode(image, topLeft, topRight, bottomLeft, frame, namedDimension, moduleSize, ref modules, destination, out var namedCharsWritten, out var namedInfo, out _);
            if (result.Other(namedStatus, namedCharsWritten, namedInfo))
                return result.Report(out charsWritten, out info);
        }

        if (!estimated)
        {
            // Snapped finders at versions 39-40 measure a few percent small, which puts the estimate past the largest version. The count does not depend on it.
            if (moduleSize >= 1f)
            {
                timingDimension = CountTimingDimension(image.Luminance, image.Width, image.Height, image.Threshold, topLeft, topRight, bottomLeft, moduleSize);
                timingCounted = true;
                // A turned symbol under 3 px/module breaks the counted runs, but its timing modules still alternate on the right grid
                if (timingDimension == 0)
                    timingDimension = matchedDimension = MatchTimingDimension(image.Luminance, image.Width, image.Height, image.Threshold, image.Grey, topLeft, topRight, bottomLeft, moduleSizes, frame);
            }
            if (timingDimension == 0 || timingDimension == namedDimension)
                return result.Report(out charsWritten, out info);
            dimension = timingDimension;
        }

        var status = SampleAndDecode(image, topLeft, topRight, bottomLeft, frame, dimension, moduleSize, ref modules, destination, out charsWritten, out info, out var versionDimension);
        if (result.Main(status, charsWritten, info))
            return status;

        // The timing patterns count the modules the estimate only measures.
        // Counted only once the estimate has failed, so a successful decode never pays for it.
        if (!timingCounted)
            timingDimension = CountTimingDimension(image.Luminance, image.Width, image.Height, image.Threshold, topLeft, topRight, bottomLeft, moduleSize);
        if (timingDimension != 0 && timingDimension != dimension && timingDimension != namedDimension)
        {
            var timingStatus = SampleAndDecode(image, topLeft, topRight, bottomLeft, frame, timingDimension, moduleSize, ref modules, destination, out var timingCharsWritten, out var timingInfo, out _);
            if (result.Other(timingStatus, timingCharsWritten, timingInfo))
                return result.Report(out charsWritten, out info);
        }

        // Version 7+ states its own version next to two finders, where a slightly wrong dimension still samples it, so it overrules the estimate.
        if (versionDimension != 0 && versionDimension != dimension && versionDimension != timingDimension && versionDimension != matchedDimension && versionDimension != namedDimension)
        {
            var versionStatus = SampleAndDecode(image, topLeft, topRight, bottomLeft, frame, versionDimension, moduleSize, ref modules, destination, out var versionCharsWritten, out var versionInfo, out _);
            if (result.Other(versionStatus, versionCharsWritten, versionInfo))
                return result.Report(out charsWritten, out info);
        }

        // A module size measured a few percent off puts the estimate a version or two out, and blur or a turn breaks the counted runs, the timing modules still alternate on the grid of the right dimension
        if (matchedDimension < 0 && moduleSize >= 1f)
        {
            matchedDimension = MatchTimingDimension(image.Luminance, image.Width, image.Height, image.Threshold, image.Grey, topLeft, topRight, bottomLeft, moduleSizes, frame);
            if (matchedDimension != 0 && matchedDimension != dimension && matchedDimension != timingDimension && matchedDimension != versionDimension && matchedDimension != namedDimension)
            {
                var matchedStatus = SampleAndDecode(image, topLeft, topRight, bottomLeft, frame, matchedDimension, moduleSize, ref modules, destination, out var matchedCharsWritten, out var matchedInfo, out _);
                if (result.Other(matchedStatus, matchedCharsWritten, matchedInfo))
                    return result.Report(out charsWritten, out info);
            }
        }

        // The dimension estimate can land between two valid sizes (module-size measurement quantizes to pixels). When a plausible runner-up exists, one retry with it rescues estimates that snapped to the wrong version.
        // A guess gets no finder fallback: on a wrong size it only doubles the failure's cost.
        if (secondaryDimension != 0 && secondaryDimension != versionDimension && secondaryDimension != timingDimension && secondaryDimension != matchedDimension && secondaryDimension != namedDimension)
        {
            var secondaryStatus = SampleAndDecode(image, topLeft, topRight, bottomLeft, frame, secondaryDimension, moduleSize, ref modules, destination, out var secondaryCharsWritten, out var secondaryInfo, out _, finderFallback: false);
            if (result.Other(secondaryStatus, secondaryCharsWritten, secondaryInfo))
                return result.Report(out charsWritten, out info);
        }

        // All candidates failed: the estimate's diagnostics, the main path's
        return result.Report(out charsWritten, out info);
    }

    /// <summary>Boundaries per axis at version 40.</summary>
    private const int MaxBoundaries = 178;

    /// <summary>
    /// Decodes an upright or right-angle symbol drawn crisp at a low density through its module boundaries (<see cref="ModuleBoundaryReader"/>).
    /// </summary>
    private static DecodeStatus DecodeThroughTimingFrame(in ImageView image, in FinderPattern topLeft, in FinderPattern topRight, in FinderPattern bottomLeft, ref ModuleWorkspace modules, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info)
    {
        charsWritten = 0;
        info = NotDetectedInfo;
        Span<int> columns = stackalloc int[MaxBoundaries];
        Span<int> rows = stackalloc int[MaxBoundaries];
        if (topLeft.ModuleSize >= ModuleBoundaryReader.MaxModuleSize
            || !TryReadModuleBoundaries(image.Luminance, image.Width, image.Height, image.Threshold, topLeft, topRight, bottomLeft, columns, rows, out var frame, out var dimension))
        {
            return DecodeStatus.NotDetected;
        }

        modules.Reserve(dimension);
        var grid = modules.First(dimension);
        ModuleBoundaryReader.Sample(image.Luminance, image.Width, image.Height, image.Threshold, frame, columns, dimension, rows, dimension, grid);

        var status = DecodeWithMirrorRetry(grid, dimension, destination, out charsWritten, out info, out var transposed);
        if (status == DecodeStatus.Success)
        {
            frame.ToImage(columns[0], rows[0], out var x0, out var y0);
            frame.ToImage(columns[dimension], rows[0], out var x1, out var y1);
            frame.ToImage(columns[dimension], rows[dimension], out var x2, out var y2);
            frame.ToImage(columns[0], rows[dimension], out var x3, out var y3);
            var outline = PerspectiveTransform.QuadrilateralToQuadrilateral(0f, 0f, dimension, 0f, dimension, dimension, 0f, dimension, x0, y0, x1, y1, x2, y2, x3, y3);
            info = info.WithCorners(SymbolGeometry.FromTransform(outline, dimension, dimension, transposed));
        }
        return status;
    }

    /// <summary>
    /// The module boundaries of an axis-aligned symbol along both axes, <c>dimension + 1</c> each: module row 6 and module column 6 run from finder to finder, and the three finders' centre lines cover their own modules.
    /// False unless both timing lines read as timing patterns and count the same dimension.
    /// </summary>
    internal static bool TryReadModuleBoundaries(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in FinderPattern topLeft, in FinderPattern topRight, in FinderPattern bottomLeft, Span<int> columns, Span<int> rows, out AxisAlignedFrame frame, out int dimension)
    {
        frame = default;
        dimension = 0;
        if (!ModuleBoundaryReader.TryAxisDirection(topRight.X - topLeft.X, topRight.Y - topLeft.Y, out var uX, out var uY)
            || !ModuleBoundaryReader.TryAxisDirection(bottomLeft.X - topLeft.X, bottomLeft.Y - topLeft.Y, out var vX, out var vY)
            || uX * vX + uY * vY != 0)
        {
            return false;
        }

        var centerX = (int)topLeft.X;
        var centerY = (int)topLeft.Y;
        var farColumn = (int)((topRight.X - topLeft.X) * uX + (topRight.Y - topLeft.Y) * uY);
        var farRow = (int)((bottomLeft.X - topLeft.X) * vX + (bottomLeft.Y - topLeft.Y) * vY);

        // From the first finder's centre to the far one's outer edge is the finder distance plus 3.5 modules, by a module size that can measure 15 % small
        var slack = (int)(8f * topLeft.ModuleSize);
        if (!ModuleBoundaryReader.TryFinderEdgeRow(luminance, width, height, threshold, centerX, centerY, vX, vY, out var rowOffset)
            || !ModuleBoundaryReader.TryFinderEdgeRow(luminance, width, height, threshold, centerX, centerY, uX, uY, out var columnOffset)
            || !ModuleBoundaryReader.TryReadTimingLine(luminance, width, height, threshold, centerX + rowOffset * vX, centerY + rowOffset * vY, uX, uY, topLeft.ModuleSize, endsOnFinder: true, allowTriples: false, farColumn + slack, columns, out dimension)
            || !ModuleBoundaryReader.TryReadTimingLine(luminance, width, height, threshold, centerX + columnOffset * uX, centerY + columnOffset * uY, vX, vY, topLeft.ModuleSize, endsOnFinder: true, allowTriples: false, farRow + slack, rows, out var rowDimension)
            || dimension != rowDimension
            || dimension < 21 || dimension > 177 || (dimension - 17) % 4 != 0)
        {
            return false;
        }

        ModuleBoundaryReader.ReadFinderLine(luminance, width, height, threshold, centerX, centerY, uX, uY, 0, columns, 0);
        ModuleBoundaryReader.ReadFinderLine(luminance, width, height, threshold, centerX, centerY, uX, uY, farColumn, columns, dimension - 7);
        ModuleBoundaryReader.ReadFinderLine(luminance, width, height, threshold, centerX, centerY, vX, vY, 0, rows, 0);
        ModuleBoundaryReader.ReadFinderLine(luminance, width, height, threshold, centerX, centerY, vX, vY, farRow, rows, dimension - 7);

        // The centre squares' inner boundaries, from the modules in line with them
        ModuleBoundaryReader.ReadRunInteriors(luminance, width, height, threshold, centerX, centerY, uX, uY, vX, vY, columns, dimension, rows, dimension);
        ModuleBoundaryReader.ReadRunInteriors(luminance, width, height, threshold, centerX, centerY, vX, vY, uX, uY, rows, dimension, columns, dimension);
        if (!ModuleBoundaryReader.TryFillBoundaries(columns, dimension) || !ModuleBoundaryReader.TryFillBoundaries(rows, dimension))
            return false;

        frame = new AxisAlignedFrame(centerX, centerY, uX, uY, vX, vY);
        return true;
    }

    /// <summary>
    /// Samples and decodes a module grid, including the mirror retry and optional finder-only resampling.
    /// </summary>
    /// <remarks>
    /// On failure, versionDimension reports a different dimension named by the sampled version information, or zero.
    /// <paramref name="finderFallback"/> enables finder-only resampling and is disabled for speculative dimensions.
    /// </remarks>
    private static DecodeStatus SampleAndDecode(in ImageView image, in FinderPattern topLeft, in FinderPattern topRight, in FinderPattern bottomLeft, in FinderFrame frame, int dimension, float moduleSize, ref ModuleWorkspace modules, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info, out int versionDimension, bool finderFallback = true)
    {
        // Mirroring leaves finder geometry unchanged but transposes data. Retry every unsettled decode failure:
        // a transposed format can match the wrong BCH word and fail later as DataUncorrectable.
        // DestinationTooSmall is terminal because Reed-Solomon correction has already succeeded.
        // Finder fallback uses the parallelogram after an anchored-grid failure, or the finder frame when no fourth corner was anchored.
        versionDimension = 0;
        var transform = BuildGridTransform(image.Luminance, image.Width, image.Height, image.Threshold, image.Grey, frame, dimension, moduleSize, out var alignmentAnchored);

        modules.Reserve(dimension);
        var first = modules.First(dimension);

        var meshFirstStatus = DecodeStatus.NotDetected;
        var meshFirstCharsWritten = 0;
        QRCodeDecodeInfo meshFirstInfo = default;

        // Nothing anchored the fourth corner, so the transform below is the finders' parallelogram. The rest of the alignment lattice anchors the mesh.
        // Tried ahead of the main path, the grid through the transform, which it does not replace unless it settles
        if (!alignmentAnchored)
        {
            meshFirstStatus = DecodeThroughMesh(image, frame, dimension, first, destination, out meshFirstCharsWritten, out meshFirstInfo);
            if (IsSettled(meshFirstStatus))
            {
                charsWritten = meshFirstCharsWritten;
                info = meshFirstInfo;
                return meshFirstStatus;
            }
        }

        PerspectiveGridSampler.Sample(image.Luminance, image.Width, image.Height, image.Threshold, transform, dimension, first);

        // Read before the mirror retry transposes the matrix in place
        var namedDimension = ReadVersionDimension(first, dimension);
        var status = DecodeWithMirrorRetry(first, dimension, destination, out charsWritten, out info, out var transposed);
        if (status == DecodeStatus.Success)
            info = info.WithCorners(SymbolGeometry.FromTransform(transform, dimension, dimension, transposed));
        else if (namedDimension != dimension)
            versionDimension = namedDimension;
        if (IsSettled(status))
            return status;

        var result = NewResult();
        if (!alignmentAnchored)
            result.Other(meshFirstStatus, meshFirstCharsWritten, meshFirstInfo);
        result.Main(status, charsWritten, info);

        if (image.Grey.IsEnabled)
        {
            var coverageStatus = DecodeByCoverage(image, transform, dimension, first, modules.Coverage(dimension), destination, out var coverageCharsWritten, out var coverageInfo);
            if (result.Other(coverageStatus, coverageCharsWritten, coverageInfo))
                return result.Report(out charsWritten, out info);
        }

        // A plane in perspective is one projective map, which the anchored transform fits and the mesh only interpolates, so here the mesh comes second: for a surface bent off the plane, or a fourth anchor found on another mark.
        // In a grid of its own: the finder grids below compare against the first sampling.
        if (alignmentAnchored)
        {
            var meshStatus = DecodeThroughMesh(image, frame, dimension, modules.Other(dimension), destination, out var meshCharsWritten, out var meshInfo);
            if (result.Other(meshStatus, meshCharsWritten, meshInfo))
                return result.Report(out charsWritten, out info);
        }

        // The same grid read in module space, after the mesh: it samples every module nine times, and a symbol the mesh reads does not wait for it
        if (!image.GreySource.IsEmpty)
        {
            var moduleSpaceStatus = DecodeInModuleSpace(image, transform, dimension, first, modules.Coverage(dimension), destination, out var moduleSpaceCharsWritten, out var moduleSpaceInfo);
            if (result.Other(moduleSpaceStatus, moduleSpaceCharsWritten, moduleSpaceInfo))
                return result.Report(out charsWritten, out info);
        }

        if (!finderFallback)
            return result.Report(out charsWritten, out info);

        // The mirror retry above left the first sampling transposed
        TransposeInPlace(first, dimension);

        // Alignment fallback: the alignment centre is pixel-resolved, which at about 2 px/module is a third of a module, and the transform extrapolates that error across the bottom-right block as perspective.
        // The finders alone are exact for a flat symbol, and read by coverage too: an alignment pattern drawn as dots anchors a fifth of a module off. Failure-path cost only.
        if (alignmentAnchored)
        {
            DecodeOtherGrid(image, BuildParallelogramTransform(topLeft, topRight, bottomLeft, dimension), dimension, ref modules, coverage: image.Grey.IsEnabled, destination, ref result);
            if (!IsSettled(result.Status) && TryFitToFinderOutlines(image, frame, dimension, anchored: true, transform, out var outlineFit, out var anchoredFit))
            {
                DecodeOtherGrid(image, anchoredFit, dimension, ref modules, coverage: image.Grey.IsEnabled, destination, ref result);
                if (!IsSettled(result.Status))
                    DecodeOtherGrid(image, outlineFit, dimension, ref modules, coverage: image.Grey.IsEnabled, destination, ref result);
            }
            return result.Report(out charsWritten, out info);
        }

        // Nothing anchored the fourth corner, so the parallelogram above assumed the symbol flat. The finders' foreshortening places it under perspective.
        // Not first, because on a flat symbol under 2 px/module that foreshortening is measurement noise of a percent or two
        if (!frame.IsAffine)
            DecodeOtherGrid(image, frame.Transform(dimension), dimension, ref modules, coverage: image.Grey.IsEnabled, destination, ref result);
        // Last, the fourth point measured on the image, from the two finders' edges that face it: a symbol bent off the plane is neither flat nor in perspective
        if (!IsSettled(result.Status) && TryFourthPointFromEdges(image, topLeft, topRight, bottomLeft, moduleSize, out var edgeX, out var edgeY))
            DecodeOtherGrid(image, BuildEdgePointTransform(topLeft, topRight, bottomLeft, edgeX, edgeY, dimension), dimension, ref modules, coverage: image.Grey.IsEnabled, destination, ref result);
        if (!IsSettled(result.Status) && TryFitToFinderOutlines(image, frame, dimension, anchored: false, transform, out var fitted, out _))
            DecodeOtherGrid(image, fitted, dimension, ref modules, coverage: image.Grey.IsEnabled, destination, ref result);
        return result.Report(out charsWritten, out info);
    }

    /// <summary>
    /// The grid sampled through the mesh over the alignment lattice and decoded, or <see cref="DecodeStatus.NotDetected"/> without sampling when no mesh is kept (<see cref="TryBuildLatticeMesh"/>).
    /// </summary>
    private static DecodeStatus DecodeThroughMesh(in ImageView image, in FinderFrame frame, int dimension, Span<byte> modules, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info)
    {
        charsWritten = 0;
        info = NotDetectedInfo;
        Span<float> gridCoords = stackalloc float[MaxMeshNodes];
        Span<float> nodeXs = stackalloc float[MaxMeshNodes * MaxMeshNodes];
        Span<float> nodeYs = stackalloc float[MaxMeshNodes * MaxMeshNodes];
        if (!TryBuildLatticeMesh(image.Luminance, image.Width, image.Height, image.Threshold, image.Grey, frame, dimension, gridCoords, nodeXs, nodeYs, out var meshSize, out _, out _))
            return DecodeStatus.NotDetected;

        SampleGridPiecewise(image.Luminance, image.Width, image.Height, image.Threshold, gridCoords.Slice(0, meshSize), nodeXs, nodeYs, meshSize, dimension, modules);
        var status = DecodeWithMirrorRetry(modules, dimension, destination, out charsWritten, out info, out var transposed);
        // The corners follow the mesh, because the mesh is what decoded: the four-point transform had no fourth anchor, or did not read
        if (status == DecodeStatus.Success)
            info = info.WithCorners(SymbolGeometry.FromTransform(MeshAnchoredTransform(frame.TopLeft, frame.TopRight, frame.BottomLeft, gridCoords, nodeXs, nodeYs, meshSize, dimension), dimension, dimension, transposed));
        return status;
    }

    /// <summary>
    /// Another grid for the same symbol, as one more attempt of <paramref name="result"/>: sampled into <see cref="ModuleWorkspace.Other"/> and decoded unless it samples what <see cref="ModuleWorkspace.First"/> holds, then, while nothing settles, read again by coverage when asked and in module space when the reading has a grey image.
    /// </summary>
    private static void DecodeOtherGrid(in ImageView image, in PerspectiveTransform transform, int dimension, ref ModuleWorkspace modules, bool coverage, Span<char> destination, ref SearchResult<QRCodeDecodeInfo> result)
    {
        var other = modules.Other(dimension);
        PerspectiveGridSampler.Sample(image.Luminance, image.Width, image.Height, image.Threshold, transform, dimension, other);

        // The same modules decode the same way: an alignment centre found where the finders put it, or a frame that foreshortens by under a pixel, samples identically, as on any damaged symbol
        if (other.SequenceEqual(modules.First(dimension)))
            return;

        var otherStatus = DecodeWithMirrorRetry(other, dimension, destination, out var otherCharsWritten, out var otherInfo, out var transposed);
        if (otherStatus == DecodeStatus.Success)
            otherInfo = otherInfo.WithCorners(SymbolGeometry.FromTransform(transform, dimension, dimension, transposed));
        if (result.Other(otherStatus, otherCharsWritten, otherInfo))
            return;

        if (coverage)
        {
            var coverageStatus = DecodeByCoverage(image, transform, dimension, other, modules.Coverage(dimension), destination, out var coverageCharsWritten, out var coverageInfo);
            if (result.Other(coverageStatus, coverageCharsWritten, coverageInfo))
                return;
        }
        if (image.GreySource.IsEmpty)
            return;
        var moduleSpaceStatus = DecodeInModuleSpace(image, transform, dimension, other, modules.Coverage(dimension), destination, out var moduleSpaceCharsWritten, out var moduleSpaceInfo);
        result.Other(moduleSpaceStatus, moduleSpaceCharsWritten, moduleSpaceInfo);
    }

    /// <summary>The grid read again into <paramref name="modules"/> with each module's luminance interpolated at its centre and split halfway between the two levels.</summary>
    private static DecodeStatus DecodeByCoverage(in ImageView image, in PerspectiveTransform transform, int dimension, ReadOnlySpan<byte> transposedFailure, Span<byte> modules, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info)
    {
        SampleGridByCoverage(image.Luminance, image.Width, image.Height, image.Grey.Midpoint, transform, dimension, modules);
        // The grid that already failed decodes the same way again. The mirror retry left it transposed.
        // Reported as not tried, which it was: a failure made up for it would outrank the grid's own under the rule Micro QR and rMQR report by
        if (IsTransposeOf(modules, transposedFailure, dimension))
        {
            charsWritten = 0;
            info = NotDetectedInfo;
            return DecodeStatus.NotDetected;
        }
        var status = DecodeWithMirrorRetry(modules, dimension, destination, out charsWritten, out info, out var transposed);
        if (status == DecodeStatus.Success)
            info = info.WithCorners(SymbolGeometry.FromTransform(transform, dimension, dimension, transposed));
        return status;
    }

    private static bool IsTransposeOf(ReadOnlySpan<byte> modules, ReadOnlySpan<byte> transposed, int dimension)
    {
        for (var v = 0; v < dimension; v++)
        {
            for (var u = 0; u < dimension; u++)
            {
                if (modules[v * dimension + u] != transposed[u * dimension + v])
                    return false;
            }
        }
        return true;
    }

    internal static void SampleGridByCoverage(ReadOnlySpan<byte> luminance, int width, int height, float midpoint, in PerspectiveTransform transform, int dimension, Span<byte> modules)
    {
        for (var v = 0; v < dimension; v++)
        {
            for (var u = 0; u < dimension; u++)
            {
                transform.Transform(u + 0.5f, v + 0.5f, out var x, out var y);
                modules[v * dimension + u] = LuminanceSampler.Bilinear(luminance, width, height, x, y) < midpoint ? (byte)1 : (byte)0;
            }
        }
    }

    /// <summary>
    /// Decodes the sampled matrix, retrying once transposed (a mirrored capture) unless the first grid settled it.
    /// The grid as sampled is the main path, so on failure its diagnostics are reported, or the mirrored grid's result when that settles: its verdict on the content says more than the other's failure to read it.
    /// <paramref name="transposed"/> is true when the transposed grid read, because the transpose swaps the grid's axes relative to the symbol's and the corners of the read have to follow.
    /// </summary>
    /// <remarks>
    /// The grid is transposed in place because the matrix level reads the modules two at a time along the placement runs, which a transposed view would not keep contiguous.
    /// Micro QR reads its transpose through a view, since its matrix level reads every module through one, and rMQR samples again with the frame's axes swapped, since a transposed rMQR grid is no rMQR grid.
    /// Out of line: the JIT would inline it into <see cref="DecodeOtherGrid"/> alone, a failure path, where it only adds code.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static DecodeStatus DecodeWithMirrorRetry(Span<byte> modules, int dimension, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info, out bool transposed)
    {
        transposed = false;
        var status = QRMatrixDecoder.DecodeMatrix(modules, dimension, destination, out charsWritten, out info);
        if (IsSettled(status))
            return status;

        var result = NewResult();
        result.Main(status, charsWritten, info);

        TransposeInPlace(modules, dimension);
        var mirroredStatus = QRMatrixDecoder.DecodeMatrix(modules, dimension, destination, out var mirroredCharsWritten, out var mirroredInfo);
        result.Other(mirroredStatus, mirroredCharsWritten, mirroredInfo);
        transposed = mirroredStatus == DecodeStatus.Success;
        return result.Report(out charsWritten, out info);
    }

    /// <summary>
    /// Which failure this decoder reports: at each level of its search, the attempt of the main path, unless another settles.
    /// Micro QR and rMQR report the failure that went furthest instead.
    /// </summary>
    private const ReportRule Reporting = ReportRule.MainPath;

    private static QRCodeDecodeInfo NotDetectedInfo => new(DecodeStatus.NotDetected, 0, default, -1, 0);

    /// <summary>
    /// A result for one level of the search, under <see cref="Reporting"/>.
    /// A level takes one only once an attempt has not settled: either rule reports a settled attempt that follows unsettled ones as it is, and on the WebAssembly interpreter every result operation is a call.
    /// </summary>
    private static SearchResult<QRCodeDecodeInfo> NewResult() => new(Reporting, NotDetectedInfo);

    /// <summary>
    /// The module buffers this thread has out, and the most it has had out at once since a test last cleared <see cref="ModuleBuffersPeak"/>.
    /// No decode reads them, but tests that pin how many a scan holds do. One increment and one decrement a buffer.
    /// </summary>
    [ThreadStatic]
    internal static int ModuleBuffersOut, ModuleBuffersPeak;

    private static byte[] RentModules(int length)
    {
        if (++ModuleBuffersOut > ModuleBuffersPeak)
            ModuleBuffersPeak = ModuleBuffersOut;
        return ArrayPool<byte>.Shared.Rent(length);
    }

    private static void ReturnModules(byte[] rented)
    {
        ModuleBuffersOut--;
        ArrayPool<byte>.Shared.Return(rented, clearArray: false);
    }

    /// <summary>BCH(18,6) version information codewords of versions 7-40, indexed by version − 7.</summary>
    private static readonly uint[] VersionCodewords = CreateVersionCodewords();

    private static uint[] CreateVersionCodewords()
    {
        var codewords = new uint[34];
        for (var i = 0; i < codewords.Length; i++)
            codewords[i] = QRCodeConstants.GetVersionBits(i + 7);
        return codewords;
    }

    /// <summary>
    /// The dimension named by the version information sampled at <paramref name="dimension"/>, or 0 when the grid is below version 7 or neither copy is within 3 bits of a codeword.
    /// </summary>
    /// <remarks>
    /// Both copies sit within 7 modules of a finder center, and the grid is anchored on those centers, so a dimension a few modules off still samples them: 4 modules off at version 27 moves them a quarter of a module.
    /// The two copies are transposes of each other, so a mirrored image reads the same way.
    /// </remarks>
    internal static int ReadVersionDimension(ReadOnlySpan<byte> modules, int dimension)
    {
        if (dimension < 45)
            return 0;

        uint topRight = 0;
        uint bottomLeft = 0;
        for (var x = 0; x < 6; x++)
        {
            for (var y = 0; y < 3; y++)
            {
                var bit = 1u << (x * 3 + y);
                if (modules[x * dimension + dimension - 11 + y] != 0)
                    topRight |= bit;
                if (modules[(dimension - 11 + y) * dimension + x] != 0)
                    bottomLeft |= bit;
            }
        }

        return NamedDimension(topRight, bottomLeft);
    }

    /// <summary>The dimension of the version whose codeword either copy's 18 bits are within 3 of, the nearest of them, or 0.</summary>
    private static int NamedDimension(uint topRight, uint bottomLeft)
    {
        var bestVersion = 0;
        var bestDistance = 4; // up to 3 errors are corrected
        for (var i = 0; i < VersionCodewords.Length; i++)
        {
            var distance = Math.Min(PopCount(topRight ^ VersionCodewords[i]), PopCount(bottomLeft ^ VersionCodewords[i]));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestVersion = i + 7;
            }
        }

        return bestVersion == 0 ? 0 : 17 + 4 * bestVersion;

        static int PopCount(uint value)
        {
            // 32-bit SWAR popcount (netstandard2.0 has no BitOperations.PopCount)
            value -= (value >> 1) & 0x55555555u;
            value = (value & 0x33333333u) + ((value >> 2) & 0x33333333u);
            return (int)((((value + (value >> 4)) & 0x0F0F0F0Fu) * 0x01010101u) >> 24);
        }
    }

    /// <summary>
    /// Assigns the three finder centers to their corners: the two farthest apart span the diagonal (top-right / bottom-left), the remaining one is top-left, and the cross product resolves which diagonal end is which.
    /// Tilted strongly toward the top-left finder, a symbol's diagonal is not the longest side, and this names another corner (<see cref="TryCornerByTimingPatterns"/>).
    /// </summary>
    internal static void OrderFinderPatterns(ReadOnlySpan<FinderPattern> patterns, out FinderPattern topLeft, out FinderPattern topRight, out FinderPattern bottomLeft)
        => OrderAroundCorner(patterns, ShapeCorner(patterns), out topLeft, out topRight, out bottomLeft);

    /// <summary>The index of the vertex opposite the longest side of the three centres' triangle.</summary>
    private static int ShapeCorner(ReadOnlySpan<FinderPattern> patterns)
    {
        var d01 = DistanceSquared(patterns[0], patterns[1]);
        var d02 = DistanceSquared(patterns[0], patterns[2]);
        var d12 = DistanceSquared(patterns[1], patterns[2]);
        if (d01 >= d02 && d01 >= d12)
            return 2;
        return d02 >= d01 && d02 >= d12 ? 1 : 0;
    }

    /// <summary>The corners with <paramref name="corner"/> as the top-left. The cross product resolves which of the other two is top-right.</summary>
    private static void OrderAroundCorner(ReadOnlySpan<FinderPattern> patterns, int corner, out FinderPattern topLeft, out FinderPattern topRight, out FinderPattern bottomLeft)
    {
        topLeft = patterns[corner];
        var a = patterns[corner == 0 ? 1 : 0];
        var b = patterns[corner == 2 ? 1 : 2];

        // Image coordinates have y pointing down, so for the standard QR layout the cross product (a-topLeft) × (b-topLeft) is positive when a is top-right.
        var cross = (a.X - topLeft.X) * (b.Y - topLeft.Y) - (a.Y - topLeft.Y) * (b.X - topLeft.X);
        if (cross > 0)
        {
            topRight = a;
            bottomLeft = b;
        }
        else
        {
            topRight = b;
            bottomLeft = a;
        }
    }

    /// <summary>
    /// Estimates the matrix dimension from finder center distances and the module size, snapped to the nearest valid QR dimension (17 + 4·version).
    /// </summary>
    /// <remarks>
    /// The module size must NOT come from the horizontal-scan run widths: those are measured along image rows and grow by up to √2 under rotation (at 45° a row cuts the rotated rings diagonally).
    /// Instead it is measured along the actual finder-to-finder lines, which is rotation-invariant.
    /// </remarks>
    /// <param name="topLeft">The finder pattern at the top-left corner of the symbol.</param>
    /// <param name="topRight">The finder pattern at the top-right corner of the symbol.</param>
    /// <param name="bottomLeft">The finder pattern at the bottom-left corner of the symbol.</param>
    /// <param name="dimension">Nearest valid dimension to the estimate.</param>
    /// <param name="secondaryDimension">Second-nearest valid dimension when the estimate is also within one version step of it (retry candidate for estimates near a snap boundary), else 0.</param>
    /// <param name="moduleSizes">Module sizes in pixels, measured along the finder-to-finder lines (<see cref="MeasureModuleSizes"/>).</param>
    /// <param name="frame">The finders' frame built from those sizes: which lines it foreshortens.</param>
    private static bool TryEstimateDimension(in FinderPattern topLeft, in FinderPattern topRight, in FinderPattern bottomLeft, in FinderModuleSizes moduleSizes, in FinderFrame frame, out int dimension, out int secondaryDimension)
    {
        dimension = 0;
        secondaryDimension = 0;

        if (moduleSizes.Mean < 1f)
            return false; // below one pixel per module nothing can be sampled reliably

        var estimate = EstimateModules(topLeft, topRight, bottomLeft, moduleSizes, frame);

        // Snap to the nearest valid dimension, clamped to the version range so an estimate just past version 40 (or below 1) still snaps, but reject wild ones
        var versionExact = (estimate - 17f) / 4f;
        var version = Math.Min(40, Math.Max(1, (int)Math.Round(versionExact)));

        dimension = 17 + version * 4;
        if (Math.Abs(estimate - dimension) > 4f)
        {
            dimension = 0;
            return false;
        }

        // Runner-up on the other side of the estimate
        var secondaryVersion = versionExact > version ? version + 1 : version - 1;
        if (secondaryVersion >= 1 && secondaryVersion <= 40)
        {
            var candidate = 17 + secondaryVersion * 4;
            if (Math.Abs(estimate - candidate) <= 4f)
                secondaryDimension = candidate;
        }

        return true;
    }

    /// <summary>
    /// The symbol's width in modules as the finders measure it, unsnapped: the mean over the two finder lines of each line's module count plus the 7 modules outside the centres.
    /// </summary>
    /// <remarks>
    /// Where the frame is the parallelogram, the symbol has one module size and both lines divide by the mean of all four.
    /// Otherwise each line divides by the geometric mean of its own two ends, which is the count the frame itself implies:
    /// with the far end at weight w the near end measures w·L/(n − 7) and the far end L/(w·(n − 7)), so their product is (L/(n − 7))². Their plain mean is larger by (w + 1/w)/2 and puts the count short, 6 % at a size ratio of 2 and 13 % at 3, which at version 40 is five versions.
    /// A line the frame takes as flat beside one it foreshortens divides by its own sizes too, not by the mean of four: that mean holds the other line's far end, 40 % large on a symbol at 20 % keystone, which puts a version 40 two versions short.
    /// </remarks>
    internal static float EstimateModules(in FinderPattern topLeft, in FinderPattern topRight, in FinderPattern bottomLeft, in FinderModuleSizes moduleSizes, in FinderFrame frame)
    {
        var flat = frame.IsAffine;
        var widthModules = Distance(topLeft, topRight) / LineModuleSize(flat, moduleSizes.TopLeftAlongU, moduleSizes.TopRight, moduleSizes.Mean) + 7f;
        var heightModules = Distance(topLeft, bottomLeft) / LineModuleSize(flat, moduleSizes.TopLeftAlongV, moduleSizes.BottomLeft, moduleSizes.Mean) + 7f;
        return (widthModules + heightModules) / 2f;

        // A size is NaN where its run left the image, and a line missing one falls back on the mean of the rest
        static float LineModuleSize(bool flat, float near, float far, float mean)
            => flat || !(near > 0f && far > 0f) ? mean : (float)Math.Sqrt(near * far);
    }

    /// <summary>
    /// Measures the module size along the finder-to-finder axes, through each pattern's center toward (and away from) its neighbor, independent of rotation.
    /// </summary>
    internal static FinderModuleSizes MeasureModuleSizes(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, in FinderPattern topLeft, in FinderPattern topRight, in FinderPattern bottomLeft)
    {
        var alongU = MeasureLine(luminance, width, height, threshold, grey, topLeft, topRight);
        var alongV = MeasureLine(luminance, width, height, threshold, grey, topLeft, bottomLeft);

        var sum = 0f;
        var count = 0;
        Accumulate(alongU.Near, ref sum, ref count);
        Accumulate(alongU.Far, ref sum, ref count);
        Accumulate(alongV.Near, ref sum, ref count);
        Accumulate(alongV.Far, ref sum, ref count);

        // All measurements clipped (pattern at the image border): fall back to the horizontal-scan estimate, valid for near-axis-aligned inputs.
        var mean = count > 0 ? sum / count : (topLeft.ModuleSize + topRight.ModuleSize + bottomLeft.ModuleSize) / 3f;
        return new FinderModuleSizes(alongU.Near, alongV.Near, alongU.Far, alongV.Far, mean, alongU.SubPixel, alongV.SubPixel);

        static void Accumulate(float value, ref float sum, ref int count)
        {
            if (!float.IsNaN(value))
            {
                sum += value;
                count++;
            }
        }
    }

    /// <summary>
    /// The module sizes at both ends of the line from <paramref name="near"/> to <paramref name="far"/>, through each end's center along the line, measured by the finder axis estimator the single-finder symbologies share.
    /// Both come from sub-pixel edges when the image has grey levels and both walks finish, else both from whole-pixel runs: a line's change of size is compared at one resolution.
    /// A size is NaN when its run leaves the image.
    /// </summary>
    private static (float Near, float Far, bool SubPixel) MeasureLine(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, in FinderPattern near, in FinderPattern far)
    {
        var dx = far.X - near.X;
        var dy = far.Y - near.Y;
        var length = (float)Math.Sqrt(dx * dx + dy * dy);
        if (length < 1f)
            return (float.NaN, float.NaN, false);
        var dirX = dx / length;
        var dirY = dy / length;

        if (grey.IsEnabled)
        {
            // The dark ring ends 3.5 modules out, and the row scan's module size is never shorter than the module: a turn lengthens it
            var nearSize = FinderAxisEstimator.MeasureAxisAtLevel(luminance, width, height, grey.Midpoint, near.X, near.Y, dirX, dirY, 6f * near.ModuleSize);
            if (!float.IsNaN(nearSize))
            {
                var farSize = FinderAxisEstimator.MeasureAxisAtLevel(luminance, width, height, grey.Midpoint, far.X, far.Y, -dirX, -dirY, 6f * far.ModuleSize);
                if (!float.IsNaN(farSize))
                    return (nearSize, farSize, true);
            }
        }
        return (
            FinderAxisEstimator.MeasureAxis(luminance, width, height, threshold, near.X, near.Y, dirX, dirY),
            FinderAxisEstimator.MeasureAxis(luminance, width, height, threshold, far.X, far.Y, -dirX, -dirY),
            false);
    }

    /// <summary>
    /// The dimension counted on both timing patterns, or 0 when either line does not read as one or the two disagree.
    /// </summary>
    /// <remarks>
    /// Row 6 between the top-left and top-right finders, and column 6 between the top-left and bottom-left ones, alternate one module at a time, so the dark runs along either line number 2v + 3 (both finders' edge rows, and the 2v + 1 dark timing modules) whatever width each module happens to be drawn.
    /// The estimate divides a distance by a module size measured at the finders, and a render that snaps each module to whole pixels (2 or 3 px at 2.13 px/module) can give the finders a size 6 % off the symbol's, which is two versions at version 29.
    /// </remarks>
    internal static int CountTimingDimension(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in FinderPattern topLeft, in FinderPattern topRight, in FinderPattern bottomLeft, float moduleSize)
    {
        var alongRow = CountTimingLine(luminance, width, height, threshold, topLeft, topRight, bottomLeft, moduleSize);
        if (alongRow == 0)
            return 0;
        var alongColumn = CountTimingLine(luminance, width, height, threshold, topLeft, bottomLeft, topRight, moduleSize);
        return alongColumn == alongRow ? alongRow : 0;
    }

    /// <summary>
    /// The dimension within four versions of the finders' estimate whose grid puts both timing patterns on alternating modules, sampled through the finders' frame, or 0 when none leaves under an eighth of them wrong.
    /// </summary>
    /// <remarks>
    /// The estimate it searches around is <see cref="EstimateModules"/>, the frame's own count along each line: under strong keystone a count from the mean size is short by more than the four versions searched.
    /// A dimension is given up as soon as its wrong modules are as many as the best so far allows, which a false triple's timing lines, about half wrong, reach within a few dozen modules. The result is the full count's.
    /// </remarks>
    internal static int MatchTimingDimension(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, in FinderPattern topLeft, in FinderPattern topRight, in FinderPattern bottomLeft, in FinderModuleSizes moduleSizes, in FinderFrame frame)
    {
        var estimate = EstimateModules(topLeft, topRight, bottomLeft, moduleSizes, frame);
        var best = 0;
        var bestWrong = 1f / 8f;
        for (var version = 1; version <= 40; version++)
        {
            var dimension = 17 + 4 * version;
            if (Math.Abs(dimension - estimate) > 16f)
                continue;
            var transform = frame.Transform(dimension);
            var samples = 2f * (dimension - 16);
            var wrong = 0;
            for (var i = 8; i < dimension - 8; i++)
            {
                var dark = (i & 1) == 0;
                if (IsDarkAt(luminance, width, height, threshold, grey, transform, i + 0.5f, 6.5f) != dark)
                    wrong++;
                if (IsDarkAt(luminance, width, height, threshold, grey, transform, 6.5f, i + 0.5f) != dark)
                    wrong++;
                // Wrong modules only accumulate, so a share that is already too many cannot become the best
                if (wrong / samples >= bestWrong)
                    break;
            }
            var share = wrong / samples;
            if (share < bestWrong)
            {
                bestWrong = share;
                best = dimension;
            }
        }
        return best;
    }

    /// <summary>Whether the module at grid point (<paramref name="u"/>, <paramref name="v"/>) through <paramref name="transform"/> is dark: interpolated against the grey midpoint where the image has grey levels, else the pixel against the threshold.</summary>
    private static bool IsDarkAt(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, in PerspectiveTransform transform, float u, float v)
    {
        transform.Transform(u, v, out var x, out var y);
        if (grey.IsEnabled)
            return LuminanceSampler.Bilinear(luminance, width, height, x, y) < grey.Midpoint;
        var px = x < 0f ? 0 : x >= width ? width - 1 : (int)x;
        var py = y < 0f ? 0 : y >= height ? height - 1 : (int)y;
        return luminance[py * width + px] < threshold;
    }

    /// <summary>
    /// Counts the dark runs on the line through <paramref name="from"/>'s and <paramref name="to"/>'s centers, moved 3 modules toward <paramref name="side"/> onto the timing pattern (module row or column 6).
    /// 0 unless the line starts and ends on the finders' dark edge rows and every run between them is about one module long.
    /// </summary>
    private static int CountTimingLine(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in FinderPattern from, in FinderPattern to, in FinderPattern side, float moduleSize)
    {
        var sideX = side.X - from.X;
        var sideY = side.Y - from.Y;
        var sideLength = (float)Math.Sqrt(sideX * sideX + sideY * sideY);
        if (sideLength < 1f)
            return 0;
        var offsetX = sideX / sideLength * 3f * moduleSize;
        var offsetY = sideY / sideLength * 3f * moduleSize;

        var startX = from.X + offsetX;
        var startY = from.Y + offsetY;
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = (float)Math.Sqrt(dx * dx + dy * dy);
        if (length < 14f)
            return 0; // version 1 spans 14 modules between the centers, so a module is under a pixel

        // Four samples per module: enough to tell a one-module run from a merged one, and a one-pixel module crossed obliquely is still sampled
        var step = moduleSize / 4f;
        var steps = (int)(length / step);
        var stepX = dx / length * step;
        var stepY = dy / length * step;
        var minRun = 0.4f * moduleSize;
        var maxRun = 1.6f * moduleSize;

        var darkRuns = 0;
        var runStart = 0;
        var previousDark = true;
        for (var i = 0; i <= steps; i++)
        {
            var x = (int)(startX + i * stepX);
            var y = (int)(startY + i * stepY);
            if (x < 0 || x >= width || y < 0 || y >= height)
                return 0;

            var dark = luminance[y * width + x] < threshold;
            if (i == 0)
            {
                if (!dark)
                    return 0; // must start on the finder's edge row
                darkRuns = 1;
                continue;
            }
            if (dark == previousDark)
                continue;

            // A run just ended at step i. The first dark run is the finder's and is not checked.
            var runLength = (i - runStart) * step;
            if (runStart > 0 && (runLength < minRun || runLength > maxRun))
                return 0;
            if (dark)
                darkRuns++;
            runStart = i;
            previousDark = dark;
        }

        // Must end on the other finder's edge row
        if (!previousDark)
            return 0;

        var version = (darkRuns - 3) / 2;
        if ((darkRuns - 3) % 2 != 0 || version < 1 || version > 40)
            return 0;
        return 17 + 4 * version;
    }

    // Alignment lattice: at most 7 coordinates per axis (ISO/IEC 18004 Annex E)
    private const int MaxMeshNodes = 7;

    /// <summary>
    /// The geometry a mesh-sampled decode is reported with: the three finder centres plus the mesh's bottom-right lattice node as the fourth correspondence.
    /// The same construction as the alignment branch of <see cref="BuildGridTransform"/>, but anchored on a node the decode validated (the mesh sampled through it and read the symbol) rather than on an independent search whose window can catch a neighbouring pattern.
    /// </summary>
    private static PerspectiveTransform MeshAnchoredTransform(in FinderPattern topLeft, in FinderPattern topRight, in FinderPattern bottomLeft, ReadOnlySpan<float> gridCoords, ReadOnlySpan<float> nodeXs, ReadOnlySpan<float> nodeYs, int meshSize, int dimension)
    {
        // Finder centres sit at grid 3.5 / dimension − 3.5, and the last lattice coordinate is dimension − 6.5.
        // When that node was not found it is already placed, where the frame and its neighbours' offsets put it
        var last = meshSize - 1;
        var lastGrid = gridCoords[last];
        var lastNode = last * meshSize + last;

        return PerspectiveTransform.QuadrilateralToQuadrilateral(
            3.5f, 3.5f,
            dimension - 3.5f, 3.5f,
            lastGrid, lastGrid,
            3.5f, dimension - 3.5f,
            topLeft.X, topLeft.Y,
            topRight.X, topRight.Y,
            nodeXs[lastNode], nodeYs[lastNode],
            bottomLeft.X, bottomLeft.Y);
    }

    /// <summary>
    /// Reference sampler: every module center through the mesh, one module at a time.
    /// Not on the decode path. It is what the tiers behind <see cref="SampleGridPiecewise"/> are held to, pixel for pixel.
    /// </summary>
    /// <remarks>
    /// A coordinate is interpolated through three nodes of its lattice line, the two of the cell it falls in and the one beyond the nearer of them, in Newton's form: the first node's position, the slope to the second and the curvature through the third, each spacing of the lattice entering as its reciprocal. First down the lattice columns to the module's row, then along that row.
    /// Through two nodes a cell's sides are straight and its modules evenly spaced, which is neither a lens, whose lines bow, nor perspective, whose modules shrink along a line. Through three the line carries one curvature, measured at the nodes, and a module past the lattice (the borders, up to 6 modules) follows the outermost three.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when the mesh has fewer than three nodes a side.</exception>
    internal static void SampleGridPiecewiseScalar(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, ReadOnlySpan<float> gridCoords, ReadOnlySpan<float> nodeXs, ReadOnlySpan<float> nodeYs, int meshSize, int dimension, Span<byte> modules)
    {
        if (meshSize < 3)
            throw new ArgumentException($"A mesh has at least three nodes a side, got {meshSize}", nameof(meshSize));

        Span<float> rowXs = stackalloc float[MaxMeshNodes];
        Span<float> rowYs = stackalloc float[MaxMeshNodes];

        for (var v = 0; v < dimension; v++)
        {
            var gridY = v + 0.5f;
            var firstRow = FirstOfThree(gridCoords, meshSize, gridY);
            var rowToSecond = 1f / (gridCoords[firstRow + 1] - gridCoords[firstRow]);
            var rowToThird = 1f / (gridCoords[firstRow + 2] - gridCoords[firstRow + 1]);
            var rowAcross = 1f / (gridCoords[firstRow + 2] - gridCoords[firstRow]);

            // The lattice columns at this row, once
            for (var i = 0; i < meshSize; i++)
            {
                var node0 = firstRow * meshSize + i;
                var node1 = node0 + meshSize;
                var node2 = node1 + meshSize;
                var slopeX = SlopeOfThree(nodeXs[node0], nodeXs[node1], rowToSecond);
                rowXs[i] = nodeXs[node0] + (gridY - gridCoords[firstRow]) * (slopeX + (gridY - gridCoords[firstRow + 1]) * CurveOfThree(slopeX, nodeXs[node1], nodeXs[node2], rowToThird, rowAcross));
                var slopeY = SlopeOfThree(nodeYs[node0], nodeYs[node1], rowToSecond);
                rowYs[i] = nodeYs[node0] + (gridY - gridCoords[firstRow]) * (slopeY + (gridY - gridCoords[firstRow + 1]) * CurveOfThree(slopeY, nodeYs[node1], nodeYs[node2], rowToThird, rowAcross));
            }

            var rowBase = v * dimension;
            for (var u = 0; u < dimension; u++)
            {
                var gridX = u + 0.5f;
                var first = FirstOfThree(gridCoords, meshSize, gridX);
                var along = gridX - gridCoords[first];
                var past = gridX - gridCoords[first + 1];
                var toSecond = 1f / (gridCoords[first + 1] - gridCoords[first]);
                var toThird = 1f / (gridCoords[first + 2] - gridCoords[first + 1]);
                var across = 1f / (gridCoords[first + 2] - gridCoords[first]);
                var alongSlopeX = SlopeOfThree(rowXs[first], rowXs[first + 1], toSecond);
                var x = rowXs[first] + along * (alongSlopeX + past * CurveOfThree(alongSlopeX, rowXs[first + 1], rowXs[first + 2], toThird, across));
                var alongSlopeY = SlopeOfThree(rowYs[first], rowYs[first + 1], toSecond);
                var y = rowYs[first] + along * (alongSlopeY + past * CurveOfThree(alongSlopeY, rowYs[first + 1], rowYs[first + 2], toThird, across));

                // The same pixel on every runtime, whatever its cast makes of NaN and of coordinates past the int range
                var px = PixelIndex.Clamp(x, width);
                var py = PixelIndex.Clamp(y, height);

                modules[rowBase + u] = luminance[py * width + px] < threshold ? (byte)1 : (byte)0;
            }
        }
    }

    /// <summary>An alignment pattern near the prediction, its centre moved to the centroid of its centre module's darkness.</summary>
    private static bool TryFindAlignment(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, float expectedX, float expectedY, float moduleSize, (float X, float Y) axisX, (float X, float Y) axisY, float allowanceModules, out float centerX, out float centerY)
    {
        if (!AlignmentPatternFinder.TryFind(luminance, width, height, threshold, expectedX, expectedY, moduleSize, axisX, axisY, allowanceModules, out centerX, out centerY))
            return false;
        ConcentricCentroid.TryRefine(luminance, width, height, grey, axisX.X, axisX.Y, axisY.X, axisY.Y, 1f, 1f, 0.5f, ref centerX, ref centerY, out _);
        return true;
    }

    /// <summary>
    /// Builds the grid-to-pixel projective transform from the three finder centers plus a fourth correspondence point: the bottom-right alignment pattern when one exists and is found where the finders' frame predicts it, otherwise the parallelogram corner estimate (which degrades the transform to affine, exact for flat, on-axis captures).
    /// Grid coordinates put module (u, v)'s center at (u+0.5, v+0.5), so finder centers sit at 3.5 and the alignment center at dimension−6.5.
    /// </summary>
    internal static PerspectiveTransform BuildGridTransform(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, in FinderFrame frame, int dimension, float moduleSize, out bool alignmentAnchored)
    {
        alignmentAnchored = false;
        var topLeft = frame.TopLeft;
        var topRight = frame.TopRight;
        var bottomLeft = frame.BottomLeft;

        // Version 2+ has an alignment pattern centered 6.5 modules in from the bottom-right corner
        if (dimension >= 25)
        {
            float expectedX, expectedY;
            (float X, float Y) axisX, axisY;
            if (frame.IsAffine)
            {
                // Parallelogram estimate of the bottom-right corner (grid dimension−3.5), pulled toward the top-left by 3 modules on both axes
                var cornerX = topRight.X + bottomLeft.X - topLeft.X;
                var cornerY = topRight.Y + bottomLeft.Y - topLeft.Y;
                var correction = 1f - 3f / (dimension - 7);
                expectedX = topLeft.X + correction * (cornerX - topLeft.X);
                expectedY = topLeft.Y + correction * (cornerY - topLeft.Y);

                // Per-module grid axis vectors, for orientation-aware ring validation
                var span = dimension - 7;
                axisX = ((topRight.X - topLeft.X) / span, (topRight.Y - topLeft.Y) / span);
                axisY = ((bottomLeft.X - topLeft.X) / span, (bottomLeft.Y - topLeft.Y) / span);
            }
            else
            {
                // Where the foreshortening puts it, and the grid axes there, which a keystone turns and scales away from the ones at the finders
                frame.Map(dimension - 6.5f, dimension - 6.5f, dimension, out expectedX, out expectedY);
                frame.Axes(dimension - 6.5f, dimension - 6.5f, dimension, out axisX, out axisY);
            }

            // Expanding search window, since mild perspective shifts the true position
            // further from the parallelogram prediction as the tilt grows.
            foreach (var allowance in stackalloc float[] { 4f, 8f, 16f })
            {
                if (TryFindAlignment(luminance, width, height, threshold, grey, expectedX, expectedY, moduleSize, axisX, axisY, allowance, out var alignmentX, out var alignmentY))
                {
                    alignmentAnchored = true;
                    return PerspectiveTransform.QuadrilateralToQuadrilateral(
                        3.5f, 3.5f,
                        dimension - 3.5f, 3.5f,
                        dimension - 6.5f, dimension - 6.5f,
                        3.5f, dimension - 3.5f,
                        topLeft.X, topLeft.Y,
                        topRight.X, topRight.Y,
                        alignmentX, alignmentY,
                        bottomLeft.X, bottomLeft.Y);
                }
            }
        }

        // No alignment pattern (version 1) or not found: parallelogram corner
        return BuildParallelogramTransform(topLeft, topRight, bottomLeft, dimension);
    }

    /// <summary>The grid transform from the three finder centers alone, the bottom-right corner completing the parallelogram.</summary>
    internal static PerspectiveTransform BuildParallelogramTransform(in FinderPattern topLeft, in FinderPattern topRight, in FinderPattern bottomLeft, int dimension)
        => PerspectiveTransform.QuadrilateralToQuadrilateral(
            3.5f, 3.5f,
            dimension - 3.5f, 3.5f,
            dimension - 3.5f, dimension - 3.5f,
            3.5f, dimension - 3.5f,
            topLeft.X, topLeft.Y,
            topRight.X, topRight.Y,
            topRight.X + bottomLeft.X - topLeft.X, topRight.Y + bottomLeft.Y - topLeft.Y,
            bottomLeft.X, bottomLeft.Y);

    private static void TransposeInPlace(Span<byte> modules, int dimension)
    {
        for (var y = 0; y < dimension; y++)
        {
            for (var x = y + 1; x < dimension; x++)
            {
                var a = y * dimension + x;
                var b = x * dimension + y;
                (modules[a], modules[b]) = (modules[b], modules[a]);
            }
        }
    }

    private static float DistanceSquared(in FinderPattern a, in FinderPattern b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static float Distance(in FinderPattern a, in FinderPattern b)
        => (float)Math.Sqrt(DistanceSquared(a, b));
}
