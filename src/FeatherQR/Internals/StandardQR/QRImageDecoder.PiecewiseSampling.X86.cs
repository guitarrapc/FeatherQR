#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Internals.StandardQR;

internal static partial class QRImageDecoder
{
    /// <summary>
    /// Eight modules a step. A cell's start and span are taken once a row and broadcast; a step that straddles two cells selects per lane with a mask that depends on the version only.
    /// </summary>
    /// <remarks>
    /// The multiply and the add stay separate instructions: fused, a coordinate can differ from the reference's by an ulp and truncate into the next pixel.
    /// The reference takes each pixel through <see cref="ImageDecoders.PixelIndex.Clamp(float, int)"/>, the same on every runtime. Here the upper clamp is taken in float with the limit as the first operand of the minimum, so a NaN lane passes through, truncates to INT_MIN and is raised to 0 by the integer maximum, which is where the reference puts it; with the operands the other way round NaN becomes the limit and a different pixel.
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
        var maxPx = Vector256.Create((float)(width - 1));
        var maxPy = Vector256.Create((float)(height - 1));
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

                // limit first: see remarks
                var px = Vector256.Max(Avx.ConvertToVector256Int32WithTruncation(Avx.Min(maxPx, x)), zero);
                var py = Vector256.Max(Avx.ConvertToVector256Int32WithTruncation(Avx.Min(maxPy, y)), zero);
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
                var px = PixelIndex.Clamp(cellStartX[c] + cellSpanX[c] * s, width);
                var py = PixelIndex.Clamp(cellStartY[c] + cellSpanY[c] * s, height);

                modules[rowBase + u] = luminance[py * width + px] < threshold ? (byte)1 : (byte)0;
            }
        }
    }
}
#endif
