using System.Buffers;
using FeatherQR.SkiaSharp.Internals;
using SkiaSharp;
using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.SkiaSharp;

/// <summary>
/// Decodes QR codes from SkiaSharp bitmaps. Extends <see cref="QRCodeDecoder"/>,
/// so with C# 14 the overloads are also reachable as <c>QRCodeDecoder.TryDecode(bitmap, ...)</c>;
/// on older language versions call them on this class.
/// </summary>
public static class QRCodeImageDecoder
{
    extension(QRCodeDecoder)
    {
        /// <summary>
        /// Detects and decodes a QR code from a bitmap image.
        /// </summary>
        /// <remarks>
        /// Tier-1/2 image support: clean, well-lit images such as screenshots, rendered
        /// QR codes and scans, including arbitrary rotation, mirroring, reflectance reversal (light-on-dark) and mild perspective distortion. Photos with
        /// strong perspective distortion, uneven lighting or blur are out of scope —
        /// use a computer-vision grade reader (e.g. ZXing.Net) for those.
        /// </remarks>
        /// <param name="bitmap">The bitmap to scan.</param>
        /// <param name="text">Decoded text, or an empty string when decoding fails.</param>
        /// <returns>True when a QR code was detected and decoded.</returns>
        /// <exception cref="ArgumentNullException"></exception>
        public static bool TryDecode(SKBitmap bitmap, out string text)
            => TryDecode(bitmap, out text, out _);

        /// <summary>
        /// Detects and decodes a QR code from a bitmap image, with diagnostic information.
        /// </summary>
        /// <remarks>
        /// Tier-1/2 image support: clean, well-lit images such as screenshots, rendered
        /// QR codes and scans, including arbitrary rotation, mirroring, reflectance reversal (light-on-dark) and mild perspective distortion. Photos with
        /// strong perspective distortion, uneven lighting or blur are out of scope —
        /// use a computer-vision grade reader (e.g. ZXing.Net) for those.
        /// </remarks>
        /// <param name="bitmap">The bitmap to scan.</param>
        /// <param name="text">Decoded text, or an empty string when decoding fails.</param>
        /// <param name="info">Diagnostic information (status, version, ECC level, mask pattern, corrected errors).</param>
        /// <returns>True when a QR code was detected and decoded.</returns>
        /// <exception cref="ArgumentNullException"></exception>
        public static bool TryDecode(SKBitmap bitmap, out string text, out QRCodeDecodeInfo info)
        {
            if (bitmap is null)
                throw new ArgumentNullException(nameof(bitmap));

            var width = bitmap.Width;
            var height = bitmap.Height;
            if (width < 21 || height < 21 || !ImageDimensions.TryGetPixelCount(width, height, out var pixelCount))
            {
                text = string.Empty;
                info = new QRCodeDecodeInfo(DecodeStatus.NotDetected, 0, default, -1, 0);
                return false;
            }

            var rented = ArrayPool<byte>.Shared.Rent(pixelCount);
            try
            {
                var luminance = rented.AsSpan(0, pixelCount);
                BitmapLuminanceConverter.Convert(bitmap, luminance);
                return QRCodeDecoder.TryDecodeImage(luminance, width, height, out text, out info);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: false);
            }
        }
    }
}
