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
    /// Eight modules a step. The start, the slope and the curvature of a run of columns that shares its three nodes are taken once a row and broadcast. A step that straddles two runs selects per lane with a mask that depends on the version only.
    /// </summary>
    /// <remarks>
    /// Each multiply and add stays a separate instruction: fused, a coordinate can differ from the reference's by an ulp and truncate into the next pixel.
    /// The reference takes each pixel through <see cref="ImageDecoders.PixelIndex.Clamp(float, int)"/>, the same on every runtime. Here the upper clamp is taken in float with the limit as the first operand of the minimum, so a NaN lane passes through, truncates to INT_MIN and is raised to 0 by the integer maximum, which is where the reference puts it. With the operands the other way round NaN becomes the limit and a different pixel.
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
        Span<int> stepFirst = stackalloc int[MaxPiecewiseDimension / 8];
        Span<int> stepLast = stackalloc int[MaxPiecewiseDimension / 8];
        Span<int> laneMasks = stackalloc int[MaxPiecewiseDimension];
        Span<float> toSecond = stackalloc float[MaxMeshNodes];
        Span<float> toThird = stackalloc float[MaxMeshNodes];
        Span<float> across = stackalloc float[MaxMeshNodes];
        BuildColumnTable(gridCoords, meshSize, dimension, firstOf, alongOf, pastOf);
        BuildRunTable(gridCoords, meshSize, toSecond, toThird, across);
        if (!TryBuildStepTable(firstOf, dimension, 8, stepFirst, stepLast, laneMasks))
        {
            // runs of columns narrower than a step: not an Annex E lattice, and the two-run select does not cover it
            SampleGridPiecewiseColumnTable(luminance, width, height, threshold, gridCoords, nodeXs, nodeYs, meshSize, dimension, modules);
            return;
        }

        var zero = Vector256<int>.Zero;
        var maxPx = Vector256.Create((float)(width - 1));
        var maxPy = Vector256.Create((float)(height - 1));
        var widthVector = Vector256.Create(width);
        var limitVector = Vector128.Create(threshold);
        var one = Vector128.Create((byte)1);
        ref var along0 = ref MemoryMarshal.GetReference(alongOf);
        ref var past0 = ref MemoryMarshal.GetReference(pastOf);
        ref var mask0 = ref MemoryMarshal.GetReference(laneMasks);
        ref var lum = ref MemoryMarshal.GetReference(luminance);
        ref var dst = ref MemoryMarshal.GetReference(modules);

        for (var v = 0; v < dimension; v++)
        {
            InterpolateMeshRow(firstOf[v], alongOf[v], pastOf[v], meshSize, toSecond, toThird, across, nodeXs, nodeYs, rowXs, rowYs);
            RowCoefficients(meshSize, toSecond, toThird, across, rowXs, startX, slopeX, curveX);
            RowCoefficients(meshSize, toSecond, toThird, across, rowYs, startY, slopeY, curveY);

            var rowBase = v * dimension;
            var u = 0;
            for (var j = 0; u + 8 <= dimension; u += 8, j++)
            {
                var a = stepFirst[j];
                var b = stepLast[j];
                var originX = Vector256.Create(startX[a]);
                var riseX = Vector256.Create(slopeX[a]);
                var bendX = Vector256.Create(curveX[a]);
                var originY = Vector256.Create(startY[a]);
                var riseY = Vector256.Create(slopeY[a]);
                var bendY = Vector256.Create(curveY[a]);
                if (a != b)
                {
                    var inLast = Vector256.LoadUnsafe(ref mask0, (nuint)u).AsSingle();
                    originX = Vector256.ConditionalSelect(inLast, Vector256.Create(startX[b]), originX);
                    riseX = Vector256.ConditionalSelect(inLast, Vector256.Create(slopeX[b]), riseX);
                    bendX = Vector256.ConditionalSelect(inLast, Vector256.Create(curveX[b]), bendX);
                    originY = Vector256.ConditionalSelect(inLast, Vector256.Create(startY[b]), originY);
                    riseY = Vector256.ConditionalSelect(inLast, Vector256.Create(slopeY[b]), riseY);
                    bendY = Vector256.ConditionalSelect(inLast, Vector256.Create(curveY[b]), bendY);
                }

                var along = Vector256.LoadUnsafe(ref along0, (nuint)u);
                var past = Vector256.LoadUnsafe(ref past0, (nuint)u);
                var x = originX + along * (riseX + past * bendX);
                var y = originY + along * (riseY + past * bendY);

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

            // row tail (1 or 5 modules), the reference's scalar body on the same per-run floats
            for (; u < dimension; u++)
            {
                var c = firstOf[u];
                var px = PixelIndex.Clamp(startX[c] + alongOf[u] * (slopeX[c] + pastOf[u] * curveX[c]), width);
                var py = PixelIndex.Clamp(startY[c] + alongOf[u] * (slopeY[c] + pastOf[u] * curveY[c]), height);

                modules[rowBase + u] = luminance[py * width + px] < threshold ? (byte)1 : (byte)0;
            }
        }
    }
}
#endif
