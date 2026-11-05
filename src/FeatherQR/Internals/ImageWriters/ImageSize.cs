namespace FeatherQR.Internals.ImageWriters;

/// <summary>
/// The limits every image output shares.
/// </summary>
internal static class ImageSize
{
    /// <summary>The longest byte array the runtime makes, and so the longest output.</summary>
    public const int MaxArrayLength = 0x7FFFFFC7;

    /// <summary>
    /// Refuses a module size that makes either side of the image longer than <see cref="int.MaxValue"/> pixels.
    /// </summary>
    public static void ThrowIfTooLarge<TView>(in TView view, in SymbolImageOptions options)
        where TView : struct, IModuleMatrixView
    {
        long pixels = options.ModulePixelSize;
        if (view.Width * pixels > int.MaxValue || view.Height * pixels > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(options), $"A module of {pixels} pixels makes the image {view.Width * pixels} × {view.Height * pixels} pixels, and a side can be at most {int.MaxValue}.");
    }

    /// <summary>
    /// Refuses an output longer than the longest array.
    /// The length may be a lower bound, such as a pixel count, so the message does not quote it.
    /// </summary>
    public static void ThrowIfLongerThanAnArray(long length, in SymbolImageOptions options)
    {
        if (length > MaxArrayLength)
            throw new ArgumentOutOfRangeException(nameof(options), $"A module of {options.ModulePixelSize} pixels makes the image longer than the {MaxArrayLength} bytes an array can hold.");
    }
}
