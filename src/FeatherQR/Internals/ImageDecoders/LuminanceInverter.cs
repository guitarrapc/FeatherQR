#if NET8_0_OR_GREATER
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
#endif

namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// Reflectance reversal: writes the photographic negative of a luminance buffer, so a light-on-dark symbol can be retried as dark-on-light.
/// </summary>
/// <remarks>
/// Shared by all three image decoders, and paid on every image that fails to decode in its first polarity — which makes it a fixed cost of the failure path rather than of the success path.
/// <c>255 - x</c> on a byte is the ones' complement, so the vector form is a single NOT per lane; measured 17-20x over the per-byte loop across 33k-922k pixels on AVX2.
/// Two vector widths rather than one: 256-bit where it is accelerated, otherwise 128-bit, which keeps ARM64 (NEON is 128-bit, so <c>Vector256.IsHardwareAccelerated</c> is false there) off the scalar loop.
/// </remarks>
internal static class LuminanceInverter
{
    /// <summary>Writes <c>255 - source[i]</c> for every byte of <paramref name="destination"/>.</summary>
    /// <param name="source">Luminance to negate; must be at least as long as <paramref name="destination"/>.</param>
    /// <param name="destination">Receives the negated bytes. May alias <paramref name="source"/> exactly.</param>
    /// <exception cref="ArgumentException"><paramref name="source"/> is shorter than <paramref name="destination"/>.</exception>
    internal static void Invert(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        // The vector paths address both spans through unchecked offsets bounded by
        // destination.Length, so the one precondition they cannot re-derive is checked
        // once here rather than per element.
        if (source.Length < destination.Length)
            throw new ArgumentException("Source must be at least as long as destination.", nameof(source));

        var i = 0;
#if NET8_0_OR_GREATER
        ref var src = ref MemoryMarshal.GetReference(source);
        ref var dst = ref MemoryMarshal.GetReference(destination);

        // The tail is scalar rather than an overlapping final vector. Re-reading bytes
        // the aligned loop has already written would invert them a second time whenever
        // the caller passes the same buffer for both spans, and a negate that is only
        // correct when the spans are distinct is a trap for a one-line helper. At most
        // 31 bytes of a buffer that is hundreds of thousands long, so it costs nothing.
        if (Vector256.IsHardwareAccelerated && destination.Length >= Vector256<byte>.Count)
        {
            var last = destination.Length - Vector256<byte>.Count;
            for (; i <= last; i += Vector256<byte>.Count)
            {
                Vector256.OnesComplement(Vector256.LoadUnsafe(ref src, (nuint)i)).StoreUnsafe(ref dst, (nuint)i);
            }
        }
        else if (Vector128.IsHardwareAccelerated && destination.Length >= Vector128<byte>.Count)
        {
            var last = destination.Length - Vector128<byte>.Count;
            for (; i <= last; i += Vector128<byte>.Count)
            {
                Vector128.OnesComplement(Vector128.LoadUnsafe(ref src, (nuint)i)).StoreUnsafe(ref dst, (nuint)i);
            }
        }
#endif
        for (; i < destination.Length; i++)
        {
            destination[i] = (byte)(255 - source[i]);
        }
    }
}
