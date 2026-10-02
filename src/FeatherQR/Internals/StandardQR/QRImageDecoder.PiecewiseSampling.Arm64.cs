#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace FeatherQR.Internals.StandardQR;

internal static partial class QRImageDecoder
{
    /// <summary>
    /// ARM64 tier: the AVX2 tier's step on four lanes, two steps a loop body, their eight pixels packed into one word for one compare and one store.
    /// </summary>
    /// <remarks>
    /// The same float operations in the reference's order, so the same pixel; what differs from the AVX2 tier is what this machine has. Byte-lane <c>LessThan</c> is the unsigned compare (cmhi), so no min identity is needed.
    /// The scalar cast is fcvtzs on ARM64 on every runtime, saturating with NaN to 0, so the row tail's plain cast and integer clamp give the pixel the reference's <c>PixelIndex.Clamp</c> does, without converting the limits to float. The vector conversion is the same instruction; its clamp keeps the AVX2 tier's shape (the limit first in a float minimum, then an integer maximum with 0), which lands NaN and both infinities where the reference does.
    /// The pixel index is one integer multiply-add, exact. Measured on Apple M2 against the column-table tier: 0.46 to 0.51 at versions 14 to 40, upright and rotated; a per-cell broadcast kept as vectors and loading the pixels straight into lanes each measured level and were left out. Four lanes are half of the AVX2 tier's gain on that machine, and there is no wider form on this ISA.
    /// </remarks>
    internal static void SampleGridPiecewiseAdvSimd(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, ReadOnlySpan<float> gridCoords, ReadOnlySpan<float> nodeXs, ReadOnlySpan<float> nodeYs, int meshSize, int dimension, Span<byte> modules)
    {
        // The AVX2 tier's shape on this machine's 128-bit vectors: the same float operations in the reference's order, so every module comes from the same pixel, module for module.
        //
        // Per row: the mesh row's node positions (InterpolateMeshRow), then each cell's start and span in x and in y, so a module's position is start + span * fraction, the fraction tabled once a call (BuildColumnTable).
        // Per step of four columns (StepIndexAdvSimd): the cell's start and span broadcast, or selected a lane where the step straddles two cells, the lerp, the clamp, and the pixel index px + py * width.
        // Two steps a loop body: their eight indices become eight scalar byte loads packed into one word, one byte compare against the threshold gives the eight modules, and one store writes them.
        //
        //   columns   u   u+1  u+2  u+3 | u+4 ... u+7       two four-lane steps
        //   indices   i0  i1   i2   i3  | i4  ... i7        eight scattered loads, packed low byte first
        //   modules   d0  d1   d2   d3    d4  ... d7        LessThan(word, threshold) & 1, one ulong store
        //
        // What this machine changes: byte-lane LessThan is the unsigned compare, so no min identity; the scalar cast and the vector conversion are both fcvtzs (saturating, NaN to 0), so one conversion form serves every runtime where the AVX2 tier needs two. A step that touches three cells, or a mesh past the stack tables, goes to the column-table tier, as there.
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
        // Every table through one reference from here: the lengths were checked above, a cell index is below meshSize − 1 ≤ 6, a step index below dimension / 4, and a bounds check a step measured 3 to 6 % of the kernel
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
            // row tail: 1 module for even versions and 5 for odd ones (17 + 4·version), up to 7 for any other dimension; the reference's scalar body on the same per-cell floats
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
        // One step of four modules.
        // The cell of the step's first lane gives start and span; where the step's last lane is in the next cell, the lanes in it take that cell's values through the lane mask (a step never touches a third cell:
        // the step table refused such a lattice). Then the lerp as a separate multiply and add (fused, a coordinate can land an ulp off the reference's and truncate into the next pixel), the limit first in the minimum so a NaN lane converts to 0, the conversion, the clamp at 0, and px + py * width in one multiply-add.
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
}
#endif
