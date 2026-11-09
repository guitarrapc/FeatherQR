using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FeatherQR;

// Three bytes a pixel to BT.601 luminance: Rgb24, and Bgr24 with the first and third byte exchanged.
// Neither has alpha, so every pixel is the plain weighted sum the four-byte layouts take at alpha 255.
// Four tiers: an AVX2 kernel at 32 pixels per iteration (LuminanceConverter.Rgb24.X86.cs), a NEON dot-product kernel at 16
// (LuminanceConverter.Rgb24.Arm64.cs), a 128-bit kernel at 16 for x64 without AVX2 and for WebAssembly (LuminanceConverter.Rgb24.Simd.cs),
// and the per-pixel loop here for every other target and for rows too narrow for a kernel.
// All four produce identical bytes. See LuminanceConverterRgb24ParityTest.
public static partial class LuminanceConverter
{
    /// <summary>Pixels per iteration of the NEON and 128-bit kernels, and the narrowest row they take.</summary>
    internal const int Rgb24BlockPixels = 16;

    /// <summary>
    /// Three-byte pixels to luminance. <paramref name="bgr"/> is the channel order: blue first, or red first.
    /// </summary>
    /// <remarks>The extents are checked once here, since every tier walks by <c>ref</c> with no bounds check. See <see cref="ConvertRgba"/>.</remarks>
    internal static void ConvertRgb24(ReadOnlySpan<byte> pixels, Span<byte> luminance, int width, int height, int rowBytes, bool bgr)
    {
        ValidateExtents(pixels, luminance, width, height, rowBytes, bytesPerPixel: 3);
#if NET8_0_OR_GREATER
        if (System.Runtime.Intrinsics.X86.Avx2.IsSupported)
        {
            ConvertRgb24Avx2(pixels, luminance, width, height, rowBytes, bgr);
            return;
        }

        // These kernels cover a row from 16-pixel blocks plus one overlapping final block, so a narrower row has no block to take.
        if (width >= Rgb24BlockPixels)
        {
            if (System.Runtime.Intrinsics.Arm.Dp.IsSupported && System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported)
            {
                ConvertRgb24AdvSimd(pixels, luminance, width, height, rowBytes, bgr);
                return;
            }
            if (System.Runtime.Intrinsics.X86.Ssse3.IsSupported || System.Runtime.Intrinsics.Wasm.PackedSimd.IsSupported)
            {
                ConvertRgb24Vector128(pixels, luminance, width, height, rowBytes, bgr);
                return;
            }
        }
#endif
        ConvertRgb24Scalar(pixels, luminance, width, height, rowBytes, bgr);
    }

    /// <summary>
    /// The portable tier: every target without a vector kernel, and every row too narrow for one. The caller has checked the extents.
    /// </summary>
    internal static void ConvertRgb24Scalar(ReadOnlySpan<byte> pixels, Span<byte> luminance, int width, int height, int rowBytes, bool bgr)
    {
        if (bgr)
            Rows24(pixels, luminance, width, height, rowBytes, redOffset: 2, blueOffset: 0);
        else
            Rows24(pixels, luminance, width, height, rowBytes, redOffset: 0, blueOffset: 2);
    }

    /// <summary>Inlined at each call site so the offsets are constants, four pixels an iteration as the four-byte loop.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Rows24(ReadOnlySpan<byte> pixels, Span<byte> luminance, int width, int height, int rowBytes, nint redOffset, nint blueOffset)
    {
        ref var src = ref MemoryMarshal.GetReference(pixels);
        ref var dst = ref MemoryMarshal.GetReference(luminance);
        nint quadEnd = width & ~3;

        for (var y = 0; y < height; y++)
        {
            ref var row = ref Unsafe.Add(ref src, (nint)y * rowBytes);
            ref var dest = ref Unsafe.Add(ref dst, (nint)y * width);
            nint x = 0;

            for (; x < quadEnd; x += 4)
            {
                ref var p = ref Unsafe.Add(ref row, x * 3);
                Unsafe.Add(ref dest, x) = Luma(ref p, redOffset, 1, blueOffset, hasAlpha: false, premultiplied: false);
                Unsafe.Add(ref dest, x + 1) = Luma(ref Unsafe.Add(ref p, 3), redOffset, 1, blueOffset, hasAlpha: false, premultiplied: false);
                Unsafe.Add(ref dest, x + 2) = Luma(ref Unsafe.Add(ref p, 6), redOffset, 1, blueOffset, hasAlpha: false, premultiplied: false);
                Unsafe.Add(ref dest, x + 3) = Luma(ref Unsafe.Add(ref p, 9), redOffset, 1, blueOffset, hasAlpha: false, premultiplied: false);
            }
            for (; x < width; x++)
                Unsafe.Add(ref dest, x) = Luma(ref Unsafe.Add(ref row, x * 3), redOffset, 1, blueOffset, hasAlpha: false, premultiplied: false);
        }
    }
}
