namespace FeatherQR;

/// <summary>
/// Finds and decodes the symbols of every symbology in a grayscale image: Standard QR, Micro QR and rMQR codes in one call.
/// </summary>
/// <remarks>
/// <para>
/// Each symbol is returned once, as its own symbology, with where it sits in the image. Only symbols that decode are returned, and one that lies inside a symbol already read is left out.
/// The Standard QR codes come first, then the Micro QR and rMQR symbols as found, then any Standard QR code that reads once those are taken out of its way.
/// </para>
/// <para>
/// The search reads Standard QR codes as <see cref="QRCodeDecoder.DecodeImageSymbols(ReadOnlySpan{byte}, int, int)"/> does and then looks for Micro QR and rMQR symbols at every other finder of the image, so it takes longer than the read for one symbology.
/// A caller who knows the symbology of the image calls that symbology's decoder.
/// </para>
/// </remarks>
public static class SymbolDecoder
{
    /// <summary>
    /// Finds and decodes every symbol of any symbology in a grayscale image.
    /// </summary>
    /// <remarks>Allocates the array returned and the texts, and nothing else.</remarks>
    /// <param name="luminance">Grayscale pixels (0 = black, 255 = white), flat row-major order, width × height bytes. Transparent source pixels must be composited against white before conversion: the quiet zone is white by definition, and a symbol composited against black is not detected.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <returns>The symbols read, an empty array when there is none.</returns>
    /// <exception cref="ArgumentException">Thrown when the buffer is smaller than the dimensions require.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="width"/> or <paramref name="height"/> is not positive.</exception>
    public static SymbolDecodeResult[] DecodeImageSymbols(ReadOnlySpan<byte> luminance, int width, int height)
    {
        ThrowIfLuminanceDoesNotFit(luminance, width, height);
        return MixedImageDecoder.DecodeSymbols(luminance, width, height);
    }

    /// <summary>
    /// Finds and decodes the symbols of any symbology in a grayscale image into the room you provide, as many as it holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reads as <see cref="DecodeImageSymbols(ReadOnlySpan{byte}, int, int)"/> does, and stops once <paramref name="destination"/> is full, so a caller who expects a number of symbols pays for no more.
    /// A full room does not say whether the image held more.
    /// </para>
    /// <para>
    /// Nothing is allocated but the texts.
    /// </para>
    /// </remarks>
    /// <param name="luminance">Grayscale pixels (0 = black, 255 = white), flat row-major order, width × height bytes. Transparent source pixels must be composited against white before conversion: the quiet zone is white by definition, and a symbol composited against black is not detected.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="destination">Where to write the symbols read, from the start.</param>
    /// <returns>How many symbols were written.</returns>
    /// <exception cref="ArgumentException">Thrown when the buffer is smaller than the dimensions require.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="width"/> or <paramref name="height"/> is not positive.</exception>
    public static int DecodeImageSymbols(ReadOnlySpan<byte> luminance, int width, int height, Span<SymbolDecodeResult> destination)
    {
        ThrowIfLuminanceDoesNotFit(luminance, width, height);
        return MixedImageDecoder.DecodeSymbols(luminance, width, height, destination);
    }

    private static void ThrowIfLuminanceDoesNotFit(ReadOnlySpan<byte> luminance, int width, int height)
    {
        if (width < 1 || height < 1)
            throw new ArgumentOutOfRangeException(width < 1 ? nameof(width) : nameof(height), $"Image size must be positive: got {width} x {height}.");
        // long arithmetic: dimensions are caller-controlled and width·height can overflow int
        if (luminance.Length < (long)width * height)
            throw new ArgumentException($"Luminance buffer too small: required {(long)width * height}, got {luminance.Length}", nameof(luminance));
    }
}
