using System.Buffers;
using FeatherQR.SkiaSharp.Internals;
using SkiaSharp;
using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.SkiaSharp;

/// <summary>
/// Decodes rMQR Codes from SkiaSharp bitmaps, and from encoded images read from a stream.
/// Extends <see cref="RmQRCodeDecoder"/>, so with C# 14 the overloads are also reachable as <c>RmQRCodeDecoder.TryDecode(bitmap, ...)</c> and <c>RmQRCodeDecoder.DecodeAsync(stream)</c>. On older language versions call them on this class.
/// </summary>
public static class RmQRCodeImageDecoder
{
    extension(RmQRCodeDecoder)
    {
        /// <summary>
        /// Finds and decodes an rMQR code in a bitmap.
        /// </summary>
        /// <remarks>
        /// Reads screenshots, rendered rMQR codes and scans, at any rotation, mirrored, inverted, scaled, tilted, blurred or noisy, or under a shading gradient or a soft-edged shadow.
        /// Strong lens distortion and hard-edged shadows read less reliably.
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
        /// <param name="info">What the attempt found: status, version, level and corrections, and on success where the symbol sits in the image (Corners) and the FNC1 mode when the symbol carries one (<see cref="RmQRCodeDecodeInfo.Fnc1"/>).</param>
        /// <returns><c>true</c> when an rMQR code was found and decoded.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="bitmap"/> is <c>null</c>.</exception>
        public static bool TryDecode(SKBitmap bitmap, out string text, out RmQRCodeDecodeInfo info)
        {
            if (bitmap is null)
                throw new ArgumentNullException(nameof(bitmap));

            var width = bitmap.Width;
            var height = bitmap.Height;
            // The smallest symbol is 7 modules tall and 27 wide, and either axis may be the image's short side
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
        /// <summary>
        /// Reads an image from a stream asynchronously, and finds and decodes an rMQR code in it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The stream is read to its end through its asynchronous reads alone, so a stream that refuses synchronous reads, such as a server's request body, gives it.
        /// The image is then decoded as <see cref="SKBitmap.Decode(Stream)"/> decodes it, in its pixels as encoded with no EXIF orientation applied, and read as <see cref="TryDecode(SKBitmap, out string, out RmQRCodeDecodeInfo)"/> reads that bitmap.
        /// Called on a thread with a synchronization context, such as a UI thread, the call decodes on the thread pool, so the caller can await it where it is.
        /// </para>
        /// <para>
        /// A stream of more than 256 MiB, or an image whose header declares more than 268,435,456 pixels (16,384 × 16,384), is refused before any pixel is allocated.
        /// A larger image can be decoded into an <see cref="SKBitmap"/> and read with <see cref="TryDecode(SKBitmap, out string, out RmQRCodeDecodeInfo)"/>.
        /// </para>
        /// </remarks>
        /// <param name="image">The encoded image, read from its position to its end. Left open afterwards.</param>
        /// <param name="cancellationToken">Stops the call before the decode and during the reads.</param>
        /// <returns>The result in every case. On a failure <see cref="RmQRCodeDecodeResult.Success"/> is <c>false</c> and <see cref="RmQRCodeDecodeInfo.Status"/> says why.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="image"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="image"/> is not readable.</exception>
        /// <exception cref="InvalidDataException">Faults the task when the stream holds no image that can be decoded, or one past the limits.</exception>
        /// <exception cref="OperationCanceledException">Cancels the task when <paramref name="cancellationToken"/> is canceled.</exception>
        public static Task<RmQRCodeDecodeResult> DecodeAsync(Stream image, CancellationToken cancellationToken = default)
        {
            EncodedImage.ThrowIfUnreadable(image);
            return EncodedImage.ReadAsync(image, ReadOne, cancellationToken);
        }

        /// <summary>
        /// Reads an image from a stream, and finds and decodes an rMQR code in it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The synchronous twin of <see cref="DecodeAsync(Stream, CancellationToken)"/>: the stream is read to its end through its synchronous reads, and the image is decoded and read as there, under the same limits.
        /// </para>
        /// </remarks>
        /// <param name="image">The encoded image, read from its position to its end. Left open afterwards.</param>
        /// <returns>The result in every case. On a failure <see cref="RmQRCodeDecodeResult.Success"/> is <c>false</c> and <see cref="RmQRCodeDecodeInfo.Status"/> says why.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="image"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="image"/> is not readable.</exception>
        /// <exception cref="InvalidDataException">Thrown when the stream holds no image that can be decoded, or one past the limits.</exception>
        public static RmQRCodeDecodeResult Decode(Stream image)
        {
            EncodedImage.ThrowIfUnreadable(image);
            return EncodedImage.Read(image, ReadOne);
        }
    }

    private static RmQRCodeDecodeResult ReadOne(SKBitmap bitmap)
    {
        RmQRCodeDecoder.TryDecode(bitmap, out var text, out var info);
        return new RmQRCodeDecodeResult(text, info);
    }
}
