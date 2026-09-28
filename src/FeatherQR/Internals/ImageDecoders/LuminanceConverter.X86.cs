#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// AVX2 luminance conversion: 32 pixels per iteration, bit-identical to the per-pixel loop in LuminanceConverter.cs.
/// </summary>
/// <remarks>
/// The BT.601 weights sum to 256 and every channel is a byte, so the exact weighted sum never exceeds 255 · 256 = 65,280 and fits in 16 bits unsigned.
/// That is what makes this vector form exact rather than approximate.
/// <para>
/// <c>pmaddubsw</c> multiplies adjacent byte pairs into signed 16-bit lanes, so it saturates above a pair weight of 128. The weights sum to 256, so a split into two pairs of exactly 128 exists: cut green as 51 + 99 and shuffle each pixel into the byte quad <c>[R, G, G, B]</c> against weights <c>[77, 51, 99, 29]</c>.
/// Each partial peaks at 255 · 128 = 32,640 and cannot saturate; <c>pmaddwd</c> with ones then adds the pair back to 77R + 150G + 29B exactly.
/// </para>
/// <para>
/// Alpha is handled without leaving the vector path in the two shapes that occur in practice.
/// Straight alpha: fully transparent pixels composite to white, and white is exactly luminance 255, so the pixel is replaced by 0xFFFFFFFF before the shuffle; a partial alpha falls back to the scalar formula for the rest of the row.
/// Premultiplied: the composite adds 255 − a to every channel, which (because the weights sum to 256) is exactly 256 · (255 − a) on the sum, so it collapses to adding 255 − a to the luminance — exact for every alpha, so that path never falls back.
/// Measured 14-30x over the per-pixel loop; see LuminanceConverterParityTest for the equivalence proof by test.
/// </para>
/// </remarks>
internal static partial class LuminanceConverter
{
    /// <summary>Pixels per iteration of the main loop.</summary>
    private const int BlockPixels = 32;

    /// <summary>
    /// pmaddubsw weights for the byte quad [R, G, G, B], repeated per pixel.
    /// Both pairs sum to exactly 128 (77+51 and 99+29), which is what keeps the 16-bit partials below the saturation point.
    /// </summary>
    private static Vector256<sbyte> Weights => Vector256.Create(
        (sbyte)77, 51, 99, 29, 77, 51, 99, 29, 77, 51, 99, 29, 77, 51, 99, 29,
        77, 51, 99, 29, 77, 51, 99, 29, 77, 51, 99, 29, 77, 51, 99, 29);

    /// <summary>
    /// vpshufb indices turning each BGRA pixel into [R, G, G, B].
    /// Indices are per 128-bit lane, so both lanes repeat 0..15.
    /// </summary>
    private static Vector256<byte> BgraShuffle => Vector256.Create(
        (byte)2, 1, 1, 0, 6, 5, 5, 4, 10, 9, 9, 8, 14, 13, 13, 12,
        2, 1, 1, 0, 6, 5, 5, 4, 10, 9, 9, 8, 14, 13, 13, 12);

    /// <summary>vpshufb indices turning each RGBA or RGB888x pixel into [R, G, G, B].</summary>
    private static Vector256<byte> RgbaShuffle => Vector256.Create(
        (byte)0, 1, 1, 2, 4, 5, 5, 6, 8, 9, 9, 10, 12, 13, 13, 14,
        0, 1, 1, 2, 4, 5, 5, 6, 8, 9, 9, 10, 12, 13, 13, 14);

    /// <summary>
    /// Converts with AVX2.
    /// <paramref name="bgra"/> selects the channel order, <paramref name="hasAlpha"/> is false for RGB888x (its fourth byte is padding).
    /// </summary>
    internal static void ConvertRgbaAvx2(ReadOnlySpan<byte> pixels, Span<byte> luminance, int width, int height, int rowBytes, bool bgra, bool hasAlpha, bool premultiplied)
    {
        // Two loops rather than one with a mode flag: a single fused method measured
        // 2-3x slower than either, on code size alone (5.7 KB vs 1.4 KB).
        if (hasAlpha && premultiplied)
            ConvertPremultipliedAvx2(pixels, luminance, width, height, rowBytes, bgra);
        else
            ConvertStraightAvx2(pixels, luminance, width, height, rowBytes, bgra, hasAlpha);
    }

    /// <summary>
    /// No alpha, or straight (non-premultiplied) alpha.
    /// Opaque and fully transparent pixels stay on the vector path; a partial alpha finishes the row scalar.
    /// </summary>
    private static void ConvertStraightAvx2(ReadOnlySpan<byte> pixels, Span<byte> luminance, int width, int height, int rowBytes, bool bgra, bool hasAlpha)
    {
        ref var src = ref MemoryMarshal.GetReference(pixels);
        ref var dst = ref MemoryMarshal.GetReference(luminance);
        var shuffle = bgra ? BgraShuffle : RgbaShuffle;
        var weights = Weights;
        var ones = Vector256.Create((short)1);
        var alphaMask = Vector256.Create(0xFF000000u).AsByte();
        var solidAlpha = Vector256.Create(255u);
        // packusdw and packuswb work per 128-bit lane, so the packed bytes arrive as
        // [a0-3, b0-3, c0-3, d0-3, a4-7, b4-7, c4-7, d4-7] in dword units.
        var order = Vector256.Create(0, 4, 1, 5, 2, 6, 3, 7);

        nint blockEnd = width & ~(BlockPixels - 1);
        nint octetEnd = width & ~7;
        var redShift = bgra ? 16 : 0;
        var blueShift = bgra ? 0 : 16;

        for (var y = 0; y < height; y++)
        {
            ref var row = ref Unsafe.Add(ref src, (nint)y * rowBytes);
            ref var dest = ref Unsafe.Add(ref dst, (nint)y * width);
            nint x = 0;

            for (; x < blockEnd; x += BlockPixels)
            {
                ref var p = ref Unsafe.Add(ref row, x * 4);
                var v0 = Vector256.LoadUnsafe(ref p);
                var v1 = Vector256.LoadUnsafe(ref p, 32);
                var v2 = Vector256.LoadUnsafe(ref p, 64);
                var v3 = Vector256.LoadUnsafe(ref p, 96);

                // One AND of all four vectors answers "every alpha is 255" at once.
                if (hasAlpha && ((v0 & v1 & v2 & v3) & alphaMask) != alphaMask
                    && !(TryWhiten(ref v0, solidAlpha) && TryWhiten(ref v1, solidAlpha)
                      && TryWhiten(ref v2, solidAlpha) && TryWhiten(ref v3, solidAlpha)))
                {
                    break;
                }

                var s0 = Luma(v0, shuffle, weights, ones);
                var s1 = Luma(v1, shuffle, weights, ones);
                var s2 = Luma(v2, shuffle, weights, ones);
                var s3 = Luma(v3, shuffle, weights, ones);
                var w0 = Avx2.ShiftRightLogical(Avx2.PackUnsignedSaturate(s0, s1), 8).AsInt16();
                var w1 = Avx2.ShiftRightLogical(Avx2.PackUnsignedSaturate(s2, s3), 8).AsInt16();
                Avx2.PermuteVar8x32(Avx2.PackUnsignedSaturate(w0, w1).AsInt32(), order).AsByte()
                    .StoreUnsafe(ref Unsafe.Add(ref dest, x));
            }

            // Row remainder: 8-pixel blocks, then one overlapping block that redoes a
            // few pixels with the same values rather than dropping to the scalar loop.
            if (x == blockEnd && x < width)
            {
                for (; x < octetEnd; x += 8)
                {
                    if (!TryOctetStraight(ref row, ref dest, x, hasAlpha, shuffle, weights, ones, alphaMask, solidAlpha))
                        break;
                }
                if (x == octetEnd && x < width && width >= 8
                    && TryOctetStraight(ref row, ref dest, width - 8, hasAlpha, shuffle, weights, ones, alphaMask, solidAlpha))
                {
                    x = width;
                }
            }

            ConvertScalarRange(ref row, ref dest, x, width, redShift, blueShift, hasAlpha, premultiplied: false);
        }
    }

    /// <summary>
    /// Premultiplied alpha.
    /// Never falls back: see the class remarks for why the composite collapses to adding 255 − a to the luminance.
    /// </summary>
    private static void ConvertPremultipliedAvx2(ReadOnlySpan<byte> pixels, Span<byte> luminance, int width, int height, int rowBytes, bool bgra)
    {
        ref var src = ref MemoryMarshal.GetReference(pixels);
        ref var dst = ref MemoryMarshal.GetReference(luminance);
        var shuffle = bgra ? BgraShuffle : RgbaShuffle;
        var weights = Weights;
        var ones = Vector256.Create((short)1);
        var order = Vector256.Create(0, 4, 1, 5, 2, 6, 3, 7);

        nint blockEnd = width & ~(BlockPixels - 1);
        nint octetEnd = width & ~7;
        var redShift = bgra ? 16 : 0;
        var blueShift = bgra ? 0 : 16;

        for (var y = 0; y < height; y++)
        {
            ref var row = ref Unsafe.Add(ref src, (nint)y * rowBytes);
            ref var dest = ref Unsafe.Add(ref dst, (nint)y * width);
            nint x = 0;

            for (; x < blockEnd; x += BlockPixels)
            {
                ref var p = ref Unsafe.Add(ref row, x * 4);
                var l0 = PremultipliedLuma(Vector256.LoadUnsafe(ref p), shuffle, weights, ones);
                var l1 = PremultipliedLuma(Vector256.LoadUnsafe(ref p, 32), shuffle, weights, ones);
                var l2 = PremultipliedLuma(Vector256.LoadUnsafe(ref p, 64), shuffle, weights, ones);
                var l3 = PremultipliedLuma(Vector256.LoadUnsafe(ref p, 96), shuffle, weights, ones);
                var packed = Avx2.PackUnsignedSaturate(
                    Avx2.PackUnsignedSaturate(l0, l1).AsInt16(),
                    Avx2.PackUnsignedSaturate(l2, l3).AsInt16());
                Avx2.PermuteVar8x32(packed.AsInt32(), order).AsByte()
                    .StoreUnsafe(ref Unsafe.Add(ref dest, x));
            }

            if (x == blockEnd && x < width)
            {
                for (; x < octetEnd; x += 8)
                    OctetPremultiplied(ref row, ref dest, x, shuffle, weights, ones);
                if (x == octetEnd && x < width && width >= 8)
                {
                    OctetPremultiplied(ref row, ref dest, width - 8, shuffle, weights, ones);
                    x = width;
                }
            }

            ConvertScalarRange(ref row, ref dest, x, width, redShift, blueShift, hasAlpha: true, premultiplied: true);
        }
    }

    /// <summary>The exact weighted sum per pixel, as one dword per pixel (at most 65,280).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Luma(Vector256<byte> v, Vector256<byte> shuffle, Vector256<sbyte> weights, Vector256<short> ones)
        => Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(Avx2.Shuffle(v, shuffle), weights), ones);

    /// <summary>
    /// Premultiplied luminance, already shifted and masked to a byte per dword: (S + 256 · (255 − a)) &gt;&gt; 8, with the scalar cast reproduced by the 8-bit mask.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> PremultipliedLuma(Vector256<byte> v, Vector256<byte> shuffle, Vector256<sbyte> weights, Vector256<short> ones)
    {
        // alpha << 8 lands at bits 8..15 after shifting the pixel right by 16.
        var alphaShifted = Avx2.And(Avx2.ShiftRightLogical(v.AsUInt32(), 16), Vector256.Create(0xFF00u));
        var white = Avx2.Subtract(Vector256.Create(0xFF00u), alphaShifted);
        var sums = Avx2.Add(Luma(v, shuffle, weights, ones).AsUInt32(), white);
        return Avx2.And(Avx2.ShiftRightLogical(sums, 8), Vector256.Create(0xFFu)).AsInt32();
    }

    /// <summary>
    /// Replaces every fully transparent pixel with white; false when some alpha is partial.
    /// Straight alpha only: there a = 0 gives (c·0 + 255·255)/255 = 255 whatever c was, while a premultiplied buffer that violates c ≤ a would not.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryWhiten(ref Vector256<byte> v, Vector256<uint> solidAlpha)
    {
        var alpha = Avx2.ShiftRightLogical(v.AsUInt32(), 24);
        var clear = Vector256.Equals(alpha, Vector256<uint>.Zero);
        if ((clear | Vector256.Equals(alpha, solidAlpha)) != Vector256<uint>.AllBitsSet)
            return false;
        v = Avx2.BlendVariable(v, Vector256<byte>.AllBitsSet, clear.AsByte());
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryOctetStraight(ref byte row, ref byte dest, nint x, bool hasAlpha, Vector256<byte> shuffle, Vector256<sbyte> weights, Vector256<short> ones, Vector256<byte> alphaMask, Vector256<uint> solidAlpha)
    {
        var v = Vector256.LoadUnsafe(ref Unsafe.Add(ref row, x * 4));
        if (hasAlpha && (v & alphaMask) != alphaMask && !TryWhiten(ref v, solidAlpha))
            return false;
        StoreOctet(ref dest, x, Luma(v, shuffle, weights, ones), shift: true);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void OctetPremultiplied(ref byte row, ref byte dest, nint x, Vector256<byte> shuffle, Vector256<sbyte> weights, Vector256<short> ones)
    {
        var v = Vector256.LoadUnsafe(ref Unsafe.Add(ref row, x * 4));
        StoreOctet(ref dest, x, PremultipliedLuma(v, shuffle, weights, ones), shift: false);
    }

    /// <summary>Packs 8 dword sums into 8 bytes and stores them; the two 128-bit lanes hold pixels 0-3 and 4-7.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreOctet(ref byte dest, nint x, Vector256<int> sums, bool shift)
    {
        var words = Avx2.PackUnsignedSaturate(sums, sums);
        if (shift)
            words = Avx2.ShiftRightLogical(words, 8);
        var bytes = Avx2.PackUnsignedSaturate(words.AsInt16(), words.AsInt16());
        var lo = bytes.GetLower().AsUInt32().ToScalar();
        var hi = bytes.GetUpper().AsUInt32().ToScalar();
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dest, x), lo | ((ulong)hi << 32));
    }

    /// <summary>
    /// Finishes a row from <paramref name="x"/> with the exact per-pixel formula.
    /// The shifts locate red and blue inside the little-endian pixel word; green is always at bit 8 and alpha at bit 24.
    /// </summary>
    private static void ConvertScalarRange(ref byte row, ref byte dest, nint x, int width, int redShift, int blueShift, bool hasAlpha, bool premultiplied)
    {
        for (; x < width; x++)
        {
            var pixel = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref row, x * 4));
            int r = (byte)(pixel >> redShift);
            int g = (byte)(pixel >> 8);
            int b = (byte)(pixel >> blueShift);

            if (hasAlpha)
            {
                int a = (byte)(pixel >> 24);
                if (a != 255)
                {
                    if (premultiplied)
                    {
                        var white = 255 - a;
                        r += white;
                        g += white;
                        b += white;
                    }
                    else
                    {
                        r = (r * a + 255 * (255 - a)) / 255;
                        g = (g * a + 255 * (255 - a)) / 255;
                        b = (b * a + 255 * (255 - a)) / 255;
                    }
                }
            }

            Unsafe.Add(ref dest, x) = (byte)((77 * r + 150 * g + 29 * b) >> 8);
        }
    }
}
#endif
