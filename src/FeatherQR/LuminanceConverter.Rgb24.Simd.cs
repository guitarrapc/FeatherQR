#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Wasm;
using System.Runtime.Intrinsics.X86;

namespace FeatherQR;

// 128-bit conversion of three-byte pixels for x64 without AVX2 and for WebAssembly: 16 pixels per iteration, bit-identical to the
// per-pixel loop in LuminanceConverter.Rgb24.cs.
// A byte shuffle spreads twelve bytes into four four-byte pixels, pshufb on x64 and i8x16.swizzle on WebAssembly, and the four-byte
// kernel (LuminanceConverter.Simd.cs) weighs them. Each spread pixel's fourth byte is whatever byte follows it, which that kernel masks off.
// Bgr24 takes the same shuffle, and the weights of its first and third channel exchanged.
//
// Sixteen pixels are 48 bytes, taken as four 16-byte loads that stay inside them:
//
//   load at 0   uses bytes 0-11  of the load     pixels 0-3
//   load at 12  uses bytes 0-11                  pixels 4-7
//   load at 24  uses bytes 0-11                  pixels 8-11
//   load at 32  uses bytes 4-15                  pixels 12-15, the load ending at the block's last byte
public static partial class LuminanceConverter
{
    /// <summary>
    /// Converts three-byte pixels with 128-bit vectors. <paramref name="bgr"/> selects the channel order.
    /// Requires <paramref name="width"/> ≥ 16. The caller keeps narrower rows scalar.
    /// </summary>
    internal static void ConvertRgb24Vector128(ReadOnlySpan<byte> pixels, Span<byte> luminance, int width, int height, int rowBytes, bool bgr)
    {
        ref var src = ref MemoryMarshal.GetReference(pixels);
        ref var dst = ref MemoryMarshal.GetReference(luminance);
        var weights = Vector128Weights(bgr);
        var front = Vector128.Create((byte)0, 1, 2, 3, 3, 4, 5, 6, 6, 7, 8, 9, 9, 10, 11, 12);
        var back = Vector128.Create((byte)4, 5, 6, 7, 7, 8, 9, 10, 10, 11, 12, 13, 13, 14, 15, 15);
        nint blockEnd = width & ~(Vector128BlockPixels - 1);
        nint tail = width - Vector128BlockPixels;
        var hasTail = blockEnd < width;

        for (var y = 0; y < height; y++)
        {
            ref var row = ref Unsafe.Add(ref src, (nint)y * rowBytes);
            ref var dest = ref Unsafe.Add(ref dst, (nint)y * width);
            for (nint x = 0; x < blockEnd; x += Vector128BlockPixels)
                Block24Vector128(ref row, ref dest, x, weights, front, back);
            // One block that ends at the row's last pixel and redoes a few pixels with the same values.
            if (hasTail)
                Block24Vector128(ref row, ref dest, tail, weights, front, back);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Block24Vector128(ref byte row, ref byte dest, nint x, Vector128<uint> weights, Vector128<byte> front, Vector128<byte> back)
    {
        ref var p = ref Unsafe.Add(ref row, x * 3);
        var v0 = Spread24Vector128(Vector128.LoadUnsafe(ref p), front);
        var v1 = Spread24Vector128(Vector128.LoadUnsafe(ref p, 12), front);
        var v2 = Spread24Vector128(Vector128.LoadUnsafe(ref p, 24), front);
        var v3 = Spread24Vector128(Vector128.LoadUnsafe(ref p, 32), back);
        StoreBlockVector128(ref dest, x, LumaVector128(v0, weights), LumaVector128(v1, weights), LumaVector128(v2, weights), LumaVector128(v3, weights), wrap: false);
    }

    /// <summary>Four three-byte pixels to four lanes. Every index is under 16, so both instructions pick the same bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> Spread24Vector128(Vector128<byte> bytes, Vector128<byte> indices)
    {
        if (Ssse3.IsSupported)
            return Ssse3.Shuffle(bytes, indices).AsUInt32();
        return PackedSimd.Swizzle(bytes, indices).AsUInt32();
    }
}
#endif
