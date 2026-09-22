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
    /// <summary>
    /// Eight modules a step. A cell's start and span are taken once a row and broadcast; a step that straddles two cells selects per lane with a mask that depends on the version only.
    /// </summary>
    /// <remarks>
    /// The multiply and the add stay separate instructions: fused, a coordinate can differ from the reference's by an ulp and truncate into the next pixel.
    /// The float-to-int conversion follows the reference's cast, which changed in .NET 9 from the raw x64 conversion to a saturating one, so the tier has a form for each and the parity test runs on both.
    /// From .NET 9 the upper clamp is taken in float with the limit as the first operand of the minimum, so a NaN lane passes through, truncates to INT_MIN and is raised to 0 by the integer maximum, which is where the reference's saturating cast puts it; with the operands the other way round NaN becomes the limit and a different pixel.
    /// A gather was measured slower than eight scalar loads and would read past the last pixel.
    /// </remarks>
    internal static void SampleGridPiecewiseAvx2(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, ReadOnlySpan<float> gridCoords, ReadOnlySpan<float> nodeXs, ReadOnlySpan<float> nodeYs, int meshSize, int dimension, Span<byte> modules)
    {
        if (!Avx2.IsSupported || width > MaxExactFloatSide || height > MaxExactFloatSide
            || !CheckPiecewiseArguments(luminance, width, height, gridCoords, nodeXs, nodeYs, meshSize, dimension, modules))
        {
            SampleGridPiecewiseColumnTable(luminance, width, height, threshold, gridCoords, nodeXs, nodeYs, meshSize, dimension, modules);
            return;
        }

        var cells = meshSize - 1;
        Span<float> rowXs = stackalloc float[MaxMeshNodes];
        Span<float> rowYs = stackalloc float[MaxMeshNodes];
        Span<float> cellStartX = stackalloc float[MaxMeshNodes];
        Span<float> cellSpanX = stackalloc float[MaxMeshNodes];
        Span<float> cellStartY = stackalloc float[MaxMeshNodes];
        Span<float> cellSpanY = stackalloc float[MaxMeshNodes];
        Span<int> cellOf = stackalloc int[MaxPiecewiseDimension];
        Span<float> fractionOf = stackalloc float[MaxPiecewiseDimension];
        Span<int> stepFirst = stackalloc int[MaxPiecewiseDimension / 8];
        Span<int> stepLast = stackalloc int[MaxPiecewiseDimension / 8];
        Span<int> laneMasks = stackalloc int[MaxPiecewiseDimension];
        BuildColumnTable(gridCoords, cells, dimension, cellOf, fractionOf);
        if (!TryBuildStepTable(cellOf, dimension, 8, stepFirst, stepLast, laneMasks))
        {
            // cells narrower than a step: not an Annex E lattice, and the two-cell select does not cover it
            SampleGridPiecewiseColumnTable(luminance, width, height, threshold, gridCoords, nodeXs, nodeYs, meshSize, dimension, modules);
            return;
        }

        var zero = Vector256<int>.Zero;
#if NET9_0_OR_GREATER
        var maxPx = Vector256.Create((float)(width - 1));
        var maxPy = Vector256.Create((float)(height - 1));
#else
        var maxPxInt = Vector256.Create(width - 1);
        var maxPyInt = Vector256.Create(height - 1);
#endif
        var widthVector = Vector256.Create(width);
        var limitVector = Vector128.Create(threshold);
        var one = Vector128.Create((byte)1);
        ref var fraction0 = ref MemoryMarshal.GetReference(fractionOf);
        ref var mask0 = ref MemoryMarshal.GetReference(laneMasks);
        ref var lum = ref MemoryMarshal.GetReference(luminance);
        ref var dst = ref MemoryMarshal.GetReference(modules);

        var cellJ = 0;
        for (var v = 0; v < dimension; v++)
        {
            InterpolateMeshRow(v, ref cellJ, cells, meshSize, gridCoords, nodeXs, nodeYs, rowXs, rowYs);
            for (var c = 0; c < cells; c++)
            {
                cellStartX[c] = rowXs[c];
                cellSpanX[c] = rowXs[c + 1] - rowXs[c];
                cellStartY[c] = rowYs[c];
                cellSpanY[c] = rowYs[c + 1] - rowYs[c];
            }

            var rowBase = v * dimension;
            var u = 0;
            for (var j = 0; u + 8 <= dimension; u += 8, j++)
            {
                var a = stepFirst[j];
                var b = stepLast[j];
                var startX = Vector256.Create(cellStartX[a]);
                var spanX = Vector256.Create(cellSpanX[a]);
                var startY = Vector256.Create(cellStartY[a]);
                var spanY = Vector256.Create(cellSpanY[a]);
                if (a != b)
                {
                    var inLast = Vector256.LoadUnsafe(ref mask0, (nuint)u).AsSingle();
                    startX = Vector256.ConditionalSelect(inLast, Vector256.Create(cellStartX[b]), startX);
                    spanX = Vector256.ConditionalSelect(inLast, Vector256.Create(cellSpanX[b]), spanX);
                    startY = Vector256.ConditionalSelect(inLast, Vector256.Create(cellStartY[b]), startY);
                    spanY = Vector256.ConditionalSelect(inLast, Vector256.Create(cellSpanY[b]), spanY);
                }

                var s = Vector256.LoadUnsafe(ref fraction0, (nuint)u);
                var x = startX + spanX * s;
                var y = startY + spanY * s;

#if NET9_0_OR_GREATER
                // limit first: see remarks
                var px = Vector256.Max(Avx.ConvertToVector256Int32WithTruncation(Avx.Min(maxPx, x)), zero);
                var py = Vector256.Max(Avx.ConvertToVector256Int32WithTruncation(Avx.Min(maxPy, y)), zero);
#else
                // Before .NET 9 the reference's cast is the raw x64 conversion: INT_MIN for NaN and for anything out of
                // range on either side, which the clamp takes to 0. The same conversion here, then the same clamp.
                var px = Vector256.Max(Vector256.Min(Avx.ConvertToVector256Int32WithTruncation(x), maxPxInt), zero);
                var py = Vector256.Max(Vector256.Min(Avx.ConvertToVector256Int32WithTruncation(y), maxPyInt), zero);
#endif
                var index = (py * widthVector + px).AsUInt32();
                var low = index.GetLower();
                var high = index.GetUpper();

                var pixels = (ulong)Unsafe.Add(ref lum, low.GetElement(0))
                    | (ulong)Unsafe.Add(ref lum, low.GetElement(1)) << 8
                    | (ulong)Unsafe.Add(ref lum, low.GetElement(2)) << 16
                    | (ulong)Unsafe.Add(ref lum, low.GetElement(3)) << 24
                    | (ulong)Unsafe.Add(ref lum, high.GetElement(0)) << 32
                    | (ulong)Unsafe.Add(ref lum, high.GetElement(1)) << 40
                    | (ulong)Unsafe.Add(ref lum, high.GetElement(2)) << 48
                    | (ulong)Unsafe.Add(ref lum, high.GetElement(3)) << 56;
                var dark = Vector128.LessThan(Vector128.CreateScalar(pixels).AsByte(), limitVector) & one;
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, rowBase + u), dark.AsUInt64().ToScalar());
            }

            // row tail (1 or 5 modules), the reference's scalar body on the same per-cell floats
            for (; u < dimension; u++)
            {
                var c = cellOf[u];
                var s = fractionOf[u];
                var px = (int)(cellStartX[c] + cellSpanX[c] * s);
                var py = (int)(cellStartY[c] + cellSpanY[c] * s);
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
    /// ARM64 tier: the AVX2 tier's step on four lanes, two steps a loop body, their eight pixels packed into one word for one compare and one store.
    /// </summary>
    /// <remarks>
    /// The same float operations in the reference's order, so the same pixel; what differs from the AVX2 tier is what this machine has. Byte-lane <c>LessThan</c> is the unsigned compare (cmhi), so no min identity is needed.
    /// The scalar cast is fcvtzs on ARM64 on every runtime, saturating with NaN to 0, and so is the vector conversion, so this tier has one conversion form where the AVX2 tier has two; the clamp keeps the AVX2 tier's .NET 9 shape (the limit first in a float minimum, then an integer maximum with 0), which lands NaN and both infinities where the reference does.
    /// The pixel index is one integer multiply-add, exact. Measured on Apple M2 against the column-table tier: 0.46 to 0.50 at versions 14 to 40, upright and rotated; a per-cell broadcast kept as vectors and loading the pixels straight into lanes each measured level and were left out. Four lanes are half of the AVX2 tier's gain on that machine, and there is no wider form on this ISA.
    /// </remarks>
    internal static void SampleGridPiecewiseAdvSimd(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, ReadOnlySpan<float> gridCoords, ReadOnlySpan<float> nodeXs, ReadOnlySpan<float> nodeYs, int meshSize, int dimension, Span<byte> modules)
    {
        if (!AdvSimd.Arm64.IsSupported || width > MaxExactFloatSide || height > MaxExactFloatSide
            || !CheckPiecewiseArguments(luminance, width, height, gridCoords, nodeXs, nodeYs, meshSize, dimension, modules))
        {
            SampleGridPiecewiseColumnTable(luminance, width, height, threshold, gridCoords, nodeXs, nodeYs, meshSize, dimension, modules);
            return;
        }

        var cells = meshSize - 1;
        Span<float> rowXs = stackalloc float[MaxMeshNodes];
        Span<float> rowYs = stackalloc float[MaxMeshNodes];
        Span<float> cellStartX = stackalloc float[MaxMeshNodes];
        Span<float> cellSpanX = stackalloc float[MaxMeshNodes];
        Span<float> cellStartY = stackalloc float[MaxMeshNodes];
        Span<float> cellSpanY = stackalloc float[MaxMeshNodes];
        Span<int> cellOf = stackalloc int[MaxPiecewiseDimension];
        Span<float> fractionOf = stackalloc float[MaxPiecewiseDimension];
        Span<int> stepFirst = stackalloc int[MaxPiecewiseDimension / 4];
        Span<int> stepLast = stackalloc int[MaxPiecewiseDimension / 4];
        Span<int> laneMasks = stackalloc int[MaxPiecewiseDimension];
        BuildColumnTable(gridCoords, cells, dimension, cellOf, fractionOf);
        if (!TryBuildStepTable(cellOf, dimension, 4, stepFirst, stepLast, laneMasks))
        {
            // cells narrower than a step: not an Annex E lattice, and the two-cell select does not cover it
            SampleGridPiecewiseColumnTable(luminance, width, height, threshold, gridCoords, nodeXs, nodeYs, meshSize, dimension, modules);
            return;
        }

        var zero = Vector128<int>.Zero;
        var maxPx = Vector128.Create((float)(width - 1));
        var maxPy = Vector128.Create((float)(height - 1));
        var widthVector = Vector128.Create(width);
        var limitVector = Vector128.Create(threshold);
        var one = Vector128.Create((byte)1);
        // Every table through one reference from here: the lengths were checked above, a cell index is below
        // meshSize − 1 ≤ 6, a step index below dimension / 4, and a bounds check a step measured 3 to 6 % of the kernel
        ref var fraction0 = ref MemoryMarshal.GetReference(fractionOf);
        ref var mask0 = ref MemoryMarshal.GetReference(laneMasks);
        ref var first0 = ref MemoryMarshal.GetReference(stepFirst);
        ref var last0 = ref MemoryMarshal.GetReference(stepLast);
        ref var startX0 = ref MemoryMarshal.GetReference(cellStartX);
        ref var spanX0 = ref MemoryMarshal.GetReference(cellSpanX);
        ref var startY0 = ref MemoryMarshal.GetReference(cellStartY);
        ref var spanY0 = ref MemoryMarshal.GetReference(cellSpanY);
        ref var lum = ref MemoryMarshal.GetReference(luminance);
        ref var dst = ref MemoryMarshal.GetReference(modules);

        var cellJ = 0;
        for (var v = 0; v < dimension; v++)
        {
            InterpolateMeshRow(v, ref cellJ, cells, meshSize, gridCoords, nodeXs, nodeYs, rowXs, rowYs);
            for (var c = 0; c < cells; c++)
            {
                cellStartX[c] = rowXs[c];
                cellSpanX[c] = rowXs[c + 1] - rowXs[c];
                cellStartY[c] = rowYs[c];
                cellSpanY[c] = rowYs[c + 1] - rowYs[c];
            }
            var rowBase = v * dimension;
            var u = 0;
            var j = 0;
            for (; u + 8 <= dimension; u += 8, j += 2)
            {
                var low = StepIndexAdvSimd(ref startX0, ref spanX0, ref startY0, ref spanY0, Unsafe.Add(ref first0, j), Unsafe.Add(ref last0, j), ref mask0, ref fraction0, u, maxPx, maxPy, zero, widthVector);
                var high = StepIndexAdvSimd(ref startX0, ref spanX0, ref startY0, ref spanY0, Unsafe.Add(ref first0, j + 1), Unsafe.Add(ref last0, j + 1), ref mask0, ref fraction0, u + 4, maxPx, maxPy, zero, widthVector);
                var pixels = (ulong)Unsafe.Add(ref lum, (nuint)low.GetElement(0))
                    | (ulong)Unsafe.Add(ref lum, (nuint)low.GetElement(1)) << 8
                    | (ulong)Unsafe.Add(ref lum, (nuint)low.GetElement(2)) << 16
                    | (ulong)Unsafe.Add(ref lum, (nuint)low.GetElement(3)) << 24
                    | (ulong)Unsafe.Add(ref lum, (nuint)high.GetElement(0)) << 32
                    | (ulong)Unsafe.Add(ref lum, (nuint)high.GetElement(1)) << 40
                    | (ulong)Unsafe.Add(ref lum, (nuint)high.GetElement(2)) << 48
                    | (ulong)Unsafe.Add(ref lum, (nuint)high.GetElement(3)) << 56;
                var dark = Vector128.LessThan(Vector128.CreateScalar(pixels).AsByte(), limitVector) & one;
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, rowBase + u), dark.AsUInt64().ToScalar());
            }
            // row tail: 1 module for every Annex E version (17 + 4·version), up to 7 for any other dimension; the reference's scalar body on the same per-cell floats
            for (; u < dimension; u++)
            {
                var c = cellOf[u];
                var s = fractionOf[u];
                var px = (int)(cellStartX[c] + cellSpanX[c] * s);
                var py = (int)(cellStartY[c] + cellSpanY[c] * s);
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

    /// <summary>The pixel index of the four modules at column <paramref name="u"/>: the AVX2 step's arithmetic on four lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> StepIndexAdvSimd(ref float startX0, ref float spanX0, ref float startY0, ref float spanY0, int a, int b, ref int mask0, ref float fraction0, int u, Vector128<float> maxPx, Vector128<float> maxPy, Vector128<int> zero, Vector128<int> widthVector)
    {
        var startX = Vector128.Create(Unsafe.Add(ref startX0, a));
        var spanX = Vector128.Create(Unsafe.Add(ref spanX0, a));
        var startY = Vector128.Create(Unsafe.Add(ref startY0, a));
        var spanY = Vector128.Create(Unsafe.Add(ref spanY0, a));
        if (a != b)
        {
            var inLast = Vector128.LoadUnsafe(ref mask0, (nuint)u).AsSingle();
            startX = Vector128.ConditionalSelect(inLast, Vector128.Create(Unsafe.Add(ref startX0, b)), startX);
            spanX = Vector128.ConditionalSelect(inLast, Vector128.Create(Unsafe.Add(ref spanX0, b)), spanX);
            startY = Vector128.ConditionalSelect(inLast, Vector128.Create(Unsafe.Add(ref startY0, b)), startY);
            spanY = Vector128.ConditionalSelect(inLast, Vector128.Create(Unsafe.Add(ref spanY0, b)), spanY);
        }
        var s = Vector128.LoadUnsafe(ref fraction0, (nuint)u);
        // multiply and add kept separate: fused, a coordinate can differ from the reference's by an ulp and truncate into the next pixel
        var x = startX + spanX * s;
        var y = startY + spanY * s;
        // a NaN lane comes out of the minimum as NaN whichever operand it is, converts to 0 and stays 0; the limit is first as in the AVX2 tier
        var px = Vector128.Max(Vector128.ConvertToInt32(Vector128.Min(maxPx, x)), zero);
        var py = Vector128.Max(Vector128.ConvertToInt32(Vector128.Min(maxPy, y)), zero);
        return AdvSimd.MultiplyAdd(px, py, widthVector).AsUInt32();
    }

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
