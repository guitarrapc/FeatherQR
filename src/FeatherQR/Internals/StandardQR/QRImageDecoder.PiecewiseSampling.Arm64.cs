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
    /// The same float operations in the reference's order, so the same pixel. What differs from the AVX2 tier is what this machine has. Byte-lane <c>LessThan</c> is the unsigned compare (cmhi), so no min identity is needed.
    /// The scalar cast is fcvtzs on ARM64 on every runtime, saturating with NaN to 0, so the row tail's plain cast and integer clamp give the pixel the reference's <c>PixelIndex.Clamp</c> does, without converting the limits to float. The vector conversion is the same instruction. Its clamp keeps the AVX2 tier's shape (the limit first in a float minimum, then an integer maximum with 0), which lands NaN and both infinities where the reference does.
    /// The pixel index is one integer multiply-add, exact. There is no wider form on this ISA.
    /// </remarks>
    internal static void SampleGridPiecewiseAdvSimd(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, ReadOnlySpan<float> gridCoords, ReadOnlySpan<float> nodeXs, ReadOnlySpan<float> nodeYs, int meshSize, int dimension, Span<byte> modules)
    {
        // A module's position is start + along * (slope + past * curvature) of its run of columns, in x and in y.
        //
        //   columns   u   u+1  u+2  u+3 | u+4 ... u+7       two four-lane steps
        //   indices   i0  i1   i2   i3  | i4  ... i7        eight scattered loads, packed low byte first
        //   modules   d0  d1   d2   d3    d4  ... d7        LessThan(word, threshold) & 1, one ulong store
        //
        // A step that touches three runs of columns, or a mesh past the stack tables, goes to the column-table tier.
        if (!AdvSimd.Arm64.IsSupported || width > MaxExactFloatSide || height > MaxExactFloatSide
            || !CheckPiecewiseArguments(luminance, width, height, gridCoords, nodeXs, nodeYs, meshSize, dimension, modules))
        {
            SampleGridPiecewiseColumnTable(luminance, width, height, threshold, gridCoords, nodeXs, nodeYs, meshSize, dimension, modules);
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
        Span<int> stepFirst = stackalloc int[MaxPiecewiseDimension / 4];
        Span<int> stepLast = stackalloc int[MaxPiecewiseDimension / 4];
        Span<int> laneMasks = stackalloc int[MaxPiecewiseDimension];
        Span<float> toSecond = stackalloc float[MaxMeshNodes];
        Span<float> toThird = stackalloc float[MaxMeshNodes];
        Span<float> across = stackalloc float[MaxMeshNodes];
        BuildColumnTable(gridCoords, meshSize, dimension, firstOf, alongOf, pastOf);
        BuildRunTable(gridCoords, meshSize, toSecond, toThird, across);
        if (!TryBuildStepTable(firstOf, dimension, 4, stepFirst, stepLast, laneMasks))
        {
            // runs of columns narrower than a step: not an Annex E lattice, and the two-run select does not cover it
            SampleGridPiecewiseColumnTable(luminance, width, height, threshold, gridCoords, nodeXs, nodeYs, meshSize, dimension, modules);
            return;
        }

        var zero = Vector128<int>.Zero;
        var maxPx = Vector128.Create((float)(width - 1));
        var maxPy = Vector128.Create((float)(height - 1));
        var widthVector = Vector128.Create(width);
        var limitVector = Vector128.Create(threshold);
        var one = Vector128.Create((byte)1);
        // Every table is read through one reference from here, with no bounds check. The lengths were checked above, a run's index is below meshSize − 2 ≤ 5, and a step index is below dimension / 4.
        ref var along0 = ref MemoryMarshal.GetReference(alongOf);
        ref var past0 = ref MemoryMarshal.GetReference(pastOf);
        ref var mask0 = ref MemoryMarshal.GetReference(laneMasks);
        ref var first0 = ref MemoryMarshal.GetReference(stepFirst);
        ref var last0 = ref MemoryMarshal.GetReference(stepLast);
        ref var startX0 = ref MemoryMarshal.GetReference(startX);
        ref var slopeX0 = ref MemoryMarshal.GetReference(slopeX);
        ref var curveX0 = ref MemoryMarshal.GetReference(curveX);
        ref var startY0 = ref MemoryMarshal.GetReference(startY);
        ref var slopeY0 = ref MemoryMarshal.GetReference(slopeY);
        ref var curveY0 = ref MemoryMarshal.GetReference(curveY);
        ref var lum = ref MemoryMarshal.GetReference(luminance);
        ref var dst = ref MemoryMarshal.GetReference(modules);

        for (var v = 0; v < dimension; v++)
        {
            InterpolateMeshRow(firstOf[v], alongOf[v], pastOf[v], meshSize, toSecond, toThird, across, nodeXs, nodeYs, rowXs, rowYs);
            RowCoefficients(meshSize, toSecond, toThird, across, rowXs, startX, slopeX, curveX);
            RowCoefficients(meshSize, toSecond, toThird, across, rowYs, startY, slopeY, curveY);
            var rowBase = v * dimension;
            var u = 0;
            var j = 0;
            for (; u + 8 <= dimension; u += 8, j += 2)
            {
                var low = StepIndexAdvSimd(ref startX0, ref slopeX0, ref curveX0, ref startY0, ref slopeY0, ref curveY0, Unsafe.Add(ref first0, j), Unsafe.Add(ref last0, j), ref mask0, ref along0, ref past0, u, maxPx, maxPy, zero, widthVector);
                var high = StepIndexAdvSimd(ref startX0, ref slopeX0, ref curveX0, ref startY0, ref slopeY0, ref curveY0, Unsafe.Add(ref first0, j + 1), Unsafe.Add(ref last0, j + 1), ref mask0, ref along0, ref past0, u + 4, maxPx, maxPy, zero, widthVector);
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
            // row tail: 1 module for even versions and 5 for odd ones (17 + 4·version), up to 7 for any other dimension, with the reference's scalar body on the same per-run floats
            for (; u < dimension; u++)
            {
                var c = firstOf[u];
                var px = (int)(startX[c] + alongOf[u] * (slopeX[c] + pastOf[u] * curveX[c]));
                var py = (int)(startY[c] + alongOf[u] * (slopeY[c] + pastOf[u] * curveY[c]));
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
    private static Vector128<uint> StepIndexAdvSimd(ref float startX0, ref float slopeX0, ref float curveX0, ref float startY0, ref float slopeY0, ref float curveY0, int a, int b, ref int mask0, ref float along0, ref float past0, int u, Vector128<float> maxPx, Vector128<float> maxPy, Vector128<int> zero, Vector128<int> widthVector)
    {
        // Where the step's last lane is in the next run, the lanes in it take that run's values through the lane mask. A step never touches a third run: the step table refuses such a lattice.
        var startX = Vector128.Create(Unsafe.Add(ref startX0, a));
        var slopeX = Vector128.Create(Unsafe.Add(ref slopeX0, a));
        var curveX = Vector128.Create(Unsafe.Add(ref curveX0, a));
        var startY = Vector128.Create(Unsafe.Add(ref startY0, a));
        var slopeY = Vector128.Create(Unsafe.Add(ref slopeY0, a));
        var curveY = Vector128.Create(Unsafe.Add(ref curveY0, a));
        if (a != b)
        {
            var inLast = Vector128.LoadUnsafe(ref mask0, (nuint)u).AsSingle();
            startX = Vector128.ConditionalSelect(inLast, Vector128.Create(Unsafe.Add(ref startX0, b)), startX);
            slopeX = Vector128.ConditionalSelect(inLast, Vector128.Create(Unsafe.Add(ref slopeX0, b)), slopeX);
            curveX = Vector128.ConditionalSelect(inLast, Vector128.Create(Unsafe.Add(ref curveX0, b)), curveX);
            startY = Vector128.ConditionalSelect(inLast, Vector128.Create(Unsafe.Add(ref startY0, b)), startY);
            slopeY = Vector128.ConditionalSelect(inLast, Vector128.Create(Unsafe.Add(ref slopeY0, b)), slopeY);
            curveY = Vector128.ConditionalSelect(inLast, Vector128.Create(Unsafe.Add(ref curveY0, b)), curveY);
        }
        var along = Vector128.LoadUnsafe(ref along0, (nuint)u);
        var past = Vector128.LoadUnsafe(ref past0, (nuint)u);
        // each multiply and add kept separate: fused, a coordinate can differ from the reference's by an ulp and truncate into the next pixel
        var x = startX + along * (slopeX + past * curveX);
        var y = startY + along * (slopeY + past * curveY);
        // a NaN lane comes out of the minimum as NaN whichever operand it is, converts to 0 and stays 0. The limit is first as in the AVX2 tier.
        var px = Vector128.Max(Vector128.ConvertToInt32(Vector128.Min(maxPx, x)), zero);
        var py = Vector128.Max(Vector128.ConvertToInt32(Vector128.Min(maxPy, y)), zero);
        return AdvSimd.MultiplyAdd(px, py, widthVector).AsUInt32();
    }
}
#endif
