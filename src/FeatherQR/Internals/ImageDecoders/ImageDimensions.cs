namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// Validates image dimensions before allocating or slicing pixel buffers.
/// </summary>
internal static class ImageDimensions
{
    /// <summary>
    /// Calculates <paramref name="width"/> × <paramref name="height"/> without allowing the result to overflow the <see cref="int"/>-sized buffers used by spans and <see cref="System.Buffers.ArrayPool{T}"/>.
    /// </summary>
    internal static bool TryGetPixelCount(int width, int height, out int pixelCount)
    {
        var count = (long)width * height;
        if (width < 1 || height < 1 || count > int.MaxValue)
        {
            pixelCount = 0;
            return false;
        }

        pixelCount = (int)count;
        return true;
    }

    /// <summary>Throws when the image size is not positive or <paramref name="luminance"/> is shorter than it. The decoders' parameters share these names, which the exceptions carry.</summary>
    internal static void ThrowIfLuminanceDoesNotFit(ReadOnlySpan<byte> luminance, int width, int height)
    {
        if (width < 1 || height < 1)
            throw new ArgumentOutOfRangeException(width < 1 ? nameof(width) : nameof(height), $"Image size must be positive: got {width} x {height}.");
        // long arithmetic: dimensions are caller-controlled and width·height can overflow int
        if (luminance.Length < (long)width * height)
            throw new ArgumentException($"Luminance buffer too small: required {(long)width * height}, got {luminance.Length}", nameof(luminance));
    }
}
