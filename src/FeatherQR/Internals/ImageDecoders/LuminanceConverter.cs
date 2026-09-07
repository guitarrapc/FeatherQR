using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// Converts pixel buffers to the 8-bit grayscale luminance the image decoders read (<c>TryDecodeImage</c> on <see cref="QRCodeDecoder"/>, <see cref="MicroQRCodeDecoder"/> and <see cref="RmQRCodeDecoder"/>).
/// </summary>
/// <remarks>
/// <para>
/// Internal, reached by the first-party rendering package through <c>InternalsVisibleTo</c>: an adapter reads the pixel layout out of its bitmap type and hands the raw bytes here.
/// The layouts are the ones QR sources actually use (<see cref="PixelLayout"/>); anything else is converted by the caller first.
/// A third-party decoder adapter produces luminance itself and calls <c>TryDecodeImage</c>; this type is not public so that the surface does not grow ahead of a concrete need.
/// </para>
/// <para>
/// Luminance is ITU-R BT.601, (77 R + 150 G + 29 B) / 256. Transparent pixels are composited against white before weighting: QR quiet zones are white by definition, and transparent-background PNGs are a common input.
/// The kernels run in three tiers (AVX2, NEON, portable scalar) that produce identical bytes.
/// </para>
/// </remarks>
internal static partial class LuminanceConverter
{
    /// <summary>
    /// Converts pixels to luminance: width × height bytes, row-major, no padding.
    /// </summary>
    /// <param name="pixels">Source pixels, rows of <paramref name="rowBytes"/> bytes; the last row may end after its <paramref name="width"/> pixels.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="rowBytes">Stride between rows in bytes, at least <paramref name="width"/> × bytes per pixel.</param>
    /// <param name="layout">Channel order and depth of <paramref name="pixels"/>.</param>
    /// <param name="premultipliedAlpha">Whether the color channels are premultiplied by alpha. Ignored for layouts without alpha.</param>
    /// <param name="luminance">Destination, at least width × height bytes (<see cref="ImageDimensions.TryGetPixelCount"/>).</param>
    /// <remarks>A zero width or height converts nothing and does not touch either buffer.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="layout"/> is not a defined value, a dimension is negative, or <paramref name="rowBytes"/> is negative or shorter than one row.</exception>
    /// <exception cref="ArgumentException"><paramref name="pixels"/> or <paramref name="luminance"/> is too short for the dimensions.</exception>
    public static void Convert(ReadOnlySpan<byte> pixels, int width, int height, int rowBytes, PixelLayout layout, bool premultipliedAlpha, Span<byte> luminance)
    {
        if (width < 0)
            throw new ArgumentOutOfRangeException(nameof(width), width, "Width must not be negative.");
        if (height < 0)
            throw new ArgumentOutOfRangeException(nameof(height), height, "Height must not be negative.");
        var bytesPerPixel = layout switch
        {
            PixelLayout.Gray8 => 1,
            PixelLayout.Rgba8888 or PixelLayout.Bgra8888 or PixelLayout.Rgb888x => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "Unknown pixel layout."),
        };
        if (width > 0 && height > 0 && rowBytes < width * (long)bytesPerPixel)
            throw new ArgumentOutOfRangeException(nameof(rowBytes), rowBytes, $"Row stride must cover a row of {width} pixels at {bytesPerPixel} bytes each.");

        switch (layout)
        {
            case PixelLayout.Gray8:
                ConvertGray8(pixels, luminance, width, height, rowBytes);
                break;
            case PixelLayout.Rgba8888:
                ConvertRgba(pixels, luminance, width, height, rowBytes, redOffset: 0, greenOffset: 1, blueOffset: 2, alphaOffset: 3, premultipliedAlpha);
                break;
            case PixelLayout.Bgra8888:
                ConvertRgba(pixels, luminance, width, height, rowBytes, redOffset: 2, greenOffset: 1, blueOffset: 0, alphaOffset: 3, premultipliedAlpha);
                break;
            default:
                ConvertRgba(pixels, luminance, width, height, rowBytes, redOffset: 0, greenOffset: 1, blueOffset: 2, alphaOffset: -1, premultiplied: false);
                break;
        }
    }

    /// <summary>
    /// 8-bit grayscale pixels to luminance: a row-by-row copy that drops the row padding.
    /// </summary>
    internal static void ConvertGray8(ReadOnlySpan<byte> pixels, Span<byte> luminance, int width, int height, int rowBytes)
    {
        ValidateExtents(pixels, luminance, width, height, rowBytes, bytesPerPixel: 1);
        for (var y = 0; y < height; y++)
        {
            pixels.Slice(y * rowBytes, width).CopyTo(luminance.Slice(y * width, width));
        }
    }

    /// <summary>
    /// Pixel buffer to BT.601 luminance.
    /// Three tiers: an AVX2 kernel at 32 pixels per iteration (LuminanceConverter.Simd.cs), a NEON kernel at 16 pixels per iteration (LuminanceConverter.Simd.Arm.cs), and this per-pixel loop everywhere else.
    /// All three produce identical bytes; see LuminanceConverterParityTest.
    /// </summary>
    /// <remarks>
    /// The extents are checked once here, not per row.
    /// Every tier walks by <c>ref</c> from a single <c>GetReference</c> and so carries no bounds check of its own — including the scalar tier, whose per-row re-slicing was removed for speed.
    /// A caller that hands over a short destination would otherwise corrupt whatever follows it (in practice a pooled rental) instead of throwing.
    /// </remarks>
    internal static void ConvertRgba(ReadOnlySpan<byte> pixels, Span<byte> luminance, int width, int height, int rowBytes, int redOffset, int greenOffset, int blueOffset, int alphaOffset, bool premultiplied, bool forceScalar = false)
    {
        ValidateExtents(pixels, luminance, width, height, rowBytes, bytesPerPixel: 4);
#if NET8_0_OR_GREATER
        // One predicate, shared with the parity tests. Written as a second copy of this
        // condition it would be free to drift, and a drifted copy makes every pinned
        // "vector" assertion silently compare the scalar tier against itself.
        if (!forceScalar && IsVectorTierTaken(width, redOffset, greenOffset, blueOffset))
        {
            if (IsAvx2TierSupported)
            {
                ConvertRgbaAvx2(pixels, luminance, width, height, rowBytes, bgra: redOffset == 2, hasAlpha: alphaOffset >= 0, premultiplied);
                return;
            }

            // The NEON kernel covers a whole row from 16-pixel blocks plus one
            // overlapping final block, so it has no scalar remainder to fall back on;
            // IsVectorTierTaken is what keeps narrower rows off it.
            ConvertRgbaAdvSimd(pixels, luminance, width, height, rowBytes, bgra: redOffset == 2, hasAlpha: alphaOffset >= 0, premultiplied);
            return;
        }
#endif
        ConvertRgbaScalar(pixels, luminance, width, height, rowBytes, redOffset, greenOffset, blueOffset, alphaOffset, premultiplied);
    }

#if NET8_0_OR_GREATER
    /// <summary>BGRA (2,1,0) or RGBA / RGB888x (0,1,2); nothing else reaches a vector tier.</summary>
    private static bool IsVectorLayout(int redOffset, int greenOffset, int blueOffset)
        => greenOffset == 1 && (redOffset == 2 ? blueOffset == 0 : redOffset == 0 && blueOffset == 2);

    /// <summary>
    /// UDOT carries the whole luminance sum (<see cref="System.Runtime.Intrinsics.Arm.Dp"/>, an ARMv8.2 extension that Cortex-A53/A72-class cores lack) and the composite path separates planes with UZP1/UZP2, which live under <c>AdvSimd.Arm64</c> — so both gates are required rather than plain AdvSimd.
    /// LD4 would also serve for the planes but has no ref-taking overload, so it was rejected (see LuminanceConverter.Simd.Arm.cs).
    /// </summary>
    private static bool IsAdvSimdTierAvailable
        => System.Runtime.Intrinsics.Arm.Dp.IsSupported && System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported;
#endif

    /// <summary>Whether the AVX2 tier runs on this machine (parity tests skip it otherwise).</summary>
    internal static bool IsAvx2TierSupported =>
#if NET8_0_OR_GREATER
        System.Runtime.Intrinsics.X86.Avx2.IsSupported;
#else
        false;
#endif

    /// <summary>Whether the NEON tier runs on this machine (parity tests skip it otherwise).</summary>
    internal static bool IsAdvSimdTierSupported =>
#if NET8_0_OR_GREATER
        IsAdvSimdTierAvailable;
#else
        false;
#endif

    /// <summary>Extents for a ref-walking tier; see the remarks on <see cref="ConvertRgba"/>.</summary>
    private static void ValidateExtents(ReadOnlySpan<byte> pixels, Span<byte> luminance, int width, int height, int rowBytes, int bytesPerPixel)
    {
        // Nothing is walked, and a negative extent would make every comparison below
        // vacuously true; the loops run zero iterations either way.
        if (width <= 0 || height <= 0)
            return;
        if (rowBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(rowBytes), rowBytes, "Row stride must not be negative.");

        if (!ImageDimensions.TryGetPixelCount(width, height, out var pixelCount) || luminance.Length < pixelCount)
            throw new ArgumentException($"Luminance buffer too small: required {(long)width * height} bytes ({width}x{height}), got {luminance.Length}.", nameof(luminance));
        var requiredPixels = (long)(height - 1) * rowBytes + (long)width * bytesPerPixel;
        if (pixels.Length < requiredPixels)
            throw new ArgumentException($"Pixel buffer too small: required {requiredPixels} bytes ({width}x{height}, rowBytes {rowBytes}), got {pixels.Length}.", nameof(pixels));
    }

    /// <summary>Which tier a parity test wants; <see cref="ConvertTier.Vector"/> refuses to fall back.</summary>
    internal enum ConvertTier
    {
        /// <summary>The portable per-layout loop.</summary>
        Scalar,
        /// <summary>The AVX2 or NEON kernel, whichever this machine runs.</summary>
        Vector,
    }

    /// <summary>
    /// Whether a vector kernel actually converts a row of this width and layout here.
    /// A parity test must consult this before pinning <see cref="ConvertTier.Vector"/>: the NEON tier declines rows narrower than one block, so on ARM64 the small widths would otherwise compare the scalar tier against itself and pass without running the kernel they name.
    /// </summary>
    internal static bool IsVectorTierTaken(int width, int redOffset, int greenOffset, int blueOffset)
    {
#if NET8_0_OR_GREATER
        if (!IsVectorTierTakenForLayout(redOffset, greenOffset, blueOffset))
            return false;
        return IsAvx2TierSupported || (IsAdvSimdTierAvailable && width >= AdvSimdBlockPixels);
#else
        return false;
#endif
    }

    /// <summary>
    /// The layout half of <see cref="IsVectorTierTaken"/> — layout ONLY, deliberately carrying no ISA or width term.
    /// A parity test skips on this and then asserts a coverage floor, so anything that switches a tier off fails the floor instead of widening the skip until the test is green and empty.
    /// </summary>
    internal static bool IsVectorTierTakenForLayout(int redOffset, int greenOffset, int blueOffset)
    {
#if NET8_0_OR_GREATER
        return IsVectorLayout(redOffset, greenOffset, blueOffset);
#else
        return false;
#endif
    }

    /// <summary>
    /// Tier-selecting entry for parity tests.
    /// <see cref="ConvertTier.Vector"/> throws when no vector kernel would run for these arguments rather than quietly handing back the scalar result — see <see cref="IsVectorTierTaken"/>.
    /// </summary>
    internal static void ConvertRgbaForTest(ReadOnlySpan<byte> pixels, Span<byte> luminance, int width, int height, int rowBytes, int redOffset, int greenOffset, int blueOffset, int alphaOffset, bool premultiplied, ConvertTier tier)
    {
        if (tier == ConvertTier.Scalar)
        {
            // Through ConvertRgba, not straight to ConvertRgbaScalar: a private copy of
            // the validation here would leave the shipping call with no test.
            ConvertRgba(pixels, luminance, width, height, rowBytes, redOffset, greenOffset, blueOffset, alphaOffset, premultiplied, forceScalar: true);
            return;
        }
        if (!IsVectorTierTaken(width, redOffset, greenOffset, blueOffset))
            throw new PlatformNotSupportedException($"{nameof(ConvertTier)}.{nameof(ConvertTier.Vector)} was pinned, but no vector kernel converts width {width} with offsets ({redOffset},{greenOffset},{blueOffset}) here. Guard the call with {nameof(IsVectorTierTaken)}.");
        ConvertRgba(pixels, luminance, width, height, rowBytes, redOffset, greenOffset, blueOffset, alphaOffset, premultiplied);
    }

    /// <summary>
    /// The portable tier: every target without a vector kernel (netstandard, x86 without AVX2, ARM64 without the dot-product extension) and every row too narrow for one, so it is shipped code rather than only a reference.
    /// </summary>
    /// <remarks>
    /// Three things earn their keep here, each measured separately on Apple M2 against the straightforward per-pixel loop this replaces (1.3-1.8x overall):
    /// <list type="bullet">
    /// <item>The channel offsets become constants of a per-layout loop instead of
    /// parameters, so the address arithmetic folds and RGB888x drops its alpha branch outright.</item>
    /// <item>Walking by <c>ref</c> rather than re-slicing spans removes the per-pixel
    /// bounds checks: worth 16-30 % on its own.</item>
    /// <item>Four pixels per iteration, worth a further 8-11 % because the loop is
    /// latency-bound on load → multiply → store rather than throughput-bound.</item>
    /// </list>
    /// <para>
    /// Two plausible-looking alternatives were measured and rejected.
    /// Reading the pixel as one <c>uint</c> and splitting it with shifts costs 37-53 % on ARM64, where a byte load at a constant offset is cheap and the extra ALU work is not; that is the opposite of the x64 result.
    /// Replacing the three multiplies with 256-entry weight tables loses 3-5 % (MUL is 1-2 cycles at one per cycle, while three more L1 loads contend with the pixel loads) and would additionally read out of bounds on a premultiplied buffer that violates c ≤ a, which nothing forbids at this boundary.
    /// Eight pixels per iteration regressed one layout by 2x.
    /// </para>
    /// </remarks>
    private static void ConvertRgbaScalar(ReadOnlySpan<byte> pixels, Span<byte> luminance, int width, int height, int rowBytes, int redOffset, int greenOffset, int blueOffset, int alphaOffset, bool premultiplied)
    {
        // Constant offsets per layout; the general arm keeps arbitrary offsets working
        // for any caller the switch above does not anticipate.
        if (greenOffset == 1 && redOffset == 2 && blueOffset == 0 && alphaOffset == 3)
            Rows(pixels, luminance, width, height, rowBytes, 2, 1, 0, true, premultiplied);
        else if (greenOffset == 1 && redOffset == 0 && blueOffset == 2 && alphaOffset == 3)
            Rows(pixels, luminance, width, height, rowBytes, 0, 1, 2, true, premultiplied);
        else if (greenOffset == 1 && redOffset == 0 && blueOffset == 2 && alphaOffset < 0)
            Rows(pixels, luminance, width, height, rowBytes, 0, 1, 2, false, premultiplied);
        else
            RowsGeneral(pixels, luminance, width, height, rowBytes, redOffset, greenOffset, blueOffset, alphaOffset, premultiplied);
    }

    /// <summary>Inlined at each call site so the offsets and the alpha flag are constants.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Rows(ReadOnlySpan<byte> pixels, Span<byte> luminance, int width, int height, int rowBytes, nint redOffset, nint greenOffset, nint blueOffset, bool hasAlpha, bool premultiplied)
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
                Unsafe.Add(ref dest, x) = Luma(ref Unsafe.Add(ref row, x * 4), redOffset, greenOffset, blueOffset, hasAlpha, premultiplied);
                Unsafe.Add(ref dest, x + 1) = Luma(ref Unsafe.Add(ref row, (x + 1) * 4), redOffset, greenOffset, blueOffset, hasAlpha, premultiplied);
                Unsafe.Add(ref dest, x + 2) = Luma(ref Unsafe.Add(ref row, (x + 2) * 4), redOffset, greenOffset, blueOffset, hasAlpha, premultiplied);
                Unsafe.Add(ref dest, x + 3) = Luma(ref Unsafe.Add(ref row, (x + 3) * 4), redOffset, greenOffset, blueOffset, hasAlpha, premultiplied);
            }
            for (; x < width; x++)
                Unsafe.Add(ref dest, x) = Luma(ref Unsafe.Add(ref row, x * 4), redOffset, greenOffset, blueOffset, hasAlpha, premultiplied);
        }
    }

    /// <summary>One pixel: composite against white if needed, then ITU-R BT.601 integer luma.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Luma(ref byte pixel, nint redOffset, nint greenOffset, nint blueOffset, bool hasAlpha, bool premultiplied)
    {
        int r = Unsafe.Add(ref pixel, redOffset);
        int g = Unsafe.Add(ref pixel, greenOffset);
        int b = Unsafe.Add(ref pixel, blueOffset);

        if (hasAlpha)
        {
            int a = Unsafe.Add(ref pixel, 3);
            if (a != 255)
            {
                // Composite against white (the quiet zone color)
                if (premultiplied)
                {
                    // Premultiplied channels already carry c·a/255
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

        return (byte)((77 * r + 150 * g + 29 * b) >> 8);
    }

    /// <summary>Arbitrary channel offsets; the straightforward loop, kept for completeness.</summary>
    private static void RowsGeneral(ReadOnlySpan<byte> pixels, Span<byte> luminance, int width, int height, int rowBytes, int redOffset, int greenOffset, int blueOffset, int alphaOffset, bool premultiplied)
    {
        for (var y = 0; y < height; y++)
        {
            var row = pixels.Slice(y * rowBytes, width * 4);
            var dest = luminance.Slice(y * width, width);
            for (var x = 0; x < width; x++)
            {
                var p = x * 4;
                int r = row[p + redOffset];
                int g = row[p + greenOffset];
                int b = row[p + blueOffset];

                if (alphaOffset >= 0)
                {
                    int a = row[p + alphaOffset];
                    if (a != 255)
                    {
                        // Composite against white (the quiet zone color)
                        if (premultiplied)
                        {
                            // Premultiplied channels already carry c·a/255
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

                // ITU-R BT.601 integer luma
                dest[x] = (byte)((77 * r + 150 * g + 29 * b) >> 8);
            }
        }
    }
}
