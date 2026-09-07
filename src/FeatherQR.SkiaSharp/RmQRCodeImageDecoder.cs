using System.Buffers;
using FeatherQR.SkiaSharp.Internals;
using SkiaSharp;
using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.SkiaSharp;

/// <summary>
/// Decodes rMQR Codes from SkiaSharp bitmaps.
/// Extends <see cref="RmQRCodeDecoder"/>, so with C# 14 the overloads are also reachable as <c>RmQRCodeDecoder.TryDecode(bitmap, ...)</c>; on older language versions call them on this class.
/// </summary>
public static class RmQRCodeImageDecoder
{
    extension(RmQRCodeDecoder)
    {
        /// <summary>
        /// Finds and decodes an rMQR code in a bitmap.
        /// </summary>
        /// <remarks>
        /// Made for clean, well-lit images: screenshots, rendered rMQR codes and scans, at any rotation, mirrored, inverted, scaled, or mildly skewed.
        /// Photos with strong perspective, uneven lighting or blur are out of scope.
        /// </remarks>
        /// <param name="bitmap">The bitmap to scan.</param>
        /// <param name="text">Decoded text, or an empty string when decoding fails.</param>
        /// <returns><c>true</c> when an rMQR code was found and decoded.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="bitmap"/> is <c>null</c>.</exception>
        public static bool TryDecode(SKBitmap bitmap, out string text)
            => TryDecode(bitmap, out text, out _);

        /// <summary>
        /// Finds and decodes an rMQR code in a bitmap, and reports what it found.
        /// </summary>
        /// <remarks>
        /// See <see cref="TryDecode(SKBitmap, out string)"/> for the supported image envelope.
        /// </remarks>
        /// <param name="bitmap">The bitmap to scan.</param>
        /// <param name="text">Decoded text, or an empty string when decoding fails.</param>
        /// <param name="info">What the attempt found: status, version, level and corrections, and on success where the symbol sits in the image (Corners).</param>
        /// <returns><c>true</c> when an rMQR code was found and decoded.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="bitmap"/> is <c>null</c>.</exception>
        public static bool TryDecode(SKBitmap bitmap, out string text, out RmQRCodeDecodeInfo info)
        {
            if (bitmap is null)
                throw new ArgumentNullException(nameof(bitmap));

            var width = bitmap.Width;
            var height = bitmap.Height;
            // The smallest symbol is 7 modules tall and 27 wide; either axis may be the image's short side
            if (width < 7 || height < 7 || !ImageDimensions.TryGetPixelCount(width, height, out var pixelCount))
            {
                text = string.Empty;
                info = new RmQRCodeDecodeInfo(DecodeStatus.NotDetected, default, default, 0);
                return false;
            }

            var rented = ArrayPool<byte>.Shared.Rent(pixelCount);
            try
            {
                var luminance = rented.AsSpan(0, pixelCount);
                BitmapLuminanceConverter.Convert(bitmap, luminance);
                return RmQRCodeDecoder.TryDecodeImage(luminance, width, height, out text, out info);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: false);
            }
        }
    }
}
