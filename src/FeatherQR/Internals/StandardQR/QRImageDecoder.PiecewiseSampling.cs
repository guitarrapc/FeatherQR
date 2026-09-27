using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
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
    /// Samples every module center through the piecewise-bilinear mesh.
    /// Bilinear interpolation is exact at the nodes, continuous across cell edges (adjacent cells share the same edge interpolation, unlike per-cell homographies), and division-free per module; within ~20-module cells its deviation from the true projective map is second order.
    /// Modules outside the lattice (borders, ≤ 6 modules) extrapolate the nearest cell.
    /// </summary>
    /// <remarks>
    /// Same module grid as <see cref="SampleGridPiecewiseScalar"/>, which means the same pixel for every module: the tiers do the reference's float operations in the reference's order and move only where they happen.
    /// A mesh the tiers' stack tables cannot hold goes to the reference.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when <paramref name="luminance"/> is smaller than width × height or <paramref name="modules"/> smaller than dimension².</exception>
    internal static void SampleGridPiecewise(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, ReadOnlySpan<float> gridCoords, ReadOnlySpan<float> nodeXs, ReadOnlySpan<float> nodeYs, int meshSize, int dimension, Span<byte> modules)
    {
#if NET8_0_OR_GREATER
        if (Avx2.IsSupported)
        {
            SampleGridPiecewiseAvx2(luminance, width, height, threshold, gridCoords, nodeXs, nodeYs, meshSize, dimension, modules);
            return;
        }
        if (AdvSimd.Arm64.IsSupported)
        {
            SampleGridPiecewiseAdvSimd(luminance, width, height, threshold, gridCoords, nodeXs, nodeYs, meshSize, dimension, modules);
            return;
        }
#endif
        SampleGridPiecewiseColumnTable(luminance, width, height, threshold, gridCoords, nodeXs, nodeYs, meshSize, dimension, modules);
    }

    /// <summary>
    /// Portable tier: the cell a column falls in and the fraction across it are the same for every row, so they are tabled once a call and the division leaves the module loop.
    /// </summary>
    internal static void SampleGridPiecewiseColumnTable(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, ReadOnlySpan<float> gridCoords, ReadOnlySpan<float> nodeXs, ReadOnlySpan<float> nodeYs, int meshSize, int dimension, Span<byte> modules)
    {
        if (!CheckPiecewiseArguments(luminance, width, height, gridCoords, nodeXs, nodeYs, meshSize, dimension, modules))
        {
            SampleGridPiecewiseScalar(luminance, width, height, threshold, gridCoords, nodeXs, nodeYs, meshSize, dimension, modules);
            return;
        }

        var cells = meshSize - 1;
        Span<float> rowXs = stackalloc float[MaxMeshNodes];
        Span<float> rowYs = stackalloc float[MaxMeshNodes];
        Span<int> cellOf = stackalloc int[MaxPiecewiseDimension];
        Span<float> fractionOf = stackalloc float[MaxPiecewiseDimension];
        BuildColumnTable(gridCoords, cells, dimension, cellOf, fractionOf);

        // Unchecked from here: a pixel index is clamped into width × height, a cell index is below
        // meshSize − 1 ≤ 6, and both lengths were checked above.
        ref var lum = ref MemoryMarshal.GetReference(luminance);
        ref var dst = ref MemoryMarshal.GetReference(modules);
        ref var cell0 = ref MemoryMarshal.GetReference(cellOf);
        ref var fraction0 = ref MemoryMarshal.GetReference(fractionOf);
        ref var rowX0 = ref MemoryMarshal.GetReference(rowXs);
        ref var rowY0 = ref MemoryMarshal.GetReference(rowYs);
        int limit = threshold;

        var cellJ = 0;
        for (var v = 0; v < dimension; v++)
        {
            InterpolateMeshRow(v, ref cellJ, cells, meshSize, gridCoords, nodeXs, nodeYs, rowXs, rowYs);

            ref var o = ref Unsafe.Add(ref dst, v * dimension);
            for (var u = 0; u < dimension; u++)
            {
                var cellI = Unsafe.Add(ref cell0, u);
                var s = Unsafe.Add(ref fraction0, u);
                var x0 = Unsafe.Add(ref rowX0, cellI);
                var y0 = Unsafe.Add(ref rowY0, cellI);
                var x = x0 + (Unsafe.Add(ref rowX0, cellI + 1) - x0) * s;
                var y = y0 + (Unsafe.Add(ref rowY0, cellI + 1) - y0) * s;

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

                Unsafe.Add(ref o, u) = Unsafe.Add(ref lum, py * width + px) < limit ? (byte)1 : (byte)0;
            }
        }
    }

#if NET8_0_OR_GREATER
    // For each step of `lanes` columns: the cell of its first lane, the cell of its last, and a lane mask (all
    // ones where the lane is in the last lane's cell). False when a step touches a third cell.
    private static bool TryBuildStepTable(ReadOnlySpan<int> cellOf, int dimension, int lanes, Span<int> stepFirst, Span<int> stepLast, Span<int> laneMasks)
    {
        for (int u = 0, j = 0; u + lanes <= dimension; u += lanes, j++)
        {
            var first = cellOf[u];
            var last = cellOf[u + lanes - 1];
            stepFirst[j] = first;
            stepLast[j] = last;
            for (var k = 0; k < lanes; k++)
            {
                var cell = cellOf[u + k];
                if (cell != first && cell != last)
                    return false;
                laneMasks[u + k] = cell == first ? 0 : -1;
            }
        }
        return true;
    }
#endif

    /// <summary>
    /// The lengths the unchecked loops rely on. Throws for a buffer too small to hold what the dimensions say; returns false for a mesh the stack tables cannot hold, which the caller hands to the reference.
    /// </summary>
    private static bool CheckPiecewiseArguments(ReadOnlySpan<byte> luminance, int width, int height, ReadOnlySpan<float> gridCoords, ReadOnlySpan<float> nodeXs, ReadOnlySpan<float> nodeYs, int meshSize, int dimension, Span<byte> modules)
    {
        if (width < 1 || height < 1 || luminance.Length < (long)width * height)
            throw new ArgumentException($"Luminance buffer too small: required {(long)width * height}, got {luminance.Length}", nameof(luminance));
        if (dimension < 1 || modules.Length < (long)dimension * dimension)
            throw new ArgumentException($"Module buffer too small: required {(long)dimension * dimension}, got {modules.Length}", nameof(modules));

        return meshSize >= 2 && meshSize <= MaxMeshNodes && dimension <= MaxPiecewiseDimension
            && gridCoords.Length >= meshSize && nodeXs.Length >= meshSize * meshSize && nodeYs.Length >= meshSize * meshSize;
    }

    // Which cell a row is in and the mesh nodes interpolated at it: the reference's expressions.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InterpolateMeshRow(int v, ref int cellJ, int cells, int meshSize, ReadOnlySpan<float> gridCoords, ReadOnlySpan<float> nodeXs, ReadOnlySpan<float> nodeYs, Span<float> rowXs, Span<float> rowYs)
    {
        var gridY = v + 0.5f;
        while (cellJ < cells - 1 && gridY >= gridCoords[cellJ + 1])
            cellJ++;
        var t = (gridY - gridCoords[cellJ]) / (gridCoords[cellJ + 1] - gridCoords[cellJ]);
        for (var i = 0; i < meshSize; i++)
        {
            var node0 = cellJ * meshSize + i;
            var node1 = (cellJ + 1) * meshSize + i;
            rowXs[i] = nodeXs[node0] + (nodeXs[node1] - nodeXs[node0]) * t;
            rowYs[i] = nodeYs[node0] + (nodeYs[node1] - nodeYs[node0]) * t;
        }
    }

    // The cell a column falls in and the fraction across it, with the reference's expression.
    private static void BuildColumnTable(ReadOnlySpan<float> gridCoords, int cells, int dimension, Span<int> cellOf, Span<float> fractionOf)
    {
        var cellI = 0;
        for (var u = 0; u < dimension; u++)
        {
            var gridX = u + 0.5f;
            while (cellI < cells - 1 && gridX >= gridCoords[cellI + 1])
                cellI++;
            cellOf[u] = cellI;
            fractionOf[u] = (gridX - gridCoords[cellI]) / (gridCoords[cellI + 1] - gridCoords[cellI]);
        }
    }
}
