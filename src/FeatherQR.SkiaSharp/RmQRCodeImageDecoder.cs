using System.Buffers;
using FeatherQR.SkiaSharp.Internals;
using SkiaSharp;
using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.SkiaSharp;

/// <summary>
/// Decodes rMQR Codes from SkiaSharp bitmaps. Extends <see cref="RmQRCodeDecoder"/>,
/// so with C# 14 the overloads are also reachable as <c>RmQRCodeDecoder.TryDecode(bitmap, ...)</c>;
/// on older language versions call them on this class.
/// </summary>
public static class RmQRCodeImageDecoder
{
    extension(RmQRCodeDecoder)
    {
        /// <summary>
        /// Detects and decodes an rMQR Code from a bitmap image.
        /// </summary>
        /// <remarks>
        /// Targets clean, well-lit images such as screenshots, rendered symbols and
        /// scans: arbitrary rotation, mirroring, reflectance reversal (light-on-dark),
        /// uniform or non-uniform scaling, translation and mild perspective distortion
        /// are handled. Strong perspective, uneven lighting and blur are out of scope.
        /// </remarks>
        /// <param name="bitmap">The bitmap to scan.</param>
        /// <param name="text">Decoded text, or an empty string when decoding fails.</param>
        /// <returns>True when an rMQR Code was detected and decoded.</returns>
        /// <exception cref="ArgumentNullException"></exception>
        public static bool TryDecode(SKBitmap bitmap, out string text)
            => TryDecode(bitmap, out text, out _);

        /// <summary>
        /// Detects and decodes an rMQR Code from a bitmap image, with diagnostic information.
        /// </summary>
        /// <remarks>
        /// See <see cref="TryDecode(SKBitmap, out string)"/> for the supported image envelope.
        /// </remarks>
        /// <param name="bitmap">The bitmap to scan.</param>
        /// <param name="text">Decoded text, or an empty string when decoding fails.</param>
        /// <param name="info">Diagnostic information (status, version, ECC level, corrected errors).</param>
        /// <returns>True when an rMQR Code was detected and decoded.</returns>
        /// <exception cref="ArgumentNullException"></exception>
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
