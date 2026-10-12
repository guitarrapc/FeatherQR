using System.Buffers;
using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Internals.StandardQR;

internal static partial class QRImageDecoder
{
    /// <summary>Samples a side a module's luminance is the mean of, spread evenly over <see cref="ModuleSampleSpan"/>.</summary>
    private const int ModuleSamplesPerSide = 3;

    /// <summary>The share of a module's side, about its centre, the samples cover: the middle, clear of the edges a neighbour's blur reaches.</summary>
    private const float ModuleSampleSpan = 0.3f;

    /// <summary>The least reach, in modules, of the window a module is compared against, nine modules a side: enough of both colours that their mean lies between the two.</summary>
    private const int MinModuleWindowReach = 4;

    /// <summary>The window's reach as a share of the dimension, one in this many, where that is more than the least: a larger symbol's light changes over more modules.</summary>
    private const int ModuleWindowDivisor = 8;

    /// <summary>The module size, in pixels, under which a sample is the pixel it falls in: between pixels it would mix in the neighbouring modules.</summary>
    private const float NearestSampleBelow = 3f;

    /// <summary>
    /// The grid read again into <paramref name="modules"/> in module space: each module's luminance the mean of samples over its middle, and the module dark when no lighter than the mean of the modules around it.
    /// A threshold over pixels, global or regional, sets its scale in pixels and reads one pixel a module. This one measures in modules, so blur and noise average out of a module and the level it is split at follows the light across the symbol.
    /// </summary>
    private static DecodeStatus DecodeInModuleSpace(in ImageView image, in PerspectiveTransform transform, int dimension, ReadOnlySpan<byte> transposedFailure, Span<byte> modules, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info)
    {
        var nearest = IsBelowInterpolation(transform, dimension);
        // A grid that is not this symbol's, at another dimension or from other marks, is refused on two lines of it
        if (!TimingPatternsAlternate(image.GreySource, image.Width, image.Height, transform, dimension, nearest, image.GreySourceNegative))
        {
            charsWritten = 0;
            info = NotDetectedInfo;
            return DecodeStatus.NotDetected;
        }

        var scratch = ArrayPool<int>.Shared.Rent(ModuleSpaceScratchLength(dimension));
        try
        {
            SampleGridInModuleSpace(image.GreySource, image.Width, image.Height, transform, dimension, nearest, image.GreySourceNegative, scratch, modules);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(scratch, clearArray: false);
        }

        // As the coverage re-read: the grid that already failed decodes the same way again
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

    /// <summary>Integers <see cref="SampleGridInModuleSpace"/> needs to work in: a level a module and a running sum a grid corner.</summary>
    internal static int ModuleSpaceScratchLength(int dimension) => dimension * dimension + (dimension + 1) * (dimension + 1);

    /// <summary>Whether the grid's modules at its centre are under <see cref="NearestSampleBelow"/> along either axis.</summary>
    internal static bool IsBelowInterpolation(in PerspectiveTransform transform, int dimension)
    {
        var half = dimension / 2f;
        transform.Transform(half, half, out var centreX, out var centreY);
        transform.Transform(half + 1f, half, out var alongX, out var alongY);
        transform.Transform(half, half + 1f, out var downX, out var downY);
        return Math.Min(Hypot(alongX - centreX, alongY - centreY), Hypot(downX - centreX, downY - centreY)) < NearestSampleBelow;

        static float Hypot(float x, float y) => (float)Math.Sqrt(x * x + y * y);
    }

    /// <summary>
    /// Whether the timing row and column of the grid alternate as timing patterns do: each module between the finders darker than the mean of its two neighbours along the line where the pattern is dark, lighter where it is light, with no more than one in <see cref="MaxTimingWrongDivisor"/> of them otherwise.
    /// Read from the image as <see cref="SampleGridInModuleSpace"/> reads a module, so it costs two lines of the grid.
    /// </summary>
    internal static bool TimingPatternsAlternate(ReadOnlySpan<byte> luminance, int width, int height, in PerspectiveTransform transform, int dimension, bool nearest, bool negative)
    {
        // From the finder's edge module to the far finder's, 6 to dimension − 7: dark at even coordinates, the separators beside the finders light
        var count = dimension - 12;
        Span<int> line = stackalloc int[MaxTimingModules];
        line = line.Slice(0, count);
        var wrong = 0;
        for (var axis = 0; axis < 2; axis++)
        {
            for (var k = 0; k < count; k++)
                line[k] = axis == 0 ? ModuleLevel(luminance, width, height, transform, 6 + k, 6, nearest, negative) : ModuleLevel(luminance, width, height, transform, 6, 6 + k, nearest, negative);
            for (var k = 1; k < count - 1; k++)
            {
                var between = line[k - 1] + line[k + 1];
                // k even is an even coordinate, dark
                if ((k & 1) == 0 ? 2 * line[k] >= between : 2 * line[k] <= between)
                    wrong++;
            }
        }
        return wrong * MaxTimingWrongDivisor <= 2 * (count - 2);
    }

    /// <summary>Modules of a timing line with the finders' edge modules, at version 40.</summary>
    private const int MaxTimingModules = 177 - 12;

    /// <summary>The share of the timing modules, one in this many, that may read against the pattern.</summary>
    private const int MaxTimingWrongDivisor = 4;

    /// <summary>The sum of the samples of module (<paramref name="u"/>, <paramref name="v"/>): the centre and the points a step either side of it along both axes, the axes taken at the module.</summary>
    private static int ModuleLevel(ReadOnlySpan<byte> luminance, int width, int height, in PerspectiveTransform transform, int u, int v, bool nearest, bool negative)
    {
        const float Step = ModuleSampleSpan / (ModuleSamplesPerSide - 1);
        transform.Transform(u + 0.5f, v + 0.5f, out var x, out var y);
        transform.Transform(u + 0.5f + Step, v + 0.5f, out var ux, out var uy);
        transform.Transform(u + 0.5f, v + 0.5f + Step, out var vx, out var vy);
        ux -= x;
        uy -= y;
        vx -= x;
        vy -= y;
        var level = 0f;
        for (var j = -(ModuleSamplesPerSide / 2); j <= ModuleSamplesPerSide / 2; j++)
        {
            for (var i = -(ModuleSamplesPerSide / 2); i <= ModuleSamplesPerSide / 2; i++)
            {
                var sx = x + i * ux + j * vx;
                var sy = y + i * uy + j * vy;
                level += nearest
                    ? luminance[PixelIndex.Clamp(sy, height) * width + PixelIndex.Clamp(sx, width)]
                    : LuminanceSampler.Bilinear(luminance, width, height, sx, sy);
            }
        }
        // The sum, kept whole: every comparison is of sums, and a mean would only round it. A negative's level is its complement
        var sum = (int)(level + 0.5f);
        return negative ? ModuleSamplesPerSide * ModuleSamplesPerSide * byte.MaxValue - sum : sum;
    }

    internal static void SampleGridInModuleSpace(ReadOnlySpan<byte> luminance, int width, int height, in PerspectiveTransform transform, int dimension, bool nearest, bool negative, Span<int> scratch, Span<byte> modules)
    {
        var levels = scratch.Slice(0, dimension * dimension);
        var stride = dimension + 1;
        var sums = scratch.Slice(dimension * dimension, stride * stride);

        for (var v = 0; v < dimension; v++)
        {
            for (var u = 0; u < dimension; u++)
                levels[v * dimension + u] = ModuleLevel(luminance, width, height, transform, u, v, nearest, negative);
        }

        // Running sums over the grid, so each window's total is four reads
        sums.Slice(0, stride).Clear();
        for (var v = 0; v < dimension; v++)
        {
            var row = 0;
            sums[(v + 1) * stride] = 0;
            for (var u = 0; u < dimension; u++)
            {
                row += levels[v * dimension + u];
                sums[(v + 1) * stride + u + 1] = sums[v * stride + u + 1] + row;
            }
        }

        var reach = Math.Max(MinModuleWindowReach, dimension / ModuleWindowDivisor);
        for (var v = 0; v < dimension; v++)
        {
            var top = Math.Max(0, v - reach);
            var bottom = Math.Min(dimension, v + reach + 1);
            for (var u = 0; u < dimension; u++)
            {
                var left = Math.Max(0, u - reach);
                var right = Math.Min(dimension, u + reach + 1);
                var total = sums[bottom * stride + right] - sums[top * stride + right] - sums[bottom * stride + left] + sums[top * stride + left];
                // level <= total / count, without the division. A level is at most 2,295 and a window 45 modules a side, so the product stays in an int, as the sums over 177 × 177 modules do.
                modules[v * dimension + u] = levels[v * dimension + u] * ((bottom - top) * (right - left)) <= total ? (byte)1 : (byte)0;
            }
        }
    }
}
