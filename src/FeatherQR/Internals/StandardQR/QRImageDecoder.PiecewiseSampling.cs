using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FeatherQR.Internals.ImageDecoders;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
#endif

namespace FeatherQR.Internals.StandardQR;

internal static partial class QRImageDecoder
{
    // The column tables live on the stack, sized for version 40.
    private const int MaxPiecewiseDimension = 177;

    // Image sides up to here are exact as floats, which the vector tier's float clamp relies on.
    private const int MaxExactFloatSide = 1 << 24;

    /// <summary>
    /// Samples every module center through the mesh, each coordinate interpolated through three nodes of its lattice line: the two of the cell it falls in and the one beyond the nearer of them, and the first or the last three for a module past them.
    /// </summary>
    /// <remarks>
    /// Same module grid as <see cref="SampleGridPiecewiseScalar"/>, which means the same pixel for every module: the tiers do the reference's float operations in the reference's order and move only where they happen.
    /// A mesh the tiers' stack tables cannot hold goes to the reference.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when <paramref name="luminance"/> is smaller than width × height, <paramref name="modules"/> smaller than dimension², or the mesh has fewer than three nodes a side.</exception>
    internal static void SampleGridPiecewise(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, ReadOnlySpan<float> gridCoords, ReadOnlySpan<float> nodeXs, ReadOnlySpan<float> nodeYs, int meshSize, int dimension, Span<byte> modules)
    {
#if NET8_0_OR_GREATER
        if (Avx2.IsSupported)
        {
            SampleGridPiecewiseAvx2(luminance, width, height, threshold, gridCoords, nodeXs, nodeYs, meshSize, dimension, modules);
            return;
        }
        // ARM64 takes the 128-bit tier: the same steps written with its own intrinsics ran no faster there
        if (Vector128.IsHardwareAccelerated)
        {
            SampleGridPiecewiseVector128(luminance, width, height, threshold, gridCoords, nodeXs, nodeYs, meshSize, dimension, modules);
            return;
        }
#endif
        SampleGridPiecewiseColumnTable(luminance, width, height, threshold, gridCoords, nodeXs, nodeYs, meshSize, dimension, modules);
    }

    /// <summary>
    /// Portable tier: the three nodes a column is interpolated through and its distances past the first two are the same for every row, so they are tabled once a call, as are the reciprocals of the lattice's spacings, and a row's slopes and curvatures are taken once a row.
    /// </summary>
    internal static void SampleGridPiecewiseColumnTable(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, ReadOnlySpan<float> gridCoords, ReadOnlySpan<float> nodeXs, ReadOnlySpan<float> nodeYs, int meshSize, int dimension, Span<byte> modules)
    {
        if (!CheckPiecewiseArguments(luminance, width, height, gridCoords, nodeXs, nodeYs, meshSize, dimension, modules))
        {
            SampleGridPiecewiseScalar(luminance, width, height, threshold, gridCoords, nodeXs, nodeYs, meshSize, dimension, modules);
            return;
        }

        Span<float> rowXs = stackalloc float[MaxMeshNodes];
        Span<float> rowYs = stackalloc float[MaxMeshNodes];
        Span<float> startX = stackalloc float[MaxMeshNodes];
        Span<float> slopeX = stackalloc float[MaxMeshNodes];
        Span<float> curveX = stackalloc float[MaxMeshNodes];
        Span<float> startY = stackalloc float[MaxMeshNodes];
        Span<float> slopeY = stackalloc float[MaxMeshNodes];
        Span<float> curveY = stackalloc float[MaxMeshNodes];
        Span<int> firstOf = stackalloc int[MaxPiecewiseDimension];
        Span<float> alongOf = stackalloc float[MaxPiecewiseDimension];
        Span<float> pastOf = stackalloc float[MaxPiecewiseDimension];
        Span<float> toSecond = stackalloc float[MaxMeshNodes];
        Span<float> toThird = stackalloc float[MaxMeshNodes];
        Span<float> across = stackalloc float[MaxMeshNodes];
        BuildColumnTable(gridCoords, meshSize, dimension, firstOf, alongOf, pastOf);
        BuildRunTable(gridCoords, meshSize, toSecond, toThird, across);

        // Unchecked from here: a pixel index is clamped into width × height, a first node is below
        // meshSize − 2 ≤ 5, and both lengths were checked above.
        ref var lum = ref MemoryMarshal.GetReference(luminance);
        ref var dst = ref MemoryMarshal.GetReference(modules);
        ref var first0 = ref MemoryMarshal.GetReference(firstOf);
        ref var along0 = ref MemoryMarshal.GetReference(alongOf);
        ref var past0 = ref MemoryMarshal.GetReference(pastOf);
        ref var startX0 = ref MemoryMarshal.GetReference(startX);
        ref var slopeX0 = ref MemoryMarshal.GetReference(slopeX);
        ref var curveX0 = ref MemoryMarshal.GetReference(curveX);
        ref var startY0 = ref MemoryMarshal.GetReference(startY);
        ref var slopeY0 = ref MemoryMarshal.GetReference(slopeY);
        ref var curveY0 = ref MemoryMarshal.GetReference(curveY);
        int limit = threshold;
        float farX = width;
        float farY = height;

        for (var v = 0; v < dimension; v++)
        {
            InterpolateMeshRow(firstOf[v], alongOf[v], pastOf[v], meshSize, toSecond, toThird, across, nodeXs, nodeYs, rowXs, rowYs);
            RowCoefficients(meshSize, toSecond, toThird, across, rowXs, startX, slopeX, curveX);
            RowCoefficients(meshSize, toSecond, toThird, across, rowYs, startY, slopeY, curveY);

            ref var o = ref Unsafe.Add(ref dst, v * dimension);
            for (var u = 0; u < dimension; u++)
            {
                var first = Unsafe.Add(ref first0, u);
                var along = Unsafe.Add(ref along0, u);
                var past = Unsafe.Add(ref past0, u);
                var x = Unsafe.Add(ref startX0, first) + along * (Unsafe.Add(ref slopeX0, first) + past * Unsafe.Add(ref curveX0, first));
                var y = Unsafe.Add(ref startY0, first) + along * (Unsafe.Add(ref slopeY0, first) + past * Unsafe.Add(ref curveY0, first));

                // The same pixel on every runtime, whatever its cast makes of NaN and of coordinates past the int range
                var px = PixelIndex.Clamp(x, farX, width);
                var py = PixelIndex.Clamp(y, farY, height);

                Unsafe.Add(ref o, u) = Unsafe.Add(ref lum, py * width + px) < limit ? (byte)1 : (byte)0;
            }
        }
    }

#if NET8_0_OR_GREATER
    // For each step of `lanes` columns: the first node of its first lane's three, that of its last lane's, and a lane mask
    // (all ones where the lane shares the last lane's). False when a step touches a third run of columns.
    private static bool TryBuildStepTable(ReadOnlySpan<int> firstOf, int dimension, int lanes, Span<int> stepFirst, Span<int> stepLast, Span<int> laneMasks)
    {
        for (int u = 0, j = 0; u + lanes <= dimension; u += lanes, j++)
        {
            var first = firstOf[u];
            var last = firstOf[u + lanes - 1];
            stepFirst[j] = first;
            stepLast[j] = last;
            for (var k = 0; k < lanes; k++)
            {
                var run = firstOf[u + k];
                if (run != first && run != last)
                    return false;
                laneMasks[u + k] = run == first ? 0 : -1;
            }
        }
        return true;
    }
#endif

    /// <summary>
    /// The lengths the unchecked loops rely on. Throws for a buffer too small to hold what the dimensions say, and returns false for a mesh the stack tables cannot hold, which the caller hands to the reference.
    /// </summary>
    private static bool CheckPiecewiseArguments(ReadOnlySpan<byte> luminance, int width, int height, ReadOnlySpan<float> gridCoords, ReadOnlySpan<float> nodeXs, ReadOnlySpan<float> nodeYs, int meshSize, int dimension, Span<byte> modules)
    {
        if (width < 1 || height < 1 || luminance.Length < (long)width * height)
            throw new ArgumentException($"Luminance buffer too small: required {(long)width * height}, got {luminance.Length}", nameof(luminance));
        if (dimension < 1 || modules.Length < (long)dimension * dimension)
            throw new ArgumentException($"Module buffer too small: required {(long)dimension * dimension}, got {modules.Length}", nameof(modules));

        return meshSize >= 3 && meshSize <= MaxMeshNodes && dimension <= MaxPiecewiseDimension
            && gridCoords.Length >= meshSize && nodeXs.Length >= meshSize * meshSize && nodeYs.Length >= meshSize * meshSize;
    }

    /// <summary>The first of the three lattice coordinates a grid coordinate is interpolated through: the cell it falls in, and the node beyond that cell's nearer end.</summary>
    private static int FirstOfThree(ReadOnlySpan<float> gridCoords, int meshSize, float at)
    {
        var cell = 0;
        while (cell < meshSize - 2 && at >= gridCoords[cell + 1])
            cell++;
        var first = at - gridCoords[cell] < gridCoords[cell + 1] - at ? cell - 1 : cell;
        return Math.Max(0, Math.Min(meshSize - 3, first));
    }

    /// <summary>The slope from the first of three values on a lattice line to the second. <paramref name="toSecond"/> is 1 over the spacing of those two nodes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SlopeOfThree(float p0, float p1, float toSecond)
        => (p1 - p0) * toSecond;

    /// <summary>The curvature through three values on a lattice line, how the slope changes from the first pair to the second, a module. <paramref name="toThird"/> is 1 over the spacing of the last two nodes and <paramref name="across"/> 1 over that of the first and the last.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float CurveOfThree(float slope, float p1, float p2, float toThird, float across)
        => ((p2 - p1) * toThird - slope) * across;

    // The mesh nodes at one row, each lattice column through three lattice rows: the reference's expressions.
    private static void InterpolateMeshRow(int first, float along, float past, int meshSize, ReadOnlySpan<float> toSecond, ReadOnlySpan<float> toThird, ReadOnlySpan<float> across, ReadOnlySpan<float> nodeXs, ReadOnlySpan<float> nodeYs, Span<float> rowXs, Span<float> rowYs)
    {
        var row0 = first * meshSize;
        var row1 = row0 + meshSize;
        var row2 = row1 + meshSize;
        var second = toSecond[first];
        var third = toThird[first];
        var whole = across[first];
        for (var i = 0; i < meshSize; i++)
        {
            var slopeX = SlopeOfThree(nodeXs[row0 + i], nodeXs[row1 + i], second);
            rowXs[i] = nodeXs[row0 + i] + along * (slopeX + past * CurveOfThree(slopeX, nodeXs[row1 + i], nodeXs[row2 + i], third, whole));
            var slopeY = SlopeOfThree(nodeYs[row0 + i], nodeYs[row1 + i], second);
            rowYs[i] = nodeYs[row0 + i] + along * (slopeY + past * CurveOfThree(slopeY, nodeYs[row1 + i], nodeYs[row2 + i], third, whole));
        }
    }

    // For each run of columns that shares its three nodes: the first node's position at this row, the slope to the second and the curvature through the third, with the reference's expressions.
    private static void RowCoefficients(int meshSize, ReadOnlySpan<float> toSecond, ReadOnlySpan<float> toThird, ReadOnlySpan<float> across, ReadOnlySpan<float> row, Span<float> start, Span<float> slope, Span<float> curve)
    {
        for (var first = 0; first < meshSize - 2; first++)
        {
            start[first] = row[first];
            slope[first] = SlopeOfThree(row[first], row[first + 1], toSecond[first]);
            curve[first] = CurveOfThree(slope[first], row[first + 1], row[first + 2], toThird[first], across[first]);
        }
    }

    // For each run, 1 over the spacing of its first two nodes, of its last two, and of its first and last: the reference's reciprocals, taken once a call.
    private static void BuildRunTable(ReadOnlySpan<float> gridCoords, int meshSize, Span<float> toSecond, Span<float> toThird, Span<float> across)
    {
        for (var first = 0; first < meshSize - 2; first++)
        {
            toSecond[first] = 1f / (gridCoords[first + 1] - gridCoords[first]);
            toThird[first] = 1f / (gridCoords[first + 2] - gridCoords[first + 1]);
            across[first] = 1f / (gridCoords[first + 2] - gridCoords[first]);
        }
    }

    // The first of a column's three nodes and its distances past that node and past the next, with the reference's expressions.
    private static void BuildColumnTable(ReadOnlySpan<float> gridCoords, int meshSize, int dimension, Span<int> firstOf, Span<float> alongOf, Span<float> pastOf)
    {
        for (var u = 0; u < dimension; u++)
        {
            var at = u + 0.5f;
            var first = FirstOfThree(gridCoords, meshSize, at);
            firstOf[u] = first;
            alongOf[u] = at - gridCoords[first];
            pastOf[u] = at - gridCoords[first + 1];
        }
    }
}
