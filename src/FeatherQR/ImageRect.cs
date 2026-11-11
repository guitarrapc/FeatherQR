namespace FeatherQR;

/// <summary>
/// An axis-aligned rectangle in an image, in whole pixels: (0, 0) is the image's top-left pixel, X grows to the right and Y downward.
/// </summary>
/// <remarks>
/// Returned by <see cref="SymbolImageWriter.GetIconRectangle"/> as where an icon lands in a symbol's image, and by
/// <see cref="SymbolImageWriter.GetSymbolRectangle(QRCodeData, in SymbolImageOptions)"/> as where the symbol's modules land. <see cref="ModuleRect"/> is the same
/// shape in modules, a different unit: this one is pixels of the image.
/// </remarks>
public readonly record struct ImageRect
{
    internal ImageRect(int x, int y, int width, int height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    /// <summary>The left edge: the column of the rectangle's first pixel.</summary>
    public int X { get; }

    /// <summary>The top edge: the row of the rectangle's first pixel.</summary>
    public int Y { get; }

    /// <summary>The width in pixels.</summary>
    public int Width { get; }

    /// <summary>The height in pixels.</summary>
    public int Height { get; }

    /// <summary>
    /// Splits the rectangle into its four members, for <c>var (x, y, width, height) = rectangle</c>.
    /// </summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    public void Deconstruct(out int x, out int y, out int width, out int height)
    {
        x = X;
        y = Y;
        width = Width;
        height = Height;
    }
}
