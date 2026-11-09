namespace FeatherQR;

/// <summary>
/// The size of an image in whole pixels.
/// </summary>
/// <remarks>
/// Returned by <see cref="SymbolImageWriter"/>'s <c>GetImageSize</c> as the size a symbol's image is written at. A symbol's own size, such as
/// <see cref="QRCodeData.Size"/>, is in modules, a different unit: this one is pixels of the image.
/// </remarks>
public readonly record struct ImageSize
{
    internal ImageSize(int width, int height)
    {
        Width = width;
        Height = height;
    }

    /// <summary>The width in pixels.</summary>
    public int Width { get; }

    /// <summary>The height in pixels.</summary>
    public int Height { get; }

    /// <summary>
    /// Splits the size into its two sides, for <c>var (width, height) = size</c>.
    /// </summary>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    public void Deconstruct(out int width, out int height)
    {
        width = Width;
        height = Height;
    }
}
