using System.Buffers;
using FeatherQR.SkiaSharp.Internals;
using SkiaSharp;
using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.SkiaSharp;

/// <summary>
/// Decodes Micro QR codes from SkiaSharp bitmaps. Extends <see cref="MicroQRCodeDecoder"/>,
/// so with C# 14 the overloads are also reachable as <c>MicroQRCodeDecoder.TryDecode(bitmap, ...)</c>;
/// on older language versions call them on this class.
/// </summary>
public static class MicroQRCodeImageDecoder
{
    extension(MicroQRCodeDecoder)
    {
        /// <summary>
        /// Detects and decodes a Micro QR code from a bitmap image.
        /// </summary>
        /// <remarks>
        /// Targets clean, well-lit images such as screenshots, rendered symbols and
        /// scans: arbitrary rotation, mirroring, reflectance reversal (light-on-dark),
        /// uniform or non-uniform scaling, translation and mild perspective distortion
        /// are handled. Strong perspective, uneven lighting and blur are out of scope.
        /// </remarks>
        /// <param name="bitmap">The bitmap to scan.</param>
        /// <param name="text">Decoded text, or an empty string when decoding fails.</param>
        /// <returns>True when a Micro QR code was detected and decoded.</returns>
        /// <exception cref="ArgumentNullException"></exception>
        public static bool TryDecode(SKBitmap bitmap, out string text)
            => TryDecode(bitmap, out text, out _);

        /// <summary>
        /// Detects and decodes a Micro QR code from a bitmap image, with diagnostic information.
        /// </summary>
        /// <remarks>
        /// See <see cref="TryDecode(SKBitmap, out string)"/> for the supported image envelope.
        /// </remarks>
        /// <param name="bitmap">The bitmap to scan.</param>
        /// <param name="text">Decoded text, or an empty string when decoding fails.</param>
        /// <param name="info">Diagnostic information (status, version, ECC level, mask pattern, corrected errors).</param>
        /// <returns>True when a Micro QR code was detected and decoded.</returns>
        /// <exception cref="ArgumentNullException"></exception>
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
