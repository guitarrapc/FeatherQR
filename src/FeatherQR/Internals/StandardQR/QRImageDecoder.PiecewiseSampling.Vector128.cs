#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Internals.StandardQR;

internal static partial class QRImageDecoder
{
    /// <summary>
    /// 128-bit tier: <see cref="SampleGridPiecewiseAdvSimd"/>'s steps on portable vectors, for the targets with neither the AVX2 nor the ARM64
    /// tier. The same float operations in the reference's order, and each coordinate to its pixel through <see cref="VectorCast.ToPixel"/>,
    /// which lands where the reference's <see cref="PixelIndex.Clamp(float, int)"/> does for every float.
    /// </summary>
    internal static void SampleGridPiecewiseVector128(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, ReadOnlySpan<float> gridCoords, ReadOnlySpan<float> nodeXs, ReadOnlySpan<float> nodeYs, int meshSize, int dimension, Span<byte> modules)
    {
        if (!Vector128.IsHardwareAccelerated || width > MaxExactFloatSide || height > MaxExactFloatSide
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

        var lastX = Vector128.Create((float)(width - 1));
        var lastY = Vector128.Create((float)(height - 1));
        var widthVector = Vector128.Create(width);
        var limitVector = Vector128.Create(threshold);
        var one = Vector128.Create((byte)1);
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
                var low = StepIndexVector128(ref startX0, ref spanX0, ref startY0, ref spanY0, Unsafe.Add(ref first0, j), Unsafe.Add(ref last0, j), ref mask0, ref fraction0, u, lastX, lastY, widthVector);
                var high = StepIndexVector128(ref startX0, ref spanX0, ref startY0, ref spanY0, Unsafe.Add(ref first0, j + 1), Unsafe.Add(ref last0, j + 1), ref mask0, ref fraction0, u + 4, lastX, lastY, widthVector);
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
            // row tail: 1 module for even versions and 5 for odd ones; the reference's scalar body on the same per-cell floats
            for (; u < dimension; u++)
            {
                var c = cellOf[u];
                var s = fractionOf[u];
                var px = PixelIndex.Clamp(cellStartX[c] + cellSpanX[c] * s, width);
                var py = PixelIndex.Clamp(cellStartY[c] + cellSpanY[c] * s, height);
                modules[rowBase + u] = luminance[py * width + px] < threshold ? (byte)1 : (byte)0;
            }
        }
    }

    /// <summary>The pixel index of the four modules at column <paramref name="u"/>: the ARM64 step on portable vectors.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> StepIndexVector128(ref float startX0, ref float spanX0, ref float startY0, ref float spanY0, int a, int b, ref int mask0, ref float fraction0, int u, Vector128<float> lastX, Vector128<float> lastY, Vector128<int> widthVector)
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
        return (VectorCast.ToPixel(y, lastY) * widthVector + VectorCast.ToPixel(x, lastX)).AsUInt32();
    }
}
#endif
