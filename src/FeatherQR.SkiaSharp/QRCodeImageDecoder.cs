using System.Buffers;
using FeatherQR.SkiaSharp.Internals;
using SkiaSharp;
using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.SkiaSharp;

/// <summary>
/// Decodes QR codes from SkiaSharp bitmaps, and from encoded images read from a stream.
/// Extends <see cref="QRCodeDecoder"/>, so with C# 14 the overloads are also reachable as <c>QRCodeDecoder.TryDecode(bitmap, ...)</c> and <c>QRCodeDecoder.DecodeAsync(stream)</c>. On older language versions call them on this class.
/// </summary>
public static class QRCodeImageDecoder
{
    extension(QRCodeDecoder)
    {
        /// <summary>
        /// Finds and decodes a QR code in a bitmap.
        /// </summary>
        /// <remarks>
        /// Reads screenshots, rendered QR codes, scans and camera photographs, at any rotation, mirrored, inverted, tilted, or under a shading gradient or a soft-edged shadow.
        /// Photographs with glare, damage, blur, a curved surface, a steep tilt or strong lens distortion read less reliably.
        /// </remarks>
        /// <param name="bitmap">The bitmap to scan.</param>
        /// <param name="text">Decoded text, or an empty string when decoding fails.</param>
        /// <returns><c>true</c> when a QR code was found and decoded.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="bitmap"/> is <c>null</c>.</exception>
        public static bool TryDecode(SKBitmap bitmap, out string text)
            => TryDecode(bitmap, out text, out _);

        /// <summary>
        /// Finds and decodes a QR code in a bitmap, and reports what it found.
        /// </summary>
        /// <remarks>
        /// Reads screenshots, rendered QR codes, scans and camera photographs, at any rotation, mirrored, inverted, tilted, or under a shading gradient or a soft-edged shadow.
        /// Photographs with glare, damage, blur, a curved surface, a steep tilt or strong lens distortion read less reliably.
        /// </remarks>
        /// <param name="bitmap">The bitmap to scan.</param>
        /// <param name="text">Decoded text, or an empty string when decoding fails.</param>
        /// <param name="info">What the attempt found: status, version, level, mask and corrections, and on success where the symbol sits in the image (Corners), the Structured Append header when the symbol is one of a set (<see cref="QRCodeDecodeInfo.StructuredAppend"/>) and the FNC1 mode when it carries one (<see cref="QRCodeDecodeInfo.Fnc1"/>).</param>
        /// <returns><c>true</c> when a QR code was found and decoded.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="bitmap"/> is <c>null</c>.</exception>
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
        /// <summary>
        /// Reads an image from a stream asynchronously, and finds and decodes a QR code in it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The stream is read to its end through its asynchronous reads alone, so a stream that refuses synchronous reads, such as a server's request body, gives it.
        /// A <see cref="MemoryStream"/> whose buffer is exposed is read in place instead, from its position to its end, with no call to its reads.
        /// The image is then decoded as <see cref="SKBitmap.Decode(Stream)"/> decodes it, in its pixels as encoded with no EXIF orientation applied, and read as <see cref="TryDecode(SKBitmap, out string, out QRCodeDecodeInfo)"/> reads that bitmap.
        /// Called on a thread with a synchronization context, such as a UI thread, or under a task scheduler of the caller's own, the call decodes on the thread pool, so the caller can await it where it is.
        /// There it decodes a copy of an exposed <see cref="MemoryStream"/>'s buffer, so the buffer may be reused once the call returns.
        /// </para>
        /// <para>
        /// The read takes the limits of <see cref="ImageStreamReadLimits.Default"/>: a stream of more than 64 MiB, or an image whose header declares more than 16,777,216 pixels (4,096 × 4,096), is refused before any pixel is allocated.
        /// Other limits are given with <see cref="DecodeAsync(Stream, ImageStreamReadLimits, CancellationToken)"/>, and a larger image can also be decoded into an <see cref="SKBitmap"/> and read with <see cref="TryDecode(SKBitmap, out string, out QRCodeDecodeInfo)"/>.
        /// </para>
        /// </remarks>
        /// <param name="image">The encoded image, read from its position to its end. Left open afterwards.</param>
        /// <param name="cancellationToken">Stops the call before the decode and during the reads.</param>
        /// <returns>The result in every case. On a failure <see cref="QRCodeDecodeResult.Success"/> is <c>false</c> and <see cref="QRCodeDecodeInfo.Status"/> says why.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="image"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="image"/> is not readable.</exception>
        /// <exception cref="InvalidDataException">Faults the task when the stream holds no image that can be decoded, or one past the limits.</exception>
        /// <exception cref="OperationCanceledException">Cancels the task when <paramref name="cancellationToken"/> is canceled.</exception>
        public static Task<QRCodeDecodeResult> DecodeAsync(Stream image, CancellationToken cancellationToken = default)
            => DecodeAsync(image, ImageStreamReadLimits.Default, cancellationToken);

        /// <summary>
        /// Reads an image from a stream asynchronously under the limits given, and finds and decodes a QR code in it.
        /// </summary>
        /// <remarks>
        /// Reads as <see cref="DecodeAsync(Stream, CancellationToken)"/> does, with <paramref name="limits"/> in place of the default ones.
        /// </remarks>
        /// <param name="image">The encoded image, read from its position to its end. Left open afterwards.</param>
        /// <param name="limits">The most bytes the read takes from the stream, and the most pixels the image's header may declare. <c>null</c> for <see cref="ImageStreamReadLimits.Default"/>.</param>
        /// <param name="cancellationToken">Stops the call before the decode and during the reads.</param>
        /// <returns>The result in every case. On a failure <see cref="QRCodeDecodeResult.Success"/> is <c>false</c> and <see cref="QRCodeDecodeInfo.Status"/> says why.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="image"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="image"/> is not readable.</exception>
        /// <exception cref="InvalidDataException">Faults the task when the stream holds no image that can be decoded, or one past the limits.</exception>
        /// <exception cref="OperationCanceledException">Cancels the task when <paramref name="cancellationToken"/> is canceled.</exception>
        public static Task<QRCodeDecodeResult> DecodeAsync(Stream image, ImageStreamReadLimits? limits, CancellationToken cancellationToken = default)
        {
            EncodedImage.ThrowIfUnreadable(image);
            return EncodedImage.ReadAsync(image, limits ?? ImageStreamReadLimits.Default, ReadOne, cancellationToken);
        }

        /// <summary>
        /// Reads an image from a stream, and finds and decodes a QR code in it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The synchronous twin of <see cref="DecodeAsync(Stream, CancellationToken)"/>: the stream is read to its end through its synchronous reads, or in place as there, and the image is decoded and read as there, under the same limits. Other limits are given with <see cref="Decode(Stream, ImageStreamReadLimits)"/>.
        /// </para>
        /// </remarks>
        /// <param name="image">The encoded image, read from its position to its end. Left open afterwards.</param>
        /// <returns>The result in every case. On a failure <see cref="QRCodeDecodeResult.Success"/> is <c>false</c> and <see cref="QRCodeDecodeInfo.Status"/> says why.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="image"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="image"/> is not readable.</exception>
        /// <exception cref="InvalidDataException">Thrown when the stream holds no image that can be decoded, or one past the limits.</exception>
        public static QRCodeDecodeResult Decode(Stream image)
            => Decode(image, ImageStreamReadLimits.Default);

        /// <summary>
        /// Reads an image from a stream under the limits given, and finds and decodes a QR code in it.
        /// </summary>
        /// <remarks>
        /// Reads as <see cref="Decode(Stream)"/> does, with <paramref name="limits"/> in place of the default ones.
        /// </remarks>
        /// <param name="image">The encoded image, read from its position to its end. Left open afterwards.</param>
        /// <param name="limits">The most bytes the read takes from the stream, and the most pixels the image's header may declare. <c>null</c> for <see cref="ImageStreamReadLimits.Default"/>.</param>
        /// <returns>The result in every case. On a failure <see cref="QRCodeDecodeResult.Success"/> is <c>false</c> and <see cref="QRCodeDecodeInfo.Status"/> says why.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="image"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="image"/> is not readable.</exception>
        /// <exception cref="InvalidDataException">Thrown when the stream holds no image that can be decoded, or one past the limits.</exception>
        public static QRCodeDecodeResult Decode(Stream image, ImageStreamReadLimits? limits)
        {
            EncodedImage.ThrowIfUnreadable(image);
            return EncodedImage.Read(image, limits ?? ImageStreamReadLimits.Default, ReadOne);
        }

        /// <summary>
        /// Finds and decodes every QR code in a bitmap.
        /// </summary>
        /// <remarks>
        /// Reads the bitmap's luminance as <see cref="QRCodeDecoder.DecodeImageSymbols(ReadOnlySpan{byte}, int, int)"/> reads it: each symbol that decodes, once, in the order found.
        /// </remarks>
        /// <param name="bitmap">The bitmap to scan.</param>
        /// <returns>The symbols read, an empty array when there is none.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="bitmap"/> is <c>null</c>.</exception>
        public static QRCodeDecodeResult[] DecodeSymbols(SKBitmap bitmap)
        {
            if (bitmap is null)
                throw new ArgumentNullException(nameof(bitmap));

            return DecodeEverySymbol(bitmap, CancellationToken.None);
        }

        /// <summary>
        /// Reads an image from a stream asynchronously, and finds and decodes every QR code in it.
        /// </summary>
        /// <remarks>
        /// Reads the stream as <see cref="DecodeAsync(Stream, CancellationToken)"/> does, under the same limits, and the bitmap as <see cref="DecodeSymbols(SKBitmap)"/> does. Other limits are given with <see cref="DecodeSymbolsAsync(Stream, ImageStreamReadLimits, CancellationToken)"/>.
        /// The token is also checked before each symbol the search tries, never inside one symbol's read, so a cancel stops the search at its next try.
        /// </remarks>
        /// <param name="image">The encoded image, read from its position to its end. Left open afterwards.</param>
        /// <param name="cancellationToken">Stops the call before the decode, during the reads, and between the symbols the search tries.</param>
        /// <returns>The symbols read, an empty array when there is none.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="image"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="image"/> is not readable.</exception>
        /// <exception cref="InvalidDataException">Faults the task when the stream holds no image that can be decoded, or one past the limits.</exception>
        /// <exception cref="OperationCanceledException">Cancels the task when <paramref name="cancellationToken"/> is canceled.</exception>
        public static Task<QRCodeDecodeResult[]> DecodeSymbolsAsync(Stream image, CancellationToken cancellationToken = default)
            => DecodeSymbolsAsync(image, ImageStreamReadLimits.Default, cancellationToken);

        /// <summary>
        /// Reads an image from a stream asynchronously under the limits given, and finds and decodes every QR code in it.
        /// </summary>
        /// <remarks>
        /// Reads as <see cref="DecodeSymbolsAsync(Stream, CancellationToken)"/> does, with <paramref name="limits"/> in place of the default ones.
        /// </remarks>
        /// <param name="image">The encoded image, read from its position to its end. Left open afterwards.</param>
        /// <param name="limits">The most bytes the read takes from the stream, and the most pixels the image's header may declare. <c>null</c> for <see cref="ImageStreamReadLimits.Default"/>.</param>
        /// <param name="cancellationToken">Stops the call before the decode, during the reads, and between the symbols the search tries.</param>
        /// <returns>The symbols read, an empty array when there is none.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="image"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="image"/> is not readable.</exception>
        /// <exception cref="InvalidDataException">Faults the task when the stream holds no image that can be decoded, or one past the limits.</exception>
        /// <exception cref="OperationCanceledException">Cancels the task when <paramref name="cancellationToken"/> is canceled.</exception>
        public static Task<QRCodeDecodeResult[]> DecodeSymbolsAsync(Stream image, ImageStreamReadLimits? limits, CancellationToken cancellationToken = default)
        {
            EncodedImage.ThrowIfUnreadable(image);
            return EncodedImage.ReadAsync(image, limits ?? ImageStreamReadLimits.Default, DecodeEverySymbol, cancellationToken);
        }

        /// <summary>
        /// Reads an image from a stream, and finds and decodes every QR code in it.
        /// </summary>
        /// <remarks>
        /// The synchronous twin of <see cref="DecodeSymbolsAsync(Stream, CancellationToken)"/>, reading the stream as <see cref="Decode(Stream)"/> does. Other limits are given with <see cref="DecodeSymbols(Stream, ImageStreamReadLimits)"/>.
        /// </remarks>
        /// <param name="image">The encoded image, read from its position to its end. Left open afterwards.</param>
        /// <returns>The symbols read, an empty array when there is none.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="image"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="image"/> is not readable.</exception>
        /// <exception cref="InvalidDataException">Thrown when the stream holds no image that can be decoded, or one past the limits.</exception>
        public static QRCodeDecodeResult[] DecodeSymbols(Stream image)
            => DecodeSymbols(image, ImageStreamReadLimits.Default);

        /// <summary>
        /// Reads an image from a stream under the limits given, and finds and decodes every QR code in it.
        /// </summary>
        /// <remarks>
        /// Reads as <see cref="DecodeSymbols(Stream)"/> does, with <paramref name="limits"/> in place of the default ones.
        /// </remarks>
        /// <param name="image">The encoded image, read from its position to its end. Left open afterwards.</param>
        /// <param name="limits">The most bytes the read takes from the stream, and the most pixels the image's header may declare. <c>null</c> for <see cref="ImageStreamReadLimits.Default"/>.</param>
        /// <returns>The symbols read, an empty array when there is none.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="image"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="image"/> is not readable.</exception>
        /// <exception cref="InvalidDataException">Thrown when the stream holds no image that can be decoded, or one past the limits.</exception>
        public static QRCodeDecodeResult[] DecodeSymbols(Stream image, ImageStreamReadLimits? limits)
        {
            EncodedImage.ThrowIfUnreadable(image);
            return EncodedImage.Read(image, limits ?? ImageStreamReadLimits.Default, DecodeEverySymbol);
        }
    }

    private static QRCodeDecodeResult ReadOne(SKBitmap bitmap, CancellationToken _)
    {
        QRCodeDecoder.TryDecode(bitmap, out var text, out var info);
        return new QRCodeDecodeResult(text, info);
    }

    /// <summary>Every QR code in a bitmap, the token checked between the symbols the search tries.</summary>
    internal static QRCodeDecodeResult[] DecodeEverySymbol(SKBitmap bitmap, CancellationToken cancellationToken)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        if (width < 21 || height < 21 || !ImageDimensions.TryGetPixelCount(width, height, out var pixelCount))
            return [];

        var rented = ArrayPool<byte>.Shared.Rent(pixelCount);
        try
        {
            var luminance = rented.AsSpan(0, pixelCount);
            BitmapLuminanceConverter.Convert(bitmap, luminance);
            return QRImageDecoder.DecodeSymbols(luminance, width, height, cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }
    }
}
