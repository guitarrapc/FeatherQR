using System.Buffers;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
#endif


using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Internals.StandardQR;

/// <summary>
/// Decodes a QR code from a grayscale image: clean, well-lit, screen-rendered or scanned inputs, including arbitrary rotation, mirroring, reflectance reversal and mild perspective distortion (Tier 2).
/// </summary>
/// <remarks>
/// Pipeline:
/// <code>
/// 1. Global binarization threshold (Otsu's method over the luminance histogram)
/// 2. Finder pattern detection (1:1:3:1:1 scan + cross checks)
/// 3. Orientation from the three finder centers (rotation-invariant)
/// 4. Dimension estimate from center distances and module size
/// 5. Bottom-right alignment pattern search (version 2+; parallelogram estimate as fallback)
/// 6. Perspective grid sampling into a module matrix (4-point projective transform)
/// 7. Matrix decoding (format → unmask → deinterleave → Reed-Solomon → bitstream)
/// </code>
/// Out of scope (documented, by design): strong perspective where the four-point transform no longer models the surface, uneven lighting (global threshold only), blur, and multiple QR codes per image.
/// </remarks>
internal static class QRImageDecoder
{
    /// <summary>
    /// Decodes a QR code from grayscale pixels.
    /// Reflectance-reversed codes (light modules on a dark background, common in dark-mode UIs) are handled by one inverted retry when the normal attempt fails.
    /// </summary>
    /// <param name="luminance">Grayscale pixels, row-major, width × height bytes.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="destination">Destination buffer for decoded characters.</param>
    /// <param name="charsWritten">Number of characters written.</param>
    /// <param name="info">Diagnostic information.</param>
    public static DecodeStatus DecodeLuminance(ReadOnlySpan<byte> luminance, int width, int height, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info)
    {
        if (!ImageDimensions.TryGetPixelCount(width, height, out var pixelCount) || luminance.Length < pixelCount)
        {
            charsWritten = 0;
            info = new QRCodeDecodeInfo(DecodeStatus.NotDetected, 0, default, -1, 0);
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

            // Both polarities failed: report the original attempt's diagnostics
            return status;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }
    }

    private static DecodeStatus DecodeLuminanceCore(ReadOnlySpan<byte> luminance, ReadOnlySpan<int> histogram, int width, int height, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info)
    {
        charsWritten = 0;

        var threshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out var grey);

        Span<FinderPattern> patterns = stackalloc FinderPattern[3];
        if (!FinderPatternFinder.TryFind(luminance, width, height, threshold, patterns, grey))
        {
            info = new QRCodeDecodeInfo(DecodeStatus.NotDetected, 0, default, -1, 0);
            return DecodeStatus.NotDetected;
        }

        OrderFinderPatterns(patterns, out var topLeft, out var topRight, out var bottomLeft);

        // Under about 1.5 px/module a crisp module is 1 or 2 px wide and a sample has an eighth
        // of a pixel to spare, which no grid extrapolated from the finder centres keeps. The
        // timing patterns mark every module boundary between the finders. First, because where
        // both grids read, this one's corners are the symbol's own edges and the other's are
        // extrapolated to within a module; two lines that do not read cost a few hundred pixels.
        var frameStatus = DecodeThroughTimingFrame(luminance, width, height, threshold, topLeft, topRight, bottomLeft, destination, out charsWritten, out info);
        if (IsTerminal(frameStatus))
            return frameStatus;

        return DecodeFromFinders(luminance, width, height, threshold, topLeft, topRight, bottomLeft, destination, out charsWritten, out info);
    }

    /// <summary>The dimension candidates in turn: the estimate, the timing count, the version information, the runner-up.</summary>
    private static DecodeStatus DecodeFromFinders(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in FinderPattern topLeft, in FinderPattern topRight, in FinderPattern bottomLeft, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info)
    {
        charsWritten = 0;
        var timingCounted = false;
        var timingDimension = 0;
        if (!TryEstimateDimension(luminance, width, height, threshold, topLeft, topRight, bottomLeft, out var dimension, out var secondaryDimension, out var moduleSize))
        {
            // Snapped finders at versions 39-40 measure a few percent small, which puts the
            // estimate past the largest version; the count does not depend on it.
            if (moduleSize >= 1f)
            {
                timingDimension = CountTimingDimension(luminance, width, height, threshold, topLeft, topRight, bottomLeft, moduleSize);
                timingCounted = true;
            }
            if (timingDimension == 0)
            {
                info = new QRCodeDecodeInfo(DecodeStatus.NotDetected, 0, default, -1, 0);
                return DecodeStatus.NotDetected;
            }
            dimension = timingDimension;
        }

        var status = SampleAndDecode(luminance, width, height, threshold, topLeft, topRight, bottomLeft, dimension, moduleSize, destination, out charsWritten, out info, out var versionDimension);
        if (IsTerminal(status))
            return status;

        // The timing patterns count the modules the estimate only measures. Counted
        // only once the estimate has failed, so a successful decode never pays for it.
        if (!timingCounted)
            timingDimension = CountTimingDimension(luminance, width, height, threshold, topLeft, topRight, bottomLeft, moduleSize);
        if (timingDimension != 0 && timingDimension != dimension)
        {
            var timingStatus = SampleAndDecode(luminance, width, height, threshold, topLeft, topRight, bottomLeft, timingDimension, moduleSize, destination, out var timingCharsWritten, out var timingInfo, out _);
            if (IsTerminal(timingStatus))
            {
                charsWritten = timingCharsWritten;
                info = timingInfo;
                return timingStatus;
            }
        }

        // Version 7+ states its own version next to two finders, where a slightly
        // wrong dimension still samples it, so it overrules the estimate.
        if (versionDimension != 0 && versionDimension != dimension && versionDimension != timingDimension)
        {
            var versionStatus = SampleAndDecode(luminance, width, height, threshold, topLeft, topRight, bottomLeft, versionDimension, moduleSize, destination, out var versionCharsWritten, out var versionInfo, out _);
            if (IsTerminal(versionStatus))
            {
                charsWritten = versionCharsWritten;
                info = versionInfo;
                return versionStatus;
            }
        }

        // The dimension estimate can land between two valid sizes (module-size
        // measurement quantizes to pixels); when a plausible runner-up exists,
        // one retry with it rescues estimates that snapped to the wrong version. A guess
        // gets no finder fallback: on a wrong size it only doubles the failure's cost.
        if (secondaryDimension != 0 && secondaryDimension != versionDimension && secondaryDimension != timingDimension)
        {
            var secondaryStatus = SampleAndDecode(luminance, width, height, threshold, topLeft, topRight, bottomLeft, secondaryDimension, moduleSize, destination, out var secondaryCharsWritten, out var secondaryInfo, out _, finderFallback: false);
            if (IsTerminal(secondaryStatus))
            {
                charsWritten = secondaryCharsWritten;
                info = secondaryInfo;
                return secondaryStatus;
            }
        }

        // All candidates failed: report the primary attempt's diagnostics
        return status;
    }

    /// <summary>Boundaries per axis at version 40.</summary>
    private const int MaxBoundaries = 178;

    /// <summary>
    /// Decodes an upright or right-angle symbol drawn crisp at a low density through its module boundaries (<see cref="ModuleBoundaryReader"/>).
    /// </summary>
    private static DecodeStatus DecodeThroughTimingFrame(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in FinderPattern topLeft, in FinderPattern topRight, in FinderPattern bottomLeft, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info)
    {
        charsWritten = 0;
        info = new QRCodeDecodeInfo(DecodeStatus.NotDetected, 0, default, -1, 0);
        Span<int> columns = stackalloc int[MaxBoundaries];
        Span<int> rows = stackalloc int[MaxBoundaries];
        if (topLeft.ModuleSize >= ModuleBoundaryReader.MaxModuleSize
            || !TryReadModuleBoundaries(luminance, width, height, threshold, topLeft, topRight, bottomLeft, columns, rows, out var frame, out var dimension))
        {
            return DecodeStatus.NotDetected;
        }

        var rented = ArrayPool<byte>.Shared.Rent(dimension * dimension);
        try
        {
            var modules = rented.AsSpan(0, dimension * dimension);
            ModuleBoundaryReader.Sample(luminance, width, height, threshold, frame, columns, dimension, rows, dimension, modules);

            var status = DecodeWithMirrorRetry(modules, dimension, destination, out charsWritten, out info, out var transposed);
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
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }
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
    /// Samples the module grid at the given dimension and decodes it, retrying once transposed for mirrored images (e.g. front-camera captures): finder geometry is identical but data is transposed.
    /// The mirror retry triggers on any non-terminal decode failure; a permuted format pattern may fall within BCH distance of a wrong candidate and surface as DataUncorrectable instead of FormatInformationInvalid.
    /// DestinationTooSmall is terminal because the non-mirrored symbol has already been read successfully through RS correction.
    /// On failure, versionDimension is the dimension the sampled version information names when it differs from the one sampled, else 0.
    /// <paramref name="finderFallback"/> allows the resample through the finders alone when the alignment-anchored grid fails; off for a dimension that is only a guess.
    /// </summary>
    private static DecodeStatus SampleAndDecode(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in FinderPattern topLeft, in FinderPattern topRight, in FinderPattern bottomLeft, int dimension, float moduleSize, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info, out int versionDimension, bool finderFallback = true)
    {
        versionDimension = 0;
        var transform = BuildGridTransform(luminance, width, height, threshold, topLeft, topRight, bottomLeft, dimension, moduleSize, out var alignmentAnchored);

        // Version 7+ symbols carry a lattice of alignment patterns; when most of
        // them are detected, a piecewise mesh replaces the single global homography
        // (local anchors absorb the measurement noise that otherwise scales with
        // distance across large symbols).
        Span<float> meshGridCoords = stackalloc float[MaxMeshNodes];
        Span<float> meshNodeXs = stackalloc float[MaxMeshNodes * MaxMeshNodes];
        Span<float> meshNodeYs = stackalloc float[MaxMeshNodes * MaxMeshNodes];
        var usePiecewise = TryBuildSampleMesh(luminance, width, height, threshold, topLeft, topRight, bottomLeft, dimension, moduleSize, meshGridCoords, meshNodeXs, meshNodeYs, out var meshSize, out _, out _);

        var rented = ArrayPool<byte>.Shared.Rent(dimension * dimension);
        try
        {
            var modules = rented.AsSpan(0, dimension * dimension);

            if (usePiecewise)
            {
                SampleGridPiecewise(luminance, width, height, threshold, meshGridCoords.Slice(0, meshSize), meshNodeXs, meshNodeYs, meshSize, dimension, modules);
                var meshStatus = DecodeWithMirrorRetry(modules, dimension, destination, out charsWritten, out info, out var meshTransposed);
                if (IsTerminal(meshStatus))
                {
                    // The corners follow the mesh, because the mesh is what decoded: the
                    // global fit's fourth anchor is unvalidated on this path, and on large
                    // symbols under keystone it can sit on a neighbouring alignment pattern
                    // 18-30 modules from the truth while the mesh reads the symbol cleanly.
                    if (meshStatus == DecodeStatus.Success)
                        info = info.WithCorners(SymbolGeometry.FromTransform(MeshAnchoredTransform(topLeft, topRight, bottomLeft, meshGridCoords, meshNodeXs, meshNodeYs, meshSize, dimension), dimension, dimension, meshTransposed));
                    return meshStatus;
                }

                // Mesh fallback: a partially-detected mesh (unfound nodes keep
                // extrapolated predictions) can sample worse than the single global
                // homography, retrying globally guarantees the mesh path never
                // regresses below it. Failure-path cost only.
            }

            SampleGrid(luminance, width, height, threshold, transform, dimension, modules);

            // Read before the mirror retry transposes the matrix in place
            var namedDimension = ReadVersionDimension(modules, dimension);
            var status = DecodeWithMirrorRetry(modules, dimension, destination, out charsWritten, out info, out var transposed);
            if (status == DecodeStatus.Success)
            {
                info = info.WithCorners(SymbolGeometry.FromTransform(transform, dimension, dimension, transposed));
                return status;
            }
            if (namedDimension != dimension)
                versionDimension = namedDimension;
            if (IsTerminal(status) || !alignmentAnchored || !finderFallback)
                return status;

            // Alignment fallback: the alignment centre is pixel-resolved, which at about
            // 2 px/module is a third of a module, and the transform extrapolates that
            // error across the bottom-right block as perspective. The finders alone are
            // exact for a flat symbol. Failure-path cost only.
            var parallelogram = BuildParallelogramTransform(topLeft, topRight, bottomLeft, dimension);
            var rentedParallelogram = ArrayPool<byte>.Shared.Rent(dimension * dimension);
            try
            {
                var parallelogramModules = rentedParallelogram.AsSpan(0, dimension * dimension);
                SampleGrid(luminance, width, height, threshold, parallelogram, dimension, parallelogramModules);

                // The same modules decode the same way: an alignment centre found where the
                // finders put it samples identically, as on any symbol whose data is damaged.
                // The mirror retry above left the first sampling transposed.
                TransposeInPlace(modules, dimension);
                if (parallelogramModules.SequenceEqual(modules))
                    return status;

                var parallelogramStatus = DecodeWithMirrorRetry(parallelogramModules, dimension, destination, out var parallelogramCharsWritten, out var parallelogramInfo, out var parallelogramTransposed);
                if (!IsTerminal(parallelogramStatus))
                    return status;

                charsWritten = parallelogramCharsWritten;
                info = parallelogramStatus == DecodeStatus.Success
                    ? parallelogramInfo.WithCorners(SymbolGeometry.FromTransform(parallelogram, dimension, dimension, parallelogramTransposed))
                    : parallelogramInfo;
                return parallelogramStatus;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rentedParallelogram, clearArray: false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }
    }

    /// <summary>
    /// Decodes the sampled matrix, retrying once transposed (mirrored capture).
    /// On non-terminal failure reports the non-mirrored attempt's diagnostics.
    /// <paramref name="transposed"/> says which attempt produced the result, because the transpose swaps the grid's axes relative to the symbol's and the reported corners have to follow.
    /// </summary>
    private static DecodeStatus DecodeWithMirrorRetry(Span<byte> modules, int dimension, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info, out bool transposed)
    {
        transposed = false;
        var status = QRMatrixDecoder.DecodeMatrix(modules, dimension, destination, out charsWritten, out info);
        if (IsTerminal(status))
            return status;

        TransposeInPlace(modules, dimension);
        var mirroredStatus = QRMatrixDecoder.DecodeMatrix(modules, dimension, destination, out charsWritten, out var mirroredInfo);
        if (IsTerminal(mirroredStatus))
        {
            info = mirroredInfo;
            transposed = true;
            return mirroredStatus;
        }

        return status;
    }

    private static bool IsTerminal(DecodeStatus status)
        => status is DecodeStatus.Success or DecodeStatus.DestinationTooSmall;

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
    /// Assigns the three finder centers to their corners: the two farthest apart span the diagonal (top-right / bottom-left), the remaining one is top-left; the cross product resolves which diagonal end is which.
    /// </summary>
    internal static void OrderFinderPatterns(ReadOnlySpan<FinderPattern> patterns, out FinderPattern topLeft, out FinderPattern topRight, out FinderPattern bottomLeft)
    {
        var d01 = DistanceSquared(patterns[0], patterns[1]);
        var d02 = DistanceSquared(patterns[0], patterns[2]);
        var d12 = DistanceSquared(patterns[1], patterns[2]);

        FinderPattern a, b;
        if (d01 >= d02 && d01 >= d12)
        {
            topLeft = patterns[2];
            a = patterns[0];
            b = patterns[1];
        }
        else if (d02 >= d01 && d02 >= d12)
        {
            topLeft = patterns[1];
            a = patterns[0];
            b = patterns[2];
        }
        else
        {
            topLeft = patterns[0];
            a = patterns[1];
            b = patterns[2];
        }

        // Image coordinates have y pointing down, so for the standard QR layout the
        // cross product (a-topLeft) × (b-topLeft) is positive when a is top-right.
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
    /// <param name="luminance">Grayscale pixels, row-major, width × height bytes.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="threshold">Binarization threshold: a pixel is dark when luminance &lt; threshold.</param>
    /// <param name="topLeft">The finder pattern at the top-left corner of the symbol.</param>
    /// <param name="topRight">The finder pattern at the top-right corner of the symbol.</param>
    /// <param name="bottomLeft">The finder pattern at the bottom-left corner of the symbol.</param>
    /// <param name="dimension">Nearest valid dimension to the estimate.</param>
    /// <param name="secondaryDimension">Second-nearest valid dimension when the estimate is also within one version step of it (retry candidate for estimates near a snap boundary), else 0.</param>
    /// <param name="moduleSize">Measured module size in pixels (for the alignment pattern search).</param>
    private static bool TryEstimateDimension(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in FinderPattern topLeft, in FinderPattern topRight, in FinderPattern bottomLeft, out int dimension, out int secondaryDimension, out float moduleSize)
    {
        dimension = 0;
        secondaryDimension = 0;

        moduleSize = MeasureModuleSize(luminance, width, height, threshold, topLeft, topRight, bottomLeft);
        if (moduleSize < 1f)
            return false; // below one pixel per module nothing can be sampled reliably

        // Finder centers sit 7 modules apart from the matrix edges
        var widthModules = Distance(topLeft, topRight) / moduleSize + 7f;
        var heightModules = Distance(topLeft, bottomLeft) / moduleSize + 7f;
        var estimate = (widthModules + heightModules) / 2f;

        // Snap to the nearest valid dimension, clamped to the version range so an
        // estimate just past version 40 (or below 1) still snaps; reject wild ones
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
    /// Measures the module size along the finder-to-finder axes, through each pattern's center toward (and away from) its neighbor, independent of rotation.
    /// </summary>
    private static float MeasureModuleSize(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in FinderPattern topLeft, in FinderPattern topRight, in FinderPattern bottomLeft)
    {
        var sum = 0f;
        var count = 0;

        Accumulate(MeasureBothWays(luminance, width, height, threshold, topLeft, topRight), ref sum, ref count);
        Accumulate(MeasureBothWays(luminance, width, height, threshold, topRight, topLeft), ref sum, ref count);
        Accumulate(MeasureBothWays(luminance, width, height, threshold, topLeft, bottomLeft), ref sum, ref count);
        Accumulate(MeasureBothWays(luminance, width, height, threshold, bottomLeft, topLeft), ref sum, ref count);

        if (count > 0)
            return sum / count;

        // All measurements clipped (pattern at the image border): fall back to the
        // horizontal-scan estimate, valid for near-axis-aligned inputs.
        return (topLeft.ModuleSize + topRight.ModuleSize + bottomLeft.ModuleSize) / 3f;

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
    /// Module size through <paramref name="from"/>'s center along the line toward <paramref name="towards"/>, measured by the finder axis estimator the single-finder symbologies share.
    /// Returns the module size, or NaN when the run leaves the image.
    /// </summary>
    private static float MeasureBothWays(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in FinderPattern from, in FinderPattern towards)
    {
        var dx = towards.X - from.X;
        var dy = towards.Y - from.Y;
        var length = (float)Math.Sqrt(dx * dx + dy * dy);
        if (length < 1f)
            return float.NaN;

        return FinderAxisEstimator.MeasureAxis(luminance, width, height, threshold, from.X, from.Y, dx / length, dy / length);
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

        // Four samples per module: enough to tell a one-module run from a merged one, and
        // a one-pixel module crossed obliquely is still sampled
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

            // A run just ended at step i; the first dark run is the finder's and is not checked
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
    /// Builds the piecewise sampling mesh for version 7+ symbols: nodes at every alignment lattice position (grid coordinate c + 0.5 for each Annex E center coordinate c), located by the alignment finder around the global-transform prediction.
    /// Unfound nodes keep the prediction; the three finder corners have no alignment pattern and always keep it (the prediction is anchored by the finder itself there).
    /// </summary>
    /// <returns>True when the mesh should be used: at least half of the searched nodes were actually detected. With mostly-predicted nodes the mesh is merely a bilinear approximation of the global homography, strictly worse, so the caller keeps the global transform instead.</returns>
    internal static bool TryBuildSampleMesh(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in FinderPattern topLeft, in FinderPattern topRight, in FinderPattern bottomLeft, int dimension, float moduleSize, Span<float> gridCoords, Span<float> nodeXs, Span<float> nodeYs, out int meshSize, out int searchedNodes, out int foundNodes)
    {
        meshSize = 0;
        searchedNodes = 0;
        foundNodes = 0;
        var version = (dimension - 17) / 4;
        if (version < 7)
            return false;

        var baseValues = QRCodeConstants.AlignmentPatternBaseValues.Slice((version - 1) * 7, 7);
        var count = 0;
        for (var i = 0; i < 7; i++)
        {
            if (baseValues[i] != 0)
                gridCoords[count++] = baseValues[i] + 0.5f;
        }
        // Mesh needs a 4×4 lattice (version 14+): quadratic edge extrapolation
        // requires three interior nodes per line, and the linear fallback re-breaks
        // the first cell band through the same foreshortening drift it is meant to
        // fix. Below that the global homography's envelope is fine anyway (small
        // spans keep its fourth-anchor error inside the alignment search window).
        if (count < 4)
            return false;
        meshSize = count;

        var span = dimension - 7;
        (float X, float Y) axisX = ((topRight.X - topLeft.X) / span, (topRight.Y - topLeft.Y) / span);
        (float X, float Y) axisY = ((bottomLeft.X - topLeft.X) / span, (bottomLeft.Y - topLeft.Y) / span);

        // Wavefront propagation from the top-left: each interior node is predicted
        // from its three already-processed neighbors via the local parallelogram
        // P(i,j) = P(i-1,j) + P(i,j-1) - P(i-1,j-1). Local extrapolation tracks the
        // perspective cell by cell, so predictions stay within the small search
        // window even where the global transform has drifted, its fourth anchor
        // carries the full parallelogram error (≈ 2·keystone-shrink, many modules on
        // large symbols), so predicting every node through it would make detection
        // fail exactly when the mesh is needed most.
        var searched = 0;
        var found = 0;
        Span<bool> nodeFound = stackalloc bool[MaxMeshNodes * MaxMeshNodes];
        nodeFound.Clear();
        for (var j = 0; j < count; j++)
        {
            for (var i = 0; i < count; i++)
            {
                var node = j * count + i;
                float predictedX;
                float predictedY;
                var anySourceFound = false;
                if (i >= 1 && j >= 1)
                {
                    predictedX = nodeXs[node - 1] + nodeXs[node - count] - nodeXs[node - count - 1];
                    predictedY = nodeYs[node - 1] + nodeYs[node - count] - nodeYs[node - count - 1];
                    anySourceFound = nodeFound[node - 1] || nodeFound[node - count];
                }
                else
                {
                    // First lattice row/column: seeded from the finder-only affine
                    // frame, NOT the global transform, the global fourth anchor
                    // carries the parallelogram/detection error and would bend these
                    // seeds (and with them every wavefront prediction downstream).
                    // Along the top/left edges the finder affine is nearly exact.
                    predictedX = topLeft.X + (gridCoords[i] - 3.5f) * axisX.X + (gridCoords[j] - 3.5f) * axisY.X;
                    predictedY = topLeft.Y + (gridCoords[i] - 3.5f) * axisX.Y + (gridCoords[j] - 3.5f) * axisY.Y;
                }
                nodeXs[node] = predictedX;
                nodeYs[node] = predictedY;

                // Only interior lattice nodes are searched. Coordinate-6 rows/columns
                // lie ON the timing lines, whose perfect 1-module alternation floods
                // the light-dark-light scan with false candidates (a single accepted
                // fake bends the whole top/left mesh band); the finder corners have
                // no alignment pattern at all. Both keep the prediction.
                if (baseValues[i] == 6 || baseValues[j] == 6)
                    continue;

                searched++;
                // Adaptive window: wavefront starters (no detected node among the
                // parallelogram sources) still carry the affine seeds' foreshortening
                // drift and get a wider window; once a detected neighbor feeds the
                // prediction, drift differences cancel and the tight window applies.
                // Keeping the wide window rare matters, a wide-window false positive
                // does not stay local, the wavefront propagates it downstream
                // (measured as a broad regression when every node searched wide).
                var window = anySourceFound ? 2.5f : 4f;
                if (AlignmentPatternFinder.TryFind(luminance, width, height, threshold, predictedX, predictedY, moduleSize, axisX, axisY, window, out var foundX, out var foundY))
                {
                    nodeXs[node] = foundX;
                    nodeYs[node] = foundY;
                    nodeFound[node] = true;
                    found++;
                }
            }
        }

        // Refinement pass: when nodes were missed, rebuild a homography from the
        // three finder centers plus the detected node closest to the bottom-right
        // lattice corner (a reliable, ring-validated fourth correspondence, unlike
        // the global transform's swept-window anchor), then re-search the missed
        // nodes around its predictions. For an exact-homography distortion four
        // exact correspondences reproduce the mapping, so the remaining nodes land
        // within the tight window even where the wavefront seeds drifted.
        // Iterated: each round can push the anchor further toward the bottom-right,
        // improving the next round's predictions; stops when a round finds nothing.
        for (var round = 0; round < 4 && found > 0 && found < searched; round++)
        {
            var best = -1;
            var bestRank = -1;
            for (var j = count - 1; j >= 1; j--)
            {
                for (var i = count - 1; i >= 1; i--)
                {
                    if (nodeFound[j * count + i] && i + j > bestRank)
                    {
                        best = j * count + i;
                        bestRank = i + j;
                    }
                }
            }

            // Require the anchor to be genuinely interior-far so the four
            // correspondences form a non-degenerate quad
            if (best < 0 || bestRank < 2)
                break;

            var bestI = best % count;
            var bestJ = best / count;
            var refined = PerspectiveTransform.QuadrilateralToQuadrilateral(
                3.5f, 3.5f,
                dimension - 3.5f, 3.5f,
                gridCoords[bestI], gridCoords[bestJ],
                3.5f, dimension - 3.5f,
                topLeft.X, topLeft.Y,
                topRight.X, topRight.Y,
                nodeXs[best], nodeYs[best],
                bottomLeft.X, bottomLeft.Y);

            var foundThisRound = 0;
            for (var j = 0; j < count; j++)
            {
                for (var i = 0; i < count; i++)
                {
                    var node = j * count + i;
                    if (nodeFound[node] || baseValues[i] == 6 || baseValues[j] == 6)
                        continue;

                    refined.Transform(gridCoords[i], gridCoords[j], out var predictedX, out var predictedY);
                    nodeXs[node] = predictedX;
                    nodeYs[node] = predictedY;
                    if (AlignmentPatternFinder.TryFind(luminance, width, height, threshold, predictedX, predictedY, moduleSize, axisX, axisY, allowanceModules: 2.5f, out var foundX, out var foundY))
                    {
                        nodeXs[node] = foundX;
                        nodeYs[node] = foundY;
                        nodeFound[node] = true;
                        found++;
                        foundThisRound++;
                    }
                }
            }

            if (foundThisRound == 0)
                break;
        }

        searchedNodes = searched;
        foundNodes = found;
        if (found * 2 < searched)
            return false;

        // Second pass: re-derive the prediction-only edge nodes (coordinate-6 row
        // and column plus the finder corners) from the detected interior nodes by
        // extrapolation along their lattice row/column. The affine seeds are good
        // enough to FIND patterns, but not to SAMPLE with: projective foreshortening
        // makes lattice spacing non-uniform, and the affine (average-slope) edge
        // prediction drifts up to ~1.5 modules mid-edge, enough to garble the whole
        // first cell band. LINEAR extrapolation fails for the same reason (points on
        // the lattice line are collinear, but their spacing along it is projective):
        // quadratic (Lagrange) extrapolation through three interior nodes captures
        // the first-order foreshortening change (measured ~1 px residual where
        // linear was ~12 px off). Falls back to linear when only two interior nodes
        // exist (mesh size 3, versions 7-13, where spans are small).
        for (var i = 1; i < count; i++)
        {
            Extrapolate(nodeXs, nodeYs, gridCoords, count, target: i, stride: count, first: count + i);
        }
        for (var j = 1; j < count; j++)
        {
            Extrapolate(nodeXs, nodeYs, gridCoords, count, target: j * count, stride: 1, first: j * count + 1);
        }
        // Top-left corner: average of the row and column extrapolations through the
        // just-corrected edge nodes
        {
            Span<float> cornerX = stackalloc float[2];
            Span<float> cornerY = stackalloc float[2];
            var saveX = nodeXs[0];
            var saveY = nodeYs[0];
            Extrapolate(nodeXs, nodeYs, gridCoords, count, target: 0, stride: 1, first: 1);
            cornerX[0] = nodeXs[0];
            cornerY[0] = nodeYs[0];
            nodeXs[0] = saveX;
            nodeYs[0] = saveY;
            Extrapolate(nodeXs, nodeYs, gridCoords, count, target: 0, stride: count, first: count);
            cornerX[1] = nodeXs[0];
            cornerY[1] = nodeYs[0];
            nodeXs[0] = (cornerX[0] + cornerX[1]) / 2f;
            nodeYs[0] = (cornerY[0] + cornerY[1]) / 2f;
        }

        return true;

        // Extrapolates the node at `target` (grid parameter gridCoords[0]) from the
        // nodes at `first`, `first + stride`, (and `first + 2*stride` when available)
        // whose grid parameters are gridCoords[1..3].
        static void Extrapolate(Span<float> nodeXs, Span<float> nodeYs, ReadOnlySpan<float> gridCoords, int count, int target, int stride, int first)
        {
            var t0 = gridCoords[0];
            var t1 = gridCoords[1];
            var t2 = gridCoords[2];

            if (count >= 4)
            {
                // Quadratic Lagrange through three interior nodes
                var t3 = gridCoords[3];
                var l1 = (t0 - t2) * (t0 - t3) / ((t1 - t2) * (t1 - t3));
                var l2 = (t0 - t1) * (t0 - t3) / ((t2 - t1) * (t2 - t3));
                var l3 = (t0 - t1) * (t0 - t2) / ((t3 - t1) * (t3 - t2));
                nodeXs[target] = l1 * nodeXs[first] + l2 * nodeXs[first + stride] + l3 * nodeXs[first + 2 * stride];
                nodeYs[target] = l1 * nodeYs[first] + l2 * nodeYs[first + stride] + l3 * nodeYs[first + 2 * stride];
                return;
            }

            // Linear through two interior nodes
            var ratio = (t0 - t1) / (t2 - t1);
            nodeXs[target] = nodeXs[first] + (nodeXs[first + stride] - nodeXs[first]) * ratio;
            nodeYs[target] = nodeYs[first] + (nodeYs[first + stride] - nodeYs[first]) * ratio;
        }
    }

    /// <summary>
    /// The geometry a mesh-sampled decode is reported with: the three finder centres plus the mesh's bottom-right lattice node as the fourth correspondence.
    /// The same construction as the alignment branch of <see cref="BuildGridTransform"/>, but anchored on a node the decode validated (the mesh sampled through it and read the symbol) rather than on an independent search whose window can catch a neighbouring pattern.
    /// A projective fit through four correct points puts every corner within a fraction of a module, where extrapolating the mesh's outermost bilinear cells 6.5 modules outward leaves close to a module at the far corners (measured).
    /// </summary>
    private static PerspectiveTransform MeshAnchoredTransform(in FinderPattern topLeft, in FinderPattern topRight, in FinderPattern bottomLeft, ReadOnlySpan<float> gridCoords, ReadOnlySpan<float> nodeXs, ReadOnlySpan<float> nodeYs, int meshSize, int dimension)
    {
        // Finder centres sit at grid 3.5 / dimension − 3.5; the last lattice coordinate is dimension − 6.5.
        // When that node was not detected the refinement rounds have already re-predicted it, so
        // anchoring on the nearest detected node instead measured byte-identical and was dropped.
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
    /// Samples every module center through the piecewise-bilinear mesh.
    /// Bilinear interpolation is exact at the nodes, continuous across cell edges (adjacent cells share the same edge interpolation, unlike per-cell homographies), and division-free; within ~20-module cells its deviation from the true projective map is second-order small.
    /// Modules outside the lattice (borders, ≤ 6 modules) extrapolate the nearest cell.
    /// </summary>
    internal static void SampleGridPiecewise(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, ReadOnlySpan<float> gridCoords, ReadOnlySpan<float> nodeXs, ReadOnlySpan<float> nodeYs, int meshSize, int dimension, Span<byte> modules)
    {
        var cells = meshSize - 1;
        Span<float> rowXs = stackalloc float[MaxMeshNodes];
        Span<float> rowYs = stackalloc float[MaxMeshNodes];

        var cellJ = 0;
        for (var v = 0; v < dimension; v++)
        {
            var gridY = v + 0.5f;
            while (cellJ < cells - 1 && gridY >= gridCoords[cellJ + 1])
                cellJ++;
            var t = (gridY - gridCoords[cellJ]) / (gridCoords[cellJ + 1] - gridCoords[cellJ]);

            // Interpolate the lattice columns at this row once
            for (var i = 0; i < meshSize; i++)
            {
                var node0 = cellJ * meshSize + i;
                var node1 = (cellJ + 1) * meshSize + i;
                rowXs[i] = nodeXs[node0] + (nodeXs[node1] - nodeXs[node0]) * t;
                rowYs[i] = nodeYs[node0] + (nodeYs[node1] - nodeYs[node0]) * t;
            }

            var rowBase = v * dimension;
            var cellI = 0;
            for (var u = 0; u < dimension; u++)
            {
                var gridX = u + 0.5f;
                while (cellI < cells - 1 && gridX >= gridCoords[cellI + 1])
                    cellI++;
                var s = (gridX - gridCoords[cellI]) / (gridCoords[cellI + 1] - gridCoords[cellI]);
                var x = rowXs[cellI] + (rowXs[cellI + 1] - rowXs[cellI]) * s;
                var y = rowYs[cellI] + (rowYs[cellI + 1] - rowYs[cellI]) * s;

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

                modules[rowBase + u] = luminance[py * width + px] < threshold ? (byte)1 : (byte)0;
            }
        }
    }

    /// <summary>
    /// Builds the grid-to-pixel projective transform from the three finder centers plus a fourth correspondence point: the bottom-right alignment pattern when one exists and is found, otherwise the parallelogram corner estimate (which degrades the transform to affine, exact for flat, on-axis captures).
    /// Grid coordinates put module (u, v)'s center at (u+0.5, v+0.5), so finder centers sit at 3.5 and the alignment center at dimension−6.5.
    /// </summary>
    internal static PerspectiveTransform BuildGridTransform(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in FinderPattern topLeft, in FinderPattern topRight, in FinderPattern bottomLeft, int dimension, float moduleSize, out bool alignmentAnchored)
    {
        alignmentAnchored = false;
        // Parallelogram estimate of the bottom-right corner (grid dimension−3.5)
        var cornerX = topRight.X + bottomLeft.X - topLeft.X;
        var cornerY = topRight.Y + bottomLeft.Y - topLeft.Y;

        // Version 2+ has an alignment pattern centered 6.5 modules in from the
        // bottom-right corner; its predicted position pulls the corner estimate
        // toward the top-left by 3 modules on both axes.
        if (dimension >= 25)
        {
            var correction = 1f - 3f / (dimension - 7);
            var expectedX = topLeft.X + correction * (cornerX - topLeft.X);
            var expectedY = topLeft.Y + correction * (cornerY - topLeft.Y);

            // Per-module grid axis vectors, for orientation-aware ring validation
            var span = dimension - 7;
            var axisX = ((topRight.X - topLeft.X) / span, (topRight.Y - topLeft.Y) / span);
            var axisY = ((bottomLeft.X - topLeft.X) / span, (bottomLeft.Y - topLeft.Y) / span);

            // Expanding search window; mild perspective shifts the true position
            // further from the parallelogram prediction as the tilt grows.
            foreach (var allowance in stackalloc float[] { 4f, 8f, 16f })
            {
                if (AlignmentPatternFinder.TryFind(luminance, width, height, threshold, expectedX, expectedY, moduleSize, axisX, axisY, allowance, out var alignmentX, out var alignmentY))
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

    /// <summary>
    /// Samples every module center through the projective grid-to-pixel transform.
    /// Handles rotation, scale, shear and mild perspective.
    /// </summary>
    /// <remarks>
    /// The loop is bound by scalar conversion/clamp/branch overhead, not by the divisions (module computations are independent, so out-of-order execution hides division latency, halving the division count measured no gain).
    /// The SIMD paths process 8 module centers per iteration with the exact scalar op sequence (no FMA), so lane results are bit-identical to the scalar path: one Vector256 on AVX2 (measured 2.7x at version 40; PerspectiveSample findings log), two independent Vector128 chains plus a 4-lane cleanup on NEON/WASM (measured 1.9x at version 40 on Apple M2; PerspectiveSampleArm findings log).
    /// </remarks>
    internal static void SampleGrid(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in PerspectiveTransform transform, int dimension, Span<byte> modules)
    {
#if NET8_0_OR_GREATER
        if (Vector256.IsHardwareAccelerated && dimension >= 8)
        {
            SampleGridSimd(luminance, width, height, threshold, transform, dimension, modules);
            return;
        }
        if (Vector128.IsHardwareAccelerated && dimension >= 4)
        {
            SampleGridSimd128(luminance, width, height, threshold, transform, dimension, modules);
            return;
        }
#endif
        SampleGridScalar(luminance, width, height, threshold, transform, dimension, modules);
    }

    internal static void SampleGridScalar(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in PerspectiveTransform transform, int dimension, Span<byte> modules)
    {
        for (var v = 0; v < dimension; v++)
        {
            var rowBase = v * dimension;
            var gridY = v + 0.5f;
            var rowNumeratorX = transform.a21 * gridY + transform.a31;
            var rowNumeratorY = transform.a22 * gridY + transform.a32;
            var rowDenominator = transform.a23 * gridY + transform.a33;

            for (var u = 0; u < dimension; u++)
            {
                var gridX = u + 0.5f;
                var reciprocal = 1f / (transform.a13 * gridX + rowDenominator);
                var x = (transform.a11 * gridX + rowNumeratorX) * reciprocal;
                var y = (transform.a12 * gridX + rowNumeratorY) * reciprocal;

                // Pixel edges sit on integers, so the pixel containing a point is its floor
                var px = (int)x;
                var py = (int)y;

                // Clamp: mild inaccuracy at the outermost modules must not read OOB
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

#if NET8_0_OR_GREATER
    internal static void SampleGridSimd(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in PerspectiveTransform transform, int dimension, Span<byte> modules)
    {
        var laneOffsets = Vector256.Create(0.5f, 1.5f, 2.5f, 3.5f, 4.5f, 5.5f, 6.5f, 7.5f);
        var a11 = Vector256.Create(transform.a11);
        var a12 = Vector256.Create(transform.a12);
        var a13 = Vector256.Create(transform.a13);
        var zero = Vector256<int>.Zero;
        var maxPx = Vector256.Create(width - 1);
        var maxPy = Vector256.Create(height - 1);
        var widthVector = Vector256.Create(width);

        Span<int> indices = stackalloc int[8];

        for (var v = 0; v < dimension; v++)
        {
            var rowBase = v * dimension;
            var gridY = v + 0.5f;
            var rowNumeratorX = Vector256.Create(transform.a21 * gridY + transform.a31);
            var rowNumeratorY = Vector256.Create(transform.a22 * gridY + transform.a32);
            var rowDenominator = Vector256.Create(transform.a23 * gridY + transform.a33);

            var u = 0;
            for (; u + 8 <= dimension; u += 8)
            {
                var gridX = laneOffsets + Vector256.Create((float)u);
                var reciprocal = Vector256<float>.One / (a13 * gridX + rowDenominator);
                var x = (a11 * gridX + rowNumeratorX) * reciprocal;
                var y = (a12 * gridX + rowNumeratorY) * reciprocal;

                // ConvertToInt32 truncates toward zero like the scalar cast, so both take
                // the pixel containing the point (ConvertToInt32Native is the one that
                // follows the platform's rounding); out-of-range lanes differ from scalar
                // saturation but are clamped into bounds either way.
                var px = Vector256.ConvertToInt32(x);
                var py = Vector256.ConvertToInt32(y);
                px = Vector256.Max(Vector256.Min(px, maxPx), zero);
                py = Vector256.Max(Vector256.Min(py, maxPy), zero);

                var index = py * widthVector + px;
                index.CopyTo(indices);

                for (var lane = 0; lane < 8; lane++)
                {
                    modules[rowBase + u + lane] = luminance[indices[lane]] < threshold ? (byte)1 : (byte)0;
                }
            }

            // Scalar tail, same op sequence as SampleGridScalar
            var rowNX = transform.a21 * gridY + transform.a31;
            var rowNY = transform.a22 * gridY + transform.a32;
            var rowD = transform.a23 * gridY + transform.a33;
            for (; u < dimension; u++)
            {
                var gridXs = u + 0.5f;
                var reciprocal = 1f / (transform.a13 * gridXs + rowD);
                var x = (transform.a11 * gridXs + rowNX) * reciprocal;
                var y = (transform.a12 * gridXs + rowNY) * reciprocal;

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

                modules[rowBase + u] = luminance[py * width + px] < threshold ? (byte)1 : (byte)0;
            }
        }
    }

    internal static void SampleGridSimd128(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in PerspectiveTransform transform, int dimension, Span<byte> modules)
    {
        var laneOffsetsLo = Vector128.Create(0.5f, 1.5f, 2.5f, 3.5f);
        var laneOffsetsHi = Vector128.Create(4.5f, 5.5f, 6.5f, 7.5f);
        var a11 = Vector128.Create(transform.a11);
        var a12 = Vector128.Create(transform.a12);
        var a13 = Vector128.Create(transform.a13);
        var zero = Vector128<int>.Zero;
        var maxPx = Vector128.Create(width - 1);
        var maxPy = Vector128.Create(height - 1);
        var widthVector = Vector128.Create(width);

        Span<int> indices = stackalloc int[8];

        for (var v = 0; v < dimension; v++)
        {
            var rowBase = v * dimension;
            var gridY = v + 0.5f;
            var rowNumeratorX = Vector128.Create(transform.a21 * gridY + transform.a31);
            var rowNumeratorY = Vector128.Create(transform.a22 * gridY + transform.a32);
            var rowDenominator = Vector128.Create(transform.a23 * gridY + transform.a33);

            // Two independent 4-lane chains per iteration: the second fdiv
            // overlaps the first (fdiv 4S latency would otherwise stall the
            // 4-lane loop) and per-iteration loop overhead is halved.
            var u = 0;
            for (; u + 8 <= dimension; u += 8)
            {
                var uVector = Vector128.Create((float)u);
                var gridXLo = laneOffsetsLo + uVector;
                var gridXHi = laneOffsetsHi + uVector;
                var reciprocalLo = Vector128<float>.One / (a13 * gridXLo + rowDenominator);
                var reciprocalHi = Vector128<float>.One / (a13 * gridXHi + rowDenominator);
                var xLo = (a11 * gridXLo + rowNumeratorX) * reciprocalLo;
                var yLo = (a12 * gridXLo + rowNumeratorY) * reciprocalLo;
                var xHi = (a11 * gridXHi + rowNumeratorX) * reciprocalHi;
                var yHi = (a12 * gridXHi + rowNumeratorY) * reciprocalHi;

                // ConvertToInt32 truncates toward zero like the scalar cast, so both take
                // the pixel containing the point (ConvertToInt32Native is the one that
                // follows the platform's rounding); out-of-range lanes differ from scalar
                // saturation but are clamped into bounds either way.
                var pxLo = Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(xLo), maxPx), zero);
                var pyLo = Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(yLo), maxPy), zero);
                var pxHi = Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(xHi), maxPx), zero);
                var pyHi = Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(yHi), maxPy), zero);

                (pyLo * widthVector + pxLo).CopyTo(indices);
                (pyHi * widthVector + pxHi).CopyTo(indices.Slice(4));

                for (var lane = 0; lane < 8; lane++)
                {
                    modules[rowBase + u + lane] = luminance[indices[lane]] < threshold ? (byte)1 : (byte)0;
                }
            }

            // 4-lane cleanup keeps the per-row scalar tail under 4 modules
            // (dimension mod 8 can be 4-7, e.g. 77 leaves 5 without this block).
            if (u + 4 <= dimension)
            {
                var gridX = laneOffsetsLo + Vector128.Create((float)u);
                var reciprocal = Vector128<float>.One / (a13 * gridX + rowDenominator);
                var x = (a11 * gridX + rowNumeratorX) * reciprocal;
                var y = (a12 * gridX + rowNumeratorY) * reciprocal;

                var px = Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(x), maxPx), zero);
                var py = Vector128.Max(Vector128.Min(Vector128.ConvertToInt32(y), maxPy), zero);

                (py * widthVector + px).CopyTo(indices);

                for (var lane = 0; lane < 4; lane++)
                {
                    modules[rowBase + u + lane] = luminance[indices[lane]] < threshold ? (byte)1 : (byte)0;
                }
                u += 4;
            }

            // Scalar tail, same op sequence as SampleGridScalar
            var rowNX = transform.a21 * gridY + transform.a31;
            var rowNY = transform.a22 * gridY + transform.a32;
            var rowD = transform.a23 * gridY + transform.a33;
            for (; u < dimension; u++)
            {
                var gridXs = u + 0.5f;
                var reciprocal = 1f / (transform.a13 * gridXs + rowD);
                var x = (transform.a11 * gridXs + rowNX) * reciprocal;
                var y = (transform.a12 * gridXs + rowNY) * reciprocal;

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

                modules[rowBase + u] = luminance[py * width + px] < threshold ? (byte)1 : (byte)0;
            }
        }
    }
#endif

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
