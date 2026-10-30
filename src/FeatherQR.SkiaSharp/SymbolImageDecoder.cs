using System.Buffers;
using FeatherQR.SkiaSharp.Internals;
using SkiaSharp;
using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.SkiaSharp;

/// <summary>
/// Decodes the symbols of every symbology, Standard QR, Micro QR and rMQR, from SkiaSharp bitmaps, and from encoded images read from a stream.
/// Extends <see cref="SymbolDecoder"/>, so with C# 14 the overloads are also reachable as <c>SymbolDecoder.DecodeSymbols(bitmap)</c> and <c>SymbolDecoder.DecodeSymbolsAsync(stream)</c>. On older language versions call them on this class.
/// </summary>
public static class SymbolImageDecoder
{
    extension(SymbolDecoder)
    {
        /// <summary>
        /// Finds and decodes every symbol of any symbology in a bitmap.
        /// </summary>
        /// <remarks>
        /// Reads the bitmap's luminance as <see cref="SymbolDecoder.DecodeImageSymbols(ReadOnlySpan{byte}, int, int)"/> reads it: each symbol that decodes, once, as its own symbology.
        /// </remarks>
        /// <param name="bitmap">The bitmap to scan.</param>
        /// <returns>The symbols read, an empty array when there is none.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="bitmap"/> is <c>null</c>.</exception>
        public static SymbolDecodeResult[] DecodeSymbols(SKBitmap bitmap)
        {
            if (bitmap is null)
                throw new ArgumentNullException(nameof(bitmap));

            return DecodeEverySymbol(bitmap, CancellationToken.None);
        }

        /// <summary>
        /// Reads an image from a stream asynchronously, and finds and decodes every symbol of any symbology in it.
        /// </summary>
        /// <remarks>
        /// Reads the stream as <see cref="QRCodeImageDecoder.DecodeSymbolsAsync(Stream, CancellationToken)"/> does, under the same limits, and the bitmap as <see cref="DecodeSymbols(SKBitmap)"/> does. Other limits are given with <see cref="DecodeSymbolsAsync(Stream, ImageStreamReadLimits, CancellationToken)"/>.
        /// The token is also checked before each symbol the search tries, never inside one symbol's read, so a cancel stops the search at its next try.
        /// </remarks>
        /// <param name="image">The encoded image, read from its position to its end. Left open afterwards.</param>
        /// <param name="cancellationToken">Stops the call before the decode, during the reads, and between the symbols the search tries.</param>
        /// <returns>The symbols read, an empty array when there is none.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="image"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="image"/> is not readable.</exception>
        /// <exception cref="InvalidDataException">Faults the task when the stream holds no image that can be decoded, or one past the limits.</exception>
        /// <exception cref="OperationCanceledException">Cancels the task when <paramref name="cancellationToken"/> is canceled.</exception>
        public static Task<SymbolDecodeResult[]> DecodeSymbolsAsync(Stream image, CancellationToken cancellationToken = default)
            => DecodeSymbolsAsync(image, ImageStreamReadLimits.Default, cancellationToken);

        /// <summary>
        /// Reads an image from a stream asynchronously under the limits given, and finds and decodes every symbol of any symbology in it.
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
        public static Task<SymbolDecodeResult[]> DecodeSymbolsAsync(Stream image, ImageStreamReadLimits? limits, CancellationToken cancellationToken = default)
        {
            EncodedImage.ThrowIfUnreadable(image);
            return EncodedImage.ReadAsync(image, limits ?? ImageStreamReadLimits.Default, DecodeEverySymbol, cancellationToken);
        }

        /// <summary>
        /// Reads an image from a stream, and finds and decodes every symbol of any symbology in it.
        /// </summary>
        /// <remarks>
        /// The synchronous twin of <see cref="DecodeSymbolsAsync(Stream, CancellationToken)"/>, reading the stream as <see cref="QRCodeImageDecoder.Decode(Stream)"/> does. Other limits are given with <see cref="DecodeSymbols(Stream, ImageStreamReadLimits)"/>.
        /// </remarks>
        /// <param name="image">The encoded image, read from its position to its end. Left open afterwards.</param>
        /// <returns>The symbols read, an empty array when there is none.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="image"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="image"/> is not readable.</exception>
        /// <exception cref="InvalidDataException">Thrown when the stream holds no image that can be decoded, or one past the limits.</exception>
        public static SymbolDecodeResult[] DecodeSymbols(Stream image)
            => DecodeSymbols(image, ImageStreamReadLimits.Default);

        /// <summary>
        /// Reads an image from a stream under the limits given, and finds and decodes every symbol of any symbology in it.
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
        public static SymbolDecodeResult[] DecodeSymbols(Stream image, ImageStreamReadLimits? limits)
        {
            EncodedImage.ThrowIfUnreadable(image);
            return EncodedImage.Read(image, limits ?? ImageStreamReadLimits.Default, DecodeEverySymbol);
        }
    }

    /// <summary>Every symbol of any symbology in a bitmap, the token checked between the symbols the search tries.</summary>
    internal static SymbolDecodeResult[] DecodeEverySymbol(SKBitmap bitmap, CancellationToken cancellationToken)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        // rMQR's seven modules are the smallest side of any symbology
        if (width < 7 || height < 7 || !ImageDimensions.TryGetPixelCount(width, height, out var pixelCount))
            return [];

        var rented = ArrayPool<byte>.Shared.Rent(pixelCount);
        try
        {
            var luminance = rented.AsSpan(0, pixelCount);
            BitmapLuminanceConverter.Convert(bitmap, luminance);
            return MixedImageDecoder.DecodeSymbols(luminance, width, height, cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }
    }
}
