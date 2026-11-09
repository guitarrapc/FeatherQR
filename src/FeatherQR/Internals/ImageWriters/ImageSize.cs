namespace FeatherQR.Internals.ImageWriters;

/// <summary>
/// The size limits of the SVG, PNG and pixel outputs.
/// </summary>
internal static class ImageSize
{
    /// <summary>The longest byte array the runtime makes, and so the longest output.</summary>
    public const int MaxArrayLength = 0x7FFFFFC7;

    /// <summary>
    /// The image's width and height in pixels, which every format writes.
    /// </summary>
    public static (int Width, int Height) Get<TView>(in TView view, in SymbolImageOptions options)
        where TView : struct, IModuleMatrixView
    {
        var image = ImageLayout.Create(in view, in options);
        // Refused here too, so a caller making an image of this size learns of an icon that does not fit before writing.
        IconArea.Get(in view, in options);
        return (image.Width, image.Height);
    }

    /// <summary>
    /// Refuses an output longer than the longest array, naming the image's size, since the length may be a lower bound.
    /// </summary>
    public static void ThrowIfLongerThanAnArray(long length, int width, int height)
    {
        if (length > MaxArrayLength)
            throw new ArgumentOutOfRangeException("options", $"An image of {width} × {height} pixels is longer than the {MaxArrayLength} bytes an array can hold.");
    }
}
