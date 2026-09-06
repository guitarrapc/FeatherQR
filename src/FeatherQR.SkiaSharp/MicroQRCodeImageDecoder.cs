using System.Buffers;
using FeatherQR.SkiaSharp.Internals;
using SkiaSharp;
using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.SkiaSharp;

/// <summary>
/// Decodes Micro QR codes from SkiaSharp bitmaps.
/// Extends <see cref="MicroQRCodeDecoder"/>, so with C# 14 the overloads are also reachable as <c>MicroQRCodeDecoder.TryDecode(bitmap, ...)</c>; on older language versions call them on this class.
/// </summary>
public static class MicroQRCodeImageDecoder
{
    extension(MicroQRCodeDecoder)
    {
        /// <summary>
        /// Finds and decodes a Micro QR code in a bitmap.
        /// </summary>
        /// <remarks>
        /// Made for clean, well-lit images: screenshots, rendered Micro QR codes and scans, at any rotation, mirrored, inverted, scaled, or mildly skewed.
        /// Photos with strong perspective, uneven lighting or blur are out of scope.
        /// </remarks>
        /// <param name="bitmap">The bitmap to scan.</param>
        /// <param name="text">Decoded text, or an empty string when decoding fails.</param>
        /// <returns><c>true</c> when a Micro QR code was found and decoded.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> is <c>null</c>.</exception>
        public static bool TryDecode(SKBitmap bitmap, out string text)
            => TryDecode(bitmap, out text, out _);

        /// <summary>
        /// Finds and decodes a Micro QR code in a bitmap, and reports what it found.
        /// </summary>
        /// <remarks>
        /// See <see cref="TryDecode(SKBitmap, out string)"/> for the supported image envelope.
        /// </remarks>
        /// <param name="bitmap">The bitmap to scan.</param>
        /// <param name="text">Decoded text, or an empty string when decoding fails.</param>
        /// <param name="info">What the attempt found: status, version, level, mask and corrections.</param>
        /// <returns><c>true</c> when a Micro QR code was found and decoded.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> is <c>null</c>.</exception>
        public static bool TryDecode(SKBitmap bitmap, out string text, out MicroQRCodeDecodeInfo info)
        {
            if (bitmap is null)
                throw new ArgumentNullException(nameof(bitmap));

            var width = bitmap.Width;
            var height = bitmap.Height;
            // M1 is 11 modules per side
            if (width < 11 || height < 11 || !ImageDimensions.TryGetPixelCount(width, height, out var pixelCount))
            {
                text = string.Empty;
                info = new MicroQRCodeDecodeInfo(DecodeStatus.NotDetected, 0, default, -1, 0);
                return false;
            }

            var rented = ArrayPool<byte>.Shared.Rent(pixelCount);
            try
            {
                var luminance = rented.AsSpan(0, pixelCount);
                BitmapLuminanceConverter.Convert(bitmap, luminance);
                return MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, out text, out info);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: false);
            }
        }
    }
}
