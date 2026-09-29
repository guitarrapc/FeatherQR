namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// One reading of an image, as a pass reads it: the pixels and their size, the threshold that splits them and the grey levels either side of it, carried through the stages of a decoder's search as one argument.
/// </summary>
/// <remarks>
/// The stages that measure (finder sizes, grids, samplers) still take the pieces they read, so each says what it reads and a test can call it on its own; this is what the stages that only pass the image on hand down.
/// </remarks>
internal readonly ref struct ImageView
{
    /// <summary>Grayscale pixels, row-major, <see cref="Width"/> × <see cref="Height"/> bytes.</summary>
    public readonly ReadOnlySpan<byte> Luminance;

    public readonly int Width;

    public readonly int Height;

    /// <summary>A pixel below it is dark.</summary>
    public readonly byte Threshold;

    public readonly GreyLevels Grey;

    /// <summary>The luminance an edge is located at (<see cref="GreyLevels.EdgeLevel"/>): the midpoint of the grey levels, or the threshold where they say nothing.</summary>
    public readonly float EdgeLevel;

    public ImageView(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, GreyLevels grey)
    {
        Luminance = luminance;
        Width = width;
        Height = height;
        Threshold = threshold;
        Grey = grey;
        EdgeLevel = grey.EdgeLevel(threshold);
    }
}
