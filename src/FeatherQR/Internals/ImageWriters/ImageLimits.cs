namespace FeatherQR.Internals.ImageWriters;

/// <summary>
/// The size limits of the SVG, PNG and pixel outputs.
/// </summary>
internal static class ImageLimits
{
    /// <summary>The longest byte array the runtime makes, and so the longest output.</summary>
    public const int MaxArrayLength = 0x7FFFFFC7;

    /// <summary>
    /// Refuses an output longer than the longest array, naming the image's size, since the length may be a lower bound.
    /// </summary>
    public static void ThrowIfLongerThanAnArray(long length, int width, int height)
    {
        if (length > MaxArrayLength)
            throw new ArgumentOutOfRangeException("options", $"An image of {width} × {height} pixels is longer than the {MaxArrayLength} bytes an array can hold.");
    }
}
