#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// ARM64 / NEON luminance conversion: 16 pixels per iteration, bit-identical to the per-pixel loop in LuminanceConverter.cs.
/// </summary>
/// <remarks>
/// The exactness argument is the one the AVX2 kernel uses: the BT.601 weights sum to 256 and every channel is a byte, so the weighted sum never exceeds 255 · 256 = 65,280 and fits in 16 bits unsigned.
/// <para>
/// The kernel shape, however, is not the AVX2 one.
/// NEON has no <c>pmaddubsw</c>, so the shuffle-into-<c>[R, G, G, B]</c> trick has nothing to feed; it has <c>UDOT</c> instead (ARMv8.2 dot product), which multiplies four byte pairs into each 32-bit lane and therefore computes <c>77R + 150G + 29B + 0·A</c> for four whole pixels in one instruction, with no deinterleave at all.
/// Weighting alpha 0 is also what makes Rgb888x correct, since its fourth byte is padding.
/// A transliteration of the x64 kernel using <c>LD4</c> to build channel planes was measured and lost by 1.2-1.5x.
/// </para>
/// <para>
/// Alpha is handled by row mode rather than per block.
/// Testing "is every alpha 255?" every 16 pixels costs a cross-lane reduction plus a vector-to-GPR move, which was measured at 27 % of the kernel; instead each row is converted optimistically while the pixels are ANDed into a vector accumulator, and one cross-lane test per row decides whether the row was in fact opaque.
/// A row that carries alpha is redone by the general path and the mode is sticky, so an image with alpha pays the wasted pass once rather than once per row.
/// </para>
/// <para>
/// The general path keeps every alpha shape on the vector unit.
/// Fully transparent composites to white, which is exactly luminance 255, so those pixels are replaced by 0xFFFFFFFF; a genuinely partial alpha uses the exact composite <c>c' = 255 − ceil((255 − c)·a / 255)</c>, which is equal to the scalar <c>(c·a + 255·(255 − a)) / 255</c> and stays integer-exact in 16-bit lanes because the product is at most 65,025 and <c>floor(t / 255) = (t + 1 + (t &gt;&gt; 8)) &gt;&gt; 8</c> there.
/// Premultiplied alpha adds 255 − a to every channel, which is exactly 256 · (255 − a) on the sum, so it collapses to an add before the shift and never falls back.
/// Measured 14-16x over the per-pixel loop on opaque input (Apple M2); see LuminanceConverterParityTest for the equivalence proof by test.
/// </para>
/// <para>
/// The row modes are separate methods on purpose.
/// Fusing them into one measured 2.3x <em>slower</em> on the largest inputs — on code size alone, in scenarios where the fused-in code never executed — which is the same instruction-cache failure the AVX2 kernel records for its fused variant.
/// </para>
/// </remarks>
internal static partial class LuminanceConverter
{
    /// <summary>Pixels per iteration of the NEON loops (four 128-bit loads).</summary>
    private const int AdvSimdBlockPixels = 16;

    /// <summary>
    /// UDOT weights in pixel memory order, alpha weighing 0.
    /// </summary>
    private static Vector128<byte> AdvSimdWeights(bool bgra) => bgra
        ? Vector128.Create((byte)29, 150, 77, 0, 29, 150, 77, 0, 29, 150, 77, 0, 29, 150, 77, 0)
        : Vector128.Create((byte)77, 150, 29, 0, 77, 150, 29, 0, 77, 150, 29, 0, 77, 150, 29, 0);

    /// <summary>
    /// Converts with NEON.
    /// <paramref name="bgra"/> selects the channel order, <paramref name="hasAlpha"/> is false for RGB888x (its fourth byte is padding).
    /// Requires <paramref name="width"/> ≥ 16; the caller keeps narrower rows scalar.
    /// </summary>
    internal static void ConvertRgbaAdvSimd(ReadOnlySpan<byte> pixels, Span<byte> luminance, int width, int height, int rowBytes, bool bgra, bool hasAlpha, bool premultiplied)
    {
        if (!hasAlpha)
            ConvertNoAlphaAdvSimd(pixels, luminance, width, height, rowBytes, bgra);
        else if (premultiplied)
            ConvertPremultipliedAdvSimd(pixels, luminance, width, height, rowBytes, bgra);
        else
            ConvertStraightAdvSimd(pixels, luminance, width, height, rowBytes, bgra);
    }

    /// <summary>RGB888x: no alpha to test, so no accumulator and no row mode.</summary>
    private static void ConvertNoAlphaAdvSimd(ReadOnlySpan<byte> pixels, Span<byte> luminance, int width, int height, int rowBytes, bool bgra)
    {
        ref var src = ref MemoryMarshal.GetReference(pixels);
        ref var dst = ref MemoryMarshal.GetReference(luminance);
        var weights = AdvSimdWeights(bgra);
        nint blockEnd = width & ~(AdvSimdBlockPixels - 1);
        nint tail = width - AdvSimdBlockPixels;
        var hasTail = blockEnd < width;

        for (var y = 0; y < height; y++)
        {
            ref var row = ref Unsafe.Add(ref src, (nint)y * rowBytes);
            ref var dest = ref Unsafe.Add(ref dst, (nint)y * width);
            for (nint x = 0; x < blockEnd; x += AdvSimdBlockPixels)
                PlainBlockAdvSimd(ref row, ref dest, x, weights);
            if (hasTail)
                PlainBlockAdvSimd(ref row, ref dest, tail, weights);
        }
    }

    /// <summary>Premultiplied alpha: exact for every alpha, so this never branches.</summary>
    private static void ConvertPremultipliedAdvSimd(ReadOnlySpan<byte> pixels, Span<byte> luminance, int width, int height, int rowBytes, bool bgra)
    {
        ref var src = ref MemoryMarshal.GetReference(pixels);
        ref var dst = ref MemoryMarshal.GetReference(luminance);
        var weights = AdvSimdWeights(bgra);
        nint blockEnd = width & ~(AdvSimdBlockPixels - 1);
        nint tail = width - AdvSimdBlockPixels;
        var hasTail = blockEnd < width;

        for (var y = 0; y < height; y++)
        {
            ref var row = ref Unsafe.Add(ref src, (nint)y * rowBytes);
            ref var dest = ref Unsafe.Add(ref dst, (nint)y * width);
            for (nint x = 0; x < blockEnd; x += AdvSimdBlockPixels)
                PremultipliedBlockAdvSimd(ref row, ref dest, x, weights);
            if (hasTail)
                PremultipliedBlockAdvSimd(ref row, ref dest, tail, weights);
        }
    }

    /// <summary>
    /// Straight alpha, in three sticky row modes: optimistic (no alpha handling, one cross-lane test per row), classified (per block: opaque, whiten, or composite), and composite-only once a row has shown the classification to be pointless.
    /// </summary>
    private static void ConvertStraightAdvSimd(ReadOnlySpan<byte> pixels, Span<byte> luminance, int width, int height, int rowBytes, bool bgra)
    {
        ref var src = ref MemoryMarshal.GetReference(pixels);
        ref var dst = ref MemoryMarshal.GetReference(luminance);
        var weights = AdvSimdWeights(bgra);
        var alphaMask = Vector128.Create(0xFF000000u).AsByte();
        nint blockEnd = width & ~(AdvSimdBlockPixels - 1);
        nint tail = width - AdvSimdBlockPixels;
        var hasTail = blockEnd < width;
        var optimistic = true;
        var compositeOnly = false;

        for (var y = 0; y < height; y++)
        {
            ref var row = ref Unsafe.Add(ref src, (nint)y * rowBytes);
            ref var dest = ref Unsafe.Add(ref dst, (nint)y * width);

            if (optimistic)
            {
                // Converts the row unconditionally and reports what the alphas were;
                // every stored byte is already correct if they were all 255.
                var and = Vector128<byte>.AllBitsSet;
                for (nint x = 0; x < blockEnd; x += AdvSimdBlockPixels)
                    and &= PlainBlockAdvSimd(ref row, ref dest, x, weights);
                if (hasTail)
                    and &= PlainBlockAdvSimd(ref row, ref dest, tail, weights);

                if ((and & alphaMask) == alphaMask)
                    continue;
                optimistic = false;
            }

            if (compositeOnly)
            {
                CompositeRowAdvSimd(ref row, ref dest, blockEnd, tail, hasTail, bgra);
                continue;
            }

            var composited = false;
            for (nint x = 0; x < blockEnd; x += AdvSimdBlockPixels)
                composited |= ClassifiedBlockAdvSimd(ref row, ref dest, x, weights, bgra, alphaMask);
            if (hasTail)
                composited |= ClassifiedBlockAdvSimd(ref row, ref dest, tail, weights, bgra, alphaMask);
            compositeOnly = composited;
        }
    }

    /// <summary>The exact weighted sum for four pixels, one dword each (at most 65,280).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> LumaAdvSimd(Vector128<byte> v, Vector128<byte> weights)
        => Dp.DotProduct(Vector128<uint>.Zero, v, weights);

    /// <summary>
    /// 16 pixels with no alpha handling, returning the AND of the four pixel vectors so a straight-alpha caller can accumulate it and test the whole row at once.
    /// RGB888x callers discard it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> PlainBlockAdvSimd(ref byte row, ref byte dest, nint x, Vector128<byte> weights)
    {
        ref var p = ref Unsafe.Add(ref row, x * 4);
        var v0 = Vector128.LoadUnsafe(ref p);
        var v1 = Vector128.LoadUnsafe(ref p, 16);
        var v2 = Vector128.LoadUnsafe(ref p, 32);
        var v3 = Vector128.LoadUnsafe(ref p, 48);
        StoreBlockAdvSimd(ref dest, x, LumaAdvSimd(v0, weights), LumaAdvSimd(v1, weights), LumaAdvSimd(v2, weights), LumaAdvSimd(v3, weights));
        return (v0 & v1) & (v2 & v3);
    }

    /// <summary>16 premultiplied pixels.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PremultipliedBlockAdvSimd(ref byte row, ref byte dest, nint x, Vector128<byte> weights)
    {
        ref var p = ref Unsafe.Add(ref row, x * 4);
        StoreBlockAdvSimd(ref dest, x,
            PremultipliedLumaAdvSimd(Vector128.LoadUnsafe(ref p), weights),
            PremultipliedLumaAdvSimd(Vector128.LoadUnsafe(ref p, 16), weights),
            PremultipliedLumaAdvSimd(Vector128.LoadUnsafe(ref p, 32), weights),
            PremultipliedLumaAdvSimd(Vector128.LoadUnsafe(ref p, 48), weights));
    }

    /// <summary>
    /// Premultiplied sum S + 256 · (255 − a), so the shared &gt;&gt; 8 yields (S &gt;&gt; 8) + (255 − a) and the narrowing reproduces the scalar byte cast.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> PremultipliedLumaAdvSimd(Vector128<byte> v, Vector128<byte> weights)
    {
        // alpha << 8 lands at bits 8..15 after shifting the pixel right by 16.
        var alphaShifted = AdvSimd.And(AdvSimd.ShiftRightLogical(v.AsUInt32(), 16), Vector128.Create(0xFF00u));
        var white = AdvSimd.Subtract(Vector128.Create(0xFF00u), alphaShifted);
        return AdvSimd.Add(LumaAdvSimd(v, weights), white);
    }

    /// <summary>
    /// One straight-alpha block, classified by what its alphas actually are: all 255 takes the plain dot product, all 0-or-255 takes whitening, anything else takes the exact composite.
    /// Returns whether the composite was needed, which is what makes the row mode sticky.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ClassifiedBlockAdvSimd(ref byte row, ref byte dest, nint x, Vector128<byte> weights, bool bgra, Vector128<byte> alphaMask)
    {
        ref var p = ref Unsafe.Add(ref row, x * 4);
        var v0 = Vector128.LoadUnsafe(ref p);
        var v1 = Vector128.LoadUnsafe(ref p, 16);
        var v2 = Vector128.LoadUnsafe(ref p, 32);
        var v3 = Vector128.LoadUnsafe(ref p, 48);

        if (((v0 & v1) & (v2 & v3) & alphaMask) == alphaMask || TryWhitenAdvSimd(ref v0, ref v1, ref v2, ref v3))
        {
            StoreBlockAdvSimd(ref dest, x, LumaAdvSimd(v0, weights), LumaAdvSimd(v1, weights), LumaAdvSimd(v2, weights), LumaAdvSimd(v3, weights));
            return false;
        }

        CompositeBlockAdvSimd(ref dest, x, v0, v1, v2, v3, bgra);
        return true;
    }

    /// <summary>
    /// Replaces every fully transparent pixel with white and reports whether every alpha was 0 or 255, with one cross-lane test for the whole block.
    /// Straight alpha only: there a = 0 gives (c·0 + 255·255)/255 = 255 whatever c was.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryWhitenAdvSimd(ref Vector128<byte> v0, ref Vector128<byte> v1, ref Vector128<byte> v2, ref Vector128<byte> v3)
    {
        var solid = Vector128.Create(255u);
        var a0 = AdvSimd.ShiftRightLogical(v0.AsUInt32(), 24);
        var a1 = AdvSimd.ShiftRightLogical(v1.AsUInt32(), 24);
        var a2 = AdvSimd.ShiftRightLogical(v2.AsUInt32(), 24);
        var a3 = AdvSimd.ShiftRightLogical(v3.AsUInt32(), 24);
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

        v0 = (v0.AsUInt32() | c0).AsByte();
        v1 = (v1.AsUInt32() | c1).AsByte();
        v2 = (v2.AsUInt32() | c2).AsByte();
        v3 = (v3.AsUInt32() | c3).AsByte();
        return true;
    }

    /// <summary>
    /// A whole composite row behind one call, so the body stays out of the straight-alpha method while the call is amortized over the row rather than paid every 16 pixels (which cost partial alpha 12 % when measured per block).
    /// The sticky row mode means only the first classified row still composites per block.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CompositeRowAdvSimd(ref byte row, ref byte dest, nint blockEnd, nint tail, bool hasTail, bool bgra)
    {
        for (nint x = 0; x < blockEnd; x += AdvSimdBlockPixels)
            CompositeBlockAdvSimd(ref row, ref dest, x, bgra);
        if (hasTail)
            CompositeBlockAdvSimd(ref row, ref dest, tail, bgra);
    }

    /// <summary>16 pixels through the exact straight-alpha composite.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CompositeBlockAdvSimd(ref byte row, ref byte dest, nint x, bool bgra)
    {
        ref var p = ref Unsafe.Add(ref row, x * 4);
        CompositeCoreAdvSimd(ref dest, x,
            Vector128.LoadUnsafe(ref p), Vector128.LoadUnsafe(ref p, 16),
            Vector128.LoadUnsafe(ref p, 32), Vector128.LoadUnsafe(ref p, 48), bgra);
    }

    /// <summary>
    /// The composite from four already-loaded pixel vectors.
    /// The channel planes are built with two rounds of UZP rather than LD4: LD4 has no ref-taking overload, and the pointer form is not worth enabling unsafe code across the library for a path only partial alpha reaches.
    /// The UZP ladder itself is free — the partial alpha scenario measured identically either way.
    /// </summary>
    /// <remarks>
    /// NoInlining is load-bearing here.
    /// This body is large, and inlining it into the classified block puts it inside the straight-alpha row loop, where a transparent-background image (which only ever whitens) and the large opaque images (which never reach the classified path at all) both pay for code they never execute: 63.5 µs against 41.9 and 89.8 against 72.0 when it was inlined.
    /// Only partial alpha calls this, so the call costs nothing where it happens.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CompositeBlockAdvSimd(ref byte dest, nint x, Vector128<byte> v0, Vector128<byte> v1, Vector128<byte> v2, Vector128<byte> v3, bool bgra)
        => CompositeCoreAdvSimd(ref dest, x, v0, v1, v2, v3, bgra);

    /// <summary>
    /// The composite body: inlined into <see cref="CompositeRowAdvSimd"/>, kept out of line everywhere else.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CompositeCoreAdvSimd(ref byte dest, nint x, Vector128<byte> v0, Vector128<byte> v1, Vector128<byte> v2, Vector128<byte> v3, bool bgra)
    {
        // Round 1 splits even from odd bytes, round 2 splits those again, leaving one
        // plane per byte position within the pixel.
        var e01 = AdvSimd.Arm64.UnzipEven(v0, v1);
        var e23 = AdvSimd.Arm64.UnzipEven(v2, v3);
        var o01 = AdvSimd.Arm64.UnzipOdd(v0, v1);
        var o23 = AdvSimd.Arm64.UnzipOdd(v2, v3);
        var c0 = AdvSimd.Arm64.UnzipEven(e01, e23);
        var c2 = AdvSimd.Arm64.UnzipOdd(e01, e23);
        var c1 = AdvSimd.Arm64.UnzipEven(o01, o23);
        var alpha = AdvSimd.Arm64.UnzipOdd(o01, o23);

        var r = CompositeWhiteAdvSimd(bgra ? c2 : c0, alpha);
        var g = CompositeWhiteAdvSimd(c1, alpha);
        var b = CompositeWhiteAdvSimd(bgra ? c0 : c2, alpha);

        var lo = AdvSimd.MultiplyWideningLower(r.GetLower(), Vector64.Create((byte)77));
        lo = AdvSimd.MultiplyWideningLowerAndAdd(lo, g.GetLower(), Vector64.Create((byte)150));
        lo = AdvSimd.MultiplyWideningLowerAndAdd(lo, b.GetLower(), Vector64.Create((byte)29));
        var hi = AdvSimd.MultiplyWideningUpper(r, Vector128.Create((byte)77));
        hi = AdvSimd.MultiplyWideningUpperAndAdd(hi, g, Vector128.Create((byte)150));
        hi = AdvSimd.MultiplyWideningUpperAndAdd(hi, b, Vector128.Create((byte)29));

        AdvSimd.ShiftRightLogicalNarrowingUpper(AdvSimd.ShiftRightLogicalNarrowingLower(lo, 8), hi, 8)
            .StoreUnsafe(ref Unsafe.Add(ref dest, x));
    }

    /// <summary>
    /// Composites one channel plane against white for straight alpha: 255 − ceil((255 − c)·a / 255), equal to the scalar (c·a + 255·(255 − a)) / 255 for every c and a — including a = 255, which returns c unchanged.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> CompositeWhiteAdvSimd(Vector128<byte> c, Vector128<byte> a)
    {
        var inv = AdvSimd.Not(c);
        var lo = DivideBy255CeilingAdvSimd(AdvSimd.MultiplyWideningLower(inv.GetLower(), a.GetLower()));
        var hi = DivideBy255CeilingAdvSimd(AdvSimd.MultiplyWideningUpper(inv, a));
        return AdvSimd.Not(AdvSimd.ExtractNarrowingUpper(AdvSimd.ExtractNarrowingLower(lo), hi));
    }

    /// <summary>
    /// ceil(d / 255) for d ≤ 65,025, as floor((d + 254) / 255); floor(t / 255) is exactly (t + 1 + (t &gt;&gt; 8)) &gt;&gt; 8 for every 16-bit t, and the largest intermediate here is 65,535, so nothing wraps.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> DivideBy255CeilingAdvSimd(Vector128<ushort> d)
    {
        var t = AdvSimd.Add(d, Vector128.Create((ushort)254));
        var u = AdvSimd.Add(AdvSimd.Add(t, AdvSimd.ShiftRightLogical(t, 8)), Vector128.Create((ushort)1));
        return AdvSimd.ShiftRightLogical(u, 8);
    }

    /// <summary>Narrows four dword sums (16 pixels) to 16 luminance bytes and stores them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreBlockAdvSimd(ref byte dest, nint x, Vector128<uint> s0, Vector128<uint> s1, Vector128<uint> s2, Vector128<uint> s3)
    {
        var lo = AdvSimd.ShiftRightLogicalNarrowingUpper(AdvSimd.ShiftRightLogicalNarrowingLower(s0, 8), s1, 8);
        var hi = AdvSimd.ShiftRightLogicalNarrowingUpper(AdvSimd.ShiftRightLogicalNarrowingLower(s2, 8), s3, 8);
        AdvSimd.ExtractNarrowingUpper(AdvSimd.ExtractNarrowingLower(lo), hi)
            .StoreUnsafe(ref Unsafe.Add(ref dest, x));
    }
}
#endif
