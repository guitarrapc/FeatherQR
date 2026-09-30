#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Wasm;
using System.Runtime.Intrinsics.X86;

namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// 128-bit luminance conversion for the targets with neither the AVX2 nor the ARM64 dot-product tier: 16 pixels per iteration,
/// bit-identical to the per-pixel loop in LuminanceConverter.cs.
/// </summary>
/// <remarks>
/// A pixel is one 32-bit lane. Its first and third channel go to the two 16-bit halves of the lane and green to the low half of another,
/// so the weighted sum is one 16-bit dot product, [c0, c2] · [77, 29] (BGRA swaps the weights), plus 150 · g in a plain 16-bit multiply.
/// The dot product is SSE2's pmaddwd and WebAssembly's i32x4.dot_i16x8_s, and a multiply and a fold of the two halves elsewhere.
/// Every product fits 16 bits (150 · 255 = 38,250) and the sum is at most 65,280, the exactness argument of the other tiers.
/// <para>
/// The alpha handling is the ARM64 tier's: rows converted optimistically while the pixels are ANDed into an accumulator, a row with alpha
/// classified block by block (opaque, all 0 or 255, or partial), the exact composite 255 − ceil((255 − c)·a / 255) in 16-bit lanes, and
/// premultiplied alpha as 256 · (255 − a) added to the sum, which the final narrowing wraps as the scalar byte cast does.
/// </para>
/// </remarks>
internal static partial class LuminanceConverter
{
    /// <summary>Pixels per iteration of the 128-bit loops (four 128-bit loads).</summary>
    private const int Vector128BlockPixels = 16;

    /// <summary>The first and third channel's weights as the two 16-bit halves of a lane.</summary>
    private static Vector128<uint> Vector128Weights(bool bgra) => Vector128.Create(bgra ? (77u << 16) | 29u : (29u << 16) | 77u);

    /// <summary>
    /// Converts with 128-bit vectors.
    /// <paramref name="bgra"/> selects the channel order, <paramref name="hasAlpha"/> is false for RGB888x (its fourth byte is padding).
    /// Requires <paramref name="width"/> ≥ 16; the caller keeps narrower rows scalar.
    /// </summary>
    internal static void ConvertRgbaVector128(ReadOnlySpan<byte> pixels, Span<byte> luminance, int width, int height, int rowBytes, bool bgra, bool hasAlpha, bool premultiplied)
    {
        if (!hasAlpha)
            ConvertNoAlphaVector128(pixels, luminance, width, height, rowBytes, bgra);
        else if (premultiplied)
            ConvertPremultipliedVector128(pixels, luminance, width, height, rowBytes, bgra);
        else
            ConvertStraightVector128(pixels, luminance, width, height, rowBytes, bgra);
    }

    /// <summary>RGB888x: no alpha to test, so no accumulator and no row mode.</summary>
    private static void ConvertNoAlphaVector128(ReadOnlySpan<byte> pixels, Span<byte> luminance, int width, int height, int rowBytes, bool bgra)
    {
        ref var src = ref MemoryMarshal.GetReference(pixels);
        ref var dst = ref MemoryMarshal.GetReference(luminance);
        var weights = Vector128Weights(bgra);
        nint blockEnd = width & ~(Vector128BlockPixels - 1);
        nint tail = width - Vector128BlockPixels;
        var hasTail = blockEnd < width;

        for (var y = 0; y < height; y++)
        {
            ref var row = ref Unsafe.Add(ref src, (nint)y * rowBytes);
            ref var dest = ref Unsafe.Add(ref dst, (nint)y * width);
            for (nint x = 0; x < blockEnd; x += Vector128BlockPixels)
                PlainBlockVector128(ref row, ref dest, x, weights);
            if (hasTail)
                PlainBlockVector128(ref row, ref dest, tail, weights);
        }
    }

    /// <summary>Premultiplied alpha: exact for every alpha, so this never branches.</summary>
    private static void ConvertPremultipliedVector128(ReadOnlySpan<byte> pixels, Span<byte> luminance, int width, int height, int rowBytes, bool bgra)
    {
        ref var src = ref MemoryMarshal.GetReference(pixels);
        ref var dst = ref MemoryMarshal.GetReference(luminance);
        var weights = Vector128Weights(bgra);
        nint blockEnd = width & ~(Vector128BlockPixels - 1);
        nint tail = width - Vector128BlockPixels;
        var hasTail = blockEnd < width;

        for (var y = 0; y < height; y++)
        {
            ref var row = ref Unsafe.Add(ref src, (nint)y * rowBytes);
            ref var dest = ref Unsafe.Add(ref dst, (nint)y * width);
            for (nint x = 0; x < blockEnd; x += Vector128BlockPixels)
                PremultipliedBlockVector128(ref row, ref dest, x, weights);
            if (hasTail)
                PremultipliedBlockVector128(ref row, ref dest, tail, weights);
        }
    }

    /// <summary>
    /// Straight alpha, in the ARM64 tier's three sticky row modes: optimistic (no alpha handling, one test per row), classified (per block:
    /// opaque, whiten, or composite), and composite-only once a row has shown the classification to be pointless.
    /// </summary>
    private static void ConvertStraightVector128(ReadOnlySpan<byte> pixels, Span<byte> luminance, int width, int height, int rowBytes, bool bgra)
    {
        ref var src = ref MemoryMarshal.GetReference(pixels);
        ref var dst = ref MemoryMarshal.GetReference(luminance);
        var weights = Vector128Weights(bgra);
        var alphaMask = Vector128.Create(0xFF000000u);
        nint blockEnd = width & ~(Vector128BlockPixels - 1);
        nint tail = width - Vector128BlockPixels;
        var hasTail = blockEnd < width;
        var optimistic = true;
        var compositeOnly = false;

        for (var y = 0; y < height; y++)
        {
            ref var row = ref Unsafe.Add(ref src, (nint)y * rowBytes);
            ref var dest = ref Unsafe.Add(ref dst, (nint)y * width);

            if (optimistic)
            {
                // Converts the row unconditionally and reports what the alphas were; every stored byte is already correct if they were all 255
                var and = Vector128<uint>.AllBitsSet;
                for (nint x = 0; x < blockEnd; x += Vector128BlockPixels)
                    and &= PlainBlockVector128(ref row, ref dest, x, weights);
                if (hasTail)
                    and &= PlainBlockVector128(ref row, ref dest, tail, weights);

                if ((and & alphaMask) == alphaMask)
                    continue;
                optimistic = false;
            }

            if (compositeOnly)
            {
                if (PackedSimd.IsSupported)
                {
                    // WebAssembly: the rest of the image to the scalar tier, see CompositeBlockVector128
                    ConvertRgbaScalar(pixels.Slice(y * rowBytes), luminance.Slice(y * width), width, height - y, rowBytes, bgra ? 2 : 0, 1, bgra ? 0 : 2, 3, premultiplied: false);
                    return;
                }
                CompositeRowVector128(ref row, ref dest, blockEnd, tail, hasTail, weights);
                continue;
            }

            var composited = false;
            for (nint x = 0; x < blockEnd; x += Vector128BlockPixels)
                composited |= ClassifiedBlockVector128(ref row, ref dest, x, weights, alphaMask, bgra);
            if (hasTail)
                composited |= ClassifiedBlockVector128(ref row, ref dest, tail, weights, alphaMask, bgra);
            compositeOnly = composited;
        }
    }

    /// <summary>The exact weighted sum for four pixels, one dword each (at most 65,280).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> LumaVector128(Vector128<uint> v, Vector128<uint> weights)
        => Dot16Vector128(v & Vector128.Create(0x00FF00FFu), weights) + GreenTermVector128((v >> 8) & Vector128.Create(0xFFu));

    /// <summary>150 · g for a green byte held alone in the low half of each lane.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> GreenTermVector128(Vector128<uint> green)
        => (green.AsUInt16() * Vector128.Create((ushort)150)).AsUInt32();

    /// <summary>
    /// Each lane's two 16-bit halves times the weights' halves, added: pmaddwd on x64 and i32x4.dot_i16x8_s on WebAssembly, one
    /// instruction each, and a multiply and a fold elsewhere (4.8x slower than the dot product on WebAssembly AOT). The halves are
    /// bytes and the weights small, so the signed instructions give the unsigned sum.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> Dot16Vector128(Vector128<uint> halves, Vector128<uint> weights)
    {
        if (Sse2.IsSupported)
            return Sse2.MultiplyAddAdjacent(halves.AsInt16(), weights.AsInt16()).AsUInt32();
        if (PackedSimd.IsSupported)
            return PackedSimd.Dot(halves.AsInt16(), weights.AsInt16()).AsUInt32();
        var products = (halves.AsUInt16() * weights.AsUInt16()).AsUInt32();
        return (products & Vector128.Create(0xFFFFu)) + (products >> 16);
    }

    /// <summary>
    /// 16 pixels with no alpha handling, returning the AND of the four pixel vectors so a straight-alpha caller can accumulate it and test
    /// the whole row at once. RGB888x callers discard it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> PlainBlockVector128(ref byte row, ref byte dest, nint x, Vector128<uint> weights)
    {
        ref var p = ref Unsafe.As<byte, uint>(ref Unsafe.Add(ref row, x * 4));
        var v0 = Vector128.LoadUnsafe(ref p);
        var v1 = Vector128.LoadUnsafe(ref p, 4);
        var v2 = Vector128.LoadUnsafe(ref p, 8);
        var v3 = Vector128.LoadUnsafe(ref p, 12);
        StoreBlockVector128(ref dest, x, LumaVector128(v0, weights), LumaVector128(v1, weights), LumaVector128(v2, weights), LumaVector128(v3, weights), wrap: false);
        return (v0 & v1) & (v2 & v3);
    }

    /// <summary>16 premultiplied pixels: the sum plus 256 · (255 − a), whose shift can pass 255 where a channel exceeds its alpha.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PremultipliedBlockVector128(ref byte row, ref byte dest, nint x, Vector128<uint> weights)
    {
        ref var p = ref Unsafe.As<byte, uint>(ref Unsafe.Add(ref row, x * 4));
        StoreBlockVector128(ref dest, x,
            PremultipliedLumaVector128(Vector128.LoadUnsafe(ref p), weights),
            PremultipliedLumaVector128(Vector128.LoadUnsafe(ref p, 4), weights),
            PremultipliedLumaVector128(Vector128.LoadUnsafe(ref p, 8), weights),
            PremultipliedLumaVector128(Vector128.LoadUnsafe(ref p, 12), weights), wrap: true);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> PremultipliedLumaVector128(Vector128<uint> v, Vector128<uint> weights)
    {
        // alpha << 8 lands at bits 8..15 after shifting the pixel right by 16
        var white = Vector128.Create(0xFF00u) - ((v >> 16) & Vector128.Create(0xFF00u));
        return LumaVector128(v, weights) + white;
    }

    /// <summary>
    /// One straight-alpha block, classified by what its alphas are: all 255 takes the plain sum, all 0 or 255 whitening, anything else the
    /// exact composite. Returns whether the composite was needed, which is what makes the row mode sticky.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ClassifiedBlockVector128(ref byte row, ref byte dest, nint x, Vector128<uint> weights, Vector128<uint> alphaMask, bool bgra)
    {
        ref var p = ref Unsafe.As<byte, uint>(ref Unsafe.Add(ref row, x * 4));
        var v0 = Vector128.LoadUnsafe(ref p);
        var v1 = Vector128.LoadUnsafe(ref p, 4);
        var v2 = Vector128.LoadUnsafe(ref p, 8);
        var v3 = Vector128.LoadUnsafe(ref p, 12);

        if (((v0 & v1) & (v2 & v3) & alphaMask) == alphaMask || TryWhitenVector128(ref v0, ref v1, ref v2, ref v3))
        {
            StoreBlockVector128(ref dest, x, LumaVector128(v0, weights), LumaVector128(v1, weights), LumaVector128(v2, weights), LumaVector128(v3, weights), wrap: false);
            return false;
        }

        CompositeBlockVector128(ref row, ref dest, x, v0, v1, v2, v3, weights, bgra);
        return true;
    }

    /// <summary>
    /// Replaces every fully transparent pixel with white and reports whether every alpha was 0 or 255.
    /// Straight alpha only: there a = 0 gives (c·0 + 255·255)/255 = 255 whatever c was.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryWhitenVector128(ref Vector128<uint> v0, ref Vector128<uint> v1, ref Vector128<uint> v2, ref Vector128<uint> v3)
    {
        var solid = Vector128.Create(255u);
        var a0 = v0 >> 24;
        var a1 = v1 >> 24;
        var a2 = v2 >> 24;
        var a3 = v3 >> 24;
        var c0 = Vector128.Equals(a0, Vector128<uint>.Zero);
        var c1 = Vector128.Equals(a1, Vector128<uint>.Zero);
        var c2 = Vector128.Equals(a2, Vector128<uint>.Zero);
        var c3 = Vector128.Equals(a3, Vector128<uint>.Zero);
        var ok = (c0 | Vector128.Equals(a0, solid))
               & (c1 | Vector128.Equals(a1, solid))
               & (c2 | Vector128.Equals(a2, solid))
               & (c3 | Vector128.Equals(a3, solid));
        if (ok != Vector128<uint>.AllBitsSet)
            return false;

        v0 |= c0;
        v1 |= c1;
        v2 |= c2;
        v3 |= c3;
        return true;
    }

    /// <summary>A whole composite row behind one call, as in the ARM64 tier: the body stays out of the straight-alpha method.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CompositeRowVector128(ref byte row, ref byte dest, nint blockEnd, nint tail, bool hasTail, Vector128<uint> weights)
    {
        for (nint x = 0; x < blockEnd; x += Vector128BlockPixels)
            CompositeLoadedBlockVector128(ref row, ref dest, x, weights);
        if (hasTail)
            CompositeLoadedBlockVector128(ref row, ref dest, tail, weights);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CompositeLoadedBlockVector128(ref byte row, ref byte dest, nint x, Vector128<uint> weights)
    {
        ref var p = ref Unsafe.As<byte, uint>(ref Unsafe.Add(ref row, x * 4));
        CompositeCoreVector128(ref dest, x, Vector128.LoadUnsafe(ref p), Vector128.LoadUnsafe(ref p, 4), Vector128.LoadUnsafe(ref p, 8), Vector128.LoadUnsafe(ref p, 12), weights);
    }

    /// <summary>
    /// The composite from four loaded pixel vectors, kept out of the classified block as in the ARM64 tier: only partial alpha calls it.
    /// On WebAssembly the 16 pixels take the per-pixel formula instead: the vector composite ran 1.5x slower than it there, AOT-compiled
    /// and interpreted.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CompositeBlockVector128(ref byte row, ref byte dest, nint x, Vector128<uint> v0, Vector128<uint> v1, Vector128<uint> v2, Vector128<uint> v3, Vector128<uint> weights, bool bgra)
    {
        if (PackedSimd.IsSupported)
        {
            for (nint i = x; i < x + Vector128BlockPixels; i++)
                Unsafe.Add(ref dest, i) = StraightLuma(ref Unsafe.Add(ref row, i * 4), bgra);
            return;
        }
        CompositeCoreVector128(ref dest, x, v0, v1, v2, v3, weights);
    }

    /// <summary>One straight-alpha pixel through the scalar tier's formula.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte StraightLuma(ref byte pixel, bool bgra)
        => bgra ? Luma(ref pixel, 2, 1, 0, hasAlpha: true, premultiplied: false) : Luma(ref pixel, 0, 1, 2, hasAlpha: true, premultiplied: false);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CompositeCoreVector128(ref byte dest, nint x, Vector128<uint> v0, Vector128<uint> v1, Vector128<uint> v2, Vector128<uint> v3, Vector128<uint> weights)
        => StoreBlockVector128(ref dest, x, CompositeLumaVector128(v0, weights), CompositeLumaVector128(v1, weights), CompositeLumaVector128(v2, weights), CompositeLumaVector128(v3, weights), wrap: false);

    /// <summary>
    /// Four straight-alpha pixels composited against white, then weighted: each channel 255 − ceil((255 − c)·a / 255), equal to the
    /// scalar (c·a + 255·(255 − a)) / 255 for every c and a, a = 255 included.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> CompositeLumaVector128(Vector128<uint> v, Vector128<uint> weights)
    {
        var inverse = ~v;
        var alpha = v >> 24;
        var alphas = (alpha | (alpha << 16)).AsUInt16();
        var white = Vector128.Create((ushort)255);
        var firstAndThird = white - DivideBy255CeilingVector128((inverse & Vector128.Create(0x00FF00FFu)).AsUInt16() * alphas);
        // The high half of green's lane holds 255 − 0 after the composite, and is cleared before the multiply
        var green = (white - DivideBy255CeilingVector128(((inverse >> 8) & Vector128.Create(0xFFu)).AsUInt16() * alphas)).AsUInt32() & Vector128.Create(0xFFu);
        return Dot16Vector128(firstAndThird.AsUInt32(), weights) + GreenTermVector128(green);
    }

    /// <summary>
    /// ceil(d / 255) for d ≤ 65,025, as floor((d + 254) / 255); floor(t / 255) is exactly (t + 1 + (t &gt;&gt; 8)) &gt;&gt; 8 for every 16-bit t,
    /// and the largest intermediate here is 65,535, so nothing wraps.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> DivideBy255CeilingVector128(Vector128<ushort> d)
    {
        var t = d + Vector128.Create((ushort)254);
        return (t + (t >> 8) + Vector128<ushort>.One) >> 8;
    }

    /// <summary>
    /// Shifts four dword sums (16 pixels) down by 8 and stores their low bytes. The shifted sums are at most 510, so the signed packs are
    /// exact; with <paramref name="wrap"/> each is first cut to its low byte, as the scalar byte cast does with a premultiplied sum above 255.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreBlockVector128(ref byte dest, nint x, Vector128<uint> s0, Vector128<uint> s1, Vector128<uint> s2, Vector128<uint> s3, bool wrap)
    {
        var t0 = (s0 >> 8).AsInt32();
        var t1 = (s1 >> 8).AsInt32();
        var t2 = (s2 >> 8).AsInt32();
        var t3 = (s3 >> 8).AsInt32();
        Vector128<byte> bytes;
        if (Sse2.IsSupported)
        {
            var lo = Sse2.PackSignedSaturate(t0, t1);
            var hi = Sse2.PackSignedSaturate(t2, t3);
            if (wrap)
            {
                lo &= Vector128.Create((short)0xFF);
                hi &= Vector128.Create((short)0xFF);
            }
            bytes = Sse2.PackUnsignedSaturate(lo, hi);
        }
        else if (PackedSimd.IsSupported)
        {
            var lo = PackedSimd.ConvertNarrowingSaturateSigned(t0, t1);
            var hi = PackedSimd.ConvertNarrowingSaturateSigned(t2, t3);
            if (wrap)
            {
                lo &= Vector128.Create((short)0xFF);
                hi &= Vector128.Create((short)0xFF);
            }
            bytes = PackedSimd.ConvertNarrowingSaturateUnsigned(lo, hi);
        }
        else
        {
            bytes = Vector128.Narrow(Vector128.Narrow(t0.AsUInt32(), t1.AsUInt32()), Vector128.Narrow(t2.AsUInt32(), t3.AsUInt32()));
        }
        bytes.StoreUnsafe(ref Unsafe.Add(ref dest, x));
    }
}
#endif
