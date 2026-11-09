#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace FeatherQR;

// AVX2 conversion of three-byte pixels: 32 pixels per iteration, bit-identical to the per-pixel loop in LuminanceConverter.Rgb24.cs.
// The arithmetic is the four-byte kernel's (LuminanceConverter.X86.cs): each pixel shuffled into the byte quad [R, G, G, B] against
// the weights [77, 51, 99, 29]. Only the shuffle differs, since it gathers each quad from three bytes instead of four.
//
// Eight pixels are 24 bytes, and vpshufb picks within a 128-bit lane, so the two lanes are loaded apart:
//
//   bytes of the group   0 ........ 11 12 ....... 15 16 ........ 23
//   lower lane, at 0     [ pixels 0-3 ][  unused    ]
//   upper lane, at 8                  [  unused    ][ pixels 4-7  ]     (bytes 8-23, its pixels at 4-15 of the lane)
//
// The two loads cover exactly the group's 24 bytes, so nothing past a row's last pixel is read.
public static partial class LuminanceConverter
{
    /// <summary>vpshufb indices turning eight Rgb24 pixels, loaded as the two lanes above, into [R, G, G, B] quads.</summary>
    private static Vector256<byte> Rgb24Shuffle => Vector256.Create(
        (byte)0, 1, 1, 2, 3, 4, 4, 5, 6, 7, 7, 8, 9, 10, 10, 11,
        4, 5, 5, 6, 7, 8, 8, 9, 10, 11, 11, 12, 13, 14, 14, 15);

    /// <summary>The same for Bgr24, whose red is each pixel's third byte.</summary>
    private static Vector256<byte> Bgr24Shuffle => Vector256.Create(
        (byte)2, 1, 1, 0, 5, 4, 4, 3, 8, 7, 7, 6, 11, 10, 10, 9,
        6, 5, 5, 4, 9, 8, 8, 7, 12, 11, 11, 10, 15, 14, 14, 13);

    /// <summary>
    /// Converts three-byte pixels with AVX2. <paramref name="bgr"/> selects the channel order.
    /// Rows under eight pixels take the per-pixel formula.
    /// </summary>
    internal static void ConvertRgb24Avx2(ReadOnlySpan<byte> pixels, Span<byte> luminance, int width, int height, int rowBytes, bool bgr)
    {
        ref var src = ref MemoryMarshal.GetReference(pixels);
        ref var dst = ref MemoryMarshal.GetReference(luminance);
        var shuffle = bgr ? Bgr24Shuffle : Rgb24Shuffle;
        var weights = Weights;
        var ones = Vector256.Create((short)1);
        // The packs work per 128-bit lane, so the packed bytes arrive as [a0-3, b0-3, c0-3, d0-3, a4-7, b4-7, c4-7, d4-7] in dword units.
        var order = Vector256.Create(0, 4, 1, 5, 2, 6, 3, 7);

        nint blockEnd = width & ~(BlockPixels - 1);
        nint octetEnd = width & ~7;
        nint redOffset = bgr ? 2 : 0;
        nint blueOffset = bgr ? 0 : 2;

        for (var y = 0; y < height; y++)
        {
            ref var row = ref Unsafe.Add(ref src, (nint)y * rowBytes);
            ref var dest = ref Unsafe.Add(ref dst, (nint)y * width);
            nint x = 0;

            for (; x < blockEnd; x += BlockPixels)
            {
                ref var p = ref Unsafe.Add(ref row, x * 3);
                var s0 = Luma(LoadOctet24(ref p), shuffle, weights, ones);
                var s1 = Luma(LoadOctet24(ref Unsafe.Add(ref p, 24)), shuffle, weights, ones);
                var s2 = Luma(LoadOctet24(ref Unsafe.Add(ref p, 48)), shuffle, weights, ones);
                var s3 = Luma(LoadOctet24(ref Unsafe.Add(ref p, 72)), shuffle, weights, ones);
                var w0 = Avx2.ShiftRightLogical(Avx2.PackUnsignedSaturate(s0, s1), 8).AsInt16();
                var w1 = Avx2.ShiftRightLogical(Avx2.PackUnsignedSaturate(s2, s3), 8).AsInt16();
                Avx2.PermuteVar8x32(Avx2.PackUnsignedSaturate(w0, w1).AsInt32(), order).AsByte()
                    .StoreUnsafe(ref Unsafe.Add(ref dest, x));
            }

            // Row remainder: 8-pixel groups, then one group that ends at the row's last pixel and redoes a few pixels with the same values.
            for (; x < octetEnd; x += 8)
                StoreOctet(ref dest, x, Luma(LoadOctet24(ref Unsafe.Add(ref row, x * 3)), shuffle, weights, ones), shift: true);
            if (x < width && width >= 8)
            {
                StoreOctet(ref dest, width - 8, Luma(LoadOctet24(ref Unsafe.Add(ref row, (nint)(width - 8) * 3)), shuffle, weights, ones), shift: true);
                x = width;
            }

            for (; x < width; x++)
                Unsafe.Add(ref dest, x) = Luma(ref Unsafe.Add(ref row, x * 3), redOffset, 1, blueOffset, hasAlpha: false, premultiplied: false);
        }
    }

    /// <summary>Eight pixels, 24 bytes, as two lanes: the first twelve bytes lead the lower lane and the last twelve end the upper one.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<byte> LoadOctet24(ref byte p)
        => Vector256.Create(Vector128.LoadUnsafe(ref p), Vector128.LoadUnsafe(ref p, 8));
}
#endif
