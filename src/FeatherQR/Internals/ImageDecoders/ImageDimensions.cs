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
}
